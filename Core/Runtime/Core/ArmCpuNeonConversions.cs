namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonConversion(uint op)
        {
            bool integer = (op & 0xffb30e10) == 0xf3b30600;
            bool fixedPoint = (op & 0xfe800e90) == 0xf2800e10 && (op >> 16 & 63) >= 8;
            bool half = (op & 0xffb30ed0) == 0xf3b20600;
            if (!integer && !fixedPoint && !half)
            {
                return false;
            }
            int size = (int)(op >> 18 & 3);
            bool direction = (op & 256) != 0;
            bool quad = (op & 64) != 0;
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int source = (int)(op & 15 | op >> 1 & 16);
            bool sourceQuad = half ? !direction : quad;
            bool destinationQuad = half ? direction : quad;
            if (integer && size != 2 || half && size != 1 || fixedPoint && (op >> 16 & 63) < 32
                || sourceQuad && (source & 1) != 0 || destinationQuad && (destination & 1) != 0)
            {
                throw new RuntimeFault("invalid NEON conversion encoding");
            }
            NeonVector input = ReadNeon(source, sourceQuad);
            int inputBits = half && direction ? 16 : 32;
            int outputBits = half && !direction ? 16 : 32;
            int count = half || quad ? 4 : 2;
            bool unsigned = integer ? (op & 128) != 0 : (op & 0x01000000) != 0;
            int fractionalBits = fixedPoint ? 64 - (int)(op >> 16 & 63) : 0;
            ulong lower = 0;
            ulong upper = 0;
            uint saved = Fpscr;
            Fpscr = saved & 0x0400009f | 0x03000000;
            try
            {
                for (int lane = 0; lane < count; lane++)
                {
                    ulong raw = input.Lane(lane, inputBits);
                    ulong result = half ? direction ? HalfToFloating((ushort)raw) : FloatingToHalf((uint)raw)
                        : direction ? FloatingToFixed(raw, false, 32, fractionalBits, unsigned, true)
                        : FixedToFloating(raw, false, 32, fractionalBits, unsigned, true);
                    result &= (1UL << outputBits) - 1;
                    int offset = lane * outputBits;
                    if (offset < 64)
                    {
                        lower |= result << offset;
                    }
                    else
                    {
                        upper |= result << (offset - 64);
                    }
                }
            }
            finally
            {
                Fpscr = saved & ~0x9fu | Fpscr & 0x9f;
            }
            D[destination] = lower;
            if (destinationQuad)
            {
                D[destination + 1] = upper;
            }
            return true;
        }
    }
}
