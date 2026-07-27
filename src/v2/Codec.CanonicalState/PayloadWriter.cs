using System;
using System.Buffers;
using System.Text;

namespace SignalRouter.V2.Codec.CanonicalState
{
    /// <summary>
    /// Emits canonical representation v1 primitives (ADR 0012): minimal-form LEB128
    /// varints, length-framed strict UTF-8 strings, fixed 8-byte big-endian 64-bit
    /// values, and IEEE-754 bit patterns. Deterministic by construction — every
    /// write is a pure function of its argument.
    ///
    /// Staging is a short-lived <see cref="ArrayPool{T}"/> rental returned in
    /// <see cref="Dispose"/> (ADR 0013: pooled staging, owned results): strings
    /// encode directly into the staging span — no per-string intermediate array —
    /// and the only allocation an encode performs is its exact-sized owned
    /// result. The codec stays stateless and thread-safe: every encode uses its
    /// own writer, and rentals never outlive the call.
    /// </summary>
    internal struct PayloadWriter : IDisposable
    {
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private byte[] buffer;
        private int written;

        internal static PayloadWriter Rent()
        {
            return new PayloadWriter
            {
                buffer = ArrayPool<byte>.Shared.Rent(4096),
                written = 0,
            };
        }

        /// <summary>The staged canonical bytes; valid until the next write or dispose.</summary>
        internal ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

        public void Dispose()
        {
            var rented = buffer;
            var used = written;
            buffer = null!;
            written = 0;
            if (rented != null)
            {
                // Canonical payloads are post-redaction but domain-scoped
                // (observation-state.md §5): the written range is cleared before
                // the buffer re-enters the process-wide pool, so no later renter
                // can read another domain's canonical bytes (ADR 0013).
                Array.Clear(rented, 0, used);
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void Ensure(int additional)
        {
            // checked: a wrapped size must fail loudly, never skip growth.
            var required = checked(written + additional);
            if (required <= buffer.Length)
            {
                return;
            }

            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
            buffer.AsSpan(0, written).CopyTo(grown);
            Array.Clear(buffer, 0, written); // domain-scoped bytes never re-enter the pool readable
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = grown;
        }

        internal void WriteRaw(byte value)
        {
            Ensure(1);
            buffer[written++] = value;
        }

        internal void WriteMagic()
        {
            Ensure(4);
            buffer[written++] = 0x53;
            buffer[written++] = 0x52;
            buffer[written++] = 0x43;
            buffer[written++] = 0x53;
        }

        internal void WriteVaruint(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Ensure(5);
            var remaining = (uint)value;
            do
            {
                var group = (byte)(remaining & 0x7F);
                remaining >>= 7;
                if (remaining != 0)
                {
                    group |= 0x80;
                }

                buffer[written++] = group;
            }
            while (remaining != 0);
        }

        internal void WriteString(string text)
        {
            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(text);
                WriteVaruint(byteCount);
                Ensure(byteCount);
                StrictUtf8.GetBytes(text.AsSpan(), buffer.AsSpan(written));
            }
            catch (EncoderFallbackException exception)
            {
                // The Contracts layer rejects unpaired surrogates at construction;
                // reaching this indicates an un-hardened input.
                throw new ArgumentException(
                    "Canonical text must be a well-formed Unicode scalar sequence.", exception);
            }

            written += byteCount;
        }

        internal void WriteBool(bool value)
        {
            Ensure(1);
            buffer[written++] = value ? (byte)0x01 : (byte)0x00;
        }

        internal void WriteInt64(long value)
        {
            Ensure(8);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                buffer[written++] = (byte)(value >> shift);
            }
        }

        internal void WriteFloatBits(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

        /// <summary>The owned, exact-sized result — the encode's one intended allocation.</summary>
        internal byte[] ToArray() => buffer.AsSpan(0, written).ToArray();
    }
}
