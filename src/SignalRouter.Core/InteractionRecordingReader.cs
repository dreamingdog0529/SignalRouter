using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SignalRouter
{
    public sealed record InteractionRecordingSession
    {
        public InteractionRecordingSession(
            int schemaVersion,
            string sessionId,
            string appBuild,
            DateTimeOffset startedAt)
        {
            if (schemaVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(schemaVersion),
                    schemaVersion,
                    "Schema versions must be positive.");
            }

            InteractionContract.RequireIdentifier(sessionId, nameof(sessionId));
            InteractionContract.RequireIdentifier(appBuild, nameof(appBuild));
            SchemaVersion = schemaVersion;
            SessionId = sessionId;
            AppBuild = appBuild;
            StartedAt = startedAt;
        }

        public int SchemaVersion { get; }

        public string SessionId { get; }

        public string AppBuild { get; }

        public DateTimeOffset StartedAt { get; }
    }

    public sealed record RecordedOutcome
    {
        public RecordedOutcome(
            InteractionStatus status,
            IEnumerable<InteractionStageProgress> stages,
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            StateObservation before,
            StateObservation after)
        {
            InteractionContract.RequireDefinedEnum(status, nameof(status));
            if (rejectionCode != null)
            {
                InteractionContract.RequireDefinedEnum(rejectionCode.Value, nameof(rejectionCode));
            }

            InteractionContract.RequireOptionalIdentifier(faultCode, nameof(faultCode));
            if (before == null)
            {
                throw new ArgumentNullException(nameof(before));
            }

            if (after == null)
            {
                throw new ArgumentNullException(nameof(after));
            }

            var progress = new StageProgress(stages);
            ValidateShape(status, progress, rejectionCode, faultCode, before, after);
            Status = status;
            Stages = progress.Stages;
            RejectionCode = rejectionCode;
            FaultCode = faultCode;
            Before = before;
            After = after;
        }

        public InteractionStatus Status { get; }

        public EquatableList<InteractionStageProgress> Stages { get; }

        public InteractionRejectionCode? RejectionCode { get; }

        public string? FaultCode { get; }

        public StateObservation Before { get; }

        public StateObservation After { get; }

        // Mirrors InteractionResult's cross-field rules (design §12) for the subset
        // a recording persists, so a structurally impossible outcome is rejected at
        // construction instead of surfacing during replay.
        private static void ValidateShape(
            InteractionStatus status,
            StageProgress progress,
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            StateObservation before,
            StateObservation after)
        {
            var stages = progress.Stages;
            var last = stages.Count == 0 ? null : stages[stages.Count - 1];
            switch (status)
            {
                case InteractionStatus.Succeeded:
                    RequireNoCodes(rejectionCode, faultCode, status);
                    if (last != null && last.Status != InteractionStageStatus.Completed)
                    {
                        throw new ArgumentException(
                            "Succeeded outcomes must only contain completed stages.",
                            nameof(stages));
                    }

                    break;
                case InteractionStatus.Rejected:
                    if (rejectionCode == null)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes require a rejection code.",
                            nameof(rejectionCode));
                    }

                    if (faultCode != null)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes must not carry a fault code.",
                            nameof(faultCode));
                    }

                    if (stages.Count != 0)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes must not contain stages.",
                            nameof(stages));
                    }

                    RequireUnchangedState(before, after, status);
                    break;
                case InteractionStatus.Faulted:
                    if (rejectionCode != null)
                    {
                        throw new ArgumentException(
                            "Faulted outcomes must not carry a rejection code.",
                            nameof(rejectionCode));
                    }

                    if (last == null || last.Status != InteractionStageStatus.Faulted)
                    {
                        throw new ArgumentException(
                            "Faulted outcomes must end with a faulted stage.",
                            nameof(stages));
                    }

                    break;
                default:
                    RequireNoCodes(rejectionCode, faultCode, status);
                    if (last == null)
                    {
                        RequireUnchangedState(before, after, status);
                    }
                    else if (last.Status != InteractionStageStatus.Cancelled)
                    {
                        throw new ArgumentException(
                            "Cancelled outcomes with stages must end with a cancelled stage.",
                            nameof(stages));
                    }

                    break;
            }
        }

        private static void RequireNoCodes(
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            InteractionStatus status)
        {
            if (rejectionCode != null || faultCode != null)
            {
                throw new ArgumentException(
                    status + " outcomes must not carry rejection or fault codes.",
                    nameof(status));
            }
        }

        private static void RequireUnchangedState(
            StateObservation before,
            StateObservation after,
            InteractionStatus status)
        {
            if (!before.Equals(after))
            {
                throw new ArgumentException(
                    status + " outcomes without stages must observe identical before "
                    + "and after state.",
                    nameof(after));
            }
        }
    }

    public sealed record RecordedInteraction
    {
        public RecordedInteraction(
            long sequence,
            string requestId,
            InteractionOrigin origin,
            string commandName,
            int commandVersion,
            string targetId,
            string argumentsJson,
            RecordedOutcome? outcome)
        {
            if (sequence <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence numbers must be positive.");
            }

            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            InteractionContract.RequireDefinedEnum(origin, nameof(origin));
            InteractionContract.RequireIdentifier(commandName, nameof(commandName));
            if (commandVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command versions must be positive.");
            }

            InteractionContract.RequireTargetId(targetId, nameof(targetId));
            InteractionContract.RequireMessage(argumentsJson, nameof(argumentsJson));
            Sequence = sequence;
            RequestId = requestId;
            Origin = origin;
            CommandName = commandName;
            CommandVersion = commandVersion;
            TargetId = targetId;
            ArgumentsJson = argumentsJson;
            Outcome = outcome;
        }

        public long Sequence { get; }

        public string RequestId { get; }

        public InteractionOrigin Origin { get; }

        public string CommandName { get; }

        public int CommandVersion { get; }

        public string TargetId { get; }

        // The redacted argument object exactly as recorded; secret markers are
        // resolved by the replayer, never by the reader.
        public string ArgumentsJson { get; }

        // Null means OutcomeUnknown (design §15.1): the request event has no
        // terminal event, and readers never invent a Faulted outcome for it.
        public RecordedOutcome? Outcome { get; }

        public bool HasKnownOutcome
        {
            get { return Outcome != null; }
        }
    }

    public sealed class InteractionRecording
    {
        internal InteractionRecording(
            InteractionRecordingSession session,
            IReadOnlyList<RecordedInteraction> interactions,
            IReadOnlyList<string> requiredSecretKeys,
            bool truncatedTailDiscarded,
            long discardedTailBytes)
        {
            Session = session;
            Interactions = EquatableList<RecordedInteraction>.Create(
                interactions,
                nameof(interactions),
                "Interactions must not contain null.");
            RequiredSecretKeys = EquatableList<string>.Create(
                requiredSecretKeys,
                nameof(requiredSecretKeys),
                "Secret keys must not contain null.");
            TruncatedTailDiscarded = truncatedTailDiscarded;
            DiscardedTailBytes = discardedTailBytes;
        }

        public InteractionRecordingSession Session { get; }

        public EquatableList<RecordedInteraction> Interactions { get; }

        // Distinct, ordinally sorted secret keys referenced by the recording; the
        // strict replayer resolves each in memory before dispatching (§16.1).
        public EquatableList<string> RequiredSecretKeys { get; }

        public bool TruncatedTailDiscarded { get; }

        public long DiscardedTailBytes { get; }
    }

    // Strict JSON Lines reader for recording schema v1 (ADR 0005). Unknown kinds,
    // unknown or duplicate fields, and unsupported schema versions are rejected
    // rather than guessed over (§15.1); the only tolerated irregularity is a final
    // byte run without a terminating newline, which is discarded as an unproven
    // write regardless of whether it parses.
    public static class InteractionRecordingReader
    {
        public static InteractionRecording Load(
            Stream stream,
            long maxRecordingBytes = InteractionRecorderOptions.DefaultMaxRecordingBytes)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanRead)
            {
                throw new ArgumentException("The recording stream must be readable.", nameof(stream));
            }

            if (maxRecordingBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRecordingBytes),
                    maxRecordingBytes,
                    "The recording size bound must be positive.");
            }

            var content = ReadBounded(stream, maxRecordingBytes);
            return Parse(content);
        }

        public static InteractionRecording LoadFile(
            string artifactRoot,
            string relativePath,
            long maxRecordingBytes = InteractionRecorderOptions.DefaultMaxRecordingBytes)
        {
            var fullPath = InteractionRecordingPaths.Resolve(artifactRoot, relativePath);

            // FileShare.ReadWrite so a live recording (writer holds the file with
            // FileShare.Read) stays readable; a live read may legitimately end in a
            // truncated tail, which the recovery rule handles.
            using var fileStream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return Load(fileStream, maxRecordingBytes);
        }

        private static byte[] ReadBounded(Stream stream, long maxRecordingBytes)
        {
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxRecordingBytes)
                {
                    throw new InteractionRecordingException(
                        InteractionRecordingError.SizeLimitExceeded,
                        "The recording exceeds the size bound of "
                        + maxRecordingBytes.ToString(CultureInfo.InvariantCulture)
                        + " bytes.");
                }

                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }

        private static InteractionRecording Parse(byte[] content)
        {
            var state = new ParseState();
            var lineStart = 0;
            var lineNumber = 0;
            for (var index = 0; index < content.Length; index++)
            {
                if (content[index] != (byte)'\n')
                {
                    continue;
                }

                lineNumber++;
                ParseLine(state, content, lineStart, index - lineStart, lineNumber);
                lineStart = index + 1;
            }

            if (state.Session == null)
            {
                // Covers the empty stream and the file whose only content is an
                // unterminated (crash-truncated) header line.
                throw new InteractionRecordingException(
                    InteractionRecordingError.MissingHeader,
                    "The recording does not contain a complete session header line.");
            }

            var discardedTailBytes = (long)(content.Length - lineStart);
            var interactions = new List<RecordedInteraction>(state.Requests.Count);
            foreach (var request in state.Requests)
            {
                state.Outcomes.TryGetValue(request.Sequence, out var outcome);
                interactions.Add(new RecordedInteraction(
                    request.Sequence,
                    request.RequestId,
                    request.Origin,
                    request.CommandName,
                    request.CommandVersion,
                    request.TargetId,
                    request.ArgumentsJson,
                    outcome));
            }

            var secretKeys = new List<string>(state.SecretKeys);
            secretKeys.Sort(StringComparer.Ordinal);
            return new InteractionRecording(
                state.Session,
                interactions,
                secretKeys,
                discardedTailBytes > 0,
                discardedTailBytes);
        }

        private static void ParseLine(
            ParseState state,
            byte[] content,
            int offset,
            int length,
            int lineNumber)
        {
            if (lineNumber == 1 && StartsWithUtf8Bom(content, offset, length))
            {
                throw Corrupt(lineNumber, "the recording must not start with a byte order mark");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    new ReadOnlyMemory<byte>(content, offset, length));
            }
            catch (JsonException exception)
            {
                if (lineNumber == 1)
                {
                    throw new InteractionRecordingException(
                        InteractionRecordingError.MissingHeader,
                        "The first recording line is not a valid JSON object.",
                        exception);
                }

                throw Corrupt(lineNumber, "the line is not valid JSON", exception);
            }

            using (document)
            {
                var properties = ReadObject(document.RootElement, lineNumber);
                var kind = RequireString(properties, InteractionRecordingSchema.KindProperty, lineNumber);
                if (lineNumber == 1)
                {
                    if (!string.Equals(kind, InteractionRecordingSchema.SessionKind, StringComparison.Ordinal))
                    {
                        throw new InteractionRecordingException(
                            InteractionRecordingError.MissingHeader,
                            "The first recording line must be the session header.");
                    }

                    state.Session = ParseSession(properties, lineNumber);
                    return;
                }

                switch (kind)
                {
                    case InteractionRecordingSchema.RequestedKind:
                        ParseRequested(state, properties, lineNumber);
                        break;
                    case InteractionRecordingSchema.CompletedKind:
                        ParseCompleted(state, properties, lineNumber);
                        break;
                    case InteractionRecordingSchema.SessionKind:
                        throw Corrupt(lineNumber, "a session header may only appear on the first line");
                    default:
                        throw Corrupt(lineNumber, "the event kind '" + kind + "' is not part of schema v1");
                }
            }
        }

        private static InteractionRecordingSession ParseSession(
            Dictionary<string, JsonElement> properties,
            int lineNumber)
        {
            // The version gate runs before full field validation so a future header
            // with additional fields is reported as an unsupported version, not as
            // an unknown field.
            var schemaVersion = RequireInt(
                properties,
                InteractionRecordingSchema.SchemaVersionProperty,
                lineNumber);
            if (schemaVersion != InteractionRecordingSchema.SchemaVersion)
            {
                throw new InteractionRecordingException(
                    InteractionRecordingError.UnsupportedSchemaVersion,
                    "Recording schema version "
                    + schemaVersion.ToString(CultureInfo.InvariantCulture)
                    + " is not supported; this reader understands version "
                    + InteractionRecordingSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                    + " only.");
            }

            var sessionId = RequireString(properties, InteractionRecordingSchema.SessionIdProperty, lineNumber);
            var appBuild = RequireString(properties, InteractionRecordingSchema.AppBuildProperty, lineNumber);
            var startedAtText = RequireString(properties, InteractionRecordingSchema.StartedAtProperty, lineNumber);
            RequireNoUnknownFields(
                properties,
                lineNumber,
                InteractionRecordingSchema.KindProperty,
                InteractionRecordingSchema.SchemaVersionProperty,
                InteractionRecordingSchema.SessionIdProperty,
                InteractionRecordingSchema.AppBuildProperty,
                InteractionRecordingSchema.StartedAtProperty);
            if (!DateTimeOffset.TryParseExact(
                startedAtText,
                "O",
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var startedAt))
            {
                throw Corrupt(lineNumber, "startedAt must be a round-trip ISO 8601 timestamp");
            }

            try
            {
                return new InteractionRecordingSession(schemaVersion, sessionId, appBuild, startedAt);
            }
            catch (ArgumentException exception)
            {
                throw Corrupt(lineNumber, "the session header carries an invalid value", exception);
            }
        }

        private static void ParseRequested(
            ParseState state,
            Dictionary<string, JsonElement> properties,
            int lineNumber)
        {
            var sequence = RequireSequence(properties, lineNumber);
            var requestId = RequireString(properties, InteractionRecordingSchema.RequestIdProperty, lineNumber);
            var originText = RequireString(properties, InteractionRecordingSchema.OriginProperty, lineNumber);
            var command = RequireObject(properties, InteractionRecordingSchema.CommandProperty, lineNumber);
            RequireNoUnknownFields(
                properties,
                lineNumber,
                InteractionRecordingSchema.KindProperty,
                InteractionRecordingSchema.SequenceProperty,
                InteractionRecordingSchema.RequestIdProperty,
                InteractionRecordingSchema.OriginProperty,
                InteractionRecordingSchema.CommandProperty);
            if (sequence <= state.LastRequestSequence)
            {
                throw new InteractionRecordingException(
                    InteractionRecordingError.NonMonotonicSequence,
                    "Request events must carry strictly increasing sequence numbers; line "
                    + lineNumber.ToString(CultureInfo.InvariantCulture)
                    + " repeats or reorders sequence "
                    + sequence.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (!state.RequestIds.Add(requestId))
            {
                throw Corrupt(lineNumber, "the request ID was already used by an earlier request event");
            }

            var origin = ParseEnum<InteractionOrigin>(originText, lineNumber, "origin");
            var commandProperties = ReadObject(command, lineNumber);
            var commandName = RequireString(commandProperties, InteractionRecordingSchema.NameProperty, lineNumber);
            var commandVersion = RequireInt(commandProperties, InteractionRecordingSchema.VersionProperty, lineNumber);
            var targetId = RequireString(commandProperties, InteractionRecordingSchema.TargetIdProperty, lineNumber);
            var arguments = RequireObject(commandProperties, InteractionRecordingSchema.ArgumentsProperty, lineNumber);
            RequireNoUnknownFields(
                commandProperties,
                lineNumber,
                InteractionRecordingSchema.NameProperty,
                InteractionRecordingSchema.VersionProperty,
                InteractionRecordingSchema.TargetIdProperty,
                InteractionRecordingSchema.ArgumentsProperty);
            CollectSecretKeys(state, arguments, lineNumber);

            RecordedRequest request;
            try
            {
                request = new RecordedRequest(
                    sequence,
                    requestId,
                    origin,
                    commandName,
                    commandVersion,
                    targetId,
                    arguments.GetRawText());
            }
            catch (ArgumentException exception)
            {
                throw Corrupt(lineNumber, "the request event carries an invalid value", exception);
            }

            state.LastRequestSequence = sequence;
            state.Requests.Add(request);
            state.RequestsBySequence.Add(sequence, request);
        }

        private static void ParseCompleted(
            ParseState state,
            Dictionary<string, JsonElement> properties,
            int lineNumber)
        {
            var sequence = RequireSequence(properties, lineNumber);
            var requestId = RequireString(properties, InteractionRecordingSchema.RequestIdProperty, lineNumber);
            var result = RequireObject(properties, InteractionRecordingSchema.ResultProperty, lineNumber);
            var stateElement = RequireObject(properties, InteractionRecordingSchema.StateProperty, lineNumber);
            RequireNoUnknownFields(
                properties,
                lineNumber,
                InteractionRecordingSchema.KindProperty,
                InteractionRecordingSchema.SequenceProperty,
                InteractionRecordingSchema.RequestIdProperty,
                InteractionRecordingSchema.ResultProperty,
                InteractionRecordingSchema.StateProperty);
            if (state.Outcomes.ContainsKey(sequence))
            {
                throw new InteractionRecordingException(
                    InteractionRecordingError.DuplicateTerminalEvent,
                    "Sequence " + sequence.ToString(CultureInfo.InvariantCulture)
                    + " already has a terminal event.");
            }

            if (!state.RequestsBySequence.TryGetValue(sequence, out var request))
            {
                throw new InteractionRecordingException(
                    InteractionRecordingError.UnmatchedTerminalEvent,
                    "Sequence " + sequence.ToString(CultureInfo.InvariantCulture)
                    + " has a terminal event but no earlier request event.");
            }

            if (!string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
            {
                throw Corrupt(lineNumber, "the terminal event's request ID does not match its request event");
            }

            var resultProperties = ReadObject(result, lineNumber);
            var statusText = RequireString(resultProperties, InteractionRecordingSchema.StatusProperty, lineNumber);
            var status = ParseEnum<InteractionStatus>(statusText, lineNumber, "status");
            var stagesElement = RequireArray(resultProperties, InteractionRecordingSchema.StagesProperty, lineNumber);
            InteractionRejectionCode? rejectionCode = null;
            if (resultProperties.TryGetValue(InteractionRecordingSchema.RejectionCodeProperty, out var rejectionElement))
            {
                if (rejectionElement.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt(lineNumber, "rejectionCode must be a string");
                }

                rejectionCode = ParseEnum<InteractionRejectionCode>(
                    rejectionElement.GetString()!,
                    lineNumber,
                    "rejectionCode");
            }

            var hasFaultCode = resultProperties.TryGetValue(
                InteractionRecordingSchema.FaultCodeProperty,
                out var faultElement);
            string? faultCode = null;
            if (hasFaultCode)
            {
                if (faultElement.ValueKind == JsonValueKind.String)
                {
                    faultCode = faultElement.GetString();
                }
                else if (faultElement.ValueKind != JsonValueKind.Null)
                {
                    throw Corrupt(lineNumber, "faultCode must be a string or null");
                }
            }

            if ((status == InteractionStatus.Faulted) != hasFaultCode)
            {
                throw Corrupt(
                    lineNumber,
                    "faultCode must be present exactly when the status is Faulted");
            }

            if ((status == InteractionStatus.Rejected) != (rejectionCode != null))
            {
                throw Corrupt(
                    lineNumber,
                    "rejectionCode must be present exactly when the status is Rejected");
            }

            RequireNoUnknownFields(
                resultProperties,
                lineNumber,
                InteractionRecordingSchema.StatusProperty,
                InteractionRecordingSchema.StagesProperty,
                InteractionRecordingSchema.RejectionCodeProperty,
                InteractionRecordingSchema.FaultCodeProperty);

            var stages = ParseStages(stagesElement, lineNumber);
            var stateProperties = ReadObject(stateElement, lineNumber);
            var before = RequireObject(stateProperties, InteractionRecordingSchema.BeforeProperty, lineNumber);
            var after = RequireObject(stateProperties, InteractionRecordingSchema.AfterProperty, lineNumber);
            RequireNoUnknownFields(
                stateProperties,
                lineNumber,
                InteractionRecordingSchema.BeforeProperty,
                InteractionRecordingSchema.AfterProperty);
            var beforeObservation = ParseObservation(before, lineNumber);
            var afterObservation = ParseObservation(after, lineNumber);

            RecordedOutcome outcome;
            try
            {
                outcome = new RecordedOutcome(
                    status,
                    stages,
                    rejectionCode,
                    faultCode,
                    beforeObservation,
                    afterObservation);
            }
            catch (ArgumentException exception)
            {
                throw Corrupt(lineNumber, "the terminal event carries an inconsistent outcome", exception);
            }

            state.Outcomes.Add(sequence, outcome);
        }

        private static List<InteractionStageProgress> ParseStages(
            JsonElement stagesElement,
            int lineNumber)
        {
            var stages = new List<InteractionStageProgress>();
            foreach (var stageElement in stagesElement.EnumerateArray())
            {
                if (stageElement.ValueKind != JsonValueKind.Object)
                {
                    throw Corrupt(lineNumber, "each stage must be a JSON object");
                }

                var stageProperties = ReadObject(stageElement, lineNumber);
                var id = RequireString(stageProperties, InteractionRecordingSchema.StageIdProperty, lineNumber);
                var statusText = RequireString(stageProperties, InteractionRecordingSchema.StatusProperty, lineNumber);
                RequireNoUnknownFields(
                    stageProperties,
                    lineNumber,
                    InteractionRecordingSchema.StageIdProperty,
                    InteractionRecordingSchema.StatusProperty);
                var status = ParseEnum<InteractionStageStatus>(statusText, lineNumber, "stage status");
                try
                {
                    stages.Add(new InteractionStageProgress(id, stages.Count, status));
                }
                catch (ArgumentException exception)
                {
                    throw Corrupt(lineNumber, "a stage entry carries an invalid value", exception);
                }
            }

            return stages;
        }

        private static StateObservation ParseObservation(JsonElement element, int lineNumber)
        {
            var probes = new List<StateProbeObservation>();
            string? previousId = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Corrupt(lineNumber, "state maps must not repeat a probe ID");
                }

                if (previousId != null
                    && string.CompareOrdinal(previousId, property.Name) >= 0)
                {
                    throw Corrupt(lineNumber, "state map probe IDs must be in ascending ordinal order");
                }

                previousId = property.Name;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt(lineNumber, "state hashes must be strings");
                }

                var hash = property.Value.GetString()!;
                if (!IsCanonicalHash(hash))
                {
                    throw Corrupt(lineNumber, "state hashes must be 64 lowercase hexadecimal characters");
                }

                try
                {
                    probes.Add(new StateProbeObservation(property.Name, hash));
                }
                catch (ArgumentException exception)
                {
                    throw Corrupt(lineNumber, "a state map entry carries an invalid value", exception);
                }
            }

            try
            {
                return new StateObservation(probes);
            }
            catch (ArgumentException exception)
            {
                throw Corrupt(lineNumber, "the state map is invalid", exception);
            }
        }

        private static void CollectSecretKeys(
            ParseState state,
            JsonElement arguments,
            int lineNumber)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in arguments.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Corrupt(lineNumber, "argument objects must not repeat a property name");
                }

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        break;
                    case JsonValueKind.Object:
                        state.SecretKeys.Add(ReadSecretKey(property.Value, lineNumber));
                        break;
                    default:
                        throw Corrupt(
                            lineNumber,
                            "argument values must be scalars or secret references in schema v1");
                }
            }
        }

        private static string ReadSecretKey(JsonElement element, int lineNumber)
        {
            string? key = null;
            var count = 0;
            foreach (var property in element.EnumerateObject())
            {
                count++;
                if (!string.Equals(
                    property.Name,
                    InteractionRecordingSecret.PropertyName,
                    StringComparison.Ordinal)
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt(
                        lineNumber,
                        "an object argument value must be a {\""
                        + InteractionRecordingSecret.PropertyName
                        + "\":\"<key>\"} secret reference");
                }

                key = property.Value.GetString();
            }

            if (count != 1 || string.IsNullOrEmpty(key))
            {
                throw Corrupt(
                    lineNumber,
                    "a secret reference must contain exactly the "
                    + InteractionRecordingSecret.PropertyName + " key");
            }

            return key!;
        }

        private static Dictionary<string, JsonElement> ReadObject(JsonElement element, int lineNumber)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt(lineNumber, "the value must be a JSON object");
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (properties.ContainsKey(property.Name))
                {
                    throw Corrupt(lineNumber, "objects must not repeat the property '" + property.Name + "'");
                }

                properties.Add(property.Name, property.Value);
            }

            return properties;
        }

        private static string RequireString(
            Dictionary<string, JsonElement> properties,
            string name,
            int lineNumber)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                throw Corrupt(lineNumber, "the required string field '" + name + "' is missing or invalid");
            }

            return element.GetString()!;
        }

        private static int RequireInt(
            Dictionary<string, JsonElement> properties,
            string name,
            int lineNumber)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt32(out var value))
            {
                throw Corrupt(lineNumber, "the required integer field '" + name + "' is missing or invalid");
            }

            return value;
        }

        private static long RequireSequence(
            Dictionary<string, JsonElement> properties,
            int lineNumber)
        {
            if (!properties.TryGetValue(InteractionRecordingSchema.SequenceProperty, out var element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt64(out var value)
                || value <= 0)
            {
                throw Corrupt(lineNumber, "the sequence must be a positive integer");
            }

            return value;
        }

        private static JsonElement RequireObject(
            Dictionary<string, JsonElement> properties,
            string name,
            int lineNumber)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt(lineNumber, "the required object field '" + name + "' is missing or invalid");
            }

            return element;
        }

        private static JsonElement RequireArray(
            Dictionary<string, JsonElement> properties,
            string name,
            int lineNumber)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Array)
            {
                throw Corrupt(lineNumber, "the required array field '" + name + "' is missing or invalid");
            }

            return element;
        }

        private static void RequireNoUnknownFields(
            Dictionary<string, JsonElement> properties,
            int lineNumber,
            params string[] knownNames)
        {
            foreach (var name in properties.Keys)
            {
                var known = false;
                foreach (var knownName in knownNames)
                {
                    if (string.Equals(name, knownName, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    throw Corrupt(lineNumber, "the field '" + name + "' is not part of schema v1");
                }
            }
        }

        private static TEnum ParseEnum<TEnum>(string value, int lineNumber, string description)
            where TEnum : struct
        {
            // Strict name-only parsing: numeric strings and unknown names are
            // corruption, never coerced.
            if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
                && Enum.IsDefined(typeof(TEnum), parsed)
                && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
            {
                return parsed;
            }

            throw Corrupt(lineNumber, "the " + description + " '" + value + "' is not a known value");
        }

        private static bool IsCanonicalHash(string value)
        {
            if (value.Length != 64)
            {
                return false;
            }

            foreach (var character in value)
            {
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StartsWithUtf8Bom(byte[] content, int offset, int length)
        {
            return length >= 3
                && content[offset] == 0xEF
                && content[offset + 1] == 0xBB
                && content[offset + 2] == 0xBF;
        }

        private static InteractionRecordingException Corrupt(
            int lineNumber,
            string reason,
            Exception? innerException = null)
        {
            return new InteractionRecordingException(
                InteractionRecordingError.CorruptEntry,
                "Recording line " + lineNumber.ToString(CultureInfo.InvariantCulture)
                + " is invalid: " + reason + ".",
                innerException);
        }

        private sealed class ParseState
        {
            public InteractionRecordingSession? Session { get; set; }

            public long LastRequestSequence { get; set; }

            public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

            public Dictionary<long, RecordedRequest> RequestsBySequence { get; } =
                new Dictionary<long, RecordedRequest>();

            public Dictionary<long, RecordedOutcome> Outcomes { get; } =
                new Dictionary<long, RecordedOutcome>();

            public HashSet<string> RequestIds { get; } =
                new HashSet<string>(StringComparer.Ordinal);

            public HashSet<string> SecretKeys { get; } =
                new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class RecordedRequest
        {
            public RecordedRequest(
                long sequence,
                string requestId,
                InteractionOrigin origin,
                string commandName,
                int commandVersion,
                string targetId,
                string argumentsJson)
            {
                InteractionContract.RequireIdentifier(requestId, nameof(requestId));
                InteractionContract.RequireIdentifier(commandName, nameof(commandName));
                InteractionContract.RequireTargetId(targetId, nameof(targetId));
                if (commandVersion <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(commandVersion),
                        commandVersion,
                        "Command versions must be positive.");
                }

                Sequence = sequence;
                RequestId = requestId;
                Origin = origin;
                CommandName = commandName;
                CommandVersion = commandVersion;
                TargetId = targetId;
                ArgumentsJson = argumentsJson;
            }

            public long Sequence { get; }

            public string RequestId { get; }

            public InteractionOrigin Origin { get; }

            public string CommandName { get; }

            public int CommandVersion { get; }

            public string TargetId { get; }

            public string ArgumentsJson { get; }
        }
    }
}
