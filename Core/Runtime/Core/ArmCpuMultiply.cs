using System;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteArmMultiply(uint op)
        {
            int destination = (int)(op >> 16 & 15);
            int accumulator = (int)(op >> 12 & 15);
            int left = (int)(op & 15);
            int right = (int)(op >> 8 & 15);
            uint fixedKind = op & 0x0ff000f0;
            uint dspKind = op & 0x0ff00090;
            bool longResult = fixedKind == 0x00400090 || dspKind == 0x01400080 || dspKind == 0x07400010;
            bool highMultiply = (op & 0x0ff000d0) == 0x07500010 || (op & 0x0ff000d0) == 0x075000d0;
            bool divide = (op & 0x0fd0f0f0) == 0x0710f010;
            if (!longResult && !highMultiply && !divide && fixedKind != 0x00600090 &&
                dspKind != 0x01200080 && dspKind != 0x07000010 && fixedKind != 0x07800010)
            {
                return false;
            }
            if (destination == 15 || left == 15 || right == 15 ||
                longResult && (accumulator == 15 || accumulator == destination))
            {
                throw new RuntimeFault("invalid extended multiply/divide register");
            }
            if (divide)
            {
                uint divisor = R[right];
                if (divisor == 0)
                {
                    R[destination] = 0;
                }
                else if ((op & 0x00200000) != 0)
                {
                    R[destination] = R[left] / divisor;
                }
                else
                {
                    R[destination] = unchecked((uint)((long)(int)R[left] / (int)divisor));
                }
                return true;
            }
            if (fixedKind == 0x00600090)
            {
                if (accumulator == 15)
                {
                    throw new RuntimeFault("MLS cannot use PC");
                }
                R[destination] = unchecked(R[accumulator] - R[left] * R[right]);
                return true;
            }
            if (fixedKind == 0x07800010)
            {
                uint sum = accumulator == 15 ? 0 : R[accumulator];
                for (int shift = 0; shift < 32; shift += 8)
                {
                    sum = unchecked(sum + (uint)Math.Abs((int)(R[left] >> shift & 255) - (int)(R[right] >> shift & 255)));
                }
                R[destination] = sum;
                return true;
            }
            if (highMultiply)
            {
                bool subtract = (op & 0x80) != 0;
                if (subtract && accumulator == 15)
                {
                    throw new RuntimeFault("SMMLS cannot use PC");
                }
                long product = (long)(int)R[left] * (int)R[right];
                long basis = accumulator == 15 ? 0 : (long)(int)R[accumulator] << 32;
                long value = unchecked(basis + (subtract ? -product : product));
                if ((op & 0x20) != 0)
                {
                    value = unchecked(value + 0x80000000L);
                }
                R[destination] = unchecked((uint)(value >> 32));
                return true;
            }
            if (dspKind == 0x01200080)
            {
                bool accumulate = (op & 0x20) == 0;
                if (accumulate ? accumulator == 15 : accumulator != 0)
                {
                    throw new RuntimeFault("invalid word-half multiply accumulator");
                }
                long product = (long)(int)R[left] * (short)(R[right] >> ((op & 0x40) != 0 ? 16 : 0));
                long value = (product >> 16) + (accumulate ? (int)R[accumulator] : 0);
                if (accumulate)
                {
                    Q |= value < int.MinValue || value > int.MaxValue;
                }
                R[destination] = unchecked((uint)value);
                return true;
            }
            if (fixedKind == 0x00400090)
            {
                ulong value = (ulong)R[left] * R[right] + R[accumulator] + R[destination];
                R[accumulator] = (uint)value;
                R[destination] = (uint)(value >> 32);
                return true;
            }
            long productSum;
            if (dspKind == 0x01400080)
            {
                productSum = (long)(short)(R[left] >> ((op & 0x20) != 0 ? 16 : 0)) *
                    (short)(R[right] >> ((op & 0x40) != 0 ? 16 : 0));
            }
            else
            {
                uint source = R[right];
                if ((op & 0x20) != 0)
                {
                    source = source << 16 | source >> 16;
                }
                long low = (long)(short)R[left] * (short)source;
                long high = (long)(short)(R[left] >> 16) * (short)(source >> 16);
                productSum = low + ((op & 0x40) != 0 ? -high : high);
            }
            if (longResult)
            {
                ulong basis = (ulong)R[destination] << 32 | R[accumulator];
                ulong value = unchecked(basis + (ulong)productSum);
                R[accumulator] = (uint)value;
                R[destination] = (uint)(value >> 32);
            }
            else
            {
                long value = productSum + (accumulator == 15 ? 0 : (int)R[accumulator]);
                Q |= value < int.MinValue || value > int.MaxValue;
                R[destination] = unchecked((uint)value);
            }
            return true;
        }
    }
}
