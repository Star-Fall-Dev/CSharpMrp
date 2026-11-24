namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteNeonMemory(uint op)
        {
            if ((op & 0xff100000) != 0xf4000000)
            {
                return false;
            }
            bool load = (op & 0x00200000) != 0;
            bool single = (op & 0x00800000) != 0;
            bool replicate = single && (op >> 10 & 3) == 3;
            int destination = (int)(op >> 12 & 15 | op >> 18 & 16);
            int basis = (int)(op >> 16 & 15);
            int offset = (int)(op & 15);
            int structures = 1;
            int registers = 1;
            int increment = 1;
            int lane = 0;
            int alignment = 1;
            int size = (int)(op >> 6 & 3);
            if (!single)
            {
                int type = (int)(op >> 8 & 15);
                int align = (int)(op >> 4 & 3);
                structures = type <= 1 ? 4 : type == 3 || type == 8 || type == 9 ? 2 : type == 4 || type == 5 ? 3 : 1;
                registers = type == 2 ? 4 : type == 6 ? 3 : type == 3 || type == 10 ? 2 : 1;
                increment = type == 1 || type == 3 || type == 5 || type == 9 ? 2 : 1;
                if (type > 10 || structures != 1 && size == 3
                    || (type == 4 || type == 5 || type == 6 || type == 7) && align >= 2
                    || (type == 8 || type == 9 || type == 10) && align == 3)
                {
                    throw new RuntimeFault("invalid NEON structure encoding");
                }
                alignment = align == 0 ? 1 : 4 << align;
            }
            else
            {
                structures = (int)(op >> 8 & 3) + 1;
                if (replicate)
                {
                    bool aligned = (op & 16) != 0;
                    bool spaced = (op & 32) != 0;
                    if (!load || size == 3 && (structures != 4 || !aligned)
                        || structures == 1 && size == 0 && aligned || structures == 3 && aligned)
                    {
                        throw new RuntimeFault("invalid NEON structure replicate encoding");
                    }
                    alignment = !aligned ? 1 : structures == 4 && size >= 2 ? (size == 3 ? 16 : 8) : structures * (1 << size);
                    if (size == 3)
                    {
                        size = 2;
                    }
                    if (structures == 1)
                    {
                        registers = spaced ? 2 : 1;
                    }
                    else
                    {
                        increment = spaced ? 2 : 1;
                    }
                }
                else
                {
                    size = (int)(op >> 10 & 3);
                    int indexAlign = (int)(op >> 4 & 15);
                    int alignBits = indexAlign & (size == 2 ? 3 : 1);
                    lane = indexAlign >> (size + 1);
                    increment = size == 0 ? 1 : (indexAlign & (1 << size)) != 0 ? 2 : 1;
                    if (structures == 1 && (increment != 1 || size == 0 && alignBits != 0 || size == 2 && alignBits != 0 && alignBits != 3)
                        || structures == 2 && size == 2 && (alignBits & 2) != 0
                        || structures == 3 && alignBits != 0 || structures == 4 && size == 2 && alignBits == 3)
                    {
                        throw new RuntimeFault("invalid NEON structure lane encoding");
                    }
                    alignment = alignBits == 0 ? 1 : structures == 1 ? 1 << size
                        : structures == 4 && size == 2 ? 4 << alignBits : structures * (1 << size);
                }
            }
            if (basis == 15 || destination + (structures - 1) * increment + registers > 32)
            {
                throw new RuntimeFault("invalid NEON structure register range");
            }
            int bytes = 1 << size;
            int bits = bytes * 8;
            int elements = single ? 1 : 8 / bytes;
            uint count = (uint)(single ? structures * bytes : structures * registers * 8);
            uint address = R[basis];
            if ((address & (uint)(alignment - 1)) != 0)
            {
                throw new RuntimeFault("unaligned NEON structure transfer");
            }
            CheckData(address, count);
            uint writeback = address + (offset == 13 ? count : R[offset]);
            ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            for (int register = 0; register < registers; register++)
            {
                for (int element = 0; element < elements; element++)
                {
                    for (int structure = 0; structure < structures; structure++)
                    {
                        int target = destination + register + structure * increment;
                        int shift = (single ? lane : element) * bits;
                        uint current = address + (uint)((replicate ? structure : (register * elements + element) * structures + structure) * bytes);
                        if (load)
                        {
                            ulong value = 0;
                            for (int index = 0; index < bytes; index++)
                            {
                                value |= (ulong)Load8(current + (uint)index) << (index * 8);
                            }
                            if (replicate)
                            {
                                ulong repeated = 0;
                                for (int position = 0; position < 64; position += bits)
                                {
                                    repeated |= value << position;
                                }
                                D[target] = repeated;
                            }
                            else
                            {
                                D[target] = D[target] & ~(mask << shift) | value << shift;
                            }
                        }
                        else
                        {
                            ulong value = D[target] >> shift;
                            for (int index = 0; index < bytes; index++)
                            {
                                Store8(current + (uint)index, (uint)(value >> (index * 8)));
                            }
                        }
                    }
                }
            }
            if (offset != 15)
            {
                R[basis] = writeback;
            }
            return true;
        }
    }
}
