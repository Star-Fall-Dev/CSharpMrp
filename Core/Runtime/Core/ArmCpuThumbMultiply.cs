namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteThumbMultiply(ushort first, ushort second)
        {
            if ((first & 0xff00) != 0xfb00)
            {
                return false;
            }
            int operation = first >> 4 & 15;
            int variant = second >> 4 & 15;
            int destination = second >> 8 & 15;
            int accumulator = second >> 12 & 15;
            int left = first & 15;
            int right = second & 15;
            if (destination == 13 || destination == 15 || accumulator == 13 ||
                left == 13 || left == 15 || right == 13 || right == 15)
            {
                throw new RuntimeFault("invalid Thumb-2 multiply/divide register");
            }
            uint encoded;
            if (operation == 0 && variant <= 1)
            {
                encoded = variant == 1 ? 0x00600090u : accumulator == 15 ? 0x00000090u : 0x00200090u;
                if (variant == 0 && accumulator == 15)
                {
                    accumulator = 0;
                }
            }
            else if (operation == 1 && variant <= 3)
            {
                encoded = (accumulator == 15 ? 0x01600080u : 0x01000080u) |
                    (uint)(variant & 2) << 4 | (uint)(variant & 1) << 6;
                if (accumulator == 15)
                {
                    accumulator = 0;
                }
            }
            else if (operation == 3 && variant <= 1)
            {
                encoded = 0x01200080 | (uint)variant << 6 | (accumulator == 15 ? 32u : 0);
                if (accumulator == 15)
                {
                    accumulator = 0;
                }
            }
            else if ((operation == 2 || operation == 4) && variant <= 1)
            {
                encoded = 0x07000010 | (operation == 4 ? 64u : 0) | (uint)variant << 5;
            }
            else if ((operation == 5 || operation == 6) && variant <= 1)
            {
                encoded = 0x07500010 | (operation == 6 ? 0xc0u : 0) | (uint)variant << 5;
            }
            else if (operation == 7 && variant == 0)
            {
                encoded = 0x07800010;
            }
            else if ((operation == 9 || operation == 11) && variant == 15 && accumulator == 15)
            {
                encoded = operation == 9 ? 0x07100010u : 0x07300010u;
            }
            else if ((operation == 8 || operation == 10 || operation == 12 || operation == 14) && variant == 0)
            {
                encoded = 0x00800090 | (operation < 12 ? 0 : 0x00200000u) | ((operation & 2) == 0 ? 0x00400000u : 0);
            }
            else if (operation == 14 && variant == 6)
            {
                encoded = 0x00400090;
            }
            else if (operation == 12 && variant >= 8 && variant <= 11)
            {
                encoded = 0x01400080 | (uint)(variant & 2) << 4 | (uint)(variant & 1) << 6;
            }
            else if ((operation == 12 || operation == 13) && (variant == 12 || variant == 13))
            {
                encoded = 0x07400010 | (operation == 13 ? 64u : 0) | (uint)(variant & 1) << 5;
            }
            else
            {
                throw new RuntimeFault("invalid Thumb-2 multiply/divide encoding");
            }
            ExecuteArm(0xe0000000 | encoded | (uint)destination << 16 | (uint)accumulator << 12 |
                (uint)right << 8 | (uint)left);
            return true;
        }
    }
}
