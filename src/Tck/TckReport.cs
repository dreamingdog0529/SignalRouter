using System;
using SignalRouter.Contracts;

namespace SignalRouter.Tck
{
    /// <summary>
    /// The aggregate answer of a TCK run (adapter-conformance.md §7.2): a run with
    /// any required skipped check is <see cref="Incomplete"/> — never presented as
    /// tier-2 completion or SDK conformance.
    /// </summary>
    public enum TckAggregate
    {
        Passed,
        Failed,
        Incomplete,
    }

    /// <summary>The status of one check.</summary>
    public enum TckCheckStatus
    {
        Passed,
        Failed,

        /// <summary>Skipped with a reason — coverage staged to a later module.</summary>
        Skipped,
    }

    /// <summary>One check's result, anchored to the obligation it verifies.</summary>
    public sealed class TckCheckResult
    {
        public TckCheckResult(
            string checkId,
            string obligation,
            bool required,
            TckCheckStatus status,
            string? detail)
        {
            CheckId = ContractGrammar.ValidateIdentifier(checkId, nameof(checkId));
            Obligation = ContractGrammar.ValidateIdentifier(obligation, nameof(obligation));
            Required = required;
            Status = status;
            Detail = detail;
        }

        public string CheckId { get; }

        /// <summary>The adapter-conformance.md §7.2 obligation this check covers.</summary>
        public string Obligation { get; }

        public bool Required { get; }

        public TckCheckStatus Status { get; }

        public string? Detail { get; }

        public override string ToString() => $"{CheckId}: {Status}" + (Detail == null ? "" : $" ({Detail})");
    }

    /// <summary>The versioned answer of one TCK run.</summary>
    public sealed class TckReport
    {
        public TckReport(string version, ValueArray<TckCheckResult> checks)
        {
            Version = ContractGrammar.ValidateIdentifier(version, nameof(version));
            Checks = checks;

            var aggregate = TckAggregate.Passed;
            foreach (var check in checks)
            {
                if (check.Required && check.Status == TckCheckStatus.Failed)
                {
                    aggregate = TckAggregate.Failed;
                    break;
                }

                if (check.Required && check.Status == TckCheckStatus.Skipped)
                {
                    aggregate = TckAggregate.Incomplete;
                }
            }

            Aggregate = aggregate;
        }

        public string Version { get; }

        public TckAggregate Aggregate { get; }

        public ValueArray<TckCheckResult> Checks { get; }
    }
}
