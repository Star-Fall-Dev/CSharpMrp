using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private readonly struct NeonVector
        {
            internal readonly ulong Lower;
            internal readonly ulong Upper;

            internal NeonVector(ulong lower, ulong upper)
            {
                Lower = lower;
                Upper = upper;
            }

            internal ulong Word(int index)
            {
                return index == 0 ? Lower : Upper;
            }

            internal ulong Lane(int index, int bits)
            {
                int offset = index * bits;
                return Word(offset / 64) >> (offset % 64) & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1);
            }
        }

        private NeonVector ReadNeon(int register, bool quad)
        {
            return new NeonVector(D[register], quad ? D[register + 1] : 0);
        }

        private bool ExecuteNeon(uint op)
        {
            if ((op & 0xfe800000) != 0xf2000000)
            {
                return ExecuteNeonConversion(op) || ExecuteNeonLong(op) || ExecuteNeonScalar(op) || ExecuteNeonImmediateShift(op)
                    || ExecuteNeonPermutation(op) || ExecuteNeonModifiedImmediate(op) || ExecuteNeonEstimate(op) || ExecuteNeonUnary(op);
            }
            bool quad = (op & 64) != 0;
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int first = (int)(op >> 16 & 15 | op >> 3 & 16);
            int second = (int)(op & 15 | op >> 1 & 16);
            if (quad && ((destination | first | second) & 1) != 0)
            {
                throw new RuntimeFault("NEON Q register requires an even D register index");
            }
            int operation = (int)(op >> 8 & 15);
            bool variant = (op & 16) != 0;
            bool unsigned = (op & 0x01000000) != 0;
            int size = (int)(op >> 20 & 3);
            if (operation >= 12)
            {
                return ExecuteNeonFloating(op);
            }
            bool logical = operation == 1 && variant;
            bool simple = operation == 8 && !variant;
            bool saturate = (operation == 0 || operation == 2) && variant;
            bool halving = (operation == 0 || operation == 1 || operation == 2) && !variant;
            bool compare = operation == 3 || operation == 8 && variant;
            bool minimumMaximum = operation == 6;
            bool absoluteDifference = operation == 7;
            bool multiply = operation == 9;
            bool multiplyHigh = operation == 11 && !variant;
            bool pairwise = operation == 10 || operation == 11 && variant && !unsigned;
            bool shiftRegister = operation == 4 || operation == 5;
            if (!logical && !simple && !saturate && !halving && !compare && !minimumMaximum && !absoluteDifference
                && !multiply && !multiplyHigh && !pairwise && !shiftRegister)
            {
                throw new RuntimeFault("unsupported NEON same-length encoding");
            }
            if (halving && size == 3)
            {
                throw new RuntimeFault("invalid NEON halving element size");
            }
            if ((compare || minimumMaximum || absoluteDifference) && size == 3)
            {
                throw new RuntimeFault("invalid NEON comparison or difference element size");
            }
            if (multiply && (size == 3 || variant && unsigned && size != 0)
                || multiplyHigh && (size == 0 || size == 3))
            {
                throw new RuntimeFault("invalid NEON multiply element size");
            }
            if (pairwise && (quad || size == 3))
            {
                throw new RuntimeFault("invalid NEON pairwise size or Q register");
            }
            NeonVector a = ReadNeon(first, quad);
            NeonVector b = ReadNeon(second, quad);
            NeonVector previous = ReadNeon(destination, quad);
            int bits = 8 << size;
            ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            for (int word = 0; word < (quad ? 2 : 1); word++)
            {
                ulong left = a.Word(word);
                ulong right = b.Word(word);
                ulong old = previous.Word(word);
                ulong result = 0;
                if (pairwise)
                {
                    for (int shift = 0; shift < 64; shift += bits)
                    {
                        ulong source = shift < 32 ? left : right;
                        int inputShift = shift % 32 * 2;
                        BigInteger x = NeonInteger(source >> inputShift & mask, bits, unsigned);
                        BigInteger y = NeonInteger(source >> (inputShift + bits) & mask, bits, unsigned);
                        BigInteger value = operation == 11 ? x + y
                            : (variant ? BigInteger.Min(x, y) : BigInteger.Max(x, y));
                        result |= (ulong)(value & mask) << shift;
                    }
                }
                else if (logical)
                {
                    int kind = size + (unsigned ? 4 : 0);
                    result = kind switch
                    {
                        0 => left & right,
                        1 => left & ~right,
                        2 => left | right,
                        3 => left | ~right,
                        4 => left ^ right,
                        5 => left & old | right & ~old,
                        6 => left & right | old & ~right,
                        _ => left & ~right | old & right
                    };
                }
                else
                {
                    for (int shift = 0; shift < 64; shift += bits)
                    {
                        ulong x = left >> shift & mask;
                        ulong y = right >> shift & mask;
                        ulong lane;
                        if (simple)
                        {
                            lane = unchecked(unsigned ? x - y : x + y) & mask;
                        }
                        else if (shiftRegister)
                        {
                            int amount = unchecked((sbyte)x);
                            BigInteger value = NeonInteger(y, bits, unsigned);
                            if (amount < 0 && operation == 5)
                            {
                                value += BigInteger.One << (-amount - 1);
                            }
                            value = amount < 0 ? value >> -amount : value << amount;
                            lane = (ulong)((variant ? SaturateNeon(value, bits, unsigned) : value) & mask);
                        }
                        else if (multiply)
                        {
                            ulong product = x * y;
                            if (variant && unsigned)
                            {
                                product = 0;
                                for (int bit = 0; bit < 8; bit++)
                                {
                                    if ((y & (1UL << bit)) != 0)
                                    {
                                        product ^= x << bit;
                                    }
                                }
                            }
                            lane = (variant ? product : unchecked(unsigned ? (old >> shift & mask) - product
                                : (old >> shift & mask) + product)) & mask;
                        }
                        else if (multiplyHigh)
                        {
                            BigInteger product = 2 * NeonInteger(x, bits, false) * NeonInteger(y, bits, false);
                            BigInteger rounded = (product + (unsigned ? BigInteger.One << (bits - 1) : BigInteger.Zero)) >> bits;
                            lane = (ulong)(SaturateNeon(rounded, bits, false) & mask);
                        }
                        else
                        {
                            BigInteger signedX = NeonInteger(x, bits, unsigned);
                            BigInteger signedY = NeonInteger(y, bits, unsigned);
                            BigInteger value = operation == 2 ? signedX - signedY : signedX + signedY;
                            if (compare)
                            {
                                bool matches = operation == 8 ? (unsigned ? x == y : (x & y) != 0)
                                    : (variant ? signedX >= signedY : signedX > signedY);
                                value = matches ? mask : 0;
                            }
                            else if (minimumMaximum)
                            {
                                value = variant ? BigInteger.Min(signedX, signedY) : BigInteger.Max(signedX, signedY);
                            }
                            else if (absoluteDifference)
                            {
                                value = BigInteger.Abs(signedX - signedY) + (variant ? old >> shift & mask : 0);
                            }
                            else if (halving)
                            {
                                value = (value + (operation == 1 ? 1 : 0)) >> 1;
                            }
                            else
                            {
                                value = SaturateNeon(value, bits, unsigned);
                            }
                            lane = (ulong)(value & mask);
                        }
                        result |= lane << shift;
                    }
                }
                D[destination + word] = result;
            }
            return true;
        }

        private static BigInteger NeonInteger(ulong value, int bits, bool unsigned)
        {
            return !unsigned && (value & (1UL << (bits - 1))) != 0 ? (BigInteger)value - (BigInteger.One << bits) : value;
        }

        private BigInteger SaturateNeon(BigInteger value, int bits, bool unsigned)
        {
            BigInteger minimum = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
            BigInteger maximum = (BigInteger.One << (unsigned ? bits : bits - 1)) - 1;
            if (value < minimum || value > maximum)
            {
                Fpscr |= 0x08000000;
                return value < minimum ? minimum : maximum;
            }
            return value;
        }
    }
}
