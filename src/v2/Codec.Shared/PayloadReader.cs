using System;
using System.Text;

namespace SignalRouter.V2.Codec.Shared
{
    /// <summary>
    /// Parses canonical representation v1 primitives (ADR 0012) with structured
    /// failures: every read bounds-checks before it allocates, varints must be
    /// minimal-form and within <see cref="int.MaxValue"/>, strings must be strict
    /// UTF-8, and list counts are pre-checked against the minimum bytes their items
    /// require so a hostile count can never force an oversized allocation.
    /// </summary>
    internal sealed class PayloadReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly byte[] data;
        private readonly int maxStringLength;
        private int position;

        internal PayloadReader(byte[] data)
            : this(data, int.MaxValue)
        {
        }

        internal PayloadReader(byte[] data, int maxStringLength)
        {
            this.data = data;
            this.maxStringLength = maxStringLength;
        }

        internal int Position => position;

        private int Remaining => data.Length - position;

        internal byte ReadByte()
        {
            if (Remaining < 1)
            {
                throw new CodecFormatException(
                    "Truncated", position, "Unexpected end of payload.");
            }

            return data[position++];
        }

        internal int ReadVaruint()
        {
            var start = position;
            var value = 0L;
            var shift = 0;
            byte group;
            var length = 0;
            do
            {
                if (length == 5)
                {
                    throw new CodecFormatException(
                        "VarintOverflow", start, "A varint exceeds five bytes.");
                }

                group = ReadByte();
                value |= (long)(group & 0x7F) << shift;
                shift += 7;
                length++;
            }
            while ((group & 0x80) != 0);

            if (length > 1 && (group & 0x7F) == 0)
            {
                throw new CodecFormatException(
                    "NonMinimalVarint", start, "A varint must use its minimal form.");
            }

            if (value > int.MaxValue)
            {
                throw new CodecFormatException(
                    "VarintOverflow", start, "A varint value exceeds Int32.MaxValue.");
            }

            return (int)value;
        }

        internal string ReadString()
        {
            var start = position;
            var length = ReadVaruint();
            if (length > maxStringLength)
            {
                throw new CodecFormatException(
                    "OverBudget", start, "A string exceeds the caller's string budget.");
            }

            if (length > Remaining)
            {
                throw new CodecFormatException(
                    "Truncated", start, "A string length exceeds the remaining payload.");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(data, position, length);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CodecFormatException(
                    "InvalidUtf8", position, "A string body is not strict UTF-8: " + exception.Message);
            }

            position += length;
            return text;
        }

        internal bool ReadBool()
        {
            var start = position;
            var value = ReadByte();
            if (value > 0x01)
            {
                throw new CodecFormatException(
                    "InvalidBoolean", start, "A boolean must be 0x00 or 0x01.");
            }

            return value == 0x01;
        }

        internal bool ReadOption()
        {
            var start = position;
            var value = ReadByte();
            if (value > 0x01)
            {
                throw new CodecFormatException(
                    "InvalidOption", start, "An option discriminator must be 0x00 or 0x01.");
            }

            return value == 0x01;
        }

        internal long ReadInt64()
        {
            if (Remaining < 8)
            {
                throw new CodecFormatException(
                    "Truncated", position, "Unexpected end of payload inside a 64-bit value.");
            }

            var value = 0L;
            for (var i = 0; i < 8; i++)
            {
                value = (value << 8) | data[position++];
            }

            return value;
        }

        internal double ReadFloatBits()
        {
            var start = position;
            var value = BitConverter.Int64BitsToDouble(ReadInt64());
            if (double.IsNaN(value))
            {
                throw new CodecFormatException(
                    "NaNFloat", start, "NaN never appears in a canonical payload.");
            }

            return value;
        }

        /// <summary>Reads a list count and rejects counts the remaining bytes cannot satisfy.</summary>
        internal int ReadCount(int minimumItemBytes)
        {
            var start = position;
            var count = ReadVaruint();
            if (count > 0 && (long)count * minimumItemBytes > Remaining)
            {
                throw new CodecFormatException(
                    "Truncated", start, "A list count exceeds what the remaining payload can hold.");
            }

            return count;
        }

        internal void ExpectEnd()
        {
            if (Remaining != 0)
            {
                throw new CodecFormatException(
                    "TrailingBytes", position, "The payload carries bytes past the canonical content.");
            }
        }
    }
}
