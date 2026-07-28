using BenchmarkDotNet.Attributes;
using SignalRouter.Contracts;
using SignalRouter.Kernel;

namespace SignalRouter.Benchmarks;

/// <summary>
/// Steady-state emission into a full trace ring (performance-track finding
/// A2): every emit at capacity evicts, so the eviction path is the cost that
/// multiplies across all ~20 kernel emit sites.
/// </summary>
[MemoryDiagnoser]
public class TraceRingBenchmarks
{
    private KernelTraceRing ring = default!;

    private SemanticEvent semanticEvent = default!;

    [GlobalSetup]
    public void Setup()
    {
        ring = new KernelTraceRing(capacity: 8192, byteCapacity: 4 * 1024 * 1024);
        semanticEvent = new SemanticEvent(
            new EventKind("StateTransition"),
            new RuntimeIncarnationId("bench-incarnation"),
            EventCausation.None,
            new RequestId("req-1"),
            operation: null,
            new LogicalOrder(7),
            new SourceRevision(3),
            detailCode: null);
        for (var i = 0; i < 8200; i++)
        {
            ring.Emit(semanticEvent);
        }
    }

    [Benchmark]
    public void EmitAtCapacity() => ring.Emit(semanticEvent);
}
