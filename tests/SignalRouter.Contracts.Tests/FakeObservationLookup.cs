using System.Collections.Generic;

namespace SignalRouter.Contracts.Tests;

/// <summary>A scripted, snapshot-local lookup for evaluator fixtures.</summary>
internal sealed class FakeObservationLookup : IObservationLookup
{
    private readonly Dictionary<FieldPath, FieldLookup> fields = new();
    private readonly Dictionary<FieldPath, CollectionCountLookup> collections = new();

    internal FakeObservationLookup()
    {
        Basis = new ObservationBasis(
            TestData.Incarnation,
            new SourceRevision(7),
            TestData.RecordView,
            new SecurityDomainId("record"),
            "root");
    }

    public ObservationBasis Basis { get; }

    internal FakeObservationLookup With(string path, FieldLookup answer)
    {
        fields[new FieldPath(path)] = answer;
        return this;
    }

    internal FakeObservationLookup WithValue(string path, FieldValue value) =>
        With(path, FieldLookup.Present(value));

    internal FakeObservationLookup WithCollection(string path, CollectionCountLookup answer)
    {
        collections[new FieldPath(path)] = answer;
        return this;
    }

    public FieldLookup Lookup(FieldPath path) =>
        fields.TryGetValue(path, out var answer) ? answer : FieldLookup.Absent;

    public CollectionCountLookup CountCollection(FieldPath path) =>
        collections.TryGetValue(path, out var answer) ? answer : CollectionCountLookup.Absent;
}
