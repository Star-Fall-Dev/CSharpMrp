namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonFloating(uint op)
        {
            int operation = (int)(op >> 8 & 15);
            bool variant = (op & 16) != 0;
            bool control = (op & 0x01000000) != 0;
            bool alternate = (op & 0x00200000) != 0;
            bool quad = (op & 64) != 0;
            bool pairwise = control && !variant && (operation == 15 || operation == 13 && !alternate);
            if ((op & 0x00100000) != 0 || operation == 12 && (!variant || control)
                || operation == 13 && variant && control && alternate
                || operation == 14 && !control && (variant || alternate) || pairwise && quad
                || operation == 15 && variant && control)
            {
                throw new RuntimeFault("invalid NEON floating encoding");
            }
            int kind = operation switch
            {
                12 => alternate ? 6 : 5,
                13 => variant ? (control ? 2 : alternate ? 4 : 3) : (control ? alternate ? 7 : 0 : alternate ? 1 : 0),
                14 => !control ? 8 : variant ? alternate ? 12 : 11 : alternate ? 10 : 9,
                _ => variant ? alternate ? 16 : 15 : alternate ? 14 : 13
            };
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int first = (int)(op >> 16 & 15 | op >> 3 & 16);
            int second = (int)(op & 15 | op >> 1 & 16);
            NeonVector a = ReadNeon(first, quad);
            NeonVector b = ReadNeon(second, quad);
            NeonVector previous = ReadNeon(destination, quad);
            uint saved = Fpscr;
            Fpscr = saved & 0x0400009f | 0x03000000;
            try
            {
                for (int word = 0; word < (quad ? 2 : 1); word++)
                {
                    ulong result = 0;
                    for (int lane = 0; lane < 2; lane++)
                    {
                        uint x = (uint)(pairwise ? (lane == 0 ? a : b).Lane(0, 32) : a.Lane(word * 2 + lane, 32));
                        uint y = (uint)(pairwise ? (lane == 0 ? a : b).Lane(1, 32) : b.Lane(word * 2 + lane, 32));
                        uint old = (uint)previous.Lane(word * 2 + lane, 32);
                        result |= (ulong)NeonFloatingLane(x, y, old, kind) << (lane * 32);
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

        private uint NeonStandardFloatingLane(uint x, uint y, uint old, int kind)
        {
            uint saved = Fpscr;
            Fpscr = saved & 0x0400009f | 0x03000000;
            try
            {
                return NeonFloatingLane(x, y, old, kind);
            }
            finally
            {
                Fpscr = saved & ~0x9fu | Fpscr & 0x9f;
            }
        }

        private uint NeonFloatingLane(uint x, uint y, uint old, int kind)
        {
            if (kind >= 15)
            {
                return NeonReciprocalStep(x, y, kind == 16);
            }
            if (kind <= 2)
            {
                return (uint)FloatingBinary(x, y, false, kind);
            }
            if (kind == 3 || kind == 4)
            {
                uint product = (uint)FloatingBinary(x, y, false, 2);
                return (uint)FloatingBinary(old, kind == 4 ? product ^ 0x80000000 : product, false, 0);
            }
            if (kind == 5 || kind == 6)
            {
                return (uint)FusedFloating(old, kind == 6 ? x ^ 0x80000000 : x, y, false);
            }
            if (kind == 7)
            {
                return (uint)FloatingBinary(x, y, false, 1) & 0x7fffffff;
            }
            if (kind <= 12)
            {
                if (kind >= 11)
                {
                    x &= 0x7fffffff;
                    y &= 0x7fffffff;
                }
                uint savedFlags = Fpscr & 0xf0000000;
                CompareFloating(x, y, false, kind != 8);
                uint flags = Fpscr >> 28;
                Fpscr = Fpscr & 0x0fffffff | savedFlags;
                bool matches = kind == 8 ? flags == 6 : kind == 9 || kind == 11 ? flags == 6 || flags == 2 : flags == 2;
                return matches ? uint.MaxValue : 0;
            }
            FloatingValue a = UnpackFloating(x, false);
            FloatingValue b = UnpackFloating(y, false);
            if (a.NaN || b.NaN)
            {
                return (uint)QuietFloatingNaN(a.Signaling ? a : b.Signaling ? b : a.NaN ? a : b, false);
            }
            if (a.Zero && b.Zero)
            {
                return (kind == 14 ? x | y : x & y) & 0x80000000;
            }
            bool less = a.Negative != b.Negative ? a.Negative : a.Negative ? a.Magnitude > b.Magnitude : a.Magnitude < b.Magnitude;
            return (uint)((kind == 14 ? less : !less) ? a.Raw : b.Raw);
        }
    }
}
