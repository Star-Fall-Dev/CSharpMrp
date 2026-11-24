namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private void ExecuteThumb32(ushort first, ushort second)
        {
            if ((first & 0xff10) == 0xf900 && ExecuteNeonMemory(0xf4000000 | (uint)(first & 255) << 16 | second))
            {
                return;
            }
            if ((first & 0xef00) == 0xef00 &&
                ExecuteNeon(0xf2000000 | (uint)(first & 255) << 16 | (uint)(first & 0x1000) << 12 | second))
            {
                return;
            }
            if (ExecuteThumbMedia(first, second) || ExecuteThumbMultiply(first, second) || ExecuteThumbMemory(first, second) ||
                ExecuteVfp((uint)first << 16 | second))
            {
                return;
            }
            int destination = second >> 8 & 15;
            int source = first & 15;
            int immediate = (first >> 10 & 1) << 11 | (second >> 12 & 7) << 8 | second & 255;
            int plain = first & 0xfbf0;
            if ((second & 0x8000) == 0 && (plain == 0xf240 || plain == 0xf2c0))
            {
                if (destination == 13 || destination == 15)
                {
                    throw new RuntimeFault("invalid Thumb-2 move-wide destination");
                }
                uint value = (uint)(immediate | (first & 15) << 12);
                R[destination] = plain == 0xf240 ? value : R[destination] & 0xffff | value << 16;
                return;
            }
            if ((second & 0x8000) == 0 && (plain == 0xf200 || plain == 0xf2a0))
            {
                if (destination == 15 || destination == 13 && source != 13)
                {
                    throw new RuntimeFault("invalid Thumb-2 add/sub-wide destination");
                }
                uint basis = source == 15 ? (pc + 4) & ~3u : R[source];
                R[destination] = plain == 0xf200 ? basis + (uint)immediate : basis - (uint)immediate;
                return;
            }
            if ((second & 0x8020) == 0 && (plain == 0xf340 || plain == 0xf3c0 || plain == 0xf360))
            {
                int offset = (second >> 12 & 7) << 2 | (second >> 6 & 3);
                int width = second & 31;
                if (destination == 13 || destination == 15 || source == 13 || source == 15 && plain != 0xf360)
                {
                    throw new RuntimeFault("invalid Thumb-2 bitfield register");
                }
                uint armKind = plain == 0xf340 ? 0x07a00050u : plain == 0xf3c0 ? 0x07e00050u : 0x07c00010u;
                ExecuteArmMedia(armKind | (uint)width << 16 | (uint)destination << 12 | (uint)offset << 7 | (uint)source);
                return;
            }
            bool register = (first & 0xfe00) == 0xea00 && (second & 0x8000) == 0;
            bool modifiedImmediate = (first & 0xfa00) == 0xf000 && (second & 0x8000) == 0;
            if (register || modifiedImmediate)
            {
                int operation = first >> 5 & 15;
                bool set = (first & 16) != 0;
                bool test = destination == 15 && set && (operation == 0 || operation == 4 || operation == 8 || operation == 13);
                if ((!test && destination == 15) || source == 15 && operation != 2 && operation != 3)
                {
                    throw new RuntimeFault("invalid Thumb-2 data register");
                }
                bool stackArithmetic = operation == 8 || operation == 13;
                bool plainMove = register && operation == 2 && source == 15 && !set && (second & 0x70f0) == 0;
                if (source == 13 && !stackArithmetic ||
                    destination == 13 && !(stackArithmetic && source == 13 || plainMove))
                {
                    throw new RuntimeFault("invalid Thumb-2 data SP operand");
                }
                bool carry = C;
                uint operand;
                if (register)
                {
                    int right = second & 15;
                    if (right == 15 || right == 13 && (!plainMove || destination == 13))
                    {
                        throw new RuntimeFault("invalid Thumb-2 shift register");
                    }
                    int amount = (second >> 12 & 7) << 2 | (second >> 6 & 3);
                    if (stackArithmetic && destination == 13 && ((second & 0x30) != 0 || amount > 3))
                    {
                        throw new RuntimeFault("invalid Thumb-2 SP arithmetic shift");
                    }
                    operand = Shift(R[right], second >> 4 & 3, amount, true, out carry);
                }
                else
                {
                    uint value = (uint)(immediate & 255);
                    if ((immediate & 0xc00) == 0)
                    {
                        int replication = immediate >> 8 & 3;
                        if (replication != 0 && value == 0)
                        {
                            throw new RuntimeFault("invalid Thumb-2 replicated immediate");
                        }
                        operand = replication switch
                        {
                            0 => value,
                            1 => value << 16 | value,
                            2 => value << 24 | value << 8,
                            _ => value * 0x01010101
                        };
                    }
                    else
                    {
                        operand = Shift((uint)(immediate & 127) | 128, 3, immediate >> 7 & 31, false, out carry);
                    }
                }
                uint left = source == 15 ? 0 : R[source];
                uint result = operation switch
                {
                    0 => left & operand,
                    1 => left & ~operand,
                    2 => left | operand,
                    3 => left | ~operand,
                    4 => left ^ operand,
                    8 => Add(left, operand, false, set),
                    10 => Add(left, operand, C, set),
                    11 => Add(left, ~operand, C, set),
                    13 => Add(left, ~operand, true, set),
                    14 => Add(operand, ~left, true, set),
                    _ => throw new RuntimeFault("unsupported Thumb-2 data operation")
                };
                if (set && operation <= 4)
                {
                    Nz(result);
                    C = carry;
                }
                if (!test)
                {
                    R[destination] = result;
                }
                return;
            }
            if ((first & 0xf800) == 0xf000 && (second & 0x8000) != 0)
            {
                int sign = first >> 10 & 1;
                int j1 = second >> 13 & 1;
                int j2 = second >> 11 & 1;
                int branchKind = second & 0xd000;
                if (branchKind == 0x8000)
                {
                    if (ITState != 0)
                    {
                        throw new RuntimeFault("conditional branch cannot execute inside IT");
                    }
                    int condition = first >> 6 & 15;
                    if (condition >= 14)
                    {
                        throw new RuntimeFault("unsupported Thumb-2 control instruction");
                    }
                    int encoded = sign << 20 | j2 << 19 | j1 << 18 | (first & 63) << 12 | (second & 2047) << 1;
                    if (Condition(condition))
                    {
                        R[15] = pc + 4 + unchecked((uint)(encoded << 11 >> 11));
                    }
                }
                else
                {
                    int encoded = sign << 24 | (j1 ^ sign ^ 1) << 23 | (j2 ^ sign ^ 1) << 22 |
                        (first & 1023) << 12 | (second & 2047) << 1;
                    uint offset = unchecked((uint)(encoded << 7 >> 7));
                    bool exchange = branchKind == 0xc000;
                    if (exchange && (second & 1) != 0)
                    {
                        throw new RuntimeFault("invalid Thumb-2 BLX encoding");
                    }
                    Branch(exchange ? ((pc + 4) & ~3u) + offset : (pc + 4 + offset) | 1);
                    if ((second & 0x4000) != 0)
                    {
                        R[14] = (pc + 4) | 1;
                    }
                }
                return;
            }
            throw new RuntimeFault("unsupported Thumb-2 encoding");
        }
    }
}
