using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonUnary(uint op)
        {
            if ((op & 0xffb00810) != 0xf3b00000)
            {
                return false;
            }
            int group = (int)(op >> 16 & 3);
            int operation = (int)(op >> 7 & 15);
            int size = (int)(op >> 18 & 3);
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int sourceRegister = (int)(op & 15 | op >> 1 & 16);
            bool narrow = group == 2 && (operation == 4 || operation == 5);
            bool quad = !narrow && (op & 64) != 0;
            bool pair = group == 0 && (operation == 4 || operation == 5 || operation == 12 || operation == 13);
            bool floatingSign = group == 1 && operation >= 14;
            bool floatingCompare = group == 1 && operation >= 8 && operation <= 12;
            bool supported = narrow || group == 0 && (pair || operation >= 8)
                || group == 1 && (operation <= 4 || operation == 6 || operation == 7 || floatingSign || floatingCompare);
            if (!supported)
            {
                throw new RuntimeFault("unsupported NEON unary encoding");
            }
            if (size == 3 || group == 0 && (operation == 10 || operation == 11) && size != 0
                || (floatingSign || floatingCompare) && size != 2 || quad && ((destination | sourceRegister) & 1) != 0
                || narrow && (sourceRegister & 1) != 0)
            {
                throw new RuntimeFault("invalid NEON unary size or Q register");
            }
            int bits = 8 << size;
            int outputBits = pair ? bits * 2 : bits;
            int inputBits = narrow ? bits * 2 : bits;
            int count = (quad ? 128 : 64) / outputBits;
            NeonVector source = ReadNeon(sourceRegister, quad || narrow);
            NeonVector previous = ReadNeon(destination, quad);
            ulong mask = outputBits == 64 ? ulong.MaxValue : (1UL << outputBits) - 1;
            ulong lower = 0;
            ulong upper = 0;
            for (int element = 0; element < count; element++)
            {
                ulong raw = source.Lane(element, inputBits);
                BigInteger value;
                if (narrow)
                {
                    int kind = (int)(op >> 6 & 3);
                    value = NeonInteger(raw, inputBits, kind == 3);
                    if (kind != 0)
                    {
                        value = SaturateNeon(value, outputBits, (kind & 1) != 0);
                    }
                }
                else if (pair)
                {
                    bool unsigned = (operation & 1) != 0;
                    value = NeonInteger(source.Lane(element * 2, bits), bits, unsigned)
                        + NeonInteger(source.Lane(element * 2 + 1, bits), bits, unsigned);
                    if (operation >= 12)
                    {
                        value += previous.Lane(element, outputBits);
                    }
                }
                else if (group == 0 && (operation == 8 || operation == 9))
                {
                    ulong test = operation == 8 && (raw & (1UL << (bits - 1))) != 0 ? ~raw : raw;
                    int position = bits - (operation == 8 ? 2 : 1);
                    int zeros = 0;
                    for (; position >= 0 && (test & 1UL << position) == 0; position--)
                    {
                        zeros++;
                    }
                    value = zeros;
                }
                else if (group == 0 && operation == 10)
                {
                    int ones = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        ones += (int)(raw >> bit & 1);
                    }
                    value = ones;
                }
                else if (group == 0 && operation == 11)
                {
                    value = ~raw;
                }
                else if (floatingCompare)
                {
                    int condition = operation & 7;
                    uint x = condition >= 3 ? 0 : (uint)raw;
                    uint y = condition >= 3 ? (uint)raw : 0;
                    value = NeonStandardFloatingLane(x, y, 0, condition == 2 ? 8 : condition == 1 || condition == 3 ? 9 : 10);
                }
                else if (floatingSign)
                {
                    value = operation == 14 ? raw & 0x7fffffff : raw ^ 0x80000000;
                }
                else
                {
                    BigInteger signed = NeonInteger(raw, bits, false);
                    if (group == 1 && operation <= 4)
                    {
                        bool matches = operation switch
                        {
                            0 => signed > 0,
                            1 => signed >= 0,
                            2 => signed.IsZero,
                            3 => signed <= 0,
                            _ => signed < 0
                        };
                        value = matches ? mask : 0;
                    }
                    else
                    {
                        value = (operation & 1) == 0 ? BigInteger.Abs(signed) : -signed;
                        if (group == 0)
                        {
                            value = SaturateNeon(value, bits, false);
                        }
                    }
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
            if (quad)
            {
                D[destination + 1] = upper;
            }
            return true;
        }
    }
}
