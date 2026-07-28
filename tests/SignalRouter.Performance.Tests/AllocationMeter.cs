using System;
using System.Threading;

namespace SignalRouter.Performance.Tests;

/// <summary>
/// Exact per-operation managed-allocation measurement on a dedicated thread —
/// the "kernel owner-thread allocation" scope of the performance track (plan
/// D8): the measured delegate runs synchronously on one fresh thread, so
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> observes exactly the
/// allocations of that code and nothing from the rest of the process. The
/// value is deterministic (an allocation counter, not a timing), which is what
/// makes it usable as an exact-zero gate later (P6); until then callers treat
/// the result as a characterization number. CoreCLR-only visibility: Mono,
/// IL2CPP, and native allocations are outside this meter (BASELINE.md caveats).
/// </summary>
public static class AllocationMeter
{
    /// <summary>
    /// Runs <paramref name="operation"/> <paramref name="warmupIterations"/>
    /// times (JIT, static initialization, pool/cache warm-up), then measures the
    /// allocation delta over <paramref name="measuredIterations"/> further runs.
    /// Returns bytes per operation, rounded up so a nonzero total can never
    /// round down to a false zero.
    /// </summary>
    public static long BytesPerOperation(
        Action operation, int warmupIterations = 64, int measuredIterations = 256)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (warmupIterations < 1 || measuredIterations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(measuredIterations), "Warmup and measured iterations are at least one.");
        }

        long total = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < warmupIterations; i++)
                {
                    operation();
                }

                // Settle before the measured window so a GC triggered by warmup
                // garbage does not land inside it.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < measuredIterations; i++)
                {
                    operation();
                }

                total = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw new InvalidOperationException("The measured operation failed.", failure);
        }

        return (total + measuredIterations - 1) / measuredIterations;
    }
}
