using System;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// Receives a snapshot request's split-phase answer. Called on the pump thread.
    /// Refusal codes: <c>ViewUnavailable</c> (unbound principal, unregistered view,
    /// and family/domain mismatch are observationally identical — existence
    /// concealment, guarantees.md §3.5), <c>CapacityExhausted</c>, <c>TornDown</c>.
    /// </summary>
    public interface ISnapshotObserver
    {
        void OnPinned(OperationId operation, PinnedSnapshot snapshot);

        void OnRefused(OperationId operation, string reasonCode);
    }

    /// <summary>
    /// One pinned, revision-consistent snapshot (observation-state.md §4): the
    /// identity tuple, the retained materialization every page reads, and the pure
    /// lookup over it. The pin holds until <see cref="IKernelControl.ReleaseSnapshot"/>
    /// or incarnation teardown; subsequent kernel mutations never alter it.
    /// </summary>
    public sealed class PinnedSnapshot
    {
        public PinnedSnapshot(ObservationSnapshot snapshot, ObservationMaterialization materialization)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
            Lookup = new MaterializationLookup(materialization);
        }

        public ObservationSnapshot Snapshot { get; }

        public ObservationMaterialization Materialization { get; }

        public IObservationLookup Lookup { get; }
    }
}
