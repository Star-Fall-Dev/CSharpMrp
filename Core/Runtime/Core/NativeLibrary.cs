using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint nativeCrc = uint.MaxValue;

        private uint CompareNativeStrings(uint left, uint right, uint count)
        {
            for (uint index = 0; index < count; index++)
            {
                uint a = Memory.U8(left + index);
                uint b = Memory.U8(right + index);
                if (a != b)
                {
                    return a < b ? uint.MaxValue : 1;
                }
                if (a == 0)
                {
                    break;
                }
            }
            return 0;
        }

        private uint AppendBounded(uint destination, uint source, uint count)
        {
            uint end = destination;
            while (Memory.U8(end) != 0)
            {
                end++;
            }
            uint length = 0;
            while (length < count && Memory.U8(source + length) != 0)
            {
                length++;
            }
            Memory.Check(end, length + 1);
            if (length != 0)
            {
                Memory.Copy(end, source, length, true);
            }
            Memory.Put8(end + length, 0);
            return destination;
        }

        private uint FindByte(uint source, uint value, uint count)
        {
            if (count == 0)
            {
                return 0;
            }
            int index = Memory.ReadSpan(source, count).IndexOf((byte)value);
            return index < 0 ? 0 : source + (uint)index;
        }

        private uint ParseUnsigned(uint source, uint endPointer, uint radix)
        {
            if (radix != 0 && (radix < 2 || radix > 36))
            {
                throw new RuntimeFault("strtoul invalid base");
            }
            if (endPointer != 0)
            {
                Memory.Check(endPointer, 4);
            }
            uint value = ParseUnsigned(Memory, null, source, radix, out uint end);
            if (endPointer != 0)
            {
                Memory.Put32(endPointer, end);
            }
            return value;
        }

        private static uint ParseUnsigned(GuestMemory? memory, string? text, uint source, uint radix, out uint end)
        {
            uint Read(uint index) => memory != null ? memory.U8(index) :
                index < text!.Length ? text[(int)index] : 0u;
            uint cursor = source;
            uint character = Read(cursor);
            while (character == 32 || character >= 9 && character <= 13)
            {
                character = Read(++cursor);
            }
            bool negative = character == '-';
            if (negative || character == '+')
            {
                character = Read(++cursor);
            }
            if ((radix == 0 || radix == 16) && character == '0' &&
                (Read(cursor + 1) == 'x' || Read(cursor + 1) == 'X'))
            {
                radix = 16;
                cursor += 2;
                character = Read(cursor);
            }
            if (radix == 0)
            {
                radix = character == '0' ? 8u : 10u;
            }
            bool any = false;
            bool overflow = false;
            ulong result = 0;
            while (true)
            {
                uint digit = character >= '0' && character <= '9' ? character - '0' :
                    character >= 'a' && character <= 'z' ? character - 'a' + 10 :
                    character >= 'A' && character <= 'Z' ? character - 'A' + 10 : uint.MaxValue;
                if (digit >= radix)
                {
                    break;
                }
                any = true;
                if (!overflow)
                {
                    result = result * radix + digit;
                    overflow = result > uint.MaxValue;
                }
                character = Read(++cursor);
            }
            end = any ? cursor : source;
            return overflow ? uint.MaxValue : negative ? unchecked(0u - (uint)result) : (uint)result;
        }

        private uint WideByteLength(uint source)
        {
            if (source == 0)
            {
                return 0;
            }
            uint length = 0;
            while (Memory.U16(source + length) != 0)
            {
                length += 2;
            }
            return length;
        }

        private uint UpdateCrc(uint source, uint count)
        {
            if (source == 0)
            {
                nativeCrc = uint.MaxValue;
            }
            else if (count != 0)
            {
                nativeCrc = Crc32.Update(nativeCrc, Memory.ReadSpan(source, count));
            }
            return nativeCrc ^ uint.MaxValue;
        }

        private void AppendCrcByte(uint value)
        {
            nativeCrc = Crc32.Append(nativeCrc, (byte)value);
        }
    }
}
