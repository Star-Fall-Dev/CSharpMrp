using System;
using System.Buffers;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static readonly EncoderFallback UnmappedUcs2 = new EncoderReplacementFallback("?");

        private uint UnicodeToGbk(uint input, uint output, uint length)
        {
            uint destination = Memory.U32(output);
            if (destination == 0)
            {
                Memory.Check(length, 4);
                if ((ulong)output + 4 > length && (ulong)length + 4 > output)
                {
                    throw new RuntimeFault("Unicode to GBK output pointer overlaps length");
                }
            }
            Encoding encoder = RequireGbkEncoding("Unicode to GBK requires an explicit CP936 encoding service");
            ReadOnlySpan<byte> text = ReadUnicodeBytes(input);
            int capacity = text.Length + 1;
            byte[]? rented = null;
            Span<byte> bytes = capacity <= 512 ? stackalloc byte[capacity] :
                (rented = ArrayPool<byte>.Shared.Rent(capacity));
            Span<byte> encoded = stackalloc byte[2];
            Span<char> unit = stackalloc char[1];
            uint allocation = 0;
            uint allocationSize = 0;
            try
            {
                int count = 0;
                for (int offset = 0; offset < text.Length; offset += 2)
                {
                    char character = (char)(text[offset] << 8 | text[offset + 1]);
                    if (character < 128)
                    {
                        bytes[count++] = (byte)character;
                        continue;
                    }
                    // Non-ASCII UCS2 units require a two-byte mapping.
                    unit[0] = character;
                    if ((character < '\ue000' || character > '\uf8ff') && encoder.GetBytes(unit, encoded) == 2)
                    {
                        bytes[count++] = encoded[0];
                        bytes[count++] = encoded[1];
                    }
                    else
                    {
                        bytes[count++] = 0xa1;
                        bytes[count++] = 0xf4;
                    }
                }
                bytes[count++] = 0;
                var result = bytes.Slice(0, count);
                if (destination != 0)
                {
                    Memory.Write(destination, result);
                    return 0;
                }

                allocationSize = (uint)count;
                allocation = Memory.NativeAllocate(allocationSize);
                if (allocation != 0) Memory.Write(allocation, result);
                Memory.Put32(length, allocationSize);
                // Publish the pointer only once a guest reader can use the complete result.
                Memory.Put32(output, allocation);
                allocation = 0;
                return 0;
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                // Put32 stores the pointer before notifying write observers.
                if (allocation != 0 && Memory.U32(output) != allocation) Memory.NativeFree(allocation, allocationSize);
            }
        }
    }
}
