namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteThumbMemory(ushort first, ushort second)
        {
            int basis = first & 15;
            int destination = second >> 12 & 15;
            if (first == 0xf3bf && (second & 0xff00) == 0x8f00 && IsMemoryBarrier(0xf57ff000 | (uint)(second & 255)))
            {
                return true;
            }
            if (first == 0xf3bf && second == 0x8f2f)
            {
                ClearExclusive();
                return true;
            }
            if ((first & 0xfff0) == 0xe8d0 && (second & 0xffe0) == 0xf000)
            {
                int index = second & 15;
                if (basis == 13 || index == 13 || index == 15)
                {
                    throw new RuntimeFault("invalid Thumb table-branch register");
                }
                CheckThumbBranchInIt();
                bool half = (second & 16) != 0;
                uint location = Reg(basis) + (R[index] << (half ? 1 : 0));
                uint offset = half ? Load16(location) : Load8(location);
                R[15] = pc + 4 + offset * 2;
                return true;
            }
            if ((first & 0xffe0) == 0xe840 || (first & 0xffe0) == 0xe8c0)
            {
                bool load = (first & 16) != 0;
                bool word = (first & 0x80) == 0;
                int secondary = second >> 8 & 15;
                int status = word ? secondary : second & 15;
                int kind = second >> 4 & 15;
                uint size = word ? 4u : kind == 4 ? 1u : kind == 5 ? 2u : kind == 7 ? 8u : 0;
                if (size == 0 || basis == 15 || destination == 13 || destination == 15 ||
                    load && (word ? secondary != 15 : (second & 15) != 15) ||
                    !word && size != 8 && secondary != 15 ||
                    size == 8 && (secondary == 13 || secondary == 15 || load && secondary == destination) ||
                    !load && (status == 13 || status == 15 || status == basis || status == destination || size == 8 && status == secondary))
                {
                    throw new RuntimeFault("invalid Thumb-2 exclusive encoding");
                }
                uint location = R[basis] + (word ? (uint)(second & 255) * 4 : 0);
                ExclusiveTransfer(location, size, load, destination, status, secondary);
                return true;
            }
            if ((first & 0xfe40) == 0xe800)
            {
                int mode = first >> 7 & 3;
                bool load = (first & 16) != 0;
                int count = 0;
                for (int register = 0; register < 16; register++)
                {
                    count += second >> register & 1;
                }
                if (mode != 1 && mode != 2 || count < 2 || (second & 0x2000) != 0 ||
                    !load && (second & 0x8000) != 0 || load && (second & 0xc000) == 0xc000 ||
                    (first & 32) != 0 && (second & 1 << basis) != 0)
                {
                    throw new RuntimeFault("invalid Thumb-2 block transfer encoding");
                }
                uint encoded = 0xe8000000 | (uint)(first & 0x1b0) << 16 | (uint)basis << 16 | second;
                ExecuteArm(encoded);
                return true;
            }
            if ((first & 0xfe40) == 0xe840 && (first & 0x120) != 0)
            {
                bool load = (first & 16) != 0;
                bool pre = (first & 0x100) != 0;
                bool writeback = (first & 32) != 0;
                int secondary = second >> 8 & 15;
                if (destination == 13 || destination == 15 || secondary == 13 || secondary == 15 ||
                    load && destination == secondary || basis == 15 && (!load || writeback) ||
                    writeback && (basis == destination || basis == secondary))
                {
                    throw new RuntimeFault("invalid Thumb-2 doubleword encoding");
                }
                uint original = basis == 15 ? (pc + 4) & ~3u : R[basis];
                uint offset = (uint)(second & 255) * 4;
                uint adjusted = (first & 128) != 0 ? original + offset : original - offset;
                uint location = pre ? adjusted : original;
                if ((location & 3) != 0)
                {
                    throw new RuntimeFault("unaligned doubleword transfer");
                }
                CheckData(location, 8);
                if (load)
                {
                    R[destination] = Load32(location);
                    R[secondary] = Load32(location + 4);
                }
                else
                {
                    Store32(location, R[destination]);
                    Store32(location + 4, R[secondary]);
                }
                if (writeback)
                {
                    R[basis] = adjusted;
                }
                return true;
            }
            if ((first & 0xfe00) != 0xf800)
            {
                return false;
            }
            int operation = first >> 4 & 31;
            int form = operation & 7;
            bool reading = (form & 1) != 0;
            bool signed = (operation & 16) != 0;
            int bytes = form < 2 ? 1 : form < 4 ? 2 : 4;
            if (reading && bytes != 4 && destination == 15)
            {
                bool literal = basis == 15;
                bool immediate = literal || (operation & 8) != 0 || (second & 0xf00) == 0xc00;
                bool registerOffset = (second & 0xfc0) == 0;
                if (immediate || registerOffset)
                {
                    bool unallocated = signed && bytes == 2;
                    int index = second & 15;
                    if (!unallocated && (literal && bytes == 2 || !immediate && (index == 13 || index == 15)))
                    {
                        throw new RuntimeFault("invalid Thumb preload encoding");
                    }
                    // PLD/PLDW/PLI and A6-19 unallocated memory hints do not
                    // perform an architectural memory access or writeback.
                    return true;
                }
            }
            if (form > 5 || signed && (!reading || bytes == 4) ||
                destination == 15 && (!reading || bytes != 4) || destination == 13 && bytes != 4 || basis == 15 && !reading)
            {
                throw new RuntimeFault("invalid Thumb-2 single transfer encoding");
            }
            uint originalBase = basis == 15 ? (pc + 4) & ~3u : R[basis];
            bool preIndex = true;
            bool updateBase = false;
            bool up = true;
            uint displacement;
            if (basis == 15 || (operation & 8) != 0)
            {
                displacement = (uint)(second & 4095);
                up = (operation & 8) != 0;
            }
            else if ((second & 0x800) == 0)
            {
                int index = second & 15;
                if (index == 13 || index == 15 || (second & 0xfc0) != 0)
                {
                    throw new RuntimeFault("invalid Thumb-2 transfer index");
                }
                displacement = R[index] << (second >> 4 & 3);
            }
            else
            {
                preIndex = (second & 0x400) != 0;
                updateBase = (second & 0x100) != 0;
                up = (second & 0x200) != 0;
                displacement = (uint)(second & 255);
                if (preIndex && up && !updateBase && (destination == 13 || destination == 15))
                {
                    throw new RuntimeFault("invalid Thumb-2 unprivileged transfer register");
                }
                if (!preIndex && !updateBase || updateBase && basis == destination)
                {
                    throw new RuntimeFault("invalid Thumb-2 transfer writeback");
                }
            }
            uint adjustedBase = up ? originalBase + displacement : originalBase - displacement;
            uint address = preIndex ? adjustedBase : originalBase;
            if (destination == 15 && (address & 3) != 0)
            {
                throw new RuntimeFault("unaligned Thumb-2 PC load");
            }
            if (reading)
            {
                uint value = bytes == 1 ? Load8(address) : bytes == 2 ? Load16(address) : Load32(address);
                if (signed)
                {
                    value = bytes == 1 ? unchecked((uint)(sbyte)value) : unchecked((uint)(short)value);
                }
                Dest(destination, value);
            }
            else if (bytes == 1)
            {
                Store8(address, R[destination]);
            }
            else if (bytes == 2)
            {
                Store16(address, R[destination]);
            }
            else
            {
                Store32(address, R[destination]);
            }
            if (updateBase)
            {
                R[basis] = adjustedBase;
            }
            return true;
        }
    }
}
