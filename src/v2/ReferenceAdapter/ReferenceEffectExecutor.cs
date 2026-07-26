using System;
using System.Collections.Generic;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.ReferenceAdapter
{
    /// <summary>
    /// The reference effect executor (adapter-conformance.md §3): synchronous
    /// adopt-or-refuse, effects applied through the message-based registry, and
    /// exactly one completion per adopted permit.
    ///
    /// Two behaviors model the two supported profiles:
    /// - the fast capability applies its mutation and reports an
    ///   <c>Applied</c> completion during the dispatching pump (within one frame);
    /// - the slow capability spans <see cref="SlowEffectFrames"/> frames, applies
    ///   its mutation on the frame tick that matures it, reports a
    ///   <c>FrameCommitted</c> completion, and honors cooperative cancellation
    ///   with exactly one <c>Cancelled</c> completion instead.
    /// </summary>
    public sealed class ReferenceEffectExecutor : IEffectExecutor
    {
        /// <summary>Frames a slow effect stays in flight before completing.</summary>
        public const int SlowEffectFrames = 2;

        private sealed class PendingSlowEffect
        {
            internal PendingSlowEffect(EffectPermitToken permit, int remainingFrames)
            {
                Permit = permit;
                RemainingFrames = remainingFrames;
            }

            internal EffectPermitToken Permit { get; }

            internal int RemainingFrames { get; set; }
        }

        private readonly INodeRegistry registry;
        private readonly NodeRef target;
        private readonly List<PendingSlowEffect> pending = new List<PendingSlowEffect>();
        private IEffectCompletionSink? sink;
        private int applyCounter;

        public ReferenceEffectExecutor(INodeRegistry registry, NodeRef target)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (target.IsDefault)
            {
                throw new ArgumentException("The executor requires a non-default target node.", nameof(target));
            }

            this.target = target;
        }

        private IEffectCompletionSink Sink =>
            sink ?? throw new InvalidOperationException("The executor is not attached.");

        public void Attach(IEffectCompletionSink completionSink) =>
            sink = completionSink ?? throw new ArgumentNullException(nameof(completionSink));

        public void Detach()
        {
            sink = null;
            pending.Clear();
        }

        public EffectAdoption Execute(EffectRequest request)
        {
            if (request.Invocation.Contract.Equals(ReferenceWorld.SetLabel))
            {
                ApplyLabel("saved");
                Sink.ReportCompletion(new EffectCompletion(
                    request.Permit,
                    EffectResolution.Succeeded(new CompletionEvidence(
                        ReferenceWorld.AppliedProfile, CompletionEvidenceKind.Applied, default))));
                return EffectAdoption.Adopted;
            }

            if (request.Invocation.Contract.Equals(ReferenceWorld.SlowSetLabel))
            {
                pending.Add(new PendingSlowEffect(request.Permit, SlowEffectFrames));
                return EffectAdoption.Adopted;
            }

            return EffectAdoption.Refused(new FaultCode("UnsupportedCapability"));
        }

        public void RequestCancel(EffectPermitToken permit)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Permit.Equals(permit))
                {
                    pending.RemoveAt(i);
                    Sink.ReportCompletion(new EffectCompletion(
                        permit,
                        EffectResolution.Cancelled(CancellationPhase.DuringEffect, "Honored")));
                    return;
                }
            }

            // A fast effect has already completed by the time a cancel could reach
            // it; the kernel resolves that race by the completion order, so there is
            // nothing to do here.
        }

        /// <summary>Pump thread, once per frame before the frame's first pump: matures slow effects.</summary>
        internal void OnFrame()
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var effect = pending[i];
                effect.RemainingFrames--;
                if (effect.RemainingFrames > 0)
                {
                    continue;
                }

                pending.RemoveAt(i);
                ApplyLabel("slow-saved");
                Sink.ReportCompletion(new EffectCompletion(
                    effect.Permit,
                    EffectResolution.Succeeded(new CompletionEvidence(
                        ReferenceWorld.FrameCommittedProfile, CompletionEvidenceKind.FrameCommitted, default))));
            }
        }

        private void ApplyLabel(string prefix)
        {
            applyCounter++;
            registry.UpdateAttributes(
                target,
                ValueList<NodeAttribute>.From(new[]
                {
                    new NodeAttribute(
                        "label", FieldValue.Of(prefix + "-" + applyCounter), Sensitivity.Standard),
                }),
                observer: null);
        }
    }
}
