using System;

namespace SignalRouter
{
    // A state probe captures a canonical, already-redacted snapshot of some slice of
    // application or runtime state (design §14). The dispatcher hashes each snapshot to
    // build the before/after StateObservation and StateDiff that accompany a terminal
    // InteractionResult. Probes are the escape hatch for side effects the semantic UI tree
    // cannot represent.
    public interface IInteractionStateProbe
    {
        // Stable, unique probe identifier (ordinal comparison), e.g. "semantic-ui".
        string Id { get; }

        // Snapshot schema version. A breaking change to the snapshot shape requires a new
        // version, mirroring command schema versioning (design §6.1).
        int Version { get; }

        // Captures the current state. The returned snapshot MUST already have sensitive
        // values redacted: redaction happens before canonicalization and hashing so that
        // secret values never enter recordings indirectly (design §14).
        StateProbeSnapshot Capture();
    }

    // An immutable, already-redacted JSON payload produced by a probe. The payload is
    // canonicalized and hashed by StateCanonicalizer; the raw bytes are retained so a later
    // property-level diff (deferred) can compare structure without recapturing.
    public sealed class StateProbeSnapshot
    {
        private readonly byte[] utf8Json;

        private StateProbeSnapshot(byte[] utf8Json)
        {
            this.utf8Json = utf8Json;
        }

        // The probe's already-redacted payload as UTF-8 JSON bytes. The returned memory is
        // a view over a private copy; callers must not assume ownership.
        internal ReadOnlyMemory<byte> Utf8Json
        {
            get { return utf8Json; }
        }

        // Builds a snapshot from a UTF-8 JSON string. JSON well-formedness and the canonical
        // value subset are enforced later, during canonicalization (fail-fast at hash time).
        public static StateProbeSnapshot FromJson(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            return new StateProbeSnapshot(System.Text.Encoding.UTF8.GetBytes(json));
        }

        // Builds a snapshot from UTF-8 JSON bytes, defensively copying so a later mutation of
        // the caller's buffer cannot change the hashed content.
        public static StateProbeSnapshot FromUtf8Bytes(ReadOnlyMemory<byte> utf8Json)
        {
            return new StateProbeSnapshot(utf8Json.ToArray());
        }
    }
}
