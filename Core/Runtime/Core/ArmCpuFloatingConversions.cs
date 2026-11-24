using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private ulong FloatingToFixed(ulong raw, bool wide, int bits, int fractionalBits, bool unsigned, bool truncate)
        {
            FloatingValue value = UnpackFloating(raw, wide);
            if (value.NaN)
            {
                Fpscr |= 1;
                return 0;
            }
            BigInteger minimum = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
            BigInteger maximum = (BigInteger.One << (unsigned ? bits : bits - 1)) - 1;
            BigInteger result;
            bool inexact = false;
            if (value.Infinity)
            {
                result = value.Negative ? minimum - 1 : maximum + 1;
            }
            else
            {
                int scale = value.Scale + fractionalBits;
                BigInteger denominator = scale < 0 ? BigInteger.One << -scale : BigInteger.One;
                BigInteger numerator = scale > 0 ? value.Significand << scale : value.Significand;
                result = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
                inexact = !remainder.IsZero;
                int mode = truncate ? 3 : (int)(Fpscr >> 22 & 3);
                bool increment = mode == 0 ? remainder * 2 > denominator || remainder * 2 == denominator && !result.IsEven :
                    inexact && (mode == 1 && !value.Negative || mode == 2 && value.Negative);
                if (increment)
                {
                    result++;
                }
                if (value.Negative)
                {
                    result = -result;
                }
            }
            if (result < minimum || result > maximum)
            {
                Fpscr |= 1;
                result = result < minimum ? minimum : maximum;
            }
            else if (inexact)
            {
                Fpscr |= 16;
            }
            return unchecked((ulong)(long)result);
        }

        private ulong FixedToFloating(ulong raw, bool wide, int bits, int fractionalBits, bool unsigned, bool nearest)
        {
            ulong mask = (1UL << bits) - 1;
            long value = (long)(raw & mask);
            if (!unsigned && (raw & (1UL << (bits - 1))) != 0)
            {
                value -= 1L << bits;
            }
            return PackFloating(value < 0, BigInteger.Abs(value), BigInteger.One, -fractionalBits, wide, nearest ? 0 : -1);
        }

        private ulong ConvertFloatingPrecision(ulong raw, bool fromWide)
        {
            FloatingValue value = UnpackFloating(raw, fromWide);
            bool wide = !fromWide;
            ulong sign = value.Negative ? 1UL << (wide ? 63 : 31) : 0;
            if (value.NaN)
            {
                ulong quiet = QuietFloatingNaN(value, fromWide);
                ulong payload = quiet & ((1UL << (fromWide ? 52 : 23)) - 1);
                return ((Fpscr & 0x02000000) != 0 ? 0 : sign) | FloatingInfinity(wide) |
                    (fromWide ? payload >> 29 : payload << 29);
            }
            if (value.Infinity)
            {
                return sign | FloatingInfinity(wide);
            }
            return PackFloating(value.Negative, value.Significand, BigInteger.One, value.Scale, wide);
        }

        private bool ExecuteFloatingConversion(uint op, int destination, int source, bool wide)
        {
            if ((op & 0x0fbe0f50) == 0x0eb20a40)
            {
                int shift = (op & 128) == 0 ? 0 : 16;
                ulong value = ReadFloating(source, false);
                if ((op & 0x00010000) == 0)
                {
                    value = HalfToFloating((ushort)(value >> shift));
                }
                else
                {
                    value = ReadFloating(destination, false) & ~(0xffffUL << shift) | (ulong)FloatingToHalf((uint)value) << shift;
                }
                WriteFloating(destination, false, value);
                return true;
            }
            if ((op & 0x0fbf0ed0) == 0x0eb70ac0)
            {
                int target = wide ? (int)(op >> 11 & 30 | op >> 22 & 1) : (int)(op >> 12 & 15 | op >> 18 & 16);
                WriteFloating(target, !wide, ConvertFloatingPrecision(ReadFloating(source, wide), wide));
                return true;
            }
            if ((op & 0x0fba0e50) == 0x0eba0a40)
            {
                int bits = (op & 128) != 0 ? 32 : 16;
                int fractionalBits = bits - (int)((op & 15) * 2 + (op >> 5 & 1));
                if (fractionalBits < 0)
                {
                    throw new RuntimeFault("invalid VFP fixed-point scale");
                }
                bool unsigned = (op & 0x00010000) != 0;
                ulong value = ReadFloating(destination, wide);
                value = (op & 0x00040000) != 0 ? FloatingToFixed(value, wide, bits, fractionalBits, unsigned, true) :
                    FixedToFloating(value, wide, bits, fractionalBits, unsigned, true);
                WriteFloating(destination, wide, value);
                return true;
            }
            if ((op & 0x0fb80e50) == 0x0eb80a40)
            {
                int kind = (int)(op >> 16 & 7);
                if (kind == 0)
                {
                    int single = (int)((op & 15) * 2 + (op >> 5 & 1));
                    WriteFloating(destination, wide, FixedToFloating(ReadFloating(single, false), wide, 32, 0, (op & 128) == 0, false));
                    return true;
                }
                if (kind == 4 || kind == 5)
                {
                    int single = (int)(op >> 11 & 30 | op >> 22 & 1);
                    WriteFloating(single, false, FloatingToFixed(ReadFloating(source, wide), wide, 32, 0, kind == 4, (op & 128) != 0));
                    return true;
                }
            }
            return false;
        }

        private uint HalfToFloating(ushort raw)
        {
            var value = new FloatingValue(raw, 16, (Fpscr & 0x04000000) != 0);
            uint sign = value.Negative ? 0x80000000u : 0;
            if (value.NaN)
            {
                if (value.Signaling)
                {
                    Fpscr |= 1;
                }
                return (Fpscr & 0x02000000) != 0 ? 0x7fc00000 : sign | 0x7fc00000 | (uint)(raw & 511) << 13;
            }
            if (value.Infinity)
            {
                return sign | 0x7f800000;
            }
            return (uint)PackFloating(value.Negative, value.Significand, BigInteger.One, value.Scale, false);
        }

        private ushort FloatingToHalf(uint raw)
        {
            FloatingValue value = UnpackFloating(raw, false);
            uint sign = value.Negative ? 0x8000u : 0;
            bool alternative = (Fpscr & 0x04000000) != 0;
            if (value.NaN)
            {
                if (value.Signaling || alternative)
                {
                    Fpscr |= 1;
                }
                return (ushort)(alternative ? sign : (Fpscr & 0x02000000) != 0 ? 0x7e00 : sign | 0x7e00 | (raw >> 13 & 511));
            }
            if (value.Infinity)
            {
                if (alternative)
                {
                    Fpscr |= 1;
                }
                return (ushort)(sign | (alternative ? 0x7fffu : 0x7c00u));
            }
            return (ushort)PackFloating(value.Negative, value.Significand, BigInteger.One, value.Scale, false, half: true);
        }
    }
}
