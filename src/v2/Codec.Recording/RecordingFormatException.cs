using System;

namespace SignalRouter.V2.Codec.Recording
{
    /// <summary>
    /// A structured artifact-format failure: a stable code, the byte offset it
    /// was detected at (-1 when positionless), and a human-readable message.
    /// Torn tails are NOT exceptions — the reader truncates and reports them in
    /// its result (ADR 0016); this throws only for inputs no artifact can have
    /// (bad magic, unsupported major, over-budget reads).
    /// </summary>
    public sealed class RecordingFormatException : Exception
    {
        public RecordingFormatException(string code, int position, string message)
            : base(message)
        {
            Code = code;
            Position = position;
        }

        public string Code { get; }

        public int Position { get; }
    }
}
