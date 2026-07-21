#nullable enable

using System;

namespace SignalRouter.Unity;

// Suppression scope (design §17.1): while a scope is open on a runtime, the
// adapters treat uGUI notifications as agent/replay echoes and emit no human
// commands. Main-thread only. The disposable is a struct, but the depth
// bookkeeping lives in a shared lease object so copies and repeated Dispose
// calls release exactly once.
public readonly struct InteractionScope : IDisposable
{
    private readonly SuppressionLease? lease;

    private InteractionScope(SuppressionLease lease)
    {
        this.lease = lease;
    }

    public static InteractionScope Suppress(InteractionRuntime runtime)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        runtime.BeginSuppression();
        return new InteractionScope(new SuppressionLease(runtime));
    }

    public void Dispose()
    {
        lease?.Release();
    }

    private sealed class SuppressionLease
    {
        private InteractionRuntime? runtime;

        public SuppressionLease(InteractionRuntime runtime)
        {
            this.runtime = runtime;
        }

        public void Release()
        {
            var owner = runtime;
            runtime = null;
            owner?.EndSuppression();
        }
    }
}
