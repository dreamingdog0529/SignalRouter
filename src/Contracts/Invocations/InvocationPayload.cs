using System;
using System.Collections.Generic;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The typed argument values of one submission. **Ephemeral by contract**
    /// (security-resources.md §3, kernel-execution.md §3): it exists in memory only,
    /// is never stored in retained mailbox structures, `RecoveryIndex`, trace, or
    /// events, and its lifetime ends at adoption refusal, terminal, or cancellation.
    /// Sensitive values travel here under protected handling; everything recorded is
    /// derived (fingerprint, redacted digest) — never the payload itself.
    /// </summary>
    public sealed class InvocationPayload
    {
        private readonly Dictionary<string, FieldValue> byName;

        public InvocationPayload(ValueArray<NamedField> fields)
        {
            byName = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (byName.ContainsKey(field.Name))
                {
                    throw new ArgumentException("Payload field names must be unique.", nameof(fields));
                }

                byName.Add(field.Name, field.Value);
            }

            Fields = fields;
        }

        public static InvocationPayload Empty { get; } = new InvocationPayload(ValueArray<NamedField>.Empty);

        public ValueArray<NamedField> Fields { get; }

        public bool TryGetValue(string name, out FieldValue value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return byName.TryGetValue(name, out value);
        }
    }
}
