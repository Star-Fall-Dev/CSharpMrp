using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonImmediateShift(uint op)
        {
            bool maximumLong = (op & 0xffb30fd0) == 0xf3b20300;
            if (!maximumLong && (op & 0xfe800010) != 0xf2800010)
            {
                return false;
            }
            int immediate = (int)(op >> 16 & 63 | op >> 1 & 64);
            if (!maximumLong && immediate < 8)
            {
                return false;
            }
            int operation = maximumLong ? 10 : (int)(op >> 8 & 15);
            bool unsigned = !maximumLong && (op & 0x01000000) != 0;
            bool narrow = operation == 8 || operation == 9;
            bool longResult = operation == 10;
            bool quad = operation < 8 && (op & 64) != 0;
            if (operation >= 11 || (operation == 4 || operation == 6) && !unsigned
                || (narrow || longResult) && !maximumLong && (op & 128) != 0
                || longResult && !maximumLong && (op & 64) != 0)
            {
                throw new RuntimeFault("unsupported or invalid NEON immediate shift encoding");
            }
            int bits = maximumLong ? 8 << (int)(op >> 18 & 3)
                : immediate >= 64 ? 64 : immediate >= 32 ? 32 : immediate >= 16 ? 16 : 8;
            if (maximumLong && bits == 64)
            {
                throw new RuntimeFault("invalid NEON maximum long shift element size");
            }
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int sourceRegister = (int)(op & 15 | op >> 1 & 16);
            if ((quad || longResult) && (destination & 1) != 0 || (quad || narrow) && (sourceRegister & 1) != 0)
            {
                throw new RuntimeFault("NEON shift Q register requires an even D register index");
            }
            int inputBits = narrow ? bits * 2 : bits;
            int outputBits = longResult ? bits * 2 : bits;
            bool left = operation >= 5 && operation <= 7 || longResult;
            int amount = maximumLong ? bits : left ? immediate - bits : bits * 2 - immediate;
            bool round = narrow ? (op & 64) != 0 : operation == 2 || operation == 3;
            bool saturate = operation == 6 || operation == 7 || narrow && (operation == 9 || unsigned);
            bool sourceUnsigned = unsigned && operation != 6 && operation != 8;
            NeonVector source = ReadNeon(sourceRegister, quad || narrow);
            NeonVector previous = ReadNeon(destination, quad || longResult);
            ulong mask = outputBits == 64 ? ulong.MaxValue : (1UL << outputBits) - 1;
            ulong lower = 0;
            ulong upper = 0;
            int elements = (quad || narrow ? 128 : 64) / inputBits;
            for (int element = 0; element < elements; element++)
            {
                BigInteger value = NeonInteger(source.Lane(element, inputBits), inputBits, sourceUnsigned);
                BigInteger old = previous.Lane(element, outputBits);
                if (round)
                {
                    value += BigInteger.One << (amount - 1);
                }
                value = left ? value << amount : value >> amount;
                if (operation == 1 || operation == 3)
                {
                    value += old;
                }
                else if (operation == 4 || operation == 5 && unsigned)
                {
                    BigInteger shiftedMask = left ? (BigInteger)mask << amount : (BigInteger)mask >> amount;
                    value = (old & ~shiftedMask) | (value & shiftedMask);
                }
                if (saturate)
                {
                    value = SaturateNeon(value, outputBits, unsigned);
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
            if (quad || longResult)
            {
                D[destination + 1] = upper;
            }
            return true;
        }
    }
}
