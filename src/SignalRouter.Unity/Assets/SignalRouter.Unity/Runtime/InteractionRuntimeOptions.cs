#nullable enable

using System;
using System.Collections.Generic;

namespace SignalRouter.Unity;

// Configuration for an explicit InteractionRuntime.Initialize call. Omitted
// values fall back to the MVP catalog and a fresh session epoch. A recorder is
// borrowed, never owned: the runtime does not dispose it on shutdown, because
// its owner typically still needs to flush and close the underlying stream.
public sealed class InteractionRuntimeOptions
{
    public InteractionRuntimeOptions(
        InteractionCommandCatalog? catalog = null,
        string? sessionEpoch = null,
        InteractionRecorder? recorder = null,
        IEnumerable<IInteractionStateProbe>? additionalProbes = null)
    {
        Catalog = catalog;
        SessionEpoch = sessionEpoch;
        Recorder = recorder;
        var probes = new List<IInteractionStateProbe>();
        if (additionalProbes != null)
        {
            foreach (var probe in additionalProbes)
            {
                probes.Add(probe ?? throw new ArgumentException(
                    "Additional probes must not contain null.",
                    nameof(additionalProbes)));
            }
        }

        AdditionalProbes = probes;
    }

    public InteractionCommandCatalog? Catalog { get; }

    public string? SessionEpoch { get; }

    public InteractionRecorder? Recorder { get; }

    public IReadOnlyList<IInteractionStateProbe> AdditionalProbes { get; }
}
