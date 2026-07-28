using System;

namespace SignalRouter.Codec.CanonicalState
{
    /// <summary>
    /// A malformed canonical payload (ADR 0012). Carries a stable code — never free
    /// text as the discriminator — and the byte offset where parsing failed
    /// (<c>-1</c> for whole-payload conditions such as a failed canonical-form
    /// comparison).
    /// Codes: <c>Truncated</c>, <c>TrailingBytes</c>, <c>NonMinimalVarint</c>,
    /// <c>VarintOverflow</c>, <c>UnknownValueTag</c>, <c>InvalidUtf8</c>,
    /// <c>UnknownReasonCode</c>, <c>NonCanonical</c>, <c>UnsupportedVersion</c>,
    /// <c>BadMagic</c>, <c>InvalidBoolean</c>, <c>InvalidOption</c>,
    /// <c>NaNFloat</c>, <c>InvalidStructure</c>.
    /// </summary>
    public sealed class CanonicalStateFormatException : Exception
    {
        public CanonicalStateFormatException(string code, int offset, string message)
            : base(message)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Offset = offset;
        }

        public string Code { get; }

        public int Offset { get; }
    }
}
