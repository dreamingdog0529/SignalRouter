using System;
using System.Security.Cryptography;

namespace SignalRouter.Protocol
{
    // Host-only handshake authentication policy (design §19, ADR 0008). Holds the
    // expected 256-bit token as 32 bytes and verifies a presented lower-case hex
    // token against it. This is a REQUIRED host input, kept deliberately off the
    // shared ProtocolPeerOptions: a nullable auth field on the type both peers use
    // to declare themselves would turn a production wiring omission into a silent
    // auth-off. The comparison is fixed-time with respect to the secret; a missing,
    // wrong-length, or non-hex token is simply a mismatch (the malformed/auth
    // boundary and timing scope are specified in ADR 0008).
    public sealed class HostHelloAuthenticationPolicy
    {
        public const int TokenByteLength = 32;

        public const int TokenHexLength = TokenByteLength * 2;

        private readonly byte[] expected;

        public HostHelloAuthenticationPolicy(byte[] expectedToken)
        {
            if (expectedToken == null)
            {
                throw new ArgumentNullException(nameof(expectedToken));
            }

            if (expectedToken.Length != TokenByteLength)
            {
                throw new ArgumentException(
                    "The expected token must be exactly 32 bytes.",
                    nameof(expectedToken));
            }

            expected = (byte[])expectedToken.Clone();
        }

        public static HostHelloAuthenticationPolicy FromHex(string expectedHex)
        {
            if (expectedHex == null)
            {
                throw new ArgumentNullException(nameof(expectedHex));
            }

            var bytes = TryDecodeToken(expectedHex);
            if (bytes == null)
            {
                throw new ArgumentException(
                    "The expected token must be 64 lower-case hex characters.",
                    nameof(expectedHex));
            }

            return new HostHelloAuthenticationPolicy(bytes);
        }

        // True only when the presented token decodes to exactly the expected 32
        // bytes. A null, wrong-length, or non-hex token returns false without
        // revealing, through secret-dependent timing, how close it was.
        public bool Verify(string? presentedToken)
        {
            if (presentedToken == null)
            {
                return false;
            }

            var presented = TryDecodeToken(presentedToken);
            if (presented == null)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expected, presented);
        }

        // Decodes exactly 64 lower-case hex characters to 32 bytes; returns null on
        // any deviation (wrong length, uppercase, or non-hex). The token is defined
        // as lower-case hex, so uppercase is rejected rather than normalized.
        internal static byte[]? TryDecodeToken(string token)
        {
            if (token.Length != TokenHexLength)
            {
                return null;
            }

            var bytes = new byte[TokenByteLength];
            for (var index = 0; index < TokenByteLength; index++)
            {
                var high = DecodeNibble(token[index * 2]);
                var low = DecodeNibble(token[(index * 2) + 1]);
                if (high < 0 || low < 0)
                {
                    return null;
                }

                bytes[index] = (byte)((high << 4) | low);
            }

            return bytes;
        }

        private static int DecodeNibble(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'a' && value <= 'f')
            {
                return (value - 'a') + 10;
            }

            return -1;
        }
    }
}
