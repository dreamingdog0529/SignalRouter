using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SignalRouter
{
    public sealed record InteractionRecorderOptions
    {
        public const long DefaultMaxRecordingBytes = 64L * 1024 * 1024;

        public InteractionRecorderOptions(
            string sessionId,
            string appBuild,
            IInteractionClock? clock = null,
            long maxRecordingBytes = DefaultMaxRecordingBytes)
        {
            InteractionContract.RequireIdentifier(sessionId, nameof(sessionId));
            InteractionContract.RequireIdentifier(appBuild, nameof(appBuild));
            if (maxRecordingBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRecordingBytes),
                    maxRecordingBytes,
                    "The recording size bound must be positive.");
            }

            SessionId = sessionId;
            AppBuild = appBuild;
            Clock = clock ?? InteractionSystemClock.Instance;
            MaxRecordingBytes = maxRecordingBytes;
        }

        public string SessionId { get; }

        public string AppBuild { get; }

        public IInteractionClock Clock { get; }

        public long MaxRecordingBytes { get; }
    }

    // Append-only JSON Lines session writer (design §15, ADR 0005). Recording is a
    // guarantee, not best effort: any environmental failure (sink I/O, size bound)
    // poisons the recorder, every later append fails with RecorderFailed, and the
    // dispatcher refuses further work on this session. A half-written final line is
    // recovered by the reader's truncated-tail rule.
    public sealed class InteractionRecorder : IDisposable
    {
        private readonly Stream stream;
        private readonly bool leaveOpen;
        private readonly InteractionRecorderOptions options;
        private readonly object sync = new object();
        private readonly Dictionary<long, string> pendingBySequence =
            new Dictionary<long, string>();
        private readonly HashSet<long> completedSequences = new HashSet<long>();
        private long bytesWritten;
        private long lastRequestedSequence;
        private bool faulted;
        private bool disposed;

        public InteractionRecorder(Stream stream, InteractionRecorderOptions options, bool leaveOpen = false)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!stream.CanWrite)
            {
                throw new ArgumentException("The recording stream must be writable.", nameof(stream));
            }

            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.options = options;

            // The header is written eagerly so that an existing recording always
            // starts with a session line, modulo crash truncation of that line.
            lock (sync)
            {
                WriteLine(BuildSessionLine());
            }
        }

        public static InteractionRecorder CreateFile(
            string artifactRoot,
            string relativePath,
            InteractionRecorderOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var fullPath = InteractionRecordingPaths.Resolve(artifactRoot, relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            var fileStream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            try
            {
                return new InteractionRecorder(fileStream, options, leaveOpen: false);
            }
            catch
            {
                fileStream.Dispose();
                throw;
            }
        }

        internal void AppendRequested(
            long sequence,
            string requestId,
            InteractionOrigin origin,
            string commandName,
            int commandVersion,
            string targetId,
            byte[] argumentsJson)
        {
            lock (sync)
            {
                ThrowIfNotWritable();
                if (sequence <= lastRequestedSequence)
                {
                    throw new InteractionInvariantViolationException(
                        "Request events must be appended in strictly increasing sequence order; "
                        + "the dispatcher must append inside its enqueue lock.");
                }

                WriteLine(BuildRequestedLine(
                    sequence,
                    requestId,
                    origin,
                    commandName,
                    commandVersion,
                    targetId,
                    argumentsJson));
                lastRequestedSequence = sequence;
                pendingBySequence.Add(sequence, requestId);
            }
        }

        internal void AppendCompleted(InteractionResult result)
        {
            lock (sync)
            {
                ThrowIfNotWritable();
                if (completedSequences.Contains(result.Sequence))
                {
                    throw new InteractionInvariantViolationException(
                        "A terminal event was already recorded for sequence "
                        + result.Sequence.ToString(CultureInfo.InvariantCulture) + ".");
                }

                if (!pendingBySequence.TryGetValue(result.Sequence, out var requestId))
                {
                    throw new InteractionInvariantViolationException(
                        "A terminal event must follow its own request event; sequence "
                        + result.Sequence.ToString(CultureInfo.InvariantCulture)
                        + " has no recorded request.");
                }

                if (!string.Equals(requestId, result.RequestId, StringComparison.Ordinal))
                {
                    throw new InteractionInvariantViolationException(
                        "The terminal event's request ID does not match the recorded request event.");
                }

                WriteLine(BuildCompletedLine(result));
                pendingBySequence.Remove(result.Sequence);
                completedSequences.Add(result.Sequence);
            }
        }

        internal void ThrowIfFaulted()
        {
            lock (sync)
            {
                ThrowIfNotWritable();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            // Every appended line was already flushed, so closing is all that
            // remains; a redundant flush here could throw on a poisoned sink.
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }

        private void ThrowIfNotWritable()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(InteractionRecorder));
            }

            if (faulted)
            {
                throw new InteractionRecordingException(
                    InteractionRecordingError.RecorderFailed,
                    "The recorder failed on an earlier append; the recording session is "
                    + "poisoned and must be replaced.");
            }
        }

        private void WriteLine(byte[] payload)
        {
            var lineLength = (long)payload.Length + 1;
            if (bytesWritten + lineLength > options.MaxRecordingBytes)
            {
                faulted = true;
                throw new InteractionRecordingException(
                    InteractionRecordingError.SizeLimitExceeded,
                    "Appending this event would exceed the recording size bound of "
                    + options.MaxRecordingBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes.");
            }

            try
            {
                var line = new byte[payload.Length + 1];
                Buffer.BlockCopy(payload, 0, line, 0, payload.Length);
                line[payload.Length] = (byte)'\n';
                stream.Write(line, 0, line.Length);
                if (stream is FileStream fileStream)
                {
                    fileStream.Flush(flushToDisk: true);
                }
                else
                {
                    stream.Flush();
                }
            }
            catch
            {
                faulted = true;
                throw;
            }

            bytesWritten += lineLength;
        }

        private byte[] BuildSessionLine()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    InteractionRecordingSchema.KindProperty,
                    InteractionRecordingSchema.SessionKind);
                writer.WriteNumber(
                    InteractionRecordingSchema.SchemaVersionProperty,
                    InteractionRecordingSchema.SchemaVersion);
                writer.WriteString(InteractionRecordingSchema.SessionIdProperty, options.SessionId);
                writer.WriteString(InteractionRecordingSchema.AppBuildProperty, options.AppBuild);
                writer.WriteString(
                    InteractionRecordingSchema.StartedAtProperty,
                    options.Clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] BuildRequestedLine(
            long sequence,
            string requestId,
            InteractionOrigin origin,
            string commandName,
            int commandVersion,
            string targetId,
            byte[] argumentsJson)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    InteractionRecordingSchema.KindProperty,
                    InteractionRecordingSchema.RequestedKind);
                writer.WriteNumber(InteractionRecordingSchema.SequenceProperty, sequence);
                writer.WriteString(InteractionRecordingSchema.RequestIdProperty, requestId);
                writer.WriteString(
                    InteractionRecordingSchema.OriginProperty,
                    origin.ToString());
                writer.WritePropertyName(InteractionRecordingSchema.CommandProperty);
                writer.WriteStartObject();
                writer.WriteString(InteractionRecordingSchema.NameProperty, commandName);
                writer.WriteNumber(InteractionRecordingSchema.VersionProperty, commandVersion);
                writer.WriteString(InteractionRecordingSchema.TargetIdProperty, targetId);
                writer.WritePropertyName(InteractionRecordingSchema.ArgumentsProperty);
                writer.WriteRawValue(argumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] BuildCompletedLine(InteractionResult result)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    InteractionRecordingSchema.KindProperty,
                    InteractionRecordingSchema.CompletedKind);
                writer.WriteNumber(InteractionRecordingSchema.SequenceProperty, result.Sequence);
                writer.WriteString(InteractionRecordingSchema.RequestIdProperty, result.RequestId);
                writer.WritePropertyName(InteractionRecordingSchema.ResultProperty);
                writer.WriteStartObject();
                writer.WriteString(
                    InteractionRecordingSchema.StatusProperty,
                    result.Status.ToString());
                writer.WritePropertyName(InteractionRecordingSchema.StagesProperty);
                writer.WriteStartArray();
                foreach (var stage in result.Stages.Stages)
                {
                    writer.WriteStartObject();
                    writer.WriteString(InteractionRecordingSchema.StageIdProperty, stage.Id);
                    writer.WriteString(
                        InteractionRecordingSchema.StatusProperty,
                        stage.Status.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                if (result.Status == InteractionStatus.Rejected)
                {
                    writer.WriteString(
                        InteractionRecordingSchema.RejectionCodeProperty,
                        result.Rejection!.Code.ToString());
                }

                if (result.Status == InteractionStatus.Faulted)
                {
                    // Only the stable application code is persisted; the .NET
                    // exception type, message, and stack trace never leave the
                    // process through a recording (design §19).
                    if (result.Fault!.ApplicationCode == null)
                    {
                        writer.WriteNull(InteractionRecordingSchema.FaultCodeProperty);
                    }
                    else
                    {
                        writer.WriteString(
                            InteractionRecordingSchema.FaultCodeProperty,
                            result.Fault.ApplicationCode);
                    }
                }

                writer.WriteEndObject();
                writer.WritePropertyName(InteractionRecordingSchema.StateProperty);
                writer.WriteStartObject();
                WriteObservation(writer, InteractionRecordingSchema.BeforeProperty, result.Before);
                WriteObservation(writer, InteractionRecordingSchema.AfterProperty, result.After);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        private static void WriteObservation(
            Utf8JsonWriter writer,
            string propertyName,
            StateObservation observation)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteStartObject();
            foreach (var probe in observation.Probes)
            {
                writer.WriteString(probe.ProbeId, probe.Hash);
            }

            writer.WriteEndObject();
        }
    }

    // Serializes a command's arguments through its catalog codec and replaces every
    // argument the catalog schema marks sensitive with a secret-key reference. The
    // catalog is the privacy floor for recordings: target-side sensitivity upgrades
    // are not visible at enqueue time (targets resolve after dequeue) and therefore
    // never influence what is persisted (ADR 0005).
    internal static class InteractionRecordingRedaction
    {
        public static byte[] SerializeArguments(
            InteractionCommandCatalogEntry entry,
            IInteractionCommand command)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                entry.WriteArguments(writer, command);
            }

            var raw = buffer.WrittenSpan.ToArray();
            if (!HasSensitiveArgument(entry.Arguments))
            {
                return raw;
            }

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InteractionInvariantViolationException(
                    "Command codecs must serialize arguments as a JSON object.");
            }

            var redacted = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(redacted))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveArgument(entry.Arguments, property.Name))
                    {
                        writer.WriteStartObject();
                        writer.WriteString(
                            InteractionRecordingSecret.PropertyName,
                            InteractionRecordingSecret.KeyFor(
                                entry.WireName,
                                entry.Version,
                                property.Name));
                        writer.WriteEndObject();
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return redacted.WrittenSpan.ToArray();
        }

        private static bool HasSensitiveArgument(InteractionArgumentSchema schema)
        {
            foreach (var definition in schema.Arguments)
            {
                if (definition.Sensitive)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSensitiveArgument(InteractionArgumentSchema schema, string name)
        {
            foreach (var definition in schema.Arguments)
            {
                if (string.Equals(definition.Name, name, StringComparison.Ordinal))
                {
                    return definition.Sensitive;
                }
            }

            return false;
        }
    }

    // Normalizes and confines recording paths to the caller-supplied artifact root
    // (design §15.1/§19). The check is lexical: ".." segments are resolved by
    // GetFullPath before the ordinal prefix comparison, but junctions or reparse
    // points below the root are not followed. No default root exists (design §25).
    internal static class InteractionRecordingPaths
    {
        public static string Resolve(string artifactRoot, string relativePath)
        {
            InteractionContract.RequireIdentifier(artifactRoot, nameof(artifactRoot));
            InteractionContract.RequireIdentifier(relativePath, nameof(relativePath));
            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException(
                    "Recording paths must be relative to the artifact root.",
                    nameof(relativePath));
            }

            var fullRoot = Path.GetFullPath(artifactRoot);
            var trimmedRoot = TrimTrailingSeparators(fullRoot);
            var fullPath = Path.GetFullPath(Path.Combine(trimmedRoot, relativePath));
            var prefix = trimmedRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The resolved recording path escapes the artifact root.",
                    nameof(relativePath));
            }

            return fullPath;
        }

        private static string TrimTrailingSeparators(string path)
        {
            var trimmed = path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            // A drive root such as "C:\" trims to "C:"; re-appending the separator
            // in the prefix comparison restores the canonical form.
            return trimmed.Length == 0 ? path : trimmed;
        }
    }
}
