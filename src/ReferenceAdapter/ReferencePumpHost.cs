using System;
using SignalRouter.AdapterSdk;

namespace SignalRouter.ReferenceAdapter
{
    /// <summary>
    /// The injected monotonic clock (ADR 0010): a tick counter the pump host
    /// advances — the kernel never reads a system clock.
    /// </summary>
    public sealed class FrameTickClock : IMonotonicClock
    {
        private long ticks;

        public long Now => ticks;

        internal void Advance() => ticks++;
    }

    /// <summary>
    /// The reference pump host (kernel-execution.md §6): a synthetic frame loop.
    /// One frame = mature slow effects, then pump Update, then pump LateUpdate (the
    /// fence phase). The host owns both clocks; the kernel is only ever driven.
    /// </summary>
    public sealed class ReferencePumpHost : IPumpHost
    {
        private const int TurnsPerPhase = 64;

        private readonly ReferenceEffectExecutor executor;
        private readonly FrameTickClock clock;
        private IPumpable? kernel;
        private long logicalNow;

        /// <summary>Whether the last driven frame observed remaining or in-flight work.</summary>
        public bool LastFrameHadWork { get; private set; }

        public ReferencePumpHost(ReferenceEffectExecutor executor, FrameTickClock clock)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>What the next pump would carry: the host logical clock's current reading.</summary>
        public LogicalTime LogicalNow => new LogicalTime(logicalNow);

        public void Attach(IPumpable pumpable) =>
            kernel = pumpable ?? throw new ArgumentNullException(nameof(pumpable));

        public void Detach() => kernel = null;

        /// <summary>Drives whole frames; returns the frames driven.</summary>
        public int DriveFrames(int frames)
        {
            if (frames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frames), "Frames must not be negative.");
            }

            var pumpable = kernel ?? throw new InvalidOperationException("The pump host is not attached.");
            for (var i = 0; i < frames; i++)
            {
                logicalNow++;
                LastFrameHadWork = false;
                executor.OnFrame();
                PumpPhase(pumpable, FramePhase.Update);
                PumpPhase(pumpable, FramePhase.LateUpdate);
            }

            return frames;
        }

        private void PumpPhase(IPumpable pumpable, FramePhase phase)
        {
            clock.Advance();
            var report = pumpable.Pump(new PumpBudget(
                TurnsPerPhase, deadline: long.MaxValue, new LogicalTime(logicalNow), phase));
            LastFrameHadWork |= report.WorkRemaining || report.AwaitingAdapterCompletion;

            // The engine's frame work for this phase runs after the pump returned:
            // adopted effects apply (and, at the fence phase, report FrameCommitted)
            // only once the dispatching pump is out of the way.
            executor.AfterPhase(phase);
        }
    }
}
