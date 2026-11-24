using System;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private bool ExecuteArmMedia(uint op)
        {
            int rd = (int)(op >> 12 & 15);
            int rm = (int)(op & 15);
            int rn = (int)(op >> 16 & 15);
            if ((op & 0x0ff00ff0) == 0x06800fb0)
            {
                if (rd == 15 || rn == 15 || rm == 15)
                {
                    throw new RuntimeFault("SEL cannot use PC");
                }
                uint selected = 0;
                for (int lane = 0; lane < 4; lane++)
                {
                    uint source = (GE & 1 << lane) != 0 ? R[rn] : R[rm];
                    selected |= source & 255u << (lane * 8);
                }
                R[rd] = selected;
                return true;
            }
            if ((op & 0x0f800f10) == 0x06000f10)
            {
                int mode = (int)(op >> 20 & 7);
                int operation = (int)(op >> 5 & 7);
                if (mode == 0 || mode == 4 || operation == 5 || operation == 6 || rd == 15 || rn == 15 || rm == 15)
                {
                    throw new RuntimeFault("invalid parallel arithmetic encoding");
                }
                bool unsigned = (mode & 4) != 0;
                int behavior = mode & 3;
                int bits = operation >= 4 ? 8 : 16;
                int mask = (1 << bits) - 1;
                uint result = 0;
                byte ge = 0;
                for (int lane = 0; lane < 32 / bits; lane++)
                {
                    int other = operation == 1 || operation == 2 ? lane ^ 1 : lane;
                    int left = (int)(R[rn] >> (lane * bits)) & mask;
                    int right = (int)(R[rm] >> (other * bits)) & mask;
                    if (!unsigned)
                    {
                        left = left << (32 - bits) >> (32 - bits);
                        right = right << (32 - bits) >> (32 - bits);
                    }
                    bool subtract = operation == 3 || operation == 7 || operation == 1 && lane == 0 || operation == 2 && lane == 1;
                    int value = subtract ? left - right : left + right;
                    if (behavior == 1)
                    {
                        bool greater = unsigned && !subtract ? value > mask : value >= 0;
                        if (greater)
                        {
                            ge |= (byte)((bits == 16 ? 3 : 1) << (lane * bits / 8));
                        }
                    }
                    else if (behavior == 2)
                    {
                        int minimum = unsigned ? 0 : -(1 << (bits - 1));
                        int maximum = unsigned ? mask : (1 << (bits - 1)) - 1;
                        value = Math.Max(minimum, Math.Min(maximum, value));
                    }
                    else
                    {
                        value >>= 1;
                    }
                    result |= ((uint)value & (uint)mask) << (lane * bits);
                }
                R[rd] = result;
                if (behavior == 1)
                {
                    GE = ge;
                }
                return true;
            }
            uint kind = op & 0x0ff00000;
            if (kind == 0x03000000 || kind == 0x03400000)
            {
                if (rd == 15)
                {
                    throw new RuntimeFault("MOVW/MOVT cannot use PC");
                }
                uint immediate = (op & 0xfff) | (op >> 4 & 0xf000);
                R[rd] = kind == 0x03000000 ? immediate : (R[rd] & 0xffff) | immediate << 16;
                return true;
            }
            if ((op & 0x0fbf0f70) == 0x06bf0f30)
            {
                if (rd == 15 || rm == 15)
                {
                    throw new RuntimeFault("reverse instruction cannot use PC");
                }
                uint value = R[rm];
                uint variant = op & 0x00400080;
                if (variant == 0x00400000)
                {
                    uint reversed = 0;
                    for (int bit = 0; bit < 32; bit++)
                    {
                        reversed = reversed << 1 | (value >> bit & 1);
                    }
                    R[rd] = reversed;
                }
                else if (variant == 0x00400080)
                {
                    R[rd] = unchecked((uint)(short)((value & 255) << 8 | (value >> 8 & 255)));
                }
                else
                {
                    uint swapped = (value & 0x00ff00ff) << 8 | (value & 0xff00ff00) >> 8;
                    R[rd] = variant == 0 ? swapped << 16 | swapped >> 16 : swapped;
                }
                return true;
            }
            if ((op & 0x0fa00070) == 0x07a00050)
            {
                int offset = (int)(op >> 7 & 31);
                int width = (int)(op >> 16 & 31) + 1;
                if (rd == 15 || rm == 15 || offset + width > 32)
                {
                    throw new RuntimeFault("invalid bitfield extract encoding");
                }
                uint value = R[rm] >> offset;
                R[rd] = (op & 0x00400000) != 0 ? value & (uint.MaxValue >> (32 - width)) :
                    unchecked((uint)((int)(value << (32 - width)) >> (32 - width)));
                return true;
            }
            if ((op & 0x0fe00070) == 0x07c00010)
            {
                int offset = (int)(op >> 7 & 31);
                int end = (int)(op >> 16 & 31);
                if (rd == 15 || end < offset)
                {
                    throw new RuntimeFault("invalid bitfield insert encoding");
                }
                uint mask = (uint.MaxValue >> (31 - end + offset)) << offset;
                uint source = rm == 15 ? 0 : R[rm];
                R[rd] = R[rd] & ~mask | source << offset & mask;
                return true;
            }
            if ((op & 0x0f8003f0) == 0x06800070)
            {
                int size = (int)(op >> 20 & 3);
                if (rd == 15 || rm == 15 || size == 1)
                {
                    throw new RuntimeFault("invalid extend encoding");
                }
                uint source = Shift(R[rm], 3, (int)(op >> 10 & 3) * 8, false, out _);
                bool unsigned = (op & 0x00400000) != 0;
                uint basis = rn == 15 ? 0 : R[rn];
                if (size == 0)
                {
                    int low = unsigned ? (byte)source : (sbyte)source;
                    int high = unsigned ? (byte)(source >> 16) : (sbyte)(source >> 16);
                    R[rd] = (uint)((basis & 0xffff) + low) & 0xffff |
                        ((uint)((basis >> 16) + high) & 0xffff) << 16;
                }
                else
                {
                    uint value = size == 2 ? unsigned ? (byte)source : unchecked((uint)(sbyte)source) :
                        unsigned ? (ushort)source : unchecked((uint)(short)source);
                    R[rd] = unchecked(basis + value);
                }
                return true;
            }
            if ((op & 0x0ff00030) == 0x06800010)
            {
                if (rd == 15 || rn == 15 || rm == 15)
                {
                    throw new RuntimeFault("pack instruction cannot use PC");
                }
                bool top = (op & 0x40) != 0;
                uint shifted = Shift(R[rm], top ? 2 : 0, (int)(op >> 7 & 31), true, out _);
                R[rd] = top ? R[rn] & 0xffff0000 | shifted & 0xffff :
                    R[rn] & 0xffff | shifted & 0xffff0000;
                return true;
            }
            if ((op & 0x0fa00030) == 0x06a00010 || (op & 0x0fb00ff0) == 0x06a00f30)
            {
                if (rd == 15 || rm == 15)
                {
                    throw new RuntimeFault("saturate instruction cannot use PC");
                }
                bool unsigned = (op & 0x00400000) != 0;
                bool lanes = (op & 0x20) != 0;
                int width = (int)(op >> 16 & (lanes ? 15u : 31u)) + (unsigned ? 0 : 1);
                if (lanes)
                {
                    uint low = Saturate((short)R[rm], width, unsigned);
                    uint high = Saturate((short)(R[rm] >> 16), width, unsigned);
                    R[rd] = low & 0xffff | high << 16;
                }
                else
                {
                    uint shifted = Shift(R[rm], (op & 0x40) != 0 ? 2 : 0, (int)(op >> 7 & 31), true, out _);
                    R[rd] = Saturate((int)shifted, width, unsigned);
                }
                return true;
            }
            if ((op & 0x0f900ff0) == 0x01000050)
            {
                if (rd == 15 || rn == 15 || rm == 15)
                {
                    throw new RuntimeFault("saturating arithmetic cannot use PC");
                }
                long right = (int)R[rn];
                if ((op & 0x00400000) != 0)
                {
                    right = (int)Saturate(right * 2, 32, false);
                }
                R[rd] = Saturate((int)R[rm] + ((op & 0x00200000) != 0 ? -right : right), 32, false);
                return true;
            }
            return false;
        }

        private uint Saturate(long value, int bits, bool unsigned)
        {
            long minimum = unsigned ? 0 : -(1L << (bits - 1));
            long maximum = (1L << (unsigned ? bits : bits - 1)) - 1;
            if (value < minimum || value > maximum)
            {
                Q = true;
                value = Math.Max(minimum, Math.Min(maximum, value));
            }
            return unchecked((uint)value);
        }
    }
}
