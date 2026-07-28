using System;
using SignalRouter.V2.Codec.Shared;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.Recording
{
    /// <summary>
    /// The per-cut payload grammar of RecordingEventSchema@1.0 (ADR 0016 —
    /// this file is the grammar appendix's implementation). Field order is
    /// exactly each cut's constructor order; every closed vocabulary encodes as
    /// its stable code string (never an enum ordinal, ADR 0009/0012); decode
    /// constructs through the public Contracts constructors so every cut
    /// invariant re-validates on read.
    /// </summary>
    internal static class RecordingPayloadCodec
    {
        // ── Closed-vocabulary code tables (two-way, ADR 0012 discipline) ─────

        private static string CodeOf(InteractionOutcome value) => value switch
        {
            InteractionOutcome.Succeeded => "Succeeded",
            InteractionOutcome.Rejected => "Rejected",
            InteractionOutcome.Faulted => "Faulted",
            InteractionOutcome.Cancelled => "Cancelled",
            InteractionOutcome.OutcomeUnknown => "OutcomeUnknown",
            _ => throw new CodecFormatException(
                "UnknownReasonCode", -1, "Unencodable interaction outcome."),
        };

        private static InteractionOutcome OutcomeOf(string code, int position) => code switch
        {
            "Succeeded" => InteractionOutcome.Succeeded,
            "Rejected" => InteractionOutcome.Rejected,
            "Faulted" => InteractionOutcome.Faulted,
            "Cancelled" => InteractionOutcome.Cancelled,
            "OutcomeUnknown" => InteractionOutcome.OutcomeUnknown,
            _ => throw new CodecFormatException(
                "UnknownReasonCode", position, "Unknown interaction outcome code."),
        };

        private static string CodeOf(CancellationPhase value) => value switch
        {
            CancellationPhase.BeforeEffect => "BeforeEffect",
            CancellationPhase.DuringEffect => "DuringEffect",
            CancellationPhase.AfterEffect => "AfterEffect",
            _ => throw new CodecFormatException(
                "UnknownReasonCode", -1, "Unencodable cancellation phase."),
        };

        private static CancellationPhase PhaseOf(string code, int position) => code switch
        {
            "BeforeEffect" => CancellationPhase.BeforeEffect,
            "DuringEffect" => CancellationPhase.DuringEffect,
            "AfterEffect" => CancellationPhase.AfterEffect,
            _ => throw new CodecFormatException(
                "UnknownReasonCode", position, "Unknown cancellation phase code."),
        };

        private static string CodeOf(PredicateResolution value) => value switch
        {
            PredicateResolution.Satisfied => "Satisfied",
            PredicateResolution.TimedOut => "TimedOut",
            PredicateResolution.Cancelled => "Cancelled",
            PredicateResolution.Faulted => "Faulted",
            PredicateResolution.Unknown => "Unknown",
            _ => throw new CodecFormatException(
                "UnknownReasonCode", -1, "Unencodable predicate resolution."),
        };

        private static PredicateResolution ResolutionOf(string code, int position) => code switch
        {
            "Satisfied" => PredicateResolution.Satisfied,
            "TimedOut" => PredicateResolution.TimedOut,
            "Cancelled" => PredicateResolution.Cancelled,
            "Faulted" => PredicateResolution.Faulted,
            "Unknown" => PredicateResolution.Unknown,
            _ => throw new CodecFormatException(
                "UnknownReasonCode", position, "Unknown predicate resolution code."),
        };

        private static string CodeOf(PostconditionResult value) => value switch
        {
            PostconditionResult.Satisfied => "Satisfied",
            PostconditionResult.False => "False",
            PostconditionResult.TimedOut => "TimedOut",
            PostconditionResult.Unknown => "Unknown",
            _ => throw new CodecFormatException(
                "UnknownReasonCode", -1, "Unencodable postcondition result."),
        };

        private static PostconditionResult PostconditionOf(string code, int position) => code switch
        {
            "Satisfied" => PostconditionResult.Satisfied,
            "False" => PostconditionResult.False,
            "TimedOut" => PostconditionResult.TimedOut,
            "Unknown" => PostconditionResult.Unknown,
            _ => throw new CodecFormatException(
                "UnknownReasonCode", position, "Unknown postcondition code."),
        };

        private static string CodeOf(Provenance value) => value switch
        {
            Provenance.HumanDirected => "HumanDirected",
            Provenance.Automation => "Automation",
            Provenance.Unknown => "Unknown",
            _ => throw new CodecFormatException(
                "UnknownReasonCode", -1, "Unencodable provenance."),
        };

        private static Provenance ProvenanceOf(string code, int position) => code switch
        {
            "HumanDirected" => Provenance.HumanDirected,
            "Automation" => Provenance.Automation,
            "Unknown" => Provenance.Unknown,
            _ => throw new CodecFormatException(
                "UnknownReasonCode", position, "Unknown provenance code."),
        };

        // ── Timeline grammar (1.1) ───────────────────────────────────────────

        internal static void WriteTimeline(ref PayloadWriter writer, TimelineRecord entry)
        {
            writer.WriteString(entry.Kind);
            switch (entry.Kind)
            {
                case TimelineRecordKinds.WaitPoll:
                    writer.WriteString(entry.Operation.Value);
                    WriteContract(ref writer, entry.Predicate.Id.Value, entry.Predicate.Version);
                    writer.WriteInt64(unchecked((long)entry.Revision.Value));
                    break;
                case TimelineRecordKinds.Gap:
                    writer.WriteInt64(entry.DroppedCount);
                    break;
                default:
                    throw new CodecFormatException(
                        "UnknownReasonCode", -1, "Unencodable timeline kind.");
            }
        }

        /// <summary>Null for a timeline kind this reader does not know — the lane is droppable, the record is skipped.</summary>
        internal static TimelineRecord? ReadTimeline(PayloadReader reader)
        {
            var kind = reader.ReadString();
            switch (kind)
            {
                case TimelineRecordKinds.WaitPoll:
                {
                    var operation = new OperationId(reader.ReadString());
                    var predicate = new PredicateContractRef(
                        new PredicateContractId(reader.ReadString()), ReadVersion(reader));
                    var revision = new SourceRevision(unchecked((ulong)reader.ReadInt64()));
                    return TimelineRecord.WaitPoll(operation, predicate, revision);
                }

                case TimelineRecordKinds.Gap:
                    return TimelineRecord.Gap(reader.ReadInt64());

                default:
                    return null;
            }
        }

        // ── Common value grammars ────────────────────────────────────────────

        private static void WriteContract(ref PayloadWriter writer, string id, ContractVersion version)
        {
            writer.WriteString(id);
            writer.WriteVaruint(version.Major);
            writer.WriteVaruint(version.Minor);
        }

        private static ContractVersion ReadVersion(PayloadReader reader) =>
            new ContractVersion(reader.ReadVaruint(), reader.ReadVaruint());

        internal static void WriteContentId(ref PayloadWriter writer, ContentId id)
        {
            writer.WriteString(id.DigestAlgorithmId);
            writer.WriteVaruint(id.CanonicalRepresentationVersion);
            var digest = id.Digest.ToArray();
            writer.WriteVaruint(digest.Length);
            foreach (var value in digest)
            {
                writer.WriteRaw(value);
            }
        }

        internal static ContentId ReadContentId(PayloadReader reader)
        {
            var algorithm = reader.ReadString();
            var version = reader.ReadVaruint();
            var length = reader.ReadCount(1);
            var digest = new byte[length];
            for (var i = 0; i < length; i++)
            {
                digest[i] = reader.ReadByte();
            }

            return new ContentId(algorithm, version, DigestValue.From(digest));
        }

        private static void WriteFieldValue(ref PayloadWriter writer, FieldValue value)
        {
            switch (value.Kind)
            {
                case FieldValueKind.Null:
                    writer.WriteString("n");
                    break;
                case FieldValueKind.String:
                    writer.WriteString("s");
                    writer.WriteString(value.AsString);
                    break;
                case FieldValueKind.Integer:
                    writer.WriteString("i");
                    writer.WriteInt64(value.AsInteger);
                    break;
                case FieldValueKind.Boolean:
                    writer.WriteString("b");
                    writer.WriteBool(value.AsBoolean);
                    break;
                case FieldValueKind.Float:
                    writer.WriteString("f");
                    writer.WriteFloatBits(value.AsFloat);
                    break;
                default:
                    // A future kind must extend the grammar explicitly, never
                    // fall through to a wrong encoding.
                    throw new CodecFormatException(
                        "UnknownValueTag", -1, "Unencodable field value kind.");
            }
        }

        private static FieldValue ReadFieldValue(PayloadReader reader)
        {
            var position = reader.Position;
            var tag = reader.ReadString();
            return tag switch
            {
                "n" => FieldValue.Null,
                "s" => FieldValue.Of(reader.ReadString()),
                "i" => FieldValue.Of(reader.ReadInt64()),
                "b" => FieldValue.Of(reader.ReadBool()),
                "f" => FieldValue.Of(reader.ReadFloatBits()),
                _ => throw new CodecFormatException(
                    "UnknownValueTag", position, "Unknown field value tag."),
            };
        }

        private static void WriteRecordedArguments(ref PayloadWriter writer, RecordedArguments arguments)
        {
            writer.WriteVaruint(arguments.Fields.Count);
            for (var i = 0; i < arguments.Fields.Count; i++)
            {
                var field = arguments.Fields[i];
                writer.WriteString(field.Name);
                writer.WriteBool(field.IsSecret);
                if (field.IsSecret)
                {
                    writer.WriteString(field.Secret.Value);
                    writer.WriteString(field.SecretValueDigest.Value);
                }
                else
                {
                    WriteFieldValue(ref writer, field.Value);
                }
            }
        }

        private static RecordedArguments ReadRecordedArguments(PayloadReader reader)
        {
            var count = reader.ReadCount(3);
            var fields = new RecordedArgument[count];
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                if (reader.ReadBool())
                {
                    fields[i] = RecordedArgument.OfSecret(
                        name,
                        new SecretReference(reader.ReadString()),
                        new ArgumentDigest(reader.ReadString()));
                }
                else
                {
                    fields[i] = RecordedArgument.OfValue(name, ReadFieldValue(reader));
                }
            }

            return new RecordedArguments(ValueArray<RecordedArgument>.From(fields));
        }

        private static void WriteTarget(ref PayloadWriter writer, TargetReference target)
        {
            if (target.Kind == TargetReferenceKind.AuthorKey)
            {
                writer.WriteString("key");
                writer.WriteString(target.Key.Value);
            }
            else
            {
                writer.WriteString("node");
                writer.WriteString(target.Node.Incarnation.Value);
                writer.WriteInt64(unchecked((long)target.Node.Value));
            }
        }

        private static TargetReference ReadTarget(PayloadReader reader)
        {
            var position = reader.Position;
            var kind = reader.ReadString();
            return kind switch
            {
                "key" => TargetReference.ForKey(new AuthorKey(reader.ReadString())),
                "node" => TargetReference.ForNode(new NodeRef(
                    new RuntimeIncarnationId(reader.ReadString()),
                    unchecked((ulong)reader.ReadInt64()))),
                _ => throw new CodecFormatException(
                    "UnknownValueTag", position, "Unknown target reference kind."),
            };
        }

        private static void WriteResolvedTarget(ref PayloadWriter writer, ResolvedTarget target)
        {
            writer.WriteString(target.Node.Incarnation.Value);
            writer.WriteInt64(unchecked((long)target.Node.Value));
            writer.WriteOption(target.AuthorKey.HasValue);
            if (target.AuthorKey.HasValue)
            {
                writer.WriteString(target.AuthorKey.Value.Value);
            }
        }

        private static ResolvedTarget ReadResolvedTarget(PayloadReader reader)
        {
            var node = new NodeRef(
                new RuntimeIncarnationId(reader.ReadString()),
                unchecked((ulong)reader.ReadInt64()));
            AuthorKey? key = null;
            if (reader.ReadOption())
            {
                key = new AuthorKey(reader.ReadString());
            }

            return new ResolvedTarget(node, key);
        }

        private static void WriteCausality(ref PayloadWriter writer, Causality causality)
        {
            switch (causality.Kind)
            {
                case CausalityKind.Root:
                    writer.WriteString("root");
                    break;
                case CausalityKind.Continuation:
                    var link = causality.Continuation!.Value;
                    writer.WriteString("continuation");
                    writer.WriteString(link.ParentRequestId.Value);
                    writer.WriteVaruint(link.ContinuationOrdinal);
                    writer.WriteString(link.Fingerprint.Value);
                    break;
                default:
                    writer.WriteString("external");
                    writer.WriteOption(causality.ExternalTriggerHint != null);
                    if (causality.ExternalTriggerHint != null)
                    {
                        writer.WriteString(causality.ExternalTriggerHint);
                    }

                    break;
            }
        }

        private static Causality ReadCausality(PayloadReader reader)
        {
            var position = reader.Position;
            var kind = reader.ReadString();
            switch (kind)
            {
                case "root":
                    return Causality.Root();
                case "continuation":
                    return Causality.OfContinuation(new ContinuationLink(
                        new RequestId(reader.ReadString()),
                        reader.ReadVaruint(),
                        new SemanticFingerprint(reader.ReadString())));
                case "external":
                    return Causality.OfExternalTrigger(
                        reader.ReadOption() ? reader.ReadString() : null);
                default:
                    throw new CodecFormatException(
                        "UnknownValueTag", position, "Unknown causality kind.");
            }
        }

        private static void WriteEnvelope(ref PayloadWriter writer, IdentityEnvelope envelope)
        {
            writer.WriteString(envelope.Principal.Kind);
            writer.WriteString(envelope.Principal.Id);
            writer.WriteString(envelope.Ingress.Value);
            writer.WriteString(CodeOf(envelope.Provenance));
            WriteCausality(ref writer, envelope.Causality);
        }

        private static IdentityEnvelope ReadEnvelope(PayloadReader reader)
        {
            var principal = new Principal(reader.ReadString(), reader.ReadString());
            var ingress = new IngressPath(reader.ReadString());
            var provenancePosition = reader.Position;
            var provenance = ProvenanceOf(reader.ReadString(), provenancePosition);
            return new IdentityEnvelope(principal, ingress, provenance, ReadCausality(reader));
        }

        // ── Cuts ─────────────────────────────────────────────────────────────

        internal static void WriteCut(ref PayloadWriter writer, EvidenceCut cut)
        {
            writer.WriteString(CutCodeOf(cut.Kind));
            writer.WriteInt64(unchecked((long)cut.Sequence.Value));
            switch (cut)
            {
                case RecordingOpened opened:
                    WriteContract(ref writer, opened.Profile.Id.Value, opened.Profile.Version);
                    WriteContract(ref writer, opened.RecordView.Id.Value, opened.RecordView.Version);
                    writer.WriteString(opened.RedactionPolicy.Value);
                    writer.WriteVaruint(opened.CompletionBindings.Count);
                    for (var i = 0; i < opened.CompletionBindings.Count; i++)
                    {
                        var binding = opened.CompletionBindings[i];
                        WriteContract(ref writer, binding.Capability.Id.Value, binding.Capability.Version);
                        WriteContract(ref writer, binding.Profile.Id.Value, binding.Profile.Version);
                    }

                    writer.WriteVaruint(opened.StateSourceContracts.Count);
                    for (var i = 0; i < opened.StateSourceContracts.Count; i++)
                    {
                        var binding = opened.StateSourceContracts[i];
                        writer.WriteString(binding.Key.Value);
                        WriteContract(ref writer, binding.Contract.Id.Value, binding.Contract.Version);
                    }

                    writer.WriteVaruint(opened.PredicateContracts.Count);
                    for (var i = 0; i < opened.PredicateContracts.Count; i++)
                    {
                        WriteContract(ref writer, opened.PredicateContracts[i].Id.Value, opened.PredicateContracts[i].Version);
                    }

                    writer.WriteString(opened.Incarnation.Value);
                    WriteContentId(ref writer, opened.BaseSnapshot);
                    break;

                case AdmissionCut admission:
                    writer.WriteString(admission.RequestId.Value);
                    writer.WriteInt64(unchecked((long)admission.LogicalOrder.Value));
                    writer.WriteString(admission.Fingerprint.Value);
                    WriteContract(
                        ref writer,
                        admission.Invocation.Contract.Id.Value,
                        admission.Invocation.Contract.Version);
                    WriteTarget(ref writer, admission.Invocation.Target);
                    writer.WriteString(admission.Invocation.Arguments.Value);
                    WriteRecordedArguments(ref writer, admission.Arguments);
                    WriteResolvedTarget(ref writer, admission.ResolvedTarget);
                    WriteEnvelope(ref writer, admission.Envelope);
                    break;

                case EffectPermit permit:
                    writer.WriteString(permit.RequestId.Value);
                    writer.WriteInt64(unchecked((long)permit.LogicalOrder.Value));
                    writer.WriteInt64(unchecked((long)permit.Watermark.Value));
                    WriteContentId(ref writer, permit.BeforeView);
                    writer.WriteBool(permit.ReusedCheckpointBlob);
                    break;

                case TerminalCut terminal:
                    writer.WriteString(terminal.RequestId.Value);
                    writer.WriteInt64(unchecked((long)terminal.LogicalOrder.Value));
                    writer.WriteString(CodeOf(terminal.Outcome));
                    writer.WriteBool(terminal.EffectPermitted);
                    WriteContentId(ref writer, terminal.AfterView);
                    writer.WriteOption(terminal.RejectionReason.HasValue);
                    if (terminal.RejectionReason.HasValue)
                    {
                        writer.WriteString(terminal.RejectionReason.Value.Value);
                    }

                    writer.WriteOption(terminal.FaultCode.HasValue);
                    if (terminal.FaultCode.HasValue)
                    {
                        writer.WriteString(terminal.FaultCode.Value.Value);
                    }

                    writer.WriteOption(terminal.CompletionEvidence != null);
                    if (terminal.CompletionEvidence != null)
                    {
                        var completion = terminal.CompletionEvidence;
                        WriteContract(ref writer, completion.Profile.Id.Value, completion.Profile.Version);
                        writer.WriteString(completion.Kind.Value);
                        writer.WriteOption(!completion.PayloadDigest.IsDefault);
                        if (!completion.PayloadDigest.IsDefault)
                        {
                            var digest = completion.PayloadDigest.ToArray();
                            writer.WriteVaruint(digest.Length);
                            foreach (var value in digest)
                            {
                                writer.WriteRaw(value);
                            }
                        }
                    }

                    writer.WriteOption(terminal.Postcondition.HasValue);
                    if (terminal.Postcondition.HasValue)
                    {
                        writer.WriteString(CodeOf(terminal.Postcondition.Value));
                    }

                    writer.WriteOption(terminal.Cancellation != null);
                    if (terminal.Cancellation != null)
                    {
                        var cancellation = terminal.Cancellation;
                        writer.WriteInt64(unchecked((long)cancellation.RequestedOrder.Value));
                        writer.WriteInt64(unchecked((long)cancellation.ObservedOrder.Value));
                        writer.WriteString(CodeOf(cancellation.Phase));
                        writer.WriteString(cancellation.Disposition);
                        writer.WriteBool(cancellation.EffectPermitted);
                        writer.WriteBool(cancellation.EffectStarted);
                    }

                    writer.WriteVaruint(terminal.Continuations.Count);
                    for (var i = 0; i < terminal.Continuations.Count; i++)
                    {
                        var commitment = terminal.Continuations[i];
                        writer.WriteVaruint(commitment.Ordinal);
                        writer.WriteString(commitment.Fingerprint.Value);
                    }

                    break;

                case ExternalMutationBarrier barrier:
                    writer.WriteInt64(unchecked((long)barrier.LastKnownCleanCut.Value));
                    writer.WriteInt64(unchecked((long)barrier.FirstObservedCut.Value));
                    writer.WriteInt64(unchecked((long)barrier.RevisionAtDetection.Value));
                    writer.WriteString(barrier.SourceHint);
                    writer.WriteVaruint(barrier.ContaminatedRequests.Count);
                    for (var i = 0; i < barrier.ContaminatedRequests.Count; i++)
                    {
                        writer.WriteString(barrier.ContaminatedRequests[i].Value);
                    }

                    break;

                case PredicateArmed armed:
                    writer.WriteString(armed.OperationId.Value);
                    WriteContract(ref writer, armed.Predicate.Id.Value, armed.Predicate.Version);
                    writer.WriteString(armed.Operands.Value);
                    writer.WriteString(armed.Fingerprint.Value);
                    WriteContract(ref writer, armed.Scope.Id.Value, armed.Scope.Version);
                    writer.WriteString(armed.ObservationScope);
                    WriteCausality(ref writer, armed.Causality);
                    writer.WriteInt64(unchecked((long)armed.ArmedSequence.Value));
                    break;

                case PredicateResolved resolved:
                    writer.WriteString(resolved.OperationId.Value);
                    writer.WriteString(CodeOf(resolved.Outcome));
                    WriteContentId(ref writer, resolved.WitnessOrFinalObservation);
                    writer.WriteInt64(unchecked((long)resolved.ResolvedSequence.Value));
                    break;

                case RecordingClosed closed:
                    writer.WriteBool(closed.Reason.IsCompleted);
                    if (!closed.Reason.IsCompleted)
                    {
                        writer.WriteString(closed.Reason.Reason.Value);
                    }

                    writer.WriteInt64(closed.DeclaredEventCount);
                    WriteContentId(ref writer, closed.FinalCheckpoint);
                    writer.WriteVaruint(closed.DeclaredReachableContentIds.Count);
                    for (var i = 0; i < closed.DeclaredReachableContentIds.Count; i++)
                    {
                        WriteContentId(ref writer, closed.DeclaredReachableContentIds[i]);
                    }

                    break;

                case AssertionEvaluated assertion:
                    writer.WriteString(assertion.Incarnation.Value);
                    writer.WriteInt64(unchecked((long)assertion.Watermark.Value));
                    WriteContract(ref writer, assertion.View.Id.Value, assertion.View.Version);
                    writer.WriteVaruint(assertion.StateSourceTableVersion);
                    writer.WriteString(assertion.Scope);
                    writer.WriteString(assertion.Domain.Value);
                    WriteContentId(ref writer, assertion.Snapshot);
                    writer.WriteBool(assertion.CompleteForScope);
                    WriteContract(ref writer, assertion.Predicate.Id.Value, assertion.Predicate.Version);
                    writer.WriteString(assertion.Operands.Value);
                    writer.WriteVaruint(assertion.Clauses.Count);
                    for (var i = 0; i < assertion.Clauses.Count; i++)
                    {
                        var clause = assertion.Clauses[i];
                        writer.WriteString(clause.ClauseId);
                        writer.WriteString(clause.Expected);
                        writer.WriteString(clause.Actual);
                    }

                    if (assertion.Outcome.Kind == PredicateEvaluationKind.Unevaluable)
                    {
                        writer.WriteString("Unevaluable");
                        writer.WriteString(assertion.Outcome.Reason.Value);
                    }
                    else
                    {
                        writer.WriteString(
                            assertion.Outcome.Kind == PredicateEvaluationKind.Satisfied
                                ? "Satisfied"
                                : "False");
                    }

                    writer.WriteVaruint(assertion.WitnessPaths.Count);
                    for (var i = 0; i < assertion.WitnessPaths.Count; i++)
                    {
                        writer.WriteString(assertion.WitnessPaths[i]);
                    }

                    break;

                default:
                    throw new CodecFormatException(
                        "UnknownValueTag", -1, "Unencodable evidence cut kind.");
            }
        }

        internal static EvidenceCut ReadCut(PayloadReader reader)
        {
            var position = reader.Position;
            var kindCode = reader.ReadString();
            var sequence = new EvidenceSequence(unchecked((ulong)reader.ReadInt64()));
            switch (kindCode)
            {
                case "RecordingOpened":
                {
                    var profile = new ReplayComparisonProfileRef(
                        new ReplayComparisonProfileId(reader.ReadString()), ReadVersion(reader));
                    var view = new ViewContractRef(
                        new ViewContractId(reader.ReadString()), ReadVersion(reader));
                    var redaction = new RedactionPolicyId(reader.ReadString());
                    var completionCount = reader.ReadCount(4);
                    var completions = new CompletionBinding[completionCount];
                    for (var i = 0; i < completionCount; i++)
                    {
                        completions[i] = new CompletionBinding(
                            new CapabilityContractRef(
                                new CapabilityContractId(reader.ReadString()), ReadVersion(reader)),
                            new CompletionProfileRef(
                                new CompletionProfileId(reader.ReadString()), ReadVersion(reader)));
                    }

                    var sourceCount = reader.ReadCount(3);
                    var sources = new StateSourceBinding[sourceCount];
                    for (var i = 0; i < sourceCount; i++)
                    {
                        sources[i] = new StateSourceBinding(
                            new StateSourceKey(reader.ReadString()),
                            new StateSourceContractRef(
                                new StateSourceContractId(reader.ReadString()), ReadVersion(reader)));
                    }

                    var predicateCount = reader.ReadCount(3);
                    var predicates = new PredicateContractRef[predicateCount];
                    for (var i = 0; i < predicateCount; i++)
                    {
                        predicates[i] = new PredicateContractRef(
                            new PredicateContractId(reader.ReadString()), ReadVersion(reader));
                    }

                    return new RecordingOpened(
                        sequence,
                        profile,
                        view,
                        redaction,
                        ValueArray<CompletionBinding>.From(completions),
                        ValueArray<StateSourceBinding>.From(sources),
                        ValueArray<PredicateContractRef>.From(predicates),
                        new RuntimeIncarnationId(reader.ReadString()),
                        ReadContentId(reader));
                }

                case "AdmissionCut":
                {
                    var request = new RequestId(reader.ReadString());
                    var order = new LogicalOrder(unchecked((ulong)reader.ReadInt64()));
                    var fingerprint = new SemanticFingerprint(reader.ReadString());
                    var contract = new CapabilityContractRef(
                        new CapabilityContractId(reader.ReadString()), ReadVersion(reader));
                    var target = ReadTarget(reader);
                    var digest = new ArgumentDigest(reader.ReadString());
                    var recorded = ReadRecordedArguments(reader);
                    var resolved = ReadResolvedTarget(reader);
                    var envelope = ReadEnvelope(reader);
                    return new AdmissionCut(
                        sequence, request, order, fingerprint,
                        new CapabilityInvocation(contract, target, digest),
                        recorded, resolved, envelope);
                }

                case "EffectPermit":
                    return new EffectPermit(
                        sequence,
                        new RequestId(reader.ReadString()),
                        new LogicalOrder(unchecked((ulong)reader.ReadInt64())),
                        new SourceRevision(unchecked((ulong)reader.ReadInt64())),
                        ReadContentId(reader),
                        reader.ReadBool());

                case "TerminalCut":
                {
                    var request = new RequestId(reader.ReadString());
                    var order = new LogicalOrder(unchecked((ulong)reader.ReadInt64()));
                    var outcomePosition = reader.Position;
                    var outcome = OutcomeOf(reader.ReadString(), outcomePosition);
                    var effectPermitted = reader.ReadBool();
                    var afterView = ReadContentId(reader);
                    RejectionReason? rejection = null;
                    if (reader.ReadOption())
                    {
                        rejection = new RejectionReason(reader.ReadString());
                    }

                    FaultCode? fault = null;
                    if (reader.ReadOption())
                    {
                        fault = new FaultCode(reader.ReadString());
                    }

                    CompletionEvidence? completion = null;
                    if (reader.ReadOption())
                    {
                        var profile = new CompletionProfileRef(
                            new CompletionProfileId(reader.ReadString()), ReadVersion(reader));
                        var kind = new CompletionEvidenceKind(reader.ReadString());
                        var payloadDigest = default(DigestValue);
                        if (reader.ReadOption())
                        {
                            var length = reader.ReadCount(1);
                            var bytes = new byte[length];
                            for (var i = 0; i < length; i++)
                            {
                                bytes[i] = reader.ReadByte();
                            }

                            payloadDigest = DigestValue.From(bytes);
                        }

                        completion = new CompletionEvidence(profile, kind, payloadDigest);
                    }

                    PostconditionResult? postcondition = null;
                    if (reader.ReadOption())
                    {
                        var postconditionPosition = reader.Position;
                        postcondition = PostconditionOf(reader.ReadString(), postconditionPosition);
                    }

                    CancellationEvidence? cancellation = null;
                    if (reader.ReadOption())
                    {
                        var requested = new LogicalOrder(unchecked((ulong)reader.ReadInt64()));
                        var observed = new LogicalOrder(unchecked((ulong)reader.ReadInt64()));
                        var phasePosition = reader.Position;
                        var phase = PhaseOf(reader.ReadString(), phasePosition);
                        cancellation = new CancellationEvidence(
                            requested, observed, phase,
                            reader.ReadString(), reader.ReadBool(), reader.ReadBool());
                    }

                    var commitmentCount = reader.ReadCount(2);
                    var commitments = new ContinuationCommitment[commitmentCount];
                    for (var i = 0; i < commitmentCount; i++)
                    {
                        commitments[i] = new ContinuationCommitment(
                            reader.ReadVaruint(),
                            new SemanticFingerprint(reader.ReadString()));
                    }

                    return new TerminalCut(
                        sequence, request, order, outcome, effectPermitted, afterView,
                        rejection, fault, completion, postcondition, cancellation,
                        ValueArray<ContinuationCommitment>.From(commitments));
                }

                case "ExternalMutationBarrier":
                {
                    var lastClean = new EvidenceSequence(unchecked((ulong)reader.ReadInt64()));
                    var firstObserved = new EvidenceSequence(unchecked((ulong)reader.ReadInt64()));
                    var revision = new SourceRevision(unchecked((ulong)reader.ReadInt64()));
                    var hint = reader.ReadString();
                    var count = reader.ReadCount(1);
                    var requests = new RequestId[count];
                    for (var i = 0; i < count; i++)
                    {
                        requests[i] = new RequestId(reader.ReadString());
                    }

                    return new ExternalMutationBarrier(
                        sequence, lastClean, firstObserved, revision, hint,
                        ValueArray<RequestId>.From(requests));
                }

                case "PredicateArmed":
                    return new PredicateArmed(
                        sequence,
                        new OperationId(reader.ReadString()),
                        new PredicateContractRef(
                            new PredicateContractId(reader.ReadString()), ReadVersion(reader)),
                        new ArgumentDigest(reader.ReadString()),
                        new SemanticFingerprint(reader.ReadString()),
                        new ViewContractRef(
                            new ViewContractId(reader.ReadString()), ReadVersion(reader)),
                        reader.ReadString(),
                        ReadCausality(reader),
                        new ViewSequence(unchecked((ulong)reader.ReadInt64())));

                case "PredicateResolved":
                {
                    var operation = new OperationId(reader.ReadString());
                    var resolutionPosition = reader.Position;
                    var resolution = ResolutionOf(reader.ReadString(), resolutionPosition);
                    return new PredicateResolved(
                        sequence, operation, resolution, ReadContentId(reader),
                        new ViewSequence(unchecked((ulong)reader.ReadInt64())));
                }

                case "RecordingClosed":
                {
                    var completed = reader.ReadBool();
                    var reason = completed
                        ? RecordingCloseReason.Completed
                        : RecordingCloseReason.Incomplete(new IncompleteReason(reader.ReadString()));
                    var declaredCount = reader.ReadInt64();
                    var finalCheckpoint = ReadContentId(reader);
                    var reachableCount = reader.ReadCount(3);
                    var reachable = new ContentId[reachableCount];
                    for (var i = 0; i < reachableCount; i++)
                    {
                        reachable[i] = ReadContentId(reader);
                    }

                    return new RecordingClosed(
                        sequence, reason, declaredCount, finalCheckpoint,
                        ValueArray<ContentId>.From(reachable));
                }

                case "AssertionEvaluated":
                {
                    var incarnation = new RuntimeIncarnationId(reader.ReadString());
                    var watermark = new SourceRevision(unchecked((ulong)reader.ReadInt64()));
                    var view = new ViewContractRef(
                        new ViewContractId(reader.ReadString()), ReadVersion(reader));
                    var tableVersion = reader.ReadVaruint();
                    var scope = reader.ReadString();
                    var domain = new SecurityDomainId(reader.ReadString());
                    var snapshot = ReadContentId(reader);
                    var complete = reader.ReadBool();
                    var predicate = new PredicateContractRef(
                        new PredicateContractId(reader.ReadString()), ReadVersion(reader));
                    var operands = new ArgumentDigest(reader.ReadString());
                    var clauseCount = reader.ReadCount(3);
                    var clauses = new ClauseEvaluation[clauseCount];
                    for (var i = 0; i < clauseCount; i++)
                    {
                        clauses[i] = new ClauseEvaluation(
                            reader.ReadString(), reader.ReadString(), reader.ReadString());
                    }

                    var outcomePosition = reader.Position;
                    var outcomeCode = reader.ReadString();
                    var outcome = outcomeCode switch
                    {
                        "Satisfied" => PredicateEvaluationOutcome.Satisfied,
                        "False" => PredicateEvaluationOutcome.False,
                        "Unevaluable" => PredicateEvaluationOutcome.Unevaluable(
                            new UnevaluableReason(reader.ReadString())),
                        _ => throw new CodecFormatException(
                            "UnknownReasonCode", outcomePosition, "Unknown evaluation outcome code."),
                    };
                    var witnessCount = reader.ReadCount(1);
                    var witnesses = new string[witnessCount];
                    for (var i = 0; i < witnessCount; i++)
                    {
                        witnesses[i] = reader.ReadString();
                    }

                    return new AssertionEvaluated(
                        sequence, incarnation, watermark, view, tableVersion, scope, domain,
                        snapshot, complete, predicate, operands,
                        ValueArray<ClauseEvaluation>.From(clauses), outcome,
                        ValueArray<string>.From(witnesses));
                }

                default:
                    throw new CodecFormatException(
                        "UnknownValueTag", position, "Unknown evidence cut code.");
            }
        }

        // ── Comparison profile document (record kind 0x04) ───────────────────

        internal static void WriteProfile(ref PayloadWriter writer, ReplayComparisonProfile profile)
        {
            WriteContract(ref writer, profile.Reference.Id.Value, profile.Reference.Version);
            WriteContract(ref writer, profile.RecordView.Id.Value, profile.RecordView.Version);
            writer.WriteString(profile.Scope);
            writer.WriteString(profile.RedactionPolicy.Value);
            writer.WriteString(profile.NodeMatching);
            writer.WriteVaruint(profile.NodeRules.Count);
            for (var i = 0; i < profile.NodeRules.Count; i++)
            {
                writer.WriteString(profile.NodeRules[i].RoleCode);
                WriteStrings(ref writer, profile.NodeRules[i].Fields);
            }

            writer.WriteVaruint(profile.SourceRules.Count);
            for (var i = 0; i < profile.SourceRules.Count; i++)
            {
                writer.WriteString(profile.SourceRules[i].Source.Value);
                WriteStrings(ref writer, profile.SourceRules[i].Fields);
            }

            writer.WriteVaruint(profile.ItemKeyRules.Count);
            for (var i = 0; i < profile.ItemKeyRules.Count; i++)
            {
                writer.WriteString(profile.ItemKeyRules[i].CollectionPath);
                writer.WriteString(profile.ItemKeyRules[i].KeyField);
            }

            writer.WriteVaruint(profile.CollectionRules.Count);
            for (var i = 0; i < profile.CollectionRules.Count; i++)
            {
                writer.WriteString(profile.CollectionRules[i].FieldPath);
                writer.WriteString(profile.CollectionRules[i].Comparison switch
                {
                    CollectionComparison.Ordered => "Ordered",
                    CollectionComparison.Set => "Set",
                    _ => "Multiset",
                });
            }

            writer.WriteVaruint(profile.NormalizationRules.Count);
            for (var i = 0; i < profile.NormalizationRules.Count; i++)
            {
                writer.WriteString(profile.NormalizationRules[i].FieldPath);
                writer.WriteString(profile.NormalizationRules[i].NormalizerCode);
            }

            writer.WriteBool(profile.RequireCompleteForScope);
            writer.WriteVaruint(profile.ExtensionPolicies.Count);
            for (var i = 0; i < profile.ExtensionPolicies.Count; i++)
            {
                writer.WriteString(profile.ExtensionPolicies[i].ExtensionId);
                writer.WriteBool(profile.ExtensionPolicies[i].Mandatory);
            }

            writer.WriteVaruint(profile.ProjectableFromVersions.Count);
            for (var i = 0; i < profile.ProjectableFromVersions.Count; i++)
            {
                writer.WriteVaruint(profile.ProjectableFromVersions[i].Major);
                writer.WriteVaruint(profile.ProjectableFromVersions[i].Minor);
            }
        }

        internal static ReplayComparisonProfile ReadProfile(PayloadReader reader)
        {
            var reference = new ReplayComparisonProfileRef(
                new ReplayComparisonProfileId(reader.ReadString()), ReadVersion(reader));
            var view = new ViewContractRef(
                new ViewContractId(reader.ReadString()), ReadVersion(reader));
            var scope = reader.ReadString();
            var redaction = new RedactionPolicyId(reader.ReadString());
            var matching = reader.ReadString();

            var nodeCount = reader.ReadCount(2);
            var nodeRules = new ComparedNodeRule[nodeCount];
            for (var i = 0; i < nodeCount; i++)
            {
                nodeRules[i] = new ComparedNodeRule(reader.ReadString(), ReadStrings(reader));
            }

            var sourceCount = reader.ReadCount(2);
            var sourceRules = new ComparedSourceRule[sourceCount];
            for (var i = 0; i < sourceCount; i++)
            {
                sourceRules[i] = new ComparedSourceRule(
                    new StateSourceKey(reader.ReadString()), ReadStrings(reader));
            }

            var itemKeyCount = reader.ReadCount(2);
            var itemKeyRules = new ItemKeyRule[itemKeyCount];
            for (var i = 0; i < itemKeyCount; i++)
            {
                itemKeyRules[i] = new ItemKeyRule(reader.ReadString(), reader.ReadString());
            }

            var collectionCount = reader.ReadCount(2);
            var collectionRules = new CollectionRule[collectionCount];
            for (var i = 0; i < collectionCount; i++)
            {
                var path = reader.ReadString();
                var comparisonPosition = reader.Position;
                var comparison = reader.ReadString() switch
                {
                    "Ordered" => CollectionComparison.Ordered,
                    "Set" => CollectionComparison.Set,
                    "Multiset" => CollectionComparison.Multiset,
                    _ => throw new CodecFormatException(
                        "UnknownReasonCode", comparisonPosition, "Unknown collection comparison code."),
                };
                collectionRules[i] = new CollectionRule(path, comparison);
            }

            var normalizationCount = reader.ReadCount(2);
            var normalizationRules = new NormalizationRule[normalizationCount];
            for (var i = 0; i < normalizationCount; i++)
            {
                normalizationRules[i] = new NormalizationRule(reader.ReadString(), reader.ReadString());
            }

            var requireComplete = reader.ReadBool();
            var extensionCount = reader.ReadCount(2);
            var extensions = new ExtensionPolicy[extensionCount];
            for (var i = 0; i < extensionCount; i++)
            {
                extensions[i] = new ExtensionPolicy(reader.ReadString(), reader.ReadBool());
            }

            var versionCount = reader.ReadCount(2);
            var versions = new ContractVersion[versionCount];
            for (var i = 0; i < versionCount; i++)
            {
                versions[i] = ReadVersion(reader);
            }

            return new ReplayComparisonProfile(
                reference, view, scope, redaction, matching,
                ValueArray<ComparedNodeRule>.From(nodeRules),
                ValueArray<ComparedSourceRule>.From(sourceRules),
                ValueArray<ItemKeyRule>.From(itemKeyRules),
                ValueArray<CollectionRule>.From(collectionRules),
                ValueArray<NormalizationRule>.From(normalizationRules),
                requireComplete,
                ValueArray<ExtensionPolicy>.From(extensions),
                ValueArray<ContractVersion>.From(versions));
        }

        private static void WriteStrings(ref PayloadWriter writer, ValueArray<string> values)
        {
            writer.WriteVaruint(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                writer.WriteString(values[i]);
            }
        }

        private static ValueArray<string> ReadStrings(PayloadReader reader)
        {
            var count = reader.ReadCount(1);
            var values = new string[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = reader.ReadString();
            }

            return ValueArray<string>.From(values);
        }

        private static string CutCodeOf(EvidenceCutKind kind) => kind switch
        {
            EvidenceCutKind.RecordingOpened => "RecordingOpened",
            EvidenceCutKind.AdmissionCut => "AdmissionCut",
            EvidenceCutKind.EffectPermit => "EffectPermit",
            EvidenceCutKind.TerminalCut => "TerminalCut",
            EvidenceCutKind.ExternalMutationBarrier => "ExternalMutationBarrier",
            EvidenceCutKind.PredicateArmed => "PredicateArmed",
            EvidenceCutKind.PredicateResolved => "PredicateResolved",
            EvidenceCutKind.RecordingClosed => "RecordingClosed",
            EvidenceCutKind.AssertionEvaluated => "AssertionEvaluated",
            _ => throw new CodecFormatException(
                "UnknownValueTag", -1, "Unencodable evidence cut kind."),
        };
    }
}
