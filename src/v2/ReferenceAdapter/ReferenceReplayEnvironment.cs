using System;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;
using SignalRouter.V2.Replay;

namespace SignalRouter.V2.ReferenceAdapter
{
    /// <summary>
    /// The reference adapter's twin builder (recording-replay.md §6): a fresh,
    /// fully isolated reference world — its own runtime, node source, ingress,
    /// executor, and frame loop — constructed around the driver's evidence
    /// coordinator. The bootstrap is the same code path as the recording
    /// world's, so twin equivalence holds by construction and the fixture
    /// contract (verification.md §5.3) is the bootstrap itself.
    /// </summary>
    public sealed class ReferenceReplayEnvironmentFactory : IReplayEnvironmentFactory
    {
        public IReplayEnvironment Create(RecordingOpened opened, IEvidenceCoordinator evidence)
        {
            if (opened == null)
            {
                throw new ArgumentNullException(nameof(opened));
            }

            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            return new ReferenceReplayEnvironment(
                ReferenceAdapterHost.Create(decorateExecutor: null, evidence));
        }

        private sealed class ReferenceReplayEnvironment : IReplayEnvironment
        {
            private readonly ReferenceAdapterHost host;

            internal ReferenceReplayEnvironment(ReferenceAdapterHost host)
            {
                this.host = host;
            }

            public KernelRuntime Runtime => host.Runtime;

            public bool Advance()
            {
                // The finest grain a frame-phased world honestly supports is one
                // whole frame — effects apply at the frame hooks, never between
                // pump turns.
                host.PumpHost.DriveFrames(1);
                return true;
            }

            public void Dispose() => host.TearDown();
        }
    }
}
