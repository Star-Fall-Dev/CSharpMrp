using System;
using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonEstimate(uint op)
        {
            if ((op & 0xffb30e10) != 0xf3b30400)
            {
                return false;
            }
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int source = (int)(op & 15 | op >> 1 & 16);
            bool quad = (op & 64) != 0;
            if ((op >> 18 & 3) != 2 || quad && ((destination | source) & 1) != 0)
            {
                throw new RuntimeFault("invalid NEON estimate encoding");
            }
            bool floating = (op & 256) != 0;
            bool root = (op & 128) != 0;
            NeonVector input = ReadNeon(source, quad);
            uint saved = Fpscr;
            Fpscr = saved & 0x0400009f | 0x03000000;
            try
            {
                for (int word = 0; word < (quad ? 2 : 1); word++)
                {
                    ulong result = 0;
                    for (int lane = 0; lane < 2; lane++)
                    {
                        uint raw = (uint)input.Lane(word * 2 + lane, 32);
                        result |= (ulong)NeonEstimateLane(raw, floating, root) << (lane * 32);
                    }
                    D[destination + word] = result;
                }
            }
            finally
            {
                Fpscr = saved & ~0x9fu | Fpscr & 0x9f;
            }
            return true;
        }

        private uint NeonEstimateLane(uint raw, bool floating, bool root)
        {
            if (!floating)
            {
                if (raw < (root ? 0x40000000u : 0x80000000u))
                {
                    return uint.MaxValue;
                }
                bool lowerInterval = raw < 0x80000000;
                uint estimate = root ? NeonRootFraction((int)(raw >> (lowerInterval ? 23 : 24)), lowerInterval)
                    : NeonReciprocalFraction((int)(raw >> 23));
                return estimate << 23;
            }
            FloatingValue value = UnpackFloating(raw, false);
            uint sign = raw & 0x80000000;
            if (value.NaN)
            {
                return (uint)QuietFloatingNaN(value, false);
            }
            if (value.Zero)
            {
                Fpscr |= 2;
                return sign | 0x7f800000;
            }
            if (root && value.Negative)
            {
                return (uint)InvalidFloating(false);
            }
            if (value.Infinity)
            {
                return sign;
            }
            int exponent = (int)(raw >> 23 & 255);
            if (!root && exponent >= 253)
            {
                Fpscr |= 8;
                return sign;
            }
            uint fraction = raw & 0x7fffff;
            uint estimated = root ? NeonRootFraction(128 + (int)(fraction >> 16), (exponent & 1) != 0)
                : NeonReciprocalFraction(256 + (int)(fraction >> 15));
            int resultExponent = root ? (380 - exponent) / 2 : 253 - exponent;
            return sign | (uint)resultExponent << 23 | (estimated - 256) << 15;
        }

        private static uint NeonReciprocalFraction(int bucket)
        {
            uint denominator = (uint)(2 * bucket + 1);
            return (524288 + denominator) / (2 * denominator);
        }

        private static uint NeonRootFraction(int bucket, bool lowerInterval)
        {
            ulong numerator = lowerInterval ? 67108864UL : 33554432UL;
            ulong denominator = (uint)(2 * bucket + 1);
            uint lower = 256;
            uint upper = 512;
            while (upper - lower > 1)
            {
                uint middle = (lower + upper) / 2;
                if ((ulong)middle * middle * denominator <= numerator)
                {
                    lower = middle;
                }
                else
                {
                    upper = middle;
                }
            }
            ulong midpoint = 2 * lower + 1;
            return numerator * 4 >= midpoint * midpoint * denominator ? lower + 1 : lower;
        }

        private uint NeonReciprocalStep(uint left, uint right, bool root)
        {
            FloatingValue a = UnpackFloating(left, false);
            FloatingValue b = UnpackFloating(right, false);
            if (a.NaN || b.NaN)
            {
                return (uint)QuietFloatingNaN(a.Signaling ? a : b.Signaling ? b : a.NaN ? a : b, false);
            }
            uint product = a.Infinity && b.Zero || a.Zero && b.Infinity ? 0 : (uint)FloatingBinary(left, right, false, 2);
            if (!root)
            {
                return (uint)FloatingBinary(0x40000000, product, false, 1);
            }
            FloatingValue value = UnpackFloating(product, false);
            if (value.Infinity)
            {
                return product ^ 0x80000000;
            }
            int scale = Math.Min(0, value.Scale);
            BigInteger scaledProduct = value.Significand << (value.Scale - scale);
            BigInteger difference = (new BigInteger(3) << -scale) - (value.Negative ? -scaledProduct : scaledProduct);
            return (uint)PackFloating(difference.Sign < 0, BigInteger.Abs(difference), BigInteger.One, scale - 1, false);
        }
    }
}
