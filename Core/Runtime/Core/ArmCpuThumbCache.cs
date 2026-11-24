using System;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private const int ThumbCacheSlots = 1024;
        private const int ThumbBlockLength = 16;
        private ThumbBlock?[]? thumbBlocks;
        private uint[]? thumbPageVersions;

        private enum ThumbOperation : byte
        {
            Shift, AddRegister, SubtractRegister, AddSmall, SubtractSmall,
            Move, Compare, AddImmediate, SubtractImmediate, Alu, HighMove, HighAdd,
            LiteralLoad, Load32, Load16, Load8, LoadSigned16, LoadSigned8,
            Store32, Store16, Store8, Address, StackAdjust, Extend, Hint,
            ConditionalBranch, Branch, Arithmetic, Exchange, BlockTransfer
        }

        private struct DecodedThumb
        {
            internal ushort Opcode;
            internal ThumbOperation Operation;
            internal byte Destination;
            internal byte Source;
            internal byte OffsetRegister;
            internal byte Kind;
            internal uint Immediate;
        }

        private sealed class ThumbBlock
        {
            internal uint Address;
            internal uint Version;
            internal int Count;
            internal ThumbBlock? Successor;
            internal ThumbBlock? Alternate;
            internal readonly DecodedThumb[] Instructions = new DecodedThumb[ThumbBlockLength];
        }

        // Memory filters ordinary data writes and invalidates all cache owners before
        // publishing the existing write notification to reentrant observers.
        private void InvalidateThumbPages(uint address, uint size)
        {
            if (size == 0)
            {
                return;
            }
            int first = (int)(address >> 12);
            int last = (int)(((ulong)address + size - 1) >> 12);
            for (int page = first; page <= last; page++)
            {
                uint version = thumbPageVersions![page];
                if (version == 0)
                {
                    continue;
                }
                if (version == uint.MaxValue)
                {
                    foreach (ThumbBlock? block in thumbBlocks!)
                    {
                        if (block != null)
                        {
                            block.Version = 0;
                        }
                    }
                    thumbPageVersions[page] = 1;
                }
                else
                {
                    thumbPageVersions[page] = version + 1;
                }
            }
        }

        private bool TryExecuteDecodedThumbBlock()
        {
            ThumbBlock? previous = null;
            while (true)
            {
                // Stay in the decoded dispatcher across ordinary block boundaries.
                // Recheck after stores/fusions before looking up the next version.
                if (R[15] == ReturnAddress)
                {
                    return true;
                }
                if (!OptimizeDecodedThumbBlocks || !Thumb || ITState != 0 ||
                    CaptureTrace || InstructionExecuted != null)
                {
                    return false;
                }
                uint address = R[15];
                if (remaining <= 0 || address < 0x1000 || (ulong)address + 2 > Memory.MappedEnd)
                {
                    return false;
                }
                if (thumbBlocks == null)
                {
                    thumbBlocks = new ThumbBlock?[ThumbCacheSlots];
                    thumbPageVersions = new uint[(Memory.MappedEnd + 4095) / 4096];
                    Memory.CodeWritten += InvalidateThumbPages;
                }
                int page = (int)(address >> 12);
                uint version = thumbPageVersions![page];
                if (version == 0)
                {
                    version = 1;
                    thumbPageVersions[page] = version;
                    Memory.WatchCodePage(page);
                }
                ThumbBlock? block = OptimizeDecodedThumbDispatch ? previous?.Successor : null;
                if (block == null || block.Address != address || block.Version != version)
                {
                    block = OptimizeDecodedThumbDispatch ? previous?.Alternate : null;
                    if (block == null || block.Address != address || block.Version != version)
                    {
                        int slot = (int)(((address >> 1) ^ (address >> 11)) & (ThumbCacheSlots - 1));
                        block = thumbBlocks[slot] ??= new ThumbBlock();
                    }
                }
                if (block.Address != address || block.Version != version)
                {
                    block.Address = address;
                    block.Version = version;
                    block.Count = 0;
                    block.Successor = null;
                    block.Alternate = null;
                    uint end = Math.Min(Memory.MappedEnd, (address & ~4095u) + 4096);
                    uint next = address;
                    while (block.Count < ThumbBlockLength && (ulong)next + 2 <= end)
                    {
                        ushort value = (ushort)Memory.U16(next);
                        // Preserve existing arithmetic fusions; they keep their real-byte
                        // matching and exact instruction-budget guards in Step.
                        DecodedThumb instruction;
                        if (OptimizeThumbArithmetic && IsThumbArithmeticStart(value))
                        {
                            ThumbArithmeticSequence sequence = FindThumbArithmeticSequence(value, next)!;
                            if ((ulong)next + (uint)sequence.Bytes.Length > end ||
                                !Memory.MatchesInstructions(next, sequence.Bytes, sequence.Words))
                            {
                                // A nonmatching template is still an ordinary supported
                                // Thumb instruction; retain its predecoded operands.
                                if (!DecodeThumb(value, OptimizeDecodedThumbDispatch, out instruction))
                                {
                                    break;
                                }
                            }
                            else
                            {
                                instruction = new DecodedThumb
                                {
                                    Opcode = value,
                                    Operation = ThumbOperation.Arithmetic,
                                    Kind = (byte)(sequence == packedBlend ? 0 : sequence == packedMaskCopy ? 1 :
                                        sequence == packedMask ? 2 : sequence == packedBitExtract ? 3 : sequence == packedLoadBits ? 4 :
                                        sequence == packedLoadBitsNext ? 5 : sequence == packedBlendAlternatePrefix ? 6 : 7)
                                };
                            }
                        }
                        else if (!DecodeThumb(value, OptimizeDecodedThumbDispatch, out instruction))
                        {
                            break;
                        }
                        block.Instructions[block.Count++] = instruction;
                        next += 2;
                        if (instruction.Operation == ThumbOperation.ConditionalBranch ||
                            instruction.Operation == ThumbOperation.Branch ||
                            instruction.Operation == ThumbOperation.Arithmetic ||
                            instruction.Operation == ThumbOperation.Exchange ||
                            instruction.Operation == ThumbOperation.BlockTransfer)
                        {
                            break;
                        }
                    }
                }
                if (block.Count == 0)
                {
                    return false;
                }
                if (OptimizeDecodedThumbDispatch && previous != null && previous.Successor != block)
                {
                    previous.Alternate = previous.Successor;
                    previous.Successor = block;
                }
                for (int index = 0; index < block.Count; index++)
                {
                    ref DecodedThumb instruction = ref block.Instructions[index];
                    if (!OptimizeDecodedThumbDispatch && (instruction.Operation >= ThumbOperation.Exchange ||
                        instruction.Operation == ThumbOperation.Arithmetic && instruction.Kind >= 6))
                    {
                        return false;
                    }
                    if (--remaining < 0)
                    {
                        throw new RuntimeFault("instruction budget exhausted");
                    }
                    if ((remaining & 4095) == 0)
                    {
                        Cancellation.ThrowIfCancellationRequested();
                    }
                    if (invocationDeadline != 0 && (remaining & 4095) == 0 &&
                        System.Diagnostics.Stopwatch.GetTimestamp() >= invocationDeadline)
                    {
                        throw new RuntimeFault("guest wall-clock limit exhausted");
                    }
                    bool mayWrite = instruction.Operation >= ThumbOperation.Store32 &&
                        instruction.Operation <= ThumbOperation.Store8 || instruction.Operation == ThumbOperation.BlockTransfer;
                    pc = R[15];
                    opcode = instruction.Opcode;
                    Instructions++;
                    if (instruction.Operation == ThumbOperation.Arithmetic)
                    {
                        ThumbArithmeticSequence sequence = instruction.Kind switch
                        {
                            0 => packedBlend,
                            1 => packedMaskCopy,
                            2 => packedMask,
                            3 => packedBitExtract,
                            4 => packedLoadBits,
                            5 => packedLoadBitsNext,
                            6 => packedBlendAlternatePrefix,
                            _ => packedBlendAlternateSuffix
                        };
                        if (!OptimizeThumbArithmetic || !TryThumbArithmetic(instruction.Opcode, sequence))
                        {
                            R[15] = pc + 2;
                            ExecuteThumb(instruction.Opcode);
                            LastInstructionSize = 2;
                        }
                        break;
                    }
                    R[15] = pc + 2;
                    ExecuteDecodedThumb(instruction);
                    LastInstructionSize = 2;
                    if (mayWrite)
                    {
                        InstructionExecuted?.Invoke(pc);
                        if (thumbPageVersions[page] != version || block.Address != address ||
                            block.Version != version || R[15] != pc + 2 || !Thumb ||
                            ITState != 0 || CaptureTrace || InstructionExecuted != null ||
                            !OptimizeDecodedThumbBlocks)
                        {
                            break;
                        }
                    }
                }
                previous = block;
            }
        }

        private bool IsThumbArithmeticStart(ushort value)
        {
            return value == 0x084e || value == 0x031b || value == 0x260e ||
                value == 0x8807 || value == 0x8802 || value == 0x4021 && OptimizeDecodedThumbDispatch;
        }

        private static bool DecodeThumb(ushort value, bool extendedDispatch, out DecodedThumb instruction)
        {
            instruction = new DecodedThumb
            {
                Opcode = value,
                Destination = (byte)(value & 7),
                Source = (byte)(value >> 3 & 7),
                OffsetRegister = byte.MaxValue
            };
            if ((value & 0xe000) == 0 && (value & 0x1800) != 0x1800)
            {
                instruction.Operation = ThumbOperation.Shift;
                instruction.Kind = (byte)(value >> 11 & 3);
                instruction.Immediate = (uint)(value >> 6 & 31);
            }
            else if ((value & 0xf800) == 0x1800)
            {
                instruction.Operation = (value & 0x400) != 0
                    ? ((value & 0x200) != 0 ? ThumbOperation.SubtractSmall : ThumbOperation.AddSmall)
                    : ((value & 0x200) != 0 ? ThumbOperation.SubtractRegister : ThumbOperation.AddRegister);
                instruction.Immediate = (uint)(value >> 6 & 7);
            }
            else if ((value & 0xe000) == 0x2000)
            {
                instruction.Destination = (byte)(value >> 8 & 7);
                instruction.Operation = (ThumbOperation)((int)ThumbOperation.Move + (value >> 11 & 3));
                instruction.Immediate = (uint)(value & 255);
            }
            else if ((value & 0xfc00) == 0x4000)
            {
                instruction.Operation = ThumbOperation.Alu;
                instruction.Kind = (byte)(value >> 6 & 15);
            }
            else if ((value & 0xfc00) == 0x4400 && ((value >> 8 & 3) == 0 || (value >> 8 & 3) == 2))
            {
                instruction.Destination |= (byte)(value >> 4 & 8);
                instruction.Source = (byte)(value >> 3 & 15);
                if (instruction.Destination == 15)
                {
                    return false;
                }
                instruction.Operation = (value & 0x200) != 0 ? ThumbOperation.HighMove : ThumbOperation.HighAdd;
            }
            else if (extendedDispatch && (value & 0xfc00) == 0x4400 && (value >> 8 & 3) == 3)
            {
                instruction.Source = (byte)(value >> 3 & 15);
                if ((value & 7) != 0 || (value & 128) != 0 && instruction.Source == 15)
                {
                    return false;
                }
                instruction.Operation = ThumbOperation.Exchange;
                instruction.Kind = (byte)((value >> 7) & 1);
            }
            else if (extendedDispatch && ((value & 0xf600) == 0xb400 || (value & 0xf000) == 0xc000))
            {
                bool load = (value & 0x800) != 0;
                uint mask = (uint)(value & 255);
                uint encoded;
                if ((value & 0xf600) == 0xb400)
                {
                    mask |= (value & 0x100) != 0 ? 1u << (load ? 15 : 14) : 0;
                    instruction.Source = 13;
                    encoded = load ? 0xe8bd0000u : 0xe92d0000u;
                }
                else
                {
                    instruction.Source = (byte)(value >> 8 & 7);
                    bool writeback = !load || (mask & (1u << instruction.Source)) == 0;
                    encoded = 0xe8800000u | (load ? 0x00100000u : 0) |
                        (writeback ? 0x00200000u : 0) | (uint)instruction.Source << 16;
                }
                instruction.Operation = ThumbOperation.BlockTransfer;
                instruction.Kind = (byte)CountTransferRegisters(mask);
                instruction.Immediate = encoded | mask;
            }
            else if ((value & 0xf800) == 0x4800)
            {
                instruction.Operation = ThumbOperation.LiteralLoad;
                instruction.Destination = (byte)(value >> 8 & 7);
                instruction.Immediate = (uint)(value & 255) * 4;
            }
            else if ((value & 0xf000) == 0x5000)
            {
                instruction.OffsetRegister = (byte)(value >> 6 & 7);
                instruction.Operation = (value >> 9 & 7) switch
                {
                    0 => ThumbOperation.Store32,
                    1 => ThumbOperation.Store16,
                    2 => ThumbOperation.Store8,
                    3 => ThumbOperation.LoadSigned8,
                    4 => ThumbOperation.Load32,
                    5 => ThumbOperation.Load16,
                    6 => ThumbOperation.Load8,
                    _ => ThumbOperation.LoadSigned16
                };
            }
            else if ((value & 0xe000) == 0x6000 || (value & 0xf000) == 0x8000)
            {
                int size = (value & 0xf000) == 0x8000 ? 2 : (value & 0x1000) != 0 ? 1 : 4;
                instruction.Immediate = (uint)(value >> 6 & 31) * (uint)size;
                instruction.Operation = (value & 0x800) != 0
                    ? (size == 4 ? ThumbOperation.Load32 : size == 2 ? ThumbOperation.Load16 : ThumbOperation.Load8)
                    : (size == 4 ? ThumbOperation.Store32 : size == 2 ? ThumbOperation.Store16 : ThumbOperation.Store8);
            }
            else if ((value & 0xf000) == 0x9000)
            {
                instruction.Operation = (value & 0x800) != 0 ? ThumbOperation.Load32 : ThumbOperation.Store32;
                instruction.Destination = (byte)(value >> 8 & 7);
                instruction.Source = 13;
                instruction.Immediate = (uint)(value & 255) * 4;
            }
            else if ((value & 0xf000) == 0xa000)
            {
                instruction.Operation = ThumbOperation.Address;
                instruction.Destination = (byte)(value >> 8 & 7);
                instruction.Source = (byte)((value & 0x800) != 0 ? 13 : 15);
                instruction.Immediate = (uint)(value & 255) * 4;
            }
            else if ((value & 0xff00) == 0xb000)
            {
                instruction.Operation = ThumbOperation.StackAdjust;
                instruction.Immediate = (value & 128) != 0
                    ? unchecked(0u - (uint)(value & 127) * 4) : (uint)(value & 127) * 4;
            }
            else if ((value & 0xff00) == 0xb200)
            {
                instruction.Operation = ThumbOperation.Extend;
                instruction.Kind = (byte)(value >> 6 & 3);
            }
            else if ((value & 0xff0f) == 0xbf00)
            {
                instruction.Operation = ThumbOperation.Hint;
            }
            else if ((value & 0xf000) == 0xd000 && (value >> 8 & 15) < 14)
            {
                instruction.Operation = ThumbOperation.ConditionalBranch;
                instruction.Kind = (byte)(value >> 8 & 15);
                instruction.Immediate = unchecked((uint)((sbyte)value * 2));
            }
            else if ((value & 0xf800) == 0xe000)
            {
                instruction.Operation = ThumbOperation.Branch;
                instruction.Immediate = unchecked((uint)((short)(value << 5) >> 4));
            }
            else
            {
                return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint DecodedAddress(in DecodedThumb instruction)
        {
            return unchecked(R[instruction.Source] + (instruction.OffsetRegister == byte.MaxValue
                ? instruction.Immediate : R[instruction.OffsetRegister]));
        }

        private void ExecuteDecodedThumb(in DecodedThumb instruction)
        {
            int destination = instruction.Destination;
            switch (instruction.Operation)
            {
                case ThumbOperation.Shift:
                    R[destination] = Shift(R[instruction.Source], instruction.Kind,
                        (int)instruction.Immediate, true, out bool carry);
                    C = carry;
                    Nz(R[destination]);
                    break;
                case ThumbOperation.AddRegister:
                    R[destination] = Add(R[instruction.Source], R[instruction.Immediate], false, true);
                    break;
                case ThumbOperation.SubtractRegister:
                    R[destination] = Add(R[instruction.Source], ~R[instruction.Immediate], true, true);
                    break;
                case ThumbOperation.AddSmall:
                    R[destination] = Add(R[instruction.Source], instruction.Immediate, false, true);
                    break;
                case ThumbOperation.SubtractSmall:
                    R[destination] = Add(R[instruction.Source], ~instruction.Immediate, true, true);
                    break;
                case ThumbOperation.Move:
                    R[destination] = instruction.Immediate;
                    Nz(R[destination]);
                    break;
                case ThumbOperation.Compare:
                    Add(R[destination], ~instruction.Immediate, true, true);
                    break;
                case ThumbOperation.AddImmediate:
                    R[destination] = Add(R[destination], instruction.Immediate, false, true);
                    break;
                case ThumbOperation.SubtractImmediate:
                    R[destination] = Add(R[destination], ~instruction.Immediate, true, true);
                    break;
                case ThumbOperation.Alu:
                    uint left = R[destination];
                    uint right = R[instruction.Source];
                    int kind = instruction.Kind;
                    bool shiftedCarry = C;
                    uint value = kind switch
                    {
                        0 or 8 => left & right,
                        1 => left ^ right,
                        2 or 3 or 4 => Shift(left, kind - 2, (int)(right & 255), false, out shiftedCarry),
                        5 => Add(left, right, C, true),
                        6 => Add(left, ~right, C, true),
                        7 => Shift(left, 3, (int)(right & 255), false, out shiftedCarry),
                        9 => Add(0, ~right, true, true),
                        10 => Add(left, ~right, true, true),
                        11 => Add(left, right, false, true),
                        12 => left | right,
                        13 => unchecked(left * right),
                        14 => left & ~right,
                        _ => ~right
                    };
                    Nz(value);
                    if (kind is 2 or 3 or 4 or 7)
                    {
                        C = shiftedCarry;
                    }
                    if (kind != 8 && kind != 10 && kind != 11)
                    {
                        R[destination] = value;
                    }
                    break;
                case ThumbOperation.HighMove:
                    R[destination] = Reg(instruction.Source);
                    break;
                case ThumbOperation.HighAdd:
                    R[destination] = unchecked(R[destination] + Reg(instruction.Source));
                    break;
                case ThumbOperation.LiteralLoad:
                    R[destination] = Load32(((pc + 4) & ~3u) + instruction.Immediate);
                    break;
                case ThumbOperation.Load32:
                    R[destination] = Load32(DecodedAddress(instruction));
                    break;
                case ThumbOperation.Load16:
                    R[destination] = Load16(DecodedAddress(instruction));
                    break;
                case ThumbOperation.Load8:
                    R[destination] = Load8(DecodedAddress(instruction));
                    break;
                case ThumbOperation.LoadSigned16:
                    R[destination] = unchecked((uint)(short)Load16(DecodedAddress(instruction)));
                    break;
                case ThumbOperation.LoadSigned8:
                    R[destination] = unchecked((uint)(sbyte)Load8(DecodedAddress(instruction)));
                    break;
                case ThumbOperation.Store32:
                    Store32(DecodedAddress(instruction), R[destination]);
                    break;
                case ThumbOperation.Store16:
                    Store16(DecodedAddress(instruction), R[destination]);
                    break;
                case ThumbOperation.Store8:
                    Store8(DecodedAddress(instruction), R[destination]);
                    break;
                case ThumbOperation.Address:
                    R[destination] = (instruction.Source == 13 ? R[13] : (pc + 4) & ~3u) + instruction.Immediate;
                    break;
                case ThumbOperation.StackAdjust:
                    R[13] = unchecked(R[13] + instruction.Immediate);
                    break;
                case ThumbOperation.Extend:
                    R[destination] = instruction.Kind switch
                    {
                        0 => unchecked((uint)(short)R[instruction.Source]),
                        1 => unchecked((uint)(sbyte)R[instruction.Source]),
                        2 => (ushort)R[instruction.Source],
                        _ => (byte)R[instruction.Source]
                    };
                    break;
                case ThumbOperation.Hint:
                    break;
                case ThumbOperation.ConditionalBranch:
                    if (Condition(instruction.Kind))
                    {
                        R[15] = pc + 4 + instruction.Immediate;
                    }
                    break;
                case ThumbOperation.Branch:
                    R[15] = pc + 4 + instruction.Immediate;
                    break;
                case ThumbOperation.Exchange:
                    Branch(Reg(instruction.Source));
                    if (instruction.Kind != 0)
                    {
                        R[14] = (pc + 2) | 1;
                    }
                    break;
                case ThumbOperation.BlockTransfer:
                    ExecuteBlockTransfer(instruction.Immediate, instruction.Source,
                        (instruction.Immediate & 0x00100000u) != 0, instruction.Kind);
                    break;
                default:
                    throw new InvalidOperationException("Invalid decoded Thumb operation.");
            }
        }
    }
}
