using System;

namespace SignalRouter.Codec.Recording
{
    /// <summary>
    /// Software CRC32C (Castagnoli, reflected polynomial 0x82F63B78). The commit
    /// marker's only job is torn-write detection (ADR 0016) — tamper evidence
    /// belongs to ContentId verification and reader-recomputed closure — so a
    /// table-driven software implementation is exactly enough and keeps the leaf
    /// dependency-free (netstandard2.1 has no BCL CRC).
    /// </summary>
    internal static class Crc32C
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
                }

                table[i] = crc;
            }

            return table;
        }
    }
}
