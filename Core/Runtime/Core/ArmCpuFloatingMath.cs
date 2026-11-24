using System;
using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private readonly struct FloatingValue
        {
            internal readonly ulong Raw;
            internal readonly ulong Magnitude;
            internal readonly bool Negative, Infinity, NaN, Signaling;
            internal readonly BigInteger Significand;
            internal readonly int Scale;
            internal bool Zero => Magnitude == 0;

            internal FloatingValue(ulong raw, bool wide)
                : this(raw, wide ? 64 : 32, false)
            {
            }

            internal FloatingValue(ulong raw, int bits, bool alternativeHalf)
            {
                int fraction = bits == 64 ? 52 : bits == 32 ? 23 : 10;
                int bias = bits == 64 ? 1023 : bits == 32 ? 127 : 15;
                ulong sign = 1UL << (bits - 1);
                Raw = raw;
                Negative = (raw & sign) != 0;
                Magnitude = raw & (sign - 1);
                int exponent = (int)(Magnitude >> fraction);
                ulong mantissa = raw & ((1UL << fraction) - 1);
                Infinity = !alternativeHalf && exponent == bias * 2 + 1 && mantissa == 0;
                NaN = !alternativeHalf && exponent == bias * 2 + 1 && mantissa != 0;
                Signaling = NaN && (mantissa & (1UL << (fraction - 1))) == 0;
                Significand = exponent == 0 ? mantissa : mantissa | 1UL << fraction;
                Scale = (exponent == 0 ? 1 : exponent) - bias - fraction;
            }
        }

        private FloatingValue UnpackFloating(ulong raw, bool wide)
        {
            ulong sign = 1UL << (wide ? 63 : 31);
            ulong magnitude = raw & (sign - 1);
            if ((Fpscr & 0x01000000) != 0 && magnitude != 0 && magnitude < (1UL << (wide ? 52 : 23)))
            {
                Fpscr |= 128;
                raw &= sign;
            }
            return new FloatingValue(raw, wide);
        }

        private static ulong FloatingInfinity(bool wide)
        {
            return wide ? 0x7ff0000000000000UL : 0x7f800000;
        }

        private ulong InvalidFloating(bool wide)
        {
            Fpscr |= 1;
            return FloatingInfinity(wide) | (1UL << (wide ? 51 : 22));
        }

        private ulong QuietFloatingNaN(FloatingValue value, bool wide)
        {
            if (value.Signaling)
            {
                Fpscr |= 1;
            }
            ulong quiet = 1UL << (wide ? 51 : 22);
            return (Fpscr & 0x02000000) != 0 ? FloatingInfinity(wide) | quiet : value.Raw | quiet;
        }

        private static int IntegerBits(BigInteger value)
        {
            byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            int bits = (bytes.Length - 1) * 8;
            for (int high = bytes[0]; high != 0; high >>= 1)
            {
                bits++;
            }
            return bits;
        }

        // Exact rational times a power of two; no host floating-point rounding is involved.
        private ulong PackFloating(bool negative, BigInteger numerator, BigInteger denominator, int scale, bool wide, int rounding = -1, bool half = false)
        {
            ulong sign = negative ? 1UL << (half ? 15 : wide ? 63 : 31) : 0;
            if (numerator.IsZero)
            {
                return sign;
            }
            int fraction = half ? 10 : wide ? 52 : 23;
            int bias = half ? 15 : wide ? 1023 : 127;
            bool alternative = half && (Fpscr & 0x04000000) != 0;
            int ratioExponent = IntegerBits(numerator) - IntegerBits(denominator);
            if (ratioExponent >= 0 ? numerator < (denominator << ratioExponent) : (numerator << -ratioExponent) < denominator)
            {
                ratioExponent--;
            }
            int exponent = ratioExponent + scale;
            bool tiny = exponent < 1 - bias;
            if (tiny && !half && (Fpscr & 0x01000000) != 0)
            {
                Fpscr |= 8;
                return sign;
            }
            int quantum = Math.Max(exponent, 1 - bias) - fraction;
            int shift = scale - quantum;
            if (shift >= 0)
            {
                numerator <<= shift;
            }
            else
            {
                denominator <<= -shift;
            }
            BigInteger rounded = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
            int mode = rounding < 0 ? (int)(Fpscr >> 22 & 3) : rounding;
            bool inexact = !remainder.IsZero;
            bool increment = mode == 0 ? remainder * 2 > denominator || remainder * 2 == denominator && !rounded.IsEven :
                inexact && (mode == 1 && !negative || mode == 2 && negative);
            if (increment)
            {
                rounded++;
            }
            if (tiny && inexact)
            {
                Fpscr |= 8;
            }
            int encodedExponent = tiny ? (rounded >= (BigInteger.One << fraction) ? 1 : 0) : exponent + bias;
            if (rounded >= (BigInteger.One << (fraction + 1)))
            {
                rounded >>= 1;
                encodedExponent++;
            }
            if (encodedExponent >= bias * 2 + (alternative ? 2 : 1))
            {
                if (alternative)
                {
                    Fpscr |= 1;
                    return sign | 0x7fff;
                }
                Fpscr |= 20;
                bool infinity = mode == 0 || mode == 1 && !negative || mode == 2 && negative;
                ulong infinityBits = (ulong)(bias * 2 + 1) << fraction;
                return sign | (infinity ? infinityBits : infinityBits - 1);
            }
            if (inexact)
            {
                Fpscr |= 16;
            }
            return sign | (ulong)encodedExponent << fraction | (ulong)(rounded & ((BigInteger.One << fraction) - 1));
        }

        private ulong FloatingBinary(ulong left, ulong right, bool wide, int operation)
        {
            FloatingValue a = UnpackFloating(left, wide);
            FloatingValue b = UnpackFloating(right, wide);
            if (a.NaN || b.NaN)
            {
                return QuietFloatingNaN(a.Signaling ? a : b.Signaling ? b : a.NaN ? a : b, wide);
            }
            ulong signBit = 1UL << (wide ? 63 : 31);
            bool bNegative = b.Negative ^ (operation == 1);
            bool negative = a.Negative ^ b.Negative;
            ulong sign = negative ? signBit : 0;
            if (operation <= 1)
            {
                if (a.Infinity || b.Infinity)
                {
                    if (a.Infinity && b.Infinity && a.Negative != bNegative)
                    {
                        return InvalidFloating(wide);
                    }
                    return FloatingInfinity(wide) | ((a.Infinity ? a.Negative : bNegative) ? signBit : 0);
                }
                if (a.Zero && b.Zero && a.Negative == bNegative)
                {
                    return a.Negative ? signBit : 0;
                }
                int scale = Math.Min(a.Scale, b.Scale);
                BigInteger first = a.Significand << (a.Scale - scale);
                BigInteger second = b.Significand << (b.Scale - scale);
                BigInteger sum = (a.Negative ? -first : first) + (bNegative ? -second : second);
                return PackFloating(sum.IsZero ? (Fpscr >> 22 & 3) == 2 : sum.Sign < 0, BigInteger.Abs(sum), BigInteger.One, scale, wide);
            }
            if (operation == 2)
            {
                if (a.Infinity && b.Zero || b.Infinity && a.Zero)
                {
                    return InvalidFloating(wide);
                }
                if (a.Infinity || b.Infinity)
                {
                    return sign | FloatingInfinity(wide);
                }
                return PackFloating(negative, a.Significand * b.Significand, BigInteger.One, a.Scale + b.Scale, wide);
            }
            if (a.Infinity && b.Infinity || a.Zero && b.Zero)
            {
                return InvalidFloating(wide);
            }
            if (a.Infinity || b.Zero)
            {
                if (!a.Infinity)
                {
                    Fpscr |= 2;
                }
                return sign | FloatingInfinity(wide);
            }
            if (a.Zero || b.Infinity)
            {
                return sign;
            }
            return PackFloating(negative, a.Significand, b.Significand, a.Scale - b.Scale, wide);
        }

        private ulong SquareRootFloating(ulong raw, bool wide)
        {
            FloatingValue value = UnpackFloating(raw, wide);
            if (value.NaN)
            {
                return QuietFloatingNaN(value, wide);
            }
            if (value.Zero || value.Infinity && !value.Negative)
            {
                return value.Raw;
            }
            if (value.Negative)
            {
                return InvalidFloating(wide);
            }
            int exponent = (IntegerBits(value.Significand) - 1 + value.Scale) >> 1;
            int quantum = exponent - (wide ? 52 : 23);
            BigInteger squared = value.Significand << (value.Scale - 2 * quantum);
            BigInteger root = BigInteger.One << ((IntegerBits(squared) + 1) / 2);
            while (true)
            {
                BigInteger next = (root + squared / root) >> 1;
                if (next >= root)
                {
                    break;
                }
                root = next;
            }
            if (root * root != squared)
            {
                Fpscr |= 16;
                int mode = (int)(Fpscr >> 22 & 3);
                BigInteger midpoint = root * 2 + 1;
                if (mode == 1 || mode == 0 && squared * 4 > midpoint * midpoint)
                {
                    root++;
                }
            }
            // The smallest nonzero square root is normal; root is now exactly representable.
            return PackFloating(false, root, BigInteger.One, quantum, wide);
        }

        private ulong FusedFloating(ulong accumulator, ulong left, ulong right, bool wide)
        {
            FloatingValue c = UnpackFloating(accumulator, wide);
            FloatingValue a = UnpackFloating(left, wide);
            FloatingValue b = UnpackFloating(right, wide);
            bool invalidProduct = a.Infinity && b.Zero || b.Infinity && a.Zero;
            if (c.NaN || a.NaN || b.NaN)
            {
                ulong result = QuietFloatingNaN(c.Signaling ? c : a.Signaling ? a : b.Signaling ? b : c.NaN ? c : a.NaN ? a : b, wide);
                return c.NaN && !c.Signaling && invalidProduct ? InvalidFloating(wide) : result;
            }
            bool productNegative = a.Negative ^ b.Negative;
            bool productInfinity = a.Infinity || b.Infinity;
            ulong signBit = 1UL << (wide ? 63 : 31);
            if (invalidProduct || c.Infinity && productInfinity && c.Negative != productNegative)
            {
                return InvalidFloating(wide);
            }
            if (c.Infinity || productInfinity)
            {
                return FloatingInfinity(wide) | ((c.Infinity ? c.Negative : productNegative) ? signBit : 0);
            }
            if (c.Zero && (a.Zero || b.Zero) && c.Negative == productNegative)
            {
                return c.Negative ? signBit : 0;
            }
            int productScale = a.Scale + b.Scale;
            int scale = Math.Min(c.Scale, productScale);
            BigInteger product = a.Significand * b.Significand << (productScale - scale);
            BigInteger addend = c.Significand << (c.Scale - scale);
            BigInteger sum = (productNegative ? -product : product) + (c.Negative ? -addend : addend);
            return PackFloating(sum.IsZero ? (Fpscr >> 22 & 3) == 2 : sum.Sign < 0, BigInteger.Abs(sum), BigInteger.One, scale, wide);
        }

        private void CompareFloating(ulong left, ulong right, bool wide, bool signalAllNaNs)
        {
            FloatingValue a = UnpackFloating(left, wide);
            FloatingValue b = UnpackFloating(right, wide);
            uint flags;
            if (a.NaN || b.NaN)
            {
                flags = 0x30000000;
                if (signalAllNaNs || a.Signaling || b.Signaling)
                {
                    Fpscr |= 1;
                }
            }
            else if (a.Raw == b.Raw || a.Zero && b.Zero)
            {
                flags = 0x60000000;
            }
            else
            {
                bool less = a.Negative != b.Negative ? a.Negative : a.Negative ? a.Magnitude > b.Magnitude : a.Magnitude < b.Magnitude;
                flags = less ? 0x80000000u : 0x20000000u;
            }
            Fpscr = Fpscr & 0x0fffffff | flags;
        }
    }
}
