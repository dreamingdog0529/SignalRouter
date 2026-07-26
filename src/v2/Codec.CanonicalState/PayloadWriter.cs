using System;
using System.Collections.Generic;
using System.Text;

namespace SignalRouter.V2.Codec.CanonicalState
{
    /// <summary>
    /// Emits canonical representation v1 primitives (ADR 0012): minimal-form LEB128
    /// varints, length-framed strict UTF-8 strings, fixed 8-byte big-endian 64-bit
    /// values, and IEEE-754 bit patterns. Deterministic by construction — every
    /// write is a pure function of its argument.
    /// </summary>
    internal sealed class PayloadWriter
    {
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly List<byte> bytes = new List<byte>(1024);

        internal void WriteRaw(byte value) => bytes.Add(value);

        internal void WriteMagic()
        {
            bytes.Add(0x53);
            bytes.Add(0x52);
            bytes.Add(0x43);
            bytes.Add(0x53);
        }

        internal void WriteVaruint(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var remaining = (uint)value;
            do
            {
                var group = (byte)(remaining & 0x7F);
                remaining >>= 7;
                if (remaining != 0)
                {
                    group |= 0x80;
                }

                bytes.Add(group);
            }
            while (remaining != 0);
        }

        internal void WriteString(string text)
        {
            byte[] encoded;
            try
            {
                encoded = StrictUtf8.GetBytes(text);
            }
            catch (EncoderFallbackException exception)
            {
                // The Contracts layer rejects unpaired surrogates at construction;
                // reaching this indicates an un-hardened input.
                throw new ArgumentException(
                    "Canonical text must be a well-formed Unicode scalar sequence.", exception);
            }

            WriteVaruint(encoded.Length);
            bytes.AddRange(encoded);
        }

        internal void WriteBool(bool value) => bytes.Add(value ? (byte)0x01 : (byte)0x00);

        internal void WriteInt64(long value)
        {
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                bytes.Add((byte)(value >> shift));
            }
        }

        internal void WriteFloatBits(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

        internal byte[] ToArray() => bytes.ToArray();
    }
}
