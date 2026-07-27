using System;
using System.Collections.Generic;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.ReferenceAdapter
{
    /// <summary>
    /// The reference effect executor (adapter-conformance.md §3): synchronous
    /// adopt-or-refuse with zero effect before <c>Adopted</c> is returned — every
    /// mutation is deferred to a frame-loop hook that runs after the dispatching
    /// pump — and exactly one completion per adopted permit.
    ///
    /// The two capabilities model the two supported profiles:
    /// - the fast capability applies its mutation right after the Update pump and
    ///   reports an <c>Applied</c> completion in the same frame (Applied evidence
    ///   asserts the committed value change, not the frame fence);
    /// - the slow capability matures over <see cref="SlowEffectFrames"/> frames,
    ///   applies its mutation after that frame's Update pump, and reports
    ///   <c>FrameCommitted</c> only after the declared LateUpdate fence phase has
    ///   pumped — the evidence claims the effect survived the fence, so it is
    ///   never reported earlier. It honors cooperative cancellation with exactly
    ///   one <c>Cancelled</c> completion instead.
    /// </summary>
    public sealed class ReferenceEffectExecutor : IEffectExecutor
    {
        /// <summary>Frames a slow effect stays in flight before it applies.</summary>
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
        private readonly List<EffectPermitToken> pendingFast = new List<EffectPermitToken>();
        private readonly List<PendingSlowEffect> pendingSlow = new List<PendingSlowEffect>();
        private readonly List<EffectPermitToken> maturedSlow = new List<EffectPermitToken>();
        private readonly List<EffectPermitToken> awaitingFence = new List<EffectPermitToken>();
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
            pendingFast.Clear();
            pendingSlow.Clear();
            maturedSlow.Clear();
            awaitingFence.Clear();
        }

        public EffectAdoption Execute(EffectRequest request)
        {
            if (request.Invocation.Contract.Equals(ReferenceWorld.SetLabel))
            {
                pendingFast.Add(request.Permit);
                return EffectAdoption.Adopted;
            }

            if (request.Invocation.Contract.Equals(ReferenceWorld.SlowSetLabel))
            {
                pendingSlow.Add(new PendingSlowEffect(request.Permit, SlowEffectFrames));
                return EffectAdoption.Adopted;
            }

            return EffectAdoption.Refused(new FaultCode("UnsupportedCapability"));
        }

        public void RequestCancel(EffectPermitToken permit)
        {
            // Cancellation is honored only while the effect has not applied yet;
            // once the mutation is out, the owed completion is the true answer and
            // the kernel resolves the cancel-versus-completion race by order.
            if (RemoveToken(pendingFast, permit) || RemoveSlow(permit))
            {
                Sink.ReportCompletion(new EffectCompletion(
                    permit,
                    EffectResolution.Cancelled(CancellationPhase.DuringEffect, "Honored")));
            }
        }

        /// <summary>Pump thread, once per frame before the frame's first pump: matures slow effects.</summary>
        internal void OnFrame()
        {
            for (var i = pendingSlow.Count - 1; i >= 0; i--)
            {
                var effect = pendingSlow[i];
                effect.RemainingFrames--;
                if (effect.RemainingFrames > 0)
                {
                    continue;
                }

                pendingSlow.RemoveAt(i);
                maturedSlow.Add(effect.Permit);
            }
        }

        /// <summary>Pump thread, right after the given phase's pump returned.</summary>
        internal void AfterPhase(FramePhase phase)
        {
            if (phase == FramePhase.Update)
            {
                // The engine applies dispatched work during the frame, after the
                // dispatching pump returned adoption.
                foreach (var permit in pendingFast)
                {
                    ApplyLabel("saved");
                    Sink.ReportCompletion(new EffectCompletion(
                        permit,
                        EffectResolution.Succeeded(new CompletionEvidence(
                            ReferenceWorld.AppliedProfile, CompletionEvidenceKind.Applied, default))));
                }

                pendingFast.Clear();

                foreach (var permit in maturedSlow)
                {
                    ApplyLabel("slow-saved");
                    awaitingFence.Add(permit);
                }

                maturedSlow.Clear();
                return;
            }

            if (phase == ReferenceWorld.Descriptor.FencePhase)
            {
                // Only now has the mutation survived the declared fence phase —
                // FrameCommitted evidence must never be reported earlier.
                foreach (var permit in awaitingFence)
                {
                    Sink.ReportCompletion(new EffectCompletion(
                        permit,
                        EffectResolution.Succeeded(new CompletionEvidence(
                            ReferenceWorld.FrameCommittedProfile,
                            CompletionEvidenceKind.FrameCommitted,
                            default))));
                }

                awaitingFence.Clear();
            }
        }

        private static bool RemoveToken(List<EffectPermitToken> tokens, EffectPermitToken permit)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Equals(permit))
                {
                    tokens.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private bool RemoveSlow(EffectPermitToken permit)
        {
            for (var i = 0; i < pendingSlow.Count; i++)
            {
                if (pendingSlow[i].Permit.Equals(permit))
                {
                    pendingSlow.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private void ApplyLabel(string prefix)
        {
            applyCounter++;
            registry.UpdateAttributes(
                target,
                ValueArray<NodeAttribute>.From(new[]
                {
                    new NodeAttribute(
                        "label", FieldValue.Of(prefix + "-" + applyCounter), Sensitivity.Standard),
                }),
                observer: null);
        }
    }
}
