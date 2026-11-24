namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteThumbMedia(ushort first, ushort second)
        {
            int destination = second >> 8 & 15;
            int basis = first & 15;
            int source = second & 15;
            if ((first & 0xfff0) == 0xf3c0 && second == 0x8f00)
            {
                if (basis == 13 || basis == 15)
                {
                    throw new RuntimeFault("invalid Thumb BXJ register");
                }
                Branch(R[basis]);
                return true;
            }
            if (first == 0xf3af && (second & 0xff00) == 0x8000)
            {
                if (second == 0x8014 && ITState != 0)
                {
                    throw new RuntimeFault("invalid CSDB inside Thumb IT block");
                }
                // Same hint profile as A32, including unallocated NOP hints.
                return true;
            }
            if (first == 0xf3ef && (second & 0xf0ff) == 0x8000)
            {
                if (destination == 13)
                {
                    throw new RuntimeFault("Thumb-2 MRS cannot use SP");
                }
                return ExecuteArmStatus(0x010f0000 | (uint)destination << 12);
            }
            if ((first & 0xfff0) == 0xf380 && (second & 0xf0ff) == 0x8000)
            {
                if (basis == 13)
                {
                    throw new RuntimeFault("Thumb-2 MSR cannot use SP");
                }
                return ExecuteArmStatus(0x0120f000 | (uint)(second >> 8 & 15) << 16 | (uint)basis);
            }
            uint encoded;
            if ((first & 0xff80) == 0xfa00 && (second & 0xf0f0) == 0xf000)
            {
                if (basis == 13 || basis == 15 || destination == 13 || destination == 15 || source == 13 || source == 15)
                {
                    throw new RuntimeFault("invalid Thumb-2 register-shift operand");
                }
                uint value = Shift(R[basis], first >> 5 & 3, (int)(R[source] & 255), false, out bool carry);
                R[destination] = value;
                if ((first & 16) != 0)
                {
                    Nz(value);
                    C = carry;
                }
                return true;
            }
            if ((first & 0xff80) == 0xfa00 && (second & 0xf0c0) == 0xf080 && (first >> 4 & 7) <= 5)
            {
                int kind = first >> 4 & 7;
                int size = kind >> 1;
                int armSize = size == 0 ? 3 : size == 1 ? 0 : 2;
                if (basis == 13 || destination == 13 || source == 13)
                {
                    throw new RuntimeFault("Thumb-2 extension cannot use SP");
                }
                encoded = 0x06800070 | (uint)armSize << 20 | (uint)(kind & 1) << 22 |
                    (uint)basis << 16 | (uint)destination << 12 | (uint)(second & 0x30) << 6 | (uint)source;
            }
            else if ((first & 0xfff0) == 0xfa90 && (second & 0xf0c0) == 0xf080 ||
                (first & 0xfff0) == 0xfab0 && (second & 0xf0f0) == 0xf080)
            {
                if (basis != source || basis == 13 || basis == 15 || destination == 13 || destination == 15)
                {
                    throw new RuntimeFault("invalid Thumb-2 reverse/count register");
                }
                if ((first & 0x20) != 0)
                {
                    ExecuteArm(0xe16f0f10 | (uint)destination << 12 | (uint)source);
                    return true;
                }
                int kind = second >> 4 & 3;
                encoded = 0x06bf0f30 | (uint)(kind & 1) << 7 | (uint)(kind >> 1) << 22 |
                    (uint)destination << 12 | (uint)source;
            }
            else if ((first & 0xff80) == 0xfa80 && (second & 0xf080) == 0xf000)
            {
                int operation = (first >> 4 & 7) switch
                {
                    0 => 4,
                    1 => 0,
                    2 => 1,
                    4 => 7,
                    5 => 3,
                    6 => 2,
                    _ => throw new RuntimeFault("invalid Thumb-2 parallel operation")
                };
                int mode = (second >> 4 & 7) + 1;
                if (mode == 4 || mode == 8 || basis == 13 || destination == 13 || source == 13)
                {
                    throw new RuntimeFault("invalid Thumb-2 parallel mode/register");
                }
                encoded = 0x06000f10 | (uint)mode << 20 | (uint)basis << 16 | (uint)destination << 12 |
                    (uint)operation << 5 | (uint)source;
            }
            else if ((first & 0xfff0) == 0xfaa0 && (second & 0xf0f0) == 0xf080 ||
                (first & 0xfff0) == 0xfa80 && (second & 0xf0c0) == 0xf080)
            {
                if (basis == 13 || destination == 13 || source == 13)
                {
                    throw new RuntimeFault("Thumb-2 select/saturating arithmetic cannot use SP");
                }
                encoded = (first & 0x20) != 0 ? 0x06800fb0u :
                    0x01000050 | (uint)(second & 16) << 18 | (uint)(second & 32) << 16;
                encoded |= (uint)basis << 16 | (uint)destination << 12 | (uint)source;
            }
            else if ((first & 0xff50) == 0xf300 && (second & 0x8020) == 0)
            {
                int shift = (second >> 12 & 7) << 2 | (second >> 6 & 3);
                bool arithmetic = (first & 32) != 0;
                bool lanes = arithmetic && shift == 0;
                if (basis == 13 || destination == 13 || lanes && (second & 16) != 0)
                {
                    throw new RuntimeFault("invalid Thumb-2 saturate encoding");
                }
                encoded = lanes ? 0x06a00f30u : 0x06a00010 | (uint)shift << 7 | (arithmetic ? 64u : 0);
                encoded |= (uint)(first & 128) << 15 | (uint)(second & 31) << 16 |
                    (uint)destination << 12 | (uint)basis;
            }
            else if ((first & 0xfff0) == 0xeac0 && (second & 0x8010) == 0)
            {
                if (basis == 13 || destination == 13 || source == 13)
                {
                    throw new RuntimeFault("Thumb-2 pack cannot use SP");
                }
                int shift = (second >> 12 & 7) << 2 | (second >> 6 & 3);
                encoded = 0x06800010 | (uint)basis << 16 | (uint)destination << 12 |
                    (uint)shift << 7 | (uint)(second & 32) << 1 | (uint)source;
            }
            else
            {
                return false;
            }
            return ExecuteArmMedia(encoded);
        }
    }
}
