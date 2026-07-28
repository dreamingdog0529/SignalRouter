using System;
using System.Collections.Generic;
using System.IO;

namespace SignalRouter.V2.Codec.Recording
{
    /// <summary>The storage answer for one framed record (ADR 0015/0016).</summary>
    public enum WriteAnswer
    {
        /// <summary>The record is durable (operating-system-level flush completed).</summary>
        Committed,

        /// <summary>Durability is in flight (reserved for future asynchronous sinks and scripted tests).</summary>
        InFlight,

        /// <summary>The record could not be made durable.</summary>
        Fault,

        /// <summary>
        /// The fully framed record would exceed the caller's byte budget; nothing
        /// was written. A resource refusal is never disguised as a sink fault.
        /// </summary>
        OverBudget,
    }

    /// <summary>
    /// One artifact's raw append sink. Records arrive fully framed (kind, length,
    /// payload, CRC32C, commit byte); the storage appends and answers
    /// <see cref="WriteAnswer.Committed"/> only after the bytes are durable
    /// (ADR 0016 defines durability at this seam).
    /// </summary>
    public interface IArtifactStorage : IDisposable
    {
        /// <summary>False for test backends; a durable coordinator refuses them at open (ADR 0015).</summary>
        bool IsDurable { get; }

        long WrittenBytes { get; }

        WriteAnswer Append(ReadOnlySpan<byte> framedRecord);
    }

    /// <summary>
    /// The artifact store: creates append sinks and reads artifacts back, confined
    /// to one root (recording-replay.md §7 — no unconstrained filesystem paths).
    /// </summary>
    public interface IArtifactStore
    {
        IArtifactStorage Create(string artifactId);

        /// <summary>
        /// Reads the raw artifact, refusing anything over <paramref name="maxBytes"/>
        /// before allocating (decode budgets apply before allocation, ADR 0016).
        /// </summary>
        byte[] ReadAll(string artifactId, long maxBytes);
    }

    /// <summary>
    /// The file-backed store: one file per artifact under one configured root;
    /// appends flush to disk before answering <see cref="WriteAnswer.Committed"/>.
    /// Artifact ids are validated against path traversal — the id is a filename,
    /// never a path.
    /// </summary>
    public sealed class FileArtifactStore : IArtifactStore
    {
        private readonly string root;

        public FileArtifactStore(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                throw new ArgumentException("A store root is required.", nameof(root));
            }

            this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        }

        public IArtifactStorage Create(string artifactId) =>
            new FileStorage(ResolveConfined(artifactId));

        public byte[] ReadAll(string artifactId, long maxBytes)
        {
            var path = ResolveConfined(artifactId);
            var length = new FileInfo(path).Length;
            if (length > maxBytes)
            {
                throw new InvalidOperationException(
                    "The artifact exceeds the caller's read budget.");
            }

            return File.ReadAllBytes(path);
        }

        private string ResolveConfined(string artifactId)
        {
            if (string.IsNullOrEmpty(artifactId) ||
                artifactId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                artifactId.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The artifact id must be a plain file name.", nameof(artifactId));
            }

            var path = Path.GetFullPath(Path.Combine(root, artifactId + ".srre"));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The artifact id escapes the configured root.", nameof(artifactId));
            }

            return path;
        }

        private sealed class FileStorage : IArtifactStorage
        {
            private readonly FileStream stream;

            internal FileStorage(string path)
            {
                stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }

            public bool IsDurable => true;

            public long WrittenBytes => stream.Length;

            public WriteAnswer Append(ReadOnlySpan<byte> framedRecord)
            {
                try
                {
                    stream.Write(framedRecord);
                    stream.Flush(flushToDisk: true);
                    return WriteAnswer.Committed;
                }
                catch (IOException)
                {
                    return WriteAnswer.Fault;
                }
            }

            public void Dispose() => stream.Dispose();
        }
    }

    /// <summary>
    /// The test backend: in-memory, explicitly non-durable, with scriptable
    /// answers and torn-write injection for the reader's crash matrix.
    /// </summary>
    public sealed class MemoryArtifactStore : IArtifactStore
    {
        private readonly Dictionary<string, MemoryStorage> artifacts =
            new Dictionary<string, MemoryStorage>(StringComparer.Ordinal);

        public IArtifactStorage Create(string artifactId)
        {
            var storage = new MemoryStorage(this);
            artifacts.Add(artifactId, storage);
            return storage;
        }

        public byte[] ReadAll(string artifactId, long maxBytes)
        {
            var bytes = artifacts[artifactId].Snapshot();
            if (bytes.LongLength > maxBytes)
            {
                throw new InvalidOperationException(
                    "The artifact exceeds the caller's read budget.");
            }

            return bytes;
        }

        /// <summary>The next appends answer these instead of Committed (test scripting).</summary>
        public Queue<WriteAnswer> ScriptedAnswers { get; } = new Queue<WriteAnswer>();

        /// <summary>Truncates a stored artifact to simulate a torn tail.</summary>
        public void Truncate(string artifactId, int length) =>
            artifacts[artifactId].Truncate(length);

        private sealed class MemoryStorage : IArtifactStorage
        {
            private readonly MemoryArtifactStore owner;
            private readonly MemoryStream stream = new MemoryStream();

            internal MemoryStorage(MemoryArtifactStore owner)
            {
                this.owner = owner;
            }

            public bool IsDurable => false;

            public long WrittenBytes => stream.Length;

            public WriteAnswer Append(ReadOnlySpan<byte> framedRecord)
            {
                var answer = owner.ScriptedAnswers.Count > 0
                    ? owner.ScriptedAnswers.Dequeue()
                    : WriteAnswer.Committed;
                if (answer == WriteAnswer.Committed)
                {
                    stream.Write(framedRecord);
                }

                return answer;
            }

            internal byte[] Snapshot() => stream.ToArray();

            internal void Truncate(int length) => stream.SetLength(length);

            public void Dispose()
            {
            }
        }
    }
}
