using System;
using System.Collections.Generic;

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

        // The probe's already-redacted payload as UTF-8 JSON bytes. Public so an
        // out-of-assembly IStatePropertyDiffProvider can parse the before/after snapshots it
        // is handed. The returned memory is a view over a private copy; callers must not
        // mutate the underlying buffer or assume ownership.
        public ReadOnlyMemory<byte> Utf8Json
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

    // Optional capability: a probe that can explain a hash change between two of its own
    // snapshots as concrete property-level changes (design §14, ADR 0002). A probe owns its
    // snapshot schema, so it — not the registry infrastructure — is what knows how to compare
    // two snapshots structurally. Probes that do not implement this report hash-level changes
    // only; a changed probe then carries an empty change set, exactly as before.
    //
    // The two snapshots are the ones already captured for the before/after readings, so they
    // are guaranteed to be canonicalizable (their hashes were computed successfully). A
    // provider that throws while diffing is a runtime invariant violation, not an application
    // fault (ADR 0001 rule 5): capturing and diffing state is dispatcher infrastructure.
    public interface IStatePropertyDiffProvider
    {
        IReadOnlyList<StatePropertyChange> DiffProperties(
            StateProbeSnapshot before,
            StateProbeSnapshot after);
    }
}
