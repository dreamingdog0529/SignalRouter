# SignalRouter UPM Package

Package ID: `com.dreamingdog0529.signalrouter`  
Version: `0.1.0`

The package defines the `SignalRouter.Core` and `SignalRouter.Protocol` assembly
boundaries. Core includes immutable commands and their versioned catalog/codecs,
structured terminal-result values, and the lifetime-scoped semantic registry.

The dispatcher contract is present, but FIFO execution, stages, state probes,
record/replay, Unity UI adapters, and transport integrations are not implemented yet.

Runtime sources are kept compatible with C# 9. Package consumers do not need to enable
preview language features.
