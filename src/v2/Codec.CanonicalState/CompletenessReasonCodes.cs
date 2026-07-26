using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.CanonicalState
{
    /// <summary>
    /// The two-way table between <see cref="CompletenessReason"/> and its stable
    /// wire code (ADR 0012): explicit switches in both directions — never
    /// <c>Enum.ToString</c>/<c>Enum.Parse</c>, so a C# enum reorder can never
    /// silently change bytes.
    /// </summary>
    internal static class CompletenessReasonCodes
    {
        internal static string ToCode(CompletenessReason reason)
        {
            switch (reason)
            {
                case CompletenessReason.Virtualized:
                    return "Virtualized";
                case CompletenessReason.Redacted:
                    return "Redacted";
                case CompletenessReason.OutOfScope:
                    return "OutOfScope";
                case CompletenessReason.BudgetTruncated:
                    return "BudgetTruncated";
                case CompletenessReason.SourceUnavailable:
                    return "SourceUnavailable";
                case CompletenessReason.Stale:
                    return "Stale";
                default:
                    return "UnsupportedContract";
            }
        }

        internal static bool TryFromCode(string code, out CompletenessReason reason)
        {
            switch (code)
            {
                case "Virtualized":
                    reason = CompletenessReason.Virtualized;
                    return true;
                case "Redacted":
                    reason = CompletenessReason.Redacted;
                    return true;
                case "OutOfScope":
                    reason = CompletenessReason.OutOfScope;
                    return true;
                case "BudgetTruncated":
                    reason = CompletenessReason.BudgetTruncated;
                    return true;
                case "SourceUnavailable":
                    reason = CompletenessReason.SourceUnavailable;
                    return true;
                case "Stale":
                    reason = CompletenessReason.Stale;
                    return true;
                case "UnsupportedContract":
                    reason = CompletenessReason.UnsupportedContract;
                    return true;
                default:
                    reason = default;
                    return false;
            }
        }
    }
}
