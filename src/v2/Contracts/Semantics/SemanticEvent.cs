using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The kind of causation a semantic event carries.</summary>
    public enum EventCausationKind
    {
        None,
        Request,
        External,
    }

    /// <summary>
    /// The causation leg of the in-memory event algebra (observation-state.md §6):
    /// caused by a `RequestId`, caused externally (with a source hint), or uncaused.
    /// Not a new identifier. A continuation's causal binding maps in without loss —
    /// the causing request is the parent; ordinal and fingerprint remain on the
    /// admission envelope (<see cref="Causality"/>).
    /// </summary>
    public readonly struct EventCausation : IEquatable<EventCausation>
    {
        private readonly RequestId request;
        private readonly string? externalHint;

        private EventCausation(EventCausationKind kind, RequestId request, string? externalHint)
        {
            Kind = kind;
            this.request = request;
            this.externalHint = externalHint;
        }

        public static EventCausation None => new EventCausation(EventCausationKind.None, default, null);

        public static EventCausation OfRequest(RequestId request)
        {
            if (request.IsDefault)
            {
                throw new ArgumentException("Causation requires a non-default RequestId.", nameof(request));
            }

            return new EventCausation(EventCausationKind.Request, request, null);
        }

        public static EventCausation OfExternal(string hint)
        {
            return new EventCausation(
                EventCausationKind.External, default,
                ContractGrammar.ValidateIdentifier(hint, nameof(hint)));
        }

        public EventCausationKind Kind { get; }

        public RequestId Request =>
            Kind == EventCausationKind.Request
                ? request
                : throw new InvalidOperationException("Only request causation carries a RequestId.");

        public string ExternalHint =>
            Kind == EventCausationKind.External
                ? externalHint!
                : throw new InvalidOperationException("Only external causation carries a hint.");

        public bool Equals(EventCausation other) =>
            Kind == other.Kind &&
            request.Equals(other.request) &&
            string.Equals(externalHint, other.externalHint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is EventCausation other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, request.GetHashCode());
            return ContractGrammar.CombineHashes(
                hash, externalHint == null ? 0 : StringComparer.Ordinal.GetHashCode(externalHint));
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case EventCausationKind.Request:
                    return $"request:{request}";
                case EventCausationKind.External:
                    return $"external:{externalHint}";
                default:
                    return "none";
            }
        }

        public static bool operator ==(EventCausation left, EventCausation right) => left.Equals(right);

        public static bool operator !=(EventCausation left, EventCausation right) => !left.Equals(right);
    }

    /// <summary>
    /// The kind leg of the in-memory event algebra: an open, kernel-owned
    /// vocabulary, non-normative for persistent schemas (observation-state.md §6).
    /// The reserved minimum set is exposed as static values.
    /// </summary>
    public readonly struct EventKind : IEquatable<EventKind>
    {
        private readonly string? value;

        public EventKind(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public static EventKind Admitted => new EventKind("Admitted");

        public static EventKind StateTransition => new EventKind("StateTransition");

        public static EventKind EffectPermitted => new EventKind("EffectPermitted");

        public static EventKind EffectFenceReached => new EventKind("EffectFenceReached");

        public static EventKind TerminalCommitted => new EventKind("TerminalCommitted");

        public static EventKind SourcePublicationAdopted => new EventKind("SourcePublicationAdopted");

        public static EventKind PredicateArmed => new EventKind("PredicateArmed");

        public static EventKind PredicateResolved => new EventKind("PredicateResolved");

        public static EventKind AssertionEvaluated => new EventKind("AssertionEvaluated");

        public static EventKind HumanIntentBlocked => new EventKind("HumanIntentBlocked");

        public static EventKind ContaminationObserved => new EventKind("ContaminationObserved");

        public static EventKind IncarnationLifecycle => new EventKind("IncarnationLifecycle");

        public static EventKind TraceGap => new EventKind("TraceGap");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default EventKind carries no value.");

        public bool Equals(EventKind other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is EventKind other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(EventKind left, EventKind right) => left.Equals(right);

        public static bool operator !=(EventKind left, EventKind right) => !left.Equals(right);
    }

    /// <summary>
    /// One in-memory semantic event — the only vocabulary the four stores share
    /// (observation-state.md §6, ADR 0003). Participation of `RequestId` and
    /// `OperationId` is per kind: mutation events carry a request, operation events
    /// an operation; neither is universally required. No serialization, no payload —
    /// persistent projections belong to their owning codecs.
    /// </summary>
    public sealed class SemanticEvent
    {
        public SemanticEvent(
            EventKind kind,
            RuntimeIncarnationId incarnation,
            EventCausation causation,
            RequestId? request = null,
            OperationId? operation = null,
            LogicalOrder? order = null,
            SourceRevision? revision = null,
            string? detailCode = null)
        {
            if (kind.IsDefault)
            {
                throw new ArgumentException("Event requires a non-default kind.", nameof(kind));
            }

            if (incarnation.IsDefault)
            {
                throw new ArgumentException("Event requires a non-default incarnation.", nameof(incarnation));
            }

            if (request.HasValue && request.Value.IsDefault)
            {
                throw new ArgumentException("A present RequestId must be non-default.", nameof(request));
            }

            if (operation.HasValue && operation.Value.IsDefault)
            {
                throw new ArgumentException("A present OperationId must be non-default.", nameof(operation));
            }

            if (detailCode != null)
            {
                ContractGrammar.ValidateCode(detailCode, nameof(detailCode));
            }

            Kind = kind;
            Incarnation = incarnation;
            Causation = causation;
            Request = request;
            Operation = operation;
            Order = order;
            Revision = revision;
            DetailCode = detailCode;
        }

        public EventKind Kind { get; }

        public RuntimeIncarnationId Incarnation { get; }

        public EventCausation Causation { get; }

        public RequestId? Request { get; }

        public OperationId? Operation { get; }

        public LogicalOrder? Order { get; }

        public SourceRevision? Revision { get; }

        /// <summary>A stable, redaction-safe detail code — never free text.</summary>
        public string? DetailCode { get; }

        public override string ToString() => $"{Kind}({Causation})";
    }
}
