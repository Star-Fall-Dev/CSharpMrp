using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonLong(uint op)
        {
            if ((op & 0xfe800050) != 0xf2800000 || (op >> 20 & 3) == 3)
            {
                return false;
            }
            int operation = (int)(op >> 8 & 15);
            int size = (int)(op >> 20 & 3);
            bool unsigned = (op & 0x01000000) != 0;
            bool narrow = operation == 4 || operation == 6;
            bool wide = operation == 1 || operation == 3;
            bool doubling = operation == 9 || operation == 11 || operation == 13;
            if (operation == 15 || doubling && (unsigned || size == 0)
                || operation == 14 && (unsigned || size != 0))
            {
                throw new RuntimeFault("invalid NEON long operation or element size");
            }
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int first = (int)(op >> 16 & 15 | op >> 3 & 16);
            int second = (int)(op & 15 | op >> 1 & 16);
            if ((!narrow && (destination & 1) != 0) || ((narrow || wide) && (first & 1) != 0)
                || (narrow && (second & 1) != 0))
            {
                throw new RuntimeFault("NEON long Q register requires an even D register index");
            }
            NeonVector a = ReadNeon(first, narrow || wide);
            NeonVector b = ReadNeon(second, narrow);
            NeonVector previous = ReadNeon(destination, !narrow);
            int bits = 8 << size;
            int outputBits = narrow ? bits : bits * 2;
            ulong mask = outputBits == 64 ? ulong.MaxValue : (1UL << outputBits) - 1;
            ulong lower = 0;
            ulong upper = 0;
            for (int element = 0; element < 64 / bits; element++)
            {
                int firstBits = narrow || wide ? bits * 2 : bits;
                int secondBits = narrow ? bits * 2 : bits;
                BigInteger x = NeonInteger(a.Lane(element, firstBits), firstBits, narrow || unsigned);
                BigInteger y = NeonInteger(b.Lane(element, secondBits), secondBits, narrow || unsigned);
                BigInteger old = NeonInteger(previous.Lane(element, outputBits), outputBits, unsigned);
                BigInteger value;
                if (narrow)
                {
                    value = operation == 4 ? x + y : x - y;
                    value = (value + (unsigned ? BigInteger.One << (bits - 1) : BigInteger.Zero)) >> bits;
                }
                else if (operation <= 3)
                {
                    value = operation < 2 ? x + y : x - y;
                }
                else if (operation == 5 || operation == 7)
                {
                    value = BigInteger.Abs(x - y) + (operation == 5 ? old : BigInteger.Zero);
                }
                else if (doubling)
                {
                    BigInteger product = SaturateNeon(2 * x * y, outputBits, false);
                    value = operation == 13 ? product
                        : SaturateNeon(operation == 9 ? old + product : old - product, outputBits, false);
                }
                else if (operation == 14)
                {
                    ulong product = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((y & (BigInteger.One << bit)) != 0)
                        {
                            product ^= (ulong)(x & 255) << bit;
                        }
                    }
                    value = product;
                }
                else
                {
                    BigInteger product = x * y;
                    value = operation == 12 ? product : (operation == 8 ? old + product : old - product);
                }
                int offset = element * outputBits;
                ulong lane = (ulong)(value & mask) << (offset % 64);
                if (offset < 64)
                {
                    lower |= lane;
                }
                else
                {
                    upper |= lane;
                }
            }
            D[destination] = lower;
            if (!narrow)
            {
                D[destination + 1] = upper;
            }
            return true;
        }
    }
}
