namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonCoreDuplicate(uint op)
        {
            if ((op & 0x0f900f5f) != 0x0e800b10)
            {
                return false;
            }
            bool byteElement = (op & 0x00400000) != 0;
            bool halfElement = (op & 32) != 0;
            bool quad = (op & 0x00200000) != 0;
            int destination = (int)(op >> 16 & 15 | op >> 3 & 16);
            int source = (int)(op >> 12 & 15);
            if (byteElement && halfElement || quad && (destination & 1) != 0
                || source == 15 || Thumb && source == 13)
            {
                throw new RuntimeFault("invalid NEON core duplicate encoding");
            }
            int bits = byteElement ? 8 : halfElement ? 16 : 32;
            ulong scalar = R[source] & ((1UL << bits) - 1);
            ulong result = 0;
            for (int offset = 0; offset < 64; offset += bits)
            {
                result |= scalar << offset;
            }
            D[destination] = result;
            if (quad)
            {
                D[destination + 1] = result;
            }
            return true;
        }

        private bool ExecuteNeonPermutation(uint op)
        {
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int first = (int)(op >> 16 & 15 | op >> 3 & 16);
            int second = (int)(op & 15 | op >> 1 & 16);
            bool quad = (op & 64) != 0;
            if ((op & 0xffb00010) == 0xf2b00000)
            {
                int offset = (int)(op >> 8 & 15);
                if (quad && ((destination | first | second) & 1) != 0 || !quad && offset >= 8)
                {
                    throw new RuntimeFault("invalid NEON extract registers or offset");
                }
                NeonVector a = ReadNeon(first, quad);
                NeonVector b = ReadNeon(second, quad);
                int bytes = quad ? 16 : 8;
                for (int word = 0; word < (quad ? 2 : 1); word++)
                {
                    ulong value = 0;
                    for (int lane = 0; lane < 8; lane++)
                    {
                        int index = word * 8 + lane + offset;
                        value |= (index < bytes ? a.Lane(index, 8) : b.Lane(index - bytes, 8)) << (lane * 8);
                    }
                    D[destination + word] = value;
                }
                return true;
            }
            if ((op & 0xffb00c10) == 0xf3b00800)
            {
                int length = (int)(op >> 8 & 3) + 1;
                if (first + length > 32)
                {
                    throw new RuntimeFault("NEON table exceeds D31");
                }
                ulong indices = D[second];
                ulong old = D[destination];
                ulong result = 0;
                for (int lane = 0; lane < 8; lane++)
                {
                    int index = (int)(indices >> (lane * 8) & 255);
                    ulong value = index < length * 8 ? D[first + index / 8] >> (index % 8 * 8) & 255
                        : ((op & 64) != 0 ? old >> (lane * 8) & 255 : 0);
                    result |= value << (lane * 8);
                }
                D[destination] = result;
                return true;
            }
            if ((op & 0xffb00f90) == 0xf3b00c00)
            {
                int immediate = (int)(op >> 16 & 15);
                if ((immediate & 7) == 0 || quad && (destination & 1) != 0)
                {
                    throw new RuntimeFault("invalid NEON duplicate size or Q register");
                }
                int bits = (immediate & 1) != 0 ? 8 : (immediate & 2) != 0 ? 16 : 32;
                int index = immediate / (bits / 4);
                ulong scalar = ReadNeon(second, false).Lane(index, bits);
                ulong result = 0;
                for (int shift = 0; shift < 64; shift += bits)
                {
                    result |= scalar << shift;
                }
                D[destination] = result;
                if (quad)
                {
                    D[destination + 1] = result;
                }
                return true;
            }
            bool reverse = (op & 0xffb30e10) == 0xf3b00000;
            bool exchange = (op & 0xffb30e10) == 0xf3b20000;
            if (!reverse && !exchange)
            {
                return false;
            }
            int size = (int)(op >> 18 & 3);
            int operation = (int)(op >> 7 & 3);
            if (quad && ((destination | second) & 1) != 0 || size == 3
                || reverse && operation + size >= 3
                || exchange && (operation == 0 && size != 0 || operation >= 2 && !quad && size == 2))
            {
                throw new RuntimeFault("invalid NEON permutation size or Q register");
            }
            if (exchange && destination == second)
            {
                throw new RuntimeFault("NEON permutation with identical outputs has unknown result");
            }
            NeonVector original = ReadNeon(destination, quad);
            NeonVector source = ReadNeon(second, quad);
            int elementBits = 8 << size;
            int count = (quad ? 128 : 64) / elementBits;
            for (int output = 0; output < (exchange ? 2 : 1); output++)
            {
                ulong lower = 0;
                ulong upper = 0;
                for (int element = 0; element < count; element++)
                {
                    ulong value;
                    if (reverse)
                    {
                        value = source.Lane(element ^ ((1 << (3 - operation - size)) - 1), elementBits);
                    }
                    else if (operation == 0)
                    {
                        value = (output == 0 ? source : original).Lane(element, elementBits);
                    }
                    else if (operation == 1)
                    {
                        value = (element % 2 == 0 ? original : source).Lane((element & ~1) + output, elementBits);
                    }
                    else if (operation == 2)
                    {
                        int index = element * 2 + output;
                        value = (index < count ? original : source).Lane(index % count, elementBits);
                    }
                    else
                    {
                        int index = element + output * count;
                        value = (index % 2 == 0 ? original : source).Lane(index / 2, elementBits);
                    }
                    int offset = element * elementBits;
                    if (offset < 64)
                    {
                        lower |= value << offset;
                    }
                    else
                    {
                        upper |= value << (offset - 64);
                    }
                }
                int target = output == 0 ? destination : second;
                D[target] = lower;
                if (quad)
                {
                    D[target + 1] = upper;
                }
            }
            return true;
        }
    }
}
