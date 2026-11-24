using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        [Flags]
        private enum PrintfFlags
        {
            None = 0,
            Left = 1,
            Plus = 2,
            Space = 4,
            Alternate = 8,
            Zero = 16
        }

        private static PrintfFlags ReadPrintfFlags(string format, ref int cursor)
        {
            PrintfFlags flags = PrintfFlags.None;
            while (cursor < format.Length)
            {
                PrintfFlags flag = format[cursor] switch
                {
                    '-' => PrintfFlags.Left,
                    '+' => PrintfFlags.Plus,
                    ' ' => PrintfFlags.Space,
                    '#' => PrintfFlags.Alternate,
                    '0' => PrintfFlags.Zero,
                    _ => PrintfFlags.None
                };
                if (flag == PrintfFlags.None) break;
                flags |= flag;
                cursor++;
            }
            return flags;
        }

        private uint Sprintf(uint destination, uint source, ArmCpu cpu)
        {
            StringBuilder result = Printf(Memory.CString(source), cpu.ReadArgument, 2, floatingPoint: true);
            int length = result.Length;
            const int poolLimit = 64 * 1024;
            char[]? characterRental = null;
            byte[]? byteRental = null;
            try
            {
                Span<char> characters = length <= 256 ? stackalloc char[length] :
                    length <= poolLimit / sizeof(char) ? (characterRental = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length) :
                    new char[length];
                result.CopyTo(0, characters, length);
                int size = checked(ScriptChunk.Bytes.GetByteCount(characters) + 1);
                Span<byte> bytes = size <= 512 ? stackalloc byte[size] :
                    size <= poolLimit ? (byteRental = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size) : new byte[size];
                int written = ScriptChunk.Bytes.GetBytes(characters, bytes);
                bytes[written] = 0;
                Memory.Write(destination, bytes.Slice(0, written + 1));
                return (uint)length;
            }
            finally
            {
                if (byteRental != null) ArrayPool<byte>.Shared.Return(byteRental, clearArray: true);
                if (characterRental != null) ArrayPool<char>.Shared.Return(characterRental, clearArray: true);
            }
        }

        private StringBuilder Printf(string format, Func<int, uint> argument, int next, bool diagnosticStrings = false,
            bool floatingPoint = false)
        {
            var result = new StringBuilder();
            int cursor = 0;
            int Field()
            {
                int value = 0;
                while (cursor < format.Length && format[cursor] >= '0' && format[cursor] <= '9')
                {
                    value = value * 10 + format[cursor++] - '0';
                    if (value > 1024)
                    {
                        throw new RuntimeFault("printf width/precision limit");
                    }
                }
                return value;
            }
            while (cursor < format.Length)
            {
                if (format[cursor++] != '%')
                {
                    result.Append(format[cursor - 1]);
                    continue;
                }
                if (cursor == format.Length)
                {
                    // A trailing percent reaches the reference formatter's NUL output.
                    // Keep the formatted prefix without reading past the format string.
                    break;
                }
                PrintfFlags flags = ReadPrintfFlags(format, ref cursor);
                long width = Field();
                if (cursor < format.Length && format[cursor] == '*')
                {
                    cursor++;
                    width = (int)argument(next++);
                    if (width < 0) flags |= PrintfFlags.Left;
                    width = Math.Abs(width);
                }
                int precision = -1;
                if (cursor < format.Length && format[cursor] == '.')
                {
                    cursor++;
                    precision = Field();
                    if (cursor < format.Length && format[cursor] == '*')
                    {
                        cursor++;
                        // The reference clamps a negative dynamic precision to zero.
                        precision = Math.Max(0, (int)argument(next++));
                    }
                }
                if (width > 1024 || precision > 1024)
                {
                    throw new RuntimeFault("printf width/precision limit");
                }
                string length = "";
                if (cursor < format.Length && "hljz".IndexOf(format[cursor]) >= 0)
                {
                    length = format[cursor++].ToString();
                    if (cursor < format.Length && (length == "h" || length == "l") && format[cursor] == length[0])
                    {
                        length += format[cursor++];
                    }
                }
                if (cursor == format.Length)
                {
                    throw new RuntimeFault("truncated printf format");
                }
                char conversion = format[cursor++];
                string text;
                if ("diuxXobp".IndexOf(conversion) >= 0)
                {
                    bool wide = conversion != 'p' && (length == "ll" || length == "j");
                    if (wide)
                    {
                        // AAPCS32: align a double-word argument to an even register/stack word.
                        next = (next + 1) & ~1;
                    }
                    ulong bits = argument(next++);
                    if (wide)
                    {
                        bits |= (ulong)argument(next++) << 32;
                    }
                    long signedValue = wide ? unchecked((long)bits) : length == "hh" ? (byte)bits :
                        length == "h" ? (short)bits : (int)bits;
                    ulong unsignedValue = length == "hh" ? (byte)bits : length == "h" ? (ushort)bits : bits;
                    AppendPrintfInteger(result, signedValue, unsignedValue, conversion, (int)width, precision, flags);
                    if (result.Length > 4096)
                    {
                        throw new RuntimeFault("printf output limit");
                    }
                    continue;
                }
                else if (floatingPoint && "fFeEgG".IndexOf(conversion) >= 0)
                {
                    next = (next + 1) & ~1;
                    ulong bits = argument(next++);
                    bits |= (ulong)argument(next++) << 32;
                    double number = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                    int digits = precision < 0 ? 6 : precision;
                    if ((conversion == 'g' || conversion == 'G') && digits == 0)
                    {
                        digits = 1;
                    }
                    text = number.ToString(conversion.ToString() + digits, CultureInfo.InvariantCulture);
                    if (number >= 0 && (flags & (PrintfFlags.Plus | PrintfFlags.Space)) != 0)
                    {
                        text = ((flags & PrintfFlags.Plus) != 0 ? "+" : " ") + text;
                    }
                }
                else if (conversion == 's')
                {
                    uint pointer = argument(next++);
                    if (diagnosticStrings && precision != 0 &&
                        (pointer < 0x1000 || pointer >= Memory.MappedEnd))
                    {
                        text = $"<invalid-string:0x{pointer:x8}>";
                        Record($"compatibility printf diagnostic: invalid string pointer 0x{pointer:x8}; " +
                            "no dereference, format logging continues");
                        result.Append(text);
                        continue;
                    }
                    AppendPrintfString(result, pointer, (int)width, precision, (flags & PrintfFlags.Left) != 0);
                    continue;
                }
                else if (conversion == 'c')
                {
                    text = ((char)(argument(next++) & 255)).ToString();
                }
                else
                {
                    // Reference printf.h disables f/e/g/t; unknown specifiers emit their letter
                    // without consuming a value. In particular, %n never writes guest memory.
                    text = conversion.ToString();
                    width = 0;
                }
                result.Append((flags & PrintfFlags.Left) != 0 ? text.PadRight((int)width) : text.PadLeft((int)width));
                if (result.Length > 4096)
                {
                    throw new RuntimeFault("printf output limit");
                }
            }
            return result;
        }

        private void AppendPrintfString(StringBuilder result, uint pointer, int width, int precision, bool left)
        {
            ReadOnlySpan<byte> bytes = ReadPrintfString(pointer, precision);
            int padding = Math.Max(0, width - bytes.Length);
            int length = result.Length + bytes.Length + padding;
            if (length > 4096) throw new RuntimeFault("printf output limit");
            result.EnsureCapacity(length);
            if (!left) result.Append(' ', padding);
            foreach (byte character in bytes) result.Append((char)character);
            if (left) result.Append(' ', padding);
        }

        private ReadOnlySpan<byte> ReadPrintfString(uint pointer, int precision)
        {
            if (precision == 0) return ReadOnlySpan<byte>.Empty;
            // Read one byte past the maximum printable length to distinguish a terminator from overflow.
            uint limit = precision < 0 ? 4097u : (uint)precision;
            Memory.Check(pointer, 1);
            var bytes = Memory.ReadSpan(pointer, Math.Min(limit, Memory.MappedEnd - pointer));
            int end = bytes.IndexOf((byte)0);
            if (end >= 0) return bytes.Slice(0, end);
            if (bytes.Length > 4096) throw new RuntimeFault("printf output limit");
            if (bytes.Length < limit) Memory.Check(pointer + (uint)bytes.Length, 1);
            return bytes;
        }

        private static void AppendPrintfInteger(StringBuilder result, long signedValue, ulong unsignedValue,
            char conversion, int width, int precision, PrintfFlags flags)
        {
            bool signed = conversion == 'd' || conversion == 'i';
            bool negative = signed && signedValue < 0;
            ulong magnitude = signed ? negative ? unchecked(0UL - (ulong)signedValue) : (ulong)signedValue : unsignedValue;
            bool pointer = conversion == 'p';
            bool upper = conversion == 'X' || pointer;
            int radix = conversion == 'o' ? 8 : conversion == 'b' ? 2 :
                conversion == 'x' || conversion == 'X' || pointer ? 16 : 10;

            const int digitLimit = 32;
            Span<char> buffer = stackalloc char[digitLimit];
            ReadOnlySpan<char> digits = buffer;
            if (magnitude == 0 && precision >= 0)
            {
                digits = ReadOnlySpan<char>.Empty;
            }
            else if (radix == 10)
            {
                magnitude.TryFormat(buffer, out int written, provider: CultureInfo.InvariantCulture);
                digits = buffer.Slice(0, written);
            }
            else
            {
                string alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";
                ulong value = magnitude;
                int start = buffer.Length;
                do
                {
                    buffer[--start] = alphabet[(int)(value % (uint)radix)];
                    value /= (uint)radix;
                }
                while (value != 0 && start != 0);
                digits = buffer.Slice(start);
            }

            char sign = negative ? '-' : !signed && !pointer ? '\0' :
                (flags & PrintfFlags.Plus) != 0 ? '+' : (flags & PrintfFlags.Space) != 0 ? ' ' : '\0';
            bool left = (flags & PrintfFlags.Left) != 0;
            bool zero = pointer || ((flags & PrintfFlags.Zero) != 0 && precision < 0);
            if (pointer) width = 8;
            int leadingZeroes = 0;
            if (!left)
            {
                if (zero && width != 0 && sign != '\0') width--;
                int minimum = Math.Max(precision, zero ? width : 0);
                leadingZeroes = Math.Max(0, Math.Min(digitLimit, minimum) - digits.Length);
            }

            string prefix = "";
            if ((flags & PrintfFlags.Alternate) != 0 && magnitude != 0 && radix != 10)
            {
                int digitCount = digits.Length + leadingZeroes;
                // The reference replaces the leading digits with the prefix when they fill the field.
                if (precision < 0 && digitCount != 0 && digitCount == width)
                {
                    int skip = Math.Min(digitCount, radix == 16 ? 2 : 1);
                    int removedZeroes = Math.Min(leadingZeroes, skip);
                    leadingZeroes -= removedZeroes;
                    digits = digits.Slice(skip - removedZeroes);
                }
                prefix = radix == 16 ? upper ? "0X" : "0x" : radix == 2 ? "0b" : "0";
            }
            int prefixLength = Math.Min(prefix.Length, digitLimit - digits.Length - leadingZeroes);
            int length = digits.Length + leadingZeroes + prefixLength;
            bool writeSign = sign != '\0' && length < digitLimit;
            if (writeSign) length++;
            int padding = zero && !left ? 0 : Math.Max(0, width - length);
            if (!left) result.Append(' ', padding);
            if (writeSign) result.Append(sign);
            result.Append(prefix, prefix.Length - prefixLength, prefixLength);
            result.Append('0', leadingZeroes);
            result.Append(digits);
            if (left) result.Append(' ', padding);
        }
    }
}
