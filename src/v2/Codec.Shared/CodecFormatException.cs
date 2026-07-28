using System;

namespace SignalRouter.V2.Codec.Shared
{
    /// <summary>
    /// The structured parse failure of the shared payload primitives. Internal on
    /// purpose: this file is compiled into each codec leaf from shared source
    /// (ADR 0007, ADR 0016), and a public type here would exist twice for a
    /// consumer referencing two leaves. Each leaf wraps it into its own public
    /// format exception at its API boundary.
    /// </summary>
    internal sealed class CodecFormatException : Exception
    {
        internal CodecFormatException(string code, int position, string message)
            : base(message)
        {
            Code = code;
            Position = position;
        }

        /// <summary>A stable failure code (e.g. "Truncated", "NonMinimalVarint").</summary>
        internal string Code { get; }

        /// <summary>The byte offset the failure was detected at, or -1.</summary>
        internal int Position { get; }
    }
}
