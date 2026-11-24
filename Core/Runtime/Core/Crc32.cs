using System;
using System.Buffers.Binary;

namespace CSharpMrp
{
    // Reflected IEEE CRC used by Mythroad and ZIP/gzip; callers own the
    // initial/final complement so incremental native updates stay compatible.
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[8 * 256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                    value = value >> 1 ^ ((value & 1) != 0 ? 0xedb88320u : 0);
                table[index] = value;
            }
            // Each later slice advances the previous CRC by one zero byte.
            for (int index = 256; index < table.Length; index++)
            {
                uint value = table[index - 256];
                table[index] = value >> 8 ^ table[(byte)value];
            }
            return table;
        }

        internal static uint Append(uint crc, byte value) => crc >> 8 ^ Table[(byte)(crc ^ value)];

        internal static uint Update(uint crc, ReadOnlySpan<byte> bytes)
        {
            while (bytes.Length >= 8)
            {
                ulong block = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ crc;
                crc = Table[1792 + (byte)block] ^ Table[1536 + (byte)(block >> 8)] ^
                    Table[1280 + (byte)(block >> 16)] ^ Table[1024 + (byte)(block >> 24)] ^
                    Table[768 + (byte)(block >> 32)] ^ Table[512 + (byte)(block >> 40)] ^
                    Table[256 + (byte)(block >> 48)] ^ Table[(byte)(block >> 56)];
                bytes = bytes.Slice(8);
            }
            if (bytes.Length >= 4)
            {
                uint word = crc ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                crc = Table[768 + (byte)word] ^ Table[512 + (byte)(word >> 8)] ^
                    Table[256 + (byte)(word >> 16)] ^ Table[word >> 24];
                bytes = bytes.Slice(4);
            }
            foreach (byte value in bytes) crc = Append(crc, value);
            return crc;
        }
    }
}
