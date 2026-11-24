namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private ulong ReadFloating(int register, bool wide)
        {
            return wide ? D[register] : (uint)(D[register >> 1] >> ((register & 1) * 32));
        }

        private void WriteFloating(int register, bool wide, ulong value)
        {
            if (wide)
            {
                D[register] = value;
            }
            else
            {
                int shift = (register & 1) * 32;
                D[register >> 1] = D[register >> 1] & ~(0xffffffffUL << shift) | (value & 0xffffffffUL) << shift;
            }
        }

        private bool ExecuteVfp(uint op)
        {
            if ((op & 0x0c000e00) != 0x0c000a00)
            {
                return false;
            }
            if (ExecuteNeonCoreDuplicate(op))
            {
                return true;
            }
            bool wide = (op & 256) != 0;
            int destination = wide ? (int)(op >> 12 & 15 | op >> 18 & 16) : (int)(op >> 11 & 30 | op >> 22 & 1);
            int source = wide ? (int)(op & 15 | op >> 1 & 16) : (int)((op & 15) << 1 | op >> 5 & 1);
            int coreRegister = (int)(op >> 12 & 15);
            if ((op & 0x0fff0fff) == 0x0ef10a10)
            {
                if (Thumb && coreRegister == 13)
                {
                    throw new RuntimeFault("Thumb VMRS cannot use SP");
                }
                if (coreRegister == 15)
                {
                    N = (Fpscr & 0x80000000) != 0;
                    Z = (Fpscr & 0x40000000) != 0;
                    C = (Fpscr & 0x20000000) != 0;
                    V = (Fpscr & 0x10000000) != 0;
                }
                else
                {
                    R[coreRegister] = Fpscr;
                }
                return true;
            }
            if ((op & 0x0fff0fff) == 0x0ee10a10)
            {
                if (coreRegister == 15 || Thumb && coreRegister == 13)
                {
                    throw new RuntimeFault("VMSR cannot use PC or Thumb SP");
                }
                // VFPv4: exception trap enables and reserved fields are read-as-zero/write-ignored.
                Fpscr = R[coreRegister] & 0xfff7009f;
                return true;
            }
            if ((op & 0x0fe00f7f) == 0x0e000a10)
            {
                int single = (int)(op >> 15 & 30 | op >> 7 & 1);
                if (coreRegister == 15 || Thumb && coreRegister == 13)
                {
                    throw new RuntimeFault("VMOV scalar/core cannot use PC or Thumb SP");
                }
                if ((op & 0x00100000) != 0)
                {
                    R[coreRegister] = (uint)ReadFloating(single, false);
                }
                else
                {
                    WriteFloating(single, false, R[coreRegister]);
                }
                return true;
            }
            if ((op & 0x0fe00ed0) == 0x0c400a10)
            {
                int second = (int)(op >> 16 & 15);
                bool reading = (op & 0x00100000) != 0;
                if (coreRegister == 15 || second == 15 || Thumb && (coreRegister == 13 || second == 13) ||
                    reading && coreRegister == second || !wide && source == 31)
                {
                    throw new RuntimeFault("invalid VMOV register pair");
                }
                if (reading)
                {
                    ulong value = ReadFloating(source, wide);
                    uint upper = wide ? (uint)(value >> 32) : (uint)ReadFloating(source + 1, false);
                    R[coreRegister] = (uint)value;
                    R[second] = upper;
                }
                else if (wide)
                {
                    D[source] = (ulong)R[second] << 32 | R[coreRegister];
                }
                else
                {
                    WriteFloating(source, false, R[coreRegister]);
                    WriteFloating(source + 1, false, R[second]);
                }
                return true;
            }
            if ((op & 0x0f000f1f) == 0x0e000b10)
            {
                bool reading = (op & 0x00100000) != 0;
                bool unsigned = (op & 0x00800000) != 0;
                int bits = (op & 0x00400000) != 0 ? 8 : (op & 32) != 0 ? 16 : (op & 96) == 0 ? 32 : 0;
                if (bits == 0 || unsigned && (!reading || bits == 32) || coreRegister == 15 || Thumb && coreRegister == 13)
                {
                    throw new RuntimeFault("invalid VMOV lane encoding");
                }
                int register = (int)(op >> 16 & 15 | op >> 3 & 16);
                int lane = (int)(op >> 21 & 1) * (32 / bits) + (bits == 8 ? (int)(op >> 5 & 3) : bits == 16 ? (int)(op >> 6 & 1) : 0);
                int shift = lane * bits;
                ulong mask = (1UL << bits) - 1;
                if (reading)
                {
                    uint value = (uint)(D[register] >> shift & mask);
                    R[coreRegister] = unsigned ? value : unchecked((uint)((int)(value << (32 - bits)) >> (32 - bits)));
                }
                else
                {
                    D[register] = D[register] & ~(mask << shift) | ((ulong)R[coreRegister] & mask) << shift;
                }
                return true;
            }
            if ((op & 0x0e000000) == 0x0c000000)
            {
                FloatingTransfer(op, destination, wide);
                return true;
            }
            if ((op & 0x0fb00ef0) == 0x0eb00a00)
            {
                CheckFloatingScalar();
                int immediate = (int)(op >> 12 & 240 | op & 15);
                int fractionBits = wide ? 52 : 23;
                int exponentBits = wide ? 11 : 8;
                int exponent = (immediate & 64) == 0 ? 1 << (exponentBits - 1) : (1 << (exponentBits - 1)) - 4;
                exponent |= immediate >> 4 & 3;
                ulong value = (ulong)(immediate >> 7) << (wide ? 63 : 31) |
                    (ulong)exponent << fractionBits | (ulong)(immediate & 15) << (fractionBits - 4);
                WriteFloating(destination, wide, value);
                return true;
            }
            if ((op & 0x0fbe0e50) == 0x0eb00a40)
            {
                CheckFloatingScalar();
                int kind = (int)(op >> 15 & 2 | op >> 7 & 1);
                if (kind == 3)
                {
                    WriteFloating(destination, wide, SquareRootFloating(ReadFloating(source, wide), wide));
                    return true;
                }
                ulong value = ReadFloating(source, wide);
                ulong sign = 1UL << (wide ? 63 : 31);
                value = kind == 1 ? value & ~sign : kind == 2 ? value ^ sign : value;
                WriteFloating(destination, wide, value);
                return true;
            }
            if ((op & 0x0fbe0e50) == 0x0eb40a40)
            {
                bool zero = (op & 0x00010000) != 0;
                if (zero && (op & 0x2f) != 0)
                {
                    throw new RuntimeFault("invalid VFP compare-zero encoding");
                }
                CompareFloating(ReadFloating(destination, wide), zero ? 0 : ReadFloating(source, wide), wide, (op & 128) != 0);
                return true;
            }
            if (ExecuteFloatingConversion(op, destination, source, wide))
            {
                return true;
            }
            int binary = (int)(op >> 20 & 11);
            bool alternate = (op & 64) != 0;
            if ((op & 0x0f000e10) == 0x0e000a00 &&
                (binary <= 3 || binary == 8 && !alternate || binary == 9 || binary == 10))
            {
                if (binary < 9)
                {
                    CheckFloatingScalar();
                }
                int first = wide ? (int)(op >> 16 & 15 | op >> 3 & 16) : (int)(op >> 15 & 30 | op >> 7 & 1);
                ulong left = ReadFloating(first, wide);
                ulong right = ReadFloating(source, wide);
                ulong sign = 1UL << (wide ? 63 : 31);
                ulong value;
                if (binary >= 9)
                {
                    ulong accumulator = ReadFloating(destination, wide) ^ (binary == 9 ? sign : 0);
                    value = FusedFloating(accumulator, left ^ (alternate ? sign : 0), right, wide);
                }
                else
                {
                    int operation = binary == 3 ? (alternate ? 1 : 0) : binary == 8 ? 3 : 2;
                    value = FloatingBinary(left, right, wide, operation);
                    if (binary <= 2 && alternate)
                    {
                        value ^= sign;
                    }
                    if (binary <= 1)
                    {
                        ulong accumulator = ReadFloating(destination, wide) ^ (binary == 1 ? sign : 0);
                        value = FloatingBinary(accumulator, value, wide, 0);
                    }
                }
                WriteFloating(destination, wide, value);
                return true;
            }
            throw new RuntimeFault("unsupported VFP encoding");
        }

        private void CheckFloatingScalar()
        {
            if ((Fpscr & 0x00370000) != 0)
            {
                throw new RuntimeFault("VFP scalar operation requires zero Len and Stride");
            }
        }

        private void FloatingTransfer(uint op, int first, bool wide)
        {
            bool before = (op & 0x01000000) != 0;
            bool add = (op & 0x00800000) != 0;
            bool writeback = (op & 0x00200000) != 0;
            bool load = (op & 0x00100000) != 0;
            int basis = (int)(op >> 16 & 15);
            uint distance = (op & 255) * 4;
            bool single = before && !writeback;
            int count = single ? 1 : (int)(op & 255) / (wide ? 2 : 1);
            if (!single && (before == add || !add && !writeback) || count == 0 || first + count > 32 ||
                !single && wide && count > 16 || basis == 15 && (writeback || Thumb && (!single || !load)))
            {
                throw new RuntimeFault("invalid VFP memory transfer encoding");
            }
            uint origin = Reg(basis);
            if (basis == 15 && single && load)
            {
                origin &= ~3u;
            }
            uint adjusted = add ? origin + distance : origin - distance;
            uint address = single || !add ? adjusted : origin;
            if ((address & 3) != 0)
            {
                throw new RuntimeFault("unaligned VFP memory transfer");
            }
            uint stride = wide ? 8u : 4u;
            CheckData(address, (uint)count * stride);
            for (int index = 0; index < count; index++, address += stride)
            {
                if (load)
                {
                    ulong value = Load32(address);
                    if (wide)
                    {
                        value |= (ulong)Load32(address + 4) << 32;
                    }
                    WriteFloating(first + index, wide, value);
                }
                else
                {
                    ulong value = ReadFloating(first + index, wide);
                    Store32(address, (uint)value);
                    if (wide)
                    {
                        Store32(address + 4, (uint)(value >> 32));
                    }
                }
            }
            if (writeback)
            {
                R[basis] = adjusted;
            }
        }
    }
}
