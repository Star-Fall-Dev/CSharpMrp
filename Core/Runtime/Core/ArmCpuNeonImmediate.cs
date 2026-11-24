namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonModifiedImmediate(uint op)
        {
            if ((op & 0xfeb80090) != 0xf2800010)
            {
                return false;
            }
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            bool quad = (op & 64) != 0;
            bool inverse = (op & 32) != 0;
            int mode = (int)(op >> 8 & 15);
            int immediate = (int)(op >> 17 & 128 | op >> 12 & 112 | op & 15);
            if (quad && (destination & 1) != 0 || inverse && mode == 15)
            {
                throw new RuntimeFault("invalid NEON modified immediate encoding");
            }
            if (immediate == 0 && (mode >= 2 && mode <= 7 || mode >= 10 && mode <= 13))
            {
                throw new RuntimeFault("unpredictable NEON zero modified immediate");
            }
            ulong value;
            if (mode < 8)
            {
                ulong word = (ulong)immediate << (mode / 2 * 8);
                value = word | word << 32;
            }
            else if (mode < 12)
            {
                ulong half = (ulong)immediate << (mode < 10 ? 0 : 8);
                value = half | half << 16 | half << 32 | half << 48;
            }
            else if (mode < 14)
            {
                int shift = mode == 12 ? 8 : 16;
                ulong word = (ulong)immediate << shift | (1UL << shift) - 1;
                value = word | word << 32;
            }
            else if (mode == 14 && inverse)
            {
                value = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((immediate & 1 << bit) != 0)
                    {
                        value |= 255UL << (bit * 8);
                    }
                }
            }
            else if (mode == 14)
            {
                value = (ulong)immediate * 0x0101010101010101;
            }
            else
            {
                uint word = (uint)(immediate >> 7) << 31 | (uint)(((immediate & 64) == 0 ? 128 : 124) | immediate >> 4 & 3) << 23
                    | (uint)(immediate & 15) << 19;
                value = word | (ulong)word << 32;
            }
            for (int word = 0; word < (quad ? 2 : 1); word++)
            {
                D[destination + word] = mode < 12 && (mode & 1) != 0
                    ? (inverse ? D[destination + word] & ~value : D[destination + word] | value)
                    : (inverse && mode != 14 ? ~value : value);
            }
            return true;
        }
    }
}
