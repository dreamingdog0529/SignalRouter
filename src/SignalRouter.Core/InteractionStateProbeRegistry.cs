using System;
using System.Collections.Generic;

namespace SignalRouter
{
    // Holds the set of registered state probes and captures observations from them
    // (design §5.1 State probe registry, §14). The dispatcher takes a before reading around
    // an interaction, then re-reads the same probe instances afterward, so the before and
    // after observations always cover an identical probe set.
    public sealed class InteractionStateProbeRegistry
    {
        private readonly Dictionary<string, IInteractionStateProbe> byId =
            new Dictionary<string, IInteractionStateProbe>(StringComparer.Ordinal);
        private readonly List<IInteractionStateProbe> ordered =
            new List<IInteractionStateProbe>();

        public int Count
        {
            get { return ordered.Count; }
        }

        // Registers a probe. Duplicate IDs (ordinal) fail immediately, mirroring the semantic
        // target registry (design §13.1). Probes are kept in ascending ordinal ID order so an
        // observation's probe order is deterministic regardless of registration order.
        public void Register(IInteractionStateProbe probe)
        {
            if (probe == null)
            {
                throw new ArgumentNullException(nameof(probe));
            }

            InteractionContract.RequireIdentifier(probe.Id, nameof(probe));
            if (probe.Version < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(probe),
                    probe.Version,
                    "A probe version must be positive.");
            }

            if (byId.ContainsKey(probe.Id))
            {
                throw new InvalidOperationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Probe ID '{0}' is already registered.",
                        probe.Id));
            }

            byId.Add(probe.Id, probe);
            var index = ordered.BinarySearch(probe, ProbeIdComparer.Instance);
            ordered.Insert(index < 0 ? ~index : index, probe);
        }

        // Captures a reading over the currently registered probe set. The reading pins those
        // probe instances so a later ReadSame() covers exactly the same probes even if the
        // registry changes in between.
        internal StateProbeReading Read()
        {
            return StateProbeReading.Capture(ordered.ToArray());
        }

        private sealed class ProbeIdComparer : IComparer<IInteractionStateProbe>
        {
            public static readonly ProbeIdComparer Instance = new ProbeIdComparer();

            public int Compare(IInteractionStateProbe? left, IInteractionStateProbe? right)
            {
                return string.CompareOrdinal(left!.Id, right!.Id);
            }
        }
    }

    // An immutable capture over a fixed probe set: each probe's hash plus the probe instances
    // themselves, so the "after" reading recaptures the identical set. This keeps the before
    // and after StateObservation probe lists equal, which InteractionResult requires.
    internal sealed class StateProbeReading
    {
        private readonly IInteractionStateProbe[] probes;
        private readonly CapturedProbe[] captured;

        private StateProbeReading(IInteractionStateProbe[] probes, CapturedProbe[] captured)
        {
            this.probes = probes;
            this.captured = captured;
        }

        public static StateProbeReading Capture(IInteractionStateProbe[] probes)
        {
            var captured = new CapturedProbe[probes.Length];
            for (var index = 0; index < probes.Length; index++)
            {
                var probe = probes[index];
                var snapshot = probe.Capture();
                if (snapshot == null)
                {
                    throw new InteractionInvariantViolationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Probe '{0}' returned a null snapshot.",
                            probe.Id));
                }

                string hash;
                try
                {
                    hash = StateCanonicalizer.ComputeHash(probe.Version, snapshot);
                }
                catch (ArgumentException exception)
                {
                    // A probe that produced an uncanonicalizable snapshot is a runtime
                    // invariant violation, not an application-stage fault: capturing state is
                    // dispatcher infrastructure and must fail fast.
                    throw new InteractionInvariantViolationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Probe '{0}' produced a snapshot that cannot be canonicalized: {1}",
                            probe.Id,
                            exception.Message));
                }

                captured[index] = new CapturedProbe(probe.Id, hash, snapshot);
            }

            return new StateProbeReading(probes, captured);
        }

        // Recaptures the same probe instances so the resulting reading has an identical probe
        // set to this one.
        public StateProbeReading ReadSame()
        {
            return Capture(probes);
        }

        public StateObservation ToObservation()
        {
            if (captured.Length == 0)
            {
                return StateObservation.Empty;
            }

            var observations = new StateProbeObservation[captured.Length];
            for (var index = 0; index < captured.Length; index++)
            {
                observations[index] =
                    new StateProbeObservation(captured[index].Id, captured[index].Hash);
            }

            return new StateObservation(observations);
        }

        // Emits a StateProbeDiff for each probe whose hash changed between the two readings.
        // A probe that implements IStatePropertyDiffProvider explains its hash change as
        // property-level changes over its own snapshot schema (design §14, ADR 0002); any
        // other probe reports the hash change with an empty change set. The per-probe
        // before/after hashes remain the diff's authority regardless.
        public static StateDiff Diff(StateProbeReading before, StateProbeReading after)
        {
            if (before.captured.Length != after.captured.Length)
            {
                throw new InteractionInvariantViolationException(
                    "Before and after readings must cover the same probe set.");
            }

            var diffs = new List<StateProbeDiff>();
            for (var index = 0; index < before.captured.Length; index++)
            {
                var beforeProbe = before.captured[index];
                var afterProbe = after.captured[index];
                if (!string.Equals(beforeProbe.Id, afterProbe.Id, StringComparison.Ordinal))
                {
                    throw new InteractionInvariantViolationException(
                        "Before and after readings must cover the same probe set.");
                }

                if (!string.Equals(beforeProbe.Hash, afterProbe.Hash, StringComparison.Ordinal))
                {
                    // before.probes and after.probes share the same pinned instances
                    // (ReadSame recaptures them), so either reading names the same probe.
                    var changes = ComputePropertyChanges(
                        before.probes[index],
                        beforeProbe.Snapshot,
                        afterProbe.Snapshot);
                    diffs.Add(
                        new StateProbeDiff(
                            beforeProbe.Id,
                            beforeProbe.Hash,
                            afterProbe.Hash,
                            changes));
                }
            }

            return diffs.Count == 0 ? StateDiff.Empty : new StateDiff(diffs);
        }

        private static IReadOnlyList<StatePropertyChange> ComputePropertyChanges(
            IInteractionStateProbe probe,
            StateProbeSnapshot before,
            StateProbeSnapshot after)
        {
            if (!(probe is IStatePropertyDiffProvider provider))
            {
                return Array.Empty<StatePropertyChange>();
            }

            try
            {
                return provider.DiffProperties(before, after)
                    ?? throw new InteractionInvariantViolationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Probe '{0}' returned a null property-change list.",
                            probe.Id));
            }
            catch (Exception exception)
                when (exception is ArgumentException || exception is FormatException)
            {
                // A diff provider that emits an invalid change (e.g. equal before/after, or a
                // value it cannot parse from its own snapshot) is a runtime invariant
                // violation, not an application-stage fault (ADR 0001 rule 5).
                throw new InteractionInvariantViolationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Probe '{0}' produced an invalid property-level diff: {1}",
                        probe.Id,
                        exception.Message));
            }
        }

        private readonly struct CapturedProbe
        {
            public CapturedProbe(string id, string hash, StateProbeSnapshot snapshot)
            {
                Id = id;
                Hash = hash;
                Snapshot = snapshot;
            }

            public string Id { get; }

            public string Hash { get; }

            // The snapshot the hash was computed from, retained so a property-level diff can
            // compare structure without recapturing (design §14, ADR 0002).
            public StateProbeSnapshot Snapshot { get; }
        }
    }
}
