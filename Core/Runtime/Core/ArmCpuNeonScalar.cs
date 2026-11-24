using System.Numerics;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonScalar(uint op)
        {
            if ((op & 0xfe800050) != 0xf2800040 || (op >> 20 & 3) == 3)
            {
                return false;
            }
            int operation = (int)(op >> 8 & 15);
            int size = (int)(op >> 20 & 3);
            bool control = (op & 0x01000000) != 0;
            bool longResult = operation == 2 || operation == 3 || operation == 6 || operation == 7
                || operation == 10 || operation == 11;
            bool doubling = operation == 3 || operation == 7 || operation == 11;
            bool high = operation == 12 || operation == 13;
            bool floating = operation == 1 || operation == 5 || operation == 9;
            if (size == 0 || operation >= 14 || doubling && control || floating && size != 2)
            {
                throw new RuntimeFault("invalid NEON scalar multiply encoding");
            }
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int first = (int)(op >> 16 & 15 | op >> 3 & 16);
            bool quad = !longResult && control;
            if ((longResult || quad) && (destination & 1) != 0 || quad && (first & 1) != 0)
            {
                throw new RuntimeFault("NEON scalar Q register requires an even D register index");
            }
            int scalarRegister = (int)(op & (size == 1 ? 7u : 15u));
            int scalarIndex = size == 1 ? (int)(op >> 4 & 2 | op >> 3 & 1) : (int)(op >> 5 & 1);
            int bits = 8 << size;
            bool unsigned = longResult && control;
            ulong scalarRaw = ReadNeon(scalarRegister, false).Lane(scalarIndex, bits);
            BigInteger scalar = NeonInteger(scalarRaw, bits, unsigned);
            NeonVector source = ReadNeon(first, quad);
            NeonVector previous = ReadNeon(destination, longResult || quad);
            if (floating)
            {
                for (int word = 0; word < (quad ? 2 : 1); word++)
                {
                    ulong result = 0;
                    for (int lane = 0; lane < 2; lane++)
                    {
                        uint x = (uint)source.Lane(word * 2 + lane, 32);
                        uint old = (uint)previous.Lane(word * 2 + lane, 32);
                        result |= (ulong)NeonStandardFloatingLane(x, (uint)scalarRaw, old, operation == 9 ? 2 : operation == 5 ? 4 : 3) << (lane * 32);
                    }
                    D[destination + word] = result;
                }
                return true;
            }
            int outputBits = longResult ? bits * 2 : bits;
            ulong mask = outputBits == 64 ? ulong.MaxValue : (1UL << outputBits) - 1;
            ulong lower = 0;
            ulong upper = 0;
            for (int element = 0; element < (quad ? 128 : 64) / bits; element++)
            {
                BigInteger x = NeonInteger(source.Lane(element, bits), bits, unsigned);
                BigInteger old = NeonInteger(previous.Lane(element, outputBits), outputBits, unsigned);
                BigInteger product = x * scalar;
                BigInteger value;
                if (high)
                {
                    value = SaturateNeon((2 * product + (operation == 13 ? BigInteger.One << (bits - 1) : BigInteger.Zero)) >> bits, bits, false);
                }
                else if (doubling)
                {
                    product = SaturateNeon(2 * product, outputBits, false);
                    value = operation == 11 ? product
                        : SaturateNeon(operation == 3 ? old + product : old - product, outputBits, false);
                }
                else
                {
                    value = operation >= 8 ? product : ((operation & 4) != 0 ? old - product : old + product);
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
            if (longResult || quad)
            {
                D[destination + 1] = upper;
            }
            return true;
        }
    }
}
