using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CSharpMrp
{
    // User-mode little-endian ARMv7 interpreter; unsupported encodings fail closed.
    public sealed partial class ArmCpu
    {
        public const uint ReturnAddress = 0xfffffff0;
        public readonly uint[] R = new uint[16];
        public readonly ulong[] D = new ulong[32];
        public uint Fpscr;
        public readonly GuestMemory Memory;
        public bool LowAddressDataPage { get; }
        public bool AllowUnalignedBlockTransfers { get; }
        public bool N, Z, C, V, Q, Thumb;
        public byte GE;
        public byte ITState;
        public Func<uint, ArmCpu, uint>? HostCall;
        public Action<uint, uint>? SemihostingCall;
        public Func<uint, string>? DescribeAddress;
        public Action<uint>? InstructionExecuted;
        internal Action? ExecutionEntered;
        internal Action? ExecutionExited;
        public uint LastInstructionSize { get; private set; }
        public long Instructions
        {
            get; private set;
        }
        public bool CaptureTrace
        {
            get; set;
        }
        public bool OptimizeThumbArithmetic { get; set; } = true;
        public bool OptimizeThumbBranches { get; set; } = true;
        public bool OptimizeDecodedThumbBlocks { get; set; } = true;
        public bool OptimizeDecodedThumbDispatch { get; set; } = true;
        public int InvocationBudget { get; set; } = 1000000;
        public int InvocationTimeLimitMilliseconds { get; set; }
        public CancellationToken Cancellation { get; set; }
        private uint[,]? recent;
        private int traceCursor;

        public IEnumerable<string> RecentInstructions()
        {
            if (recent == null)
            {
                yield break;
            }
            for (int i = Math.Max(0, traceCursor - 4096); i < traceCursor; i++)
            {
                int slot = i % 4096;
                yield return $"pc={recent[slot, 0]:x8} op={recent[slot, 1]:x8} " +
                    $"r0={recent[slot, 2]:x8} r1={recent[slot, 3]:x8} r2={recent[slot, 4]:x8} " +
                    $"r3={recent[slot, 5]:x8} r4={recent[slot, 6]:x8} r5={recent[slot, 7]:x8} " +
                    $"r6={recent[slot, 8]:x8} r7={recent[slot, 9]:x8} r9={recent[slot, 11]:x8} " +
                    $"sp={recent[slot, 15]:x8} lr={recent[slot, 16]:x8}";
            }
        }
        private long remaining;
        private long invocationDeadline;
        private int depth;
        private Action? afterInvocations;
        private uint pc;
        private uint opcode;

        public ArmCpu(GuestMemory memory, bool lowAddressDataPage = false,
            bool allowUnalignedBlockTransfers = false)
        {
            Memory = memory;
            LowAddressDataPage = lowAddressDataPage;
            AllowUnalignedBlockTransfers = allowUnalignedBlockTransfers;
            exclusiveWriteMonitor = InvalidateExclusive;
            R[13] = memory.StackTop;
        }

        internal void RunAfterInvocations(Action action)
        {
            if (depth == 0) action();
            else afterInvocations += action;
        }

        public uint Invoke(uint entry, uint[] args, int? budget = null) =>
            Invoke(entry, args.AsSpan(0, args.Length), budget);

        internal uint Invoke(uint entry, uint first, uint second) =>
            Invoke(entry, stackalloc uint[] { first, second });

        internal uint Invoke(uint entry, ReadOnlySpan<uint> args, int? budget = null)
        {
            if (depth >= 64)
            {
                throw new RuntimeFault("guest call depth exceeded");
            }
            bool nested = depth != 0;
            uint savedStack = R[13];
            Span<uint> saved = nested ? stackalloc uint[16] : default;
            Span<ulong> savedFloating = nested ? stackalloc ulong[32] : default;
            if (nested)
            {
                R.AsSpan().CopyTo(saved);
                D.AsSpan().CopyTo(savedFloating);
            }
            var flags = (N, Z, C, V, Q, Thumb, GE, ITState, Fpscr, pc, opcode);
            if (!nested)
            {
                remaining = budget ?? InvocationBudget;
                if (remaining <= 0)
                {
                    throw new RuntimeFault("invalid guest instruction budget");
                }
                if (InvocationTimeLimitMilliseconds < 0 || InvocationTimeLimitMilliseconds > 60000)
                {
                    throw new RuntimeFault("invalid guest wall-clock limit");
                }
                invocationDeadline = InvocationTimeLimitMilliseconds == 0 ? 0 :
                    Stopwatch.GetTimestamp() + Stopwatch.Frequency * InvocationTimeLimitMilliseconds / 1000;
            }
            depth++;
            bool executing = entry < 0xf0000000;
            bool executionEntered = false;
            try
            {
                if (executing)
                {
                    ExecutionEntered?.Invoke();
                    executionEntered = true;
                }
                R[13] = (R[13] - (uint)Math.Max(0, args.Length - 4) * 4 - 8) & ~7u;
                for (int index = 0; index < args.Length; index++)
                {
                    if (index < 4)
                    {
                        R[index] = args[index];
                    }
                    else
                    {
                        Memory.Put32(R[13] + (uint)(index - 4) * 4, args[index]);
                    }
                }
                R[14] = ReturnAddress;
                ITState = 0;
                Branch(entry);
                while (R[15] != ReturnAddress)
                {
                    if ((remaining & 4095) == 0)
                    {
                        Cancellation.ThrowIfCancellationRequested();
                    }
                    if (OptimizeDecodedThumbBlocks && Thumb && ITState == 0 &&
                        !CaptureTrace && InstructionExecuted == null && TryExecuteDecodedThumbBlock())
                    {
                        continue;
                    }
                    if (--remaining < 0)
                    {
                        throw new RuntimeFault("instruction budget exhausted");
                    }
                    if (invocationDeadline != 0 && (remaining & 4095) == 0 &&
                        Stopwatch.GetTimestamp() >= invocationDeadline)
                    {
                        throw new RuntimeFault("guest wall-clock limit exhausted");
                    }
                    Step();
                }
                return R[0];
            }
            catch (RuntimeFault error)
            {
                throw new RuntimeFault($"{DescribeAddress?.Invoke(pc) ?? "guest"} " +
                    $"PC=0x{pc:x8} {(Thumb ? "Thumb" : "ARM")} op=0x{opcode:x8}: {error.Message}");
            }
            finally
            {
                if (executionEntered)
                {
                    ExecutionExited?.Invoke();
                }
                depth--;
                if (nested)
                {
                    saved.CopyTo(R);
                    savedFloating.CopyTo(D);
                    (N, Z, C, V, Q, Thumb, GE, ITState, Fpscr, pc, opcode) = flags;
                }
                else
                {
                    R[13] = savedStack;
                    if (afterInvocations is Action completed)
                    {
                        afterInvocations = null;
                        completed();
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint ReadArgument(int index) =>
            index < 4 ? R[index] : Memory.U32(R[13] + (uint)(index - 4) * 4);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Reg(int index)
        {
            return index == 15 ? pc + (Thumb ? 4u : 8u) : R[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Branch(uint address)
        {
            CheckBranchTarget(address);
            ITState = 0;
            Thumb = (address & 1) != 0;
            R[15] = address & ~1u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckBranchTarget(uint address)
        {
            CheckThumbBranchInIt();
            if (LowAddressDataPage && address < 0x1000)
            {
                throw new RuntimeFault("low address compatibility page is not executable");
            }
            if ((address & 3) == 2)
            {
                throw new RuntimeFault("unaligned ARM branch");
            }
        }

        private void CheckData(uint address, uint size)
        {
            Memory.CheckCpuData(address, size, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Load8(uint address)
        {
            return Memory.LoadCpuData(address, 1, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Load16(uint address)
        {
            return Memory.LoadCpuData(address, 2, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Load32(uint address)
        {
            return Memory.LoadCpuData(address, 4, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Store8(uint address, uint value)
        {
            Memory.StoreCpuData(address, value, 1, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Store16(uint address, uint value)
        {
            Memory.StoreCpuData(address, value, 2, LowAddressDataPage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Store32(uint address, uint value)
        {
            Memory.StoreCpuData(address, value, 4, LowAddressDataPage);
        }

        private void CheckThumbBranchInIt()
        {
            if (Thumb && ITState != 0 && (ITState & 7) != 0)
            {
                throw new RuntimeFault("branch must be last in Thumb IT block");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Dest(int index, uint value)
        {
            if (index == 15)
            {
                Branch(value);
            }
            else
            {
                R[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Nz(uint value)
        {
            N = (value & 0x80000000) != 0;
            Z = value == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Add(uint a, uint b, bool carry, bool flags)
        {
            ulong wide = (ulong)a + b + (carry ? 1ul : 0);
            uint result = (uint)wide;
            if (flags)
            {
                Nz(result);
                C = wide > uint.MaxValue;
                V = ((~(a ^ b) & (a ^ result)) & 0x80000000) != 0;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Condition(int condition)
        {
            return condition switch
            {
                0 => Z,
                1 => !Z,
                2 => C,
                3 => !C,
                4 => N,
                5 => !N,
                6 => V,
                7 => !V,
                8 => C && !Z,
                9 => !C || Z,
                10 => N == V,
                11 => N != V,
                12 => !Z && N == V,
                13 => Z || N != V,
                14 => true,
                _ => false
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Shift(uint value, int type, int amount, bool immediate, out bool carry)
        {
            carry = C;
            if (immediate && amount == 0)
            {
                if (type == 3)
                {
                    carry = (value & 1) != 0;
                    return value >> 1 | (C ? 0x80000000 : 0);
                }
                if (type == 1 || type == 2)
                {
                    amount = 32;
                }
            }
            if (amount == 0)
            {
                return value;
            }
            switch (type)
            {
                case 0:
                    carry = amount <= 32 && ((value >> (32 - amount)) & 1) != 0;
                    return amount >= 32 ? 0 : value << amount;
                case 1:
                    carry = amount <= 32 && ((value >> (amount - 1)) & 1) != 0;
                    return amount >= 32 ? 0 : value >> amount;
                case 2:
                    amount = Math.Min(amount, 32);
                    carry = ((value >> (amount - 1)) & 1) != 0;
                    return (uint)((int)value >> Math.Min(amount, 31));
                default:
                    int rotate = amount & 31;
                    uint result = rotate == 0 ? value : value >> rotate | value << (32 - rotate);
                    carry = (result & 0x80000000) != 0;
                    return result;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Step()
        {
            pc = R[15];
            Instructions++;
            if (pc >= 0xf0000000)
            {
                opcode = 0;
                if (HostCall == null)
                {
                    throw new RuntimeFault($"unknown host address 0x{pc:x8}");
                }
                uint result = HostCall(pc, this);
                R[0] = result;
                Branch(R[14]);
                return;
            }
            opcode = Thumb ? Memory.U16(pc) : Memory.U32(pc);
            bool wide = Thumb && (opcode & 0xf800) >= 0xe800;
            if (wide)
            {
                opcode = opcode << 16 | Memory.U16(pc + 2);
            }
            if (Thumb && !wide && IsThumbArithmeticStart((ushort)opcode) &&
                ITState == 0 && !CaptureTrace && InstructionExecuted == null &&
                OptimizeThumbArithmetic && TryThumbArithmetic((ushort)opcode))
            {
                return;
            }
            uint executedAddress = pc;
            uint executedSize = Thumb && !wide ? 2u : 4u;
            if (CaptureTrace)
            {
                recent ??= new uint[4096, 18];
                int slot = traceCursor++ % 4096;
                recent[slot, 0] = pc;
                recent[slot, 1] = opcode;
                for (int i = 0; i < 16; i++)
                {
                    recent[slot, i + 2] = R[i];
                }
            }
            R[15] = pc + (Thumb && !wide ? 2u : 4u);
            byte previousIt = ITState;
            if (Thumb)
            {
                if (previousIt != 0 && !Condition(previousIt >> 4))
                {
                    // A skipped wide instruction still consumes both halfwords.
                }
                else if (wide)
                {
                    ExecuteThumb32((ushort)(opcode >> 16), (ushort)opcode);
                }
                else
                {
                    ExecuteThumb((ushort)opcode);
                }
            }
            else
            {
                ExecuteArm(opcode);
            }
            if (previousIt != 0)
            {
                ITState = (byte)((previousIt & 7) == 0 ? 0 : (previousIt & 0xe0) | (previousIt << 1 & 31));
            }
            LastInstructionSize = executedSize;
            InstructionExecuted?.Invoke(executedAddress);
        }

        // Exact Thumb arithmetic sequences, with paired reads only when both are mapped.
        // Current-byte matching avoids stale code; tracing and IT execute each instruction.
        private sealed class ThumbArithmeticSequence
        {
            internal readonly byte[] Bytes;
            internal readonly ulong[] Words;

            internal ThumbArithmeticSequence(byte[] bytes)
            {
                Bytes = bytes;
                Words = new ulong[bytes.Length / 8];
                for (int index = 0; index < Words.Length; index++)
                {
                    Words[index] = BitConverter.ToUInt64(bytes, index * 8);
                }
            }
        }

        private static readonly ThumbArithmeticSequence packedBlend = new ThumbArithmeticSequence(new byte[]
        {
            0x4e, 0x08, 0x36, 0x04, 0x31, 0x43, 0x16, 0x04, 0x32, 0x43,
            0x22, 0x40, 0x29, 0x40, 0x89, 0x1a, 0x59, 0x43, 0x09, 0x09,
            0x89, 0x18, 0x21, 0x40, 0x0a, 0x0c, 0x11, 0x43
        });
        private static readonly ThumbArithmeticSequence packedMaskCopy = new ThumbArithmeticSequence(new byte[]
        {
            0x1b, 0x03, 0x0a, 0x1c, 0x1a, 0x40, 0x4b, 0x08, 0x0f, 0x27,
            0xff, 0x01, 0x3b, 0x40, 0x1e, 0x26, 0x31, 0x40, 0x1a, 0x43, 0x11, 0x43
        });
        // Keep the intervening STR outside these pure arithmetic sequences: its
        // observers can change registers, code or execution mode before the suffix.
        private static readonly ThumbArithmeticSequence packedBlendAlternatePrefix = new ThumbArithmeticSequence(new byte[]
        {
            0x4e, 0x08, 0x36, 0x04, 0x0e, 0x43, 0x39, 0x04, 0x39, 0x43, 0x2e, 0x40
        });
        private static readonly ThumbArithmeticSequence packedBlendAlternateSuffix = new ThumbArithmeticSequence(new byte[]
        {
            0x21, 0x40, 0x76, 0x1a, 0x5e, 0x43, 0x33, 0x09,
            0x59, 0x18, 0x21, 0x40, 0x0b, 0x0c, 0x19, 0x43
        });
        private static readonly ThumbArithmeticSequence packedMask = new ThumbArithmeticSequence(new byte[]
        {
            0x1b, 0x03, 0x4e, 0x08, 0x0f, 0x27, 0xff, 0x01, 0x3e, 0x40,
            0x0b, 0x40, 0x33, 0x43, 0x1e, 0x26, 0x31, 0x40, 0x19, 0x43
        });
        private static readonly ThumbArithmeticSequence packedBitExtract = new ThumbArithmeticSequence(new byte[]
        {
            0x0e, 0x26, 0x0b, 0x09, 0x33, 0x40, 0xce, 0x07, 0xf6, 0x0f, 0x33, 0x43
        });
        private static readonly ThumbArithmeticSequence packedLoadBits = new ThumbArithmeticSequence(new byte[]
        {
            0x07, 0x88, 0x11, 0x88, 0x0e, 0x26, 0x0b, 0x09,
            0x33, 0x40, 0xce, 0x07, 0xf6, 0x0f, 0x33, 0x43
        });
        private static readonly ThumbArithmeticSequence packedLoadBitsNext = new ThumbArithmeticSequence(new byte[]
        {
            0x02, 0x88, 0x09, 0x88, 0x0e, 0x26, 0x0b, 0x09,
            0x33, 0x40, 0xce, 0x07, 0xf6, 0x0f, 0x33, 0x43
        });

        private ThumbArithmeticSequence? FindThumbArithmeticSequence(ushort first, uint address)
        {
            if (first == 0x084e)
            {
                return OptimizeDecodedThumbDispatch &&
                    !Memory.MatchesInstructions(address, packedBlend.Bytes, packedBlend.Words)
                    ? packedBlendAlternatePrefix : packedBlend;
            }
            else if (first == 0x031b)
            {
                return Memory.MatchesInstructions(address, packedMaskCopy.Bytes, packedMaskCopy.Words)
                    ? packedMaskCopy : packedMask;
            }
            else if (first == 0x260e)
            {
                return packedBitExtract;
            }
            else if (first == 0x8807)
            {
                return packedLoadBits;
            }
            else if (first == 0x8802)
            {
                return packedLoadBitsNext;
            }
            return first == 0x4021 && OptimizeDecodedThumbDispatch ? packedBlendAlternateSuffix : null;
        }

        private bool TryThumbArithmetic(ushort first, ThumbArithmeticSequence? verifiedSequence = null)
        {
            ThumbArithmeticSequence? sequence = verifiedSequence ?? FindThumbArithmeticSequence(first, pc);
            if (sequence == null || !OptimizeDecodedThumbDispatch &&
                (sequence == packedBlendAlternatePrefix || sequence == packedBlendAlternateSuffix))
            {
                return false;
            }
            int additional = sequence.Bytes.Length / 2 - 1;
            if (remaining < additional ||
                invocationDeadline != 0 && (remaining & 4095) < additional ||
                verifiedSequence == null && !Memory.MatchesInstructions(pc, sequence.Bytes, sequence.Words))
            {
                return false;
            }
            uint source = R[1];
            bool extract = sequence == packedBitExtract || sequence == packedLoadBits ||
                sequence == packedLoadBitsNext;
            if (sequence == packedLoadBits || sequence == packedLoadBitsNext)
            {
                uint second = sequence == packedLoadBits ? R[2] : R[1];
                if (!Memory.TryReadCpu16Pair(R[0], second, LowAddressDataPage, out uint destination, out source))
                {
                    return false;
                }
                R[sequence == packedLoadBits ? 7 : 2] = destination;
                R[1] = source;
            }
            uint branch = 0;
            bool compared = false;
            uint nextAddress = pc + (uint)sequence.Bytes.Length;
            if (extract && OptimizeThumbBranches && remaining >= additional + 1 &&
                (invocationDeadline == 0 || (remaining & 4095) >= additional + 1) &&
                (ulong)nextAddress + 2 <= Memory.MappedEnd)
            {
                uint next = Memory.U16(nextAddress);
                if ((next & 0xff00) == 0xd000)
                {
                    branch = next;
                    additional++;
                    uint value = ((source >> 4) & 14u) | (source & 1u);
                    if (value != 0 && remaining >= additional + 2 &&
                        (invocationDeadline == 0 || (remaining & 4095) >= additional + 2) &&
                        (ulong)nextAddress + 6 <= Memory.MappedEnd &&
                        Memory.U16(nextAddress + 2) == 0x2b0f)
                    {
                        uint unequal = Memory.U16(nextAddress + 4);
                        if ((unequal & 0xff00) == 0xd100)
                        {
                            branch = unequal;
                            compared = true;
                            additional += 2;
                        }
                    }
                }
            }
            if (sequence == packedBlendAlternatePrefix)
            {
                uint destination = R[7];
                R[6] = (source | ((source >> 1) << 16)) & R[5];
                R[1] = destination | (destination << 16);
                C = (destination & 0x10000) != 0;
            }
            else if (sequence == packedBlendAlternateSuffix)
            {
                uint destination = source & R[4];
                R[6] = unchecked((R[6] - destination) * R[3]);
                uint shifted = R[6] >> 4;
                uint sum = unchecked(shifted + destination);
                V = ((~(shifted ^ destination) & (shifted ^ sum)) & 0x80000000) != 0;
                uint masked = sum & R[4];
                R[3] = masked >> 16;
                R[1] = masked | R[3];
                C = (masked & 0x8000) != 0;
            }
            else if (sequence == packedBlend)
            {
                uint destination = R[2];
                R[6] = destination << 16;
                destination = (destination | R[6]) & R[4];
                uint expanded = (source | ((source >> 1) << 16)) & R[5];
                uint product = unchecked((expanded - destination) * R[3]);
                uint shifted = product >> 4;
                uint sum = unchecked(shifted + destination);
                V = ((~(shifted ^ destination) & (shifted ^ sum)) & 0x80000000) != 0;
                uint masked = sum & R[4];
                R[2] = masked >> 16;
                R[1] = masked | R[2];
                C = (masked & 0x8000) != 0;
            }
            else if (sequence == packedMaskCopy)
            {
                R[2] = source & (R[3] << 12);
                R[3] = (source >> 1) & 1920u;
                R[7] = 1920;
                R[6] = 30;
                R[2] |= R[3];
                R[1] = (source & 30u) | R[2];
                C = false;
                V = false;
            }
            else if (sequence == packedMask)
            {
                R[3] = (source & (R[3] << 12)) | ((source >> 1) & 1920u);
                R[6] = 30;
                R[7] = 1920;
                R[1] = (source & 30u) | R[3];
                C = false;
            }
            else
            {
                R[6] = source & 1u;
                R[3] = ((source >> 4) & 14u) | R[6];
                C = false;
            }
            Nz(sequence == packedBlendAlternatePrefix ? R[6] : extract ? R[3] : R[1]);
            if (compared)
            {
                Add(R[3], ~15u, true, true);
            }
            remaining -= additional;
            Instructions += additional;
            pc += (uint)(additional * 2);
            opcode = branch != 0 ? branch : (uint)(sequence.Bytes[sequence.Bytes.Length - 2] |
                sequence.Bytes[sequence.Bytes.Length - 1] << 8);
            R[15] = branch != 0 && (compared ? !Z : Z)
                ? pc + 4 + (uint)((sbyte)branch << 1) : pc + 2;
            LastInstructionSize = 2;
            return true;
        }

        private void ExecuteArm(uint op)
        {
            int condition = (int)(op >> 28);
            if (condition == 15)
            {
                if ((op & 0xfffffdff) == 0xf1010000)
                {
                    if ((op & 0x200) != 0)
                    {
                        throw new RuntimeFault("unsupported big-endian execution profile");
                    }
                    return;
                }
                if (IsMemoryBarrier(op) || ExecuteArmPreload(op) || ExecuteNeonMemory(op) || ExecuteNeon(op))
                {
                    return;
                }
                if (op == 0xf57ff01f)
                {
                    ClearExclusive();
                    return;
                }
                if ((op & 0xfe000000) == 0xfa000000)
                {
                    R[14] = pc + 4;
                    Branch((pc + 8 + (uint)((int)(op << 8) >> 6) + ((op >> 23) & 2)) | 1);
                    return;
                }
                throw new RuntimeFault("unsupported unconditional encoding");
            }
            if (condition != 14 && !Condition(condition))
            {
                return;
            }
            if ((op & 0x0fffffff) == 0x0f123456 && SemihostingCall != null)
            {
                SemihostingCall(R[0], R[1]);
                return;
            }
            int rd = (int)(op >> 12 & 15);
            int rn = (int)(op >> 16 & 15);
            int rm = (int)(op & 15);
            bool set = (op & (1 << 20)) != 0;
            // Legacy guest loaders issue ICIALLU after copying executable bytes.
            // Guest RAM is coherent; invalidate our software instruction cache too.
            if ((op & 0x0fff0fff) == 0x0e070f15)
            {
                if (rd == 15)
                {
                    throw new RuntimeFault("invalid instruction-cache maintenance register");
                }
                if (thumbPageVersions != null)
                {
                    InvalidateThumbPages(0, Memory.MappedEnd);
                }
                return;
            }
            if ((op & 0x0ffffff0) == 0x012fff20)
            {
                if (rm == 15)
                {
                    throw new RuntimeFault("invalid BXJ PC operand");
                }
                // JMCR.JE is disabled in this ARM/Thumb user-mode profile.
                Branch(R[rm]);
                return;
            }
            if ((op & 0x0ffffff0) == 0x012fff10 || (op & 0x0ffffff0) == 0x012fff30)
            {
                bool link = (op & 0x20) != 0;
                if (link && rm == 15)
                {
                    throw new RuntimeFault("invalid BLX PC operand");
                }
                uint target = Reg(rm);
                Branch(target);
                if (link)
                {
                    R[14] = pc + 4;
                }
                return;
            }
            if ((op & 0x0e000000) == 0x0a000000)
            {
                if ((op & (1 << 24)) != 0)
                {
                    R[14] = pc + 4;
                }
                R[15] = pc + 8 + (uint)((int)(op << 8) >> 6);
                return;
            }
            if ((op & 0x0fc000f0) == 0x00000090)
            {
                int source = (int)(op >> 8 & 15);
                if (rn == 15 || rm == 15 || source == 15 || ((op & (1 << 21)) != 0 ? rd == 15 : rd != 0))
                {
                    throw new RuntimeFault("invalid multiply encoding");
                }
                uint value = Reg(rm) * Reg((int)(op >> 8 & 15));
                if ((op & (1 << 21)) != 0)
                {
                    value += Reg(rd);
                }
                R[rn] = value;
                if (set)
                {
                    Nz(value);
                }
                return;
            }
            if ((op & 0x0f8000f0) == 0x00800090)
            {
                if (rn == 15 || rd == 15 || rn == rd || rm == 15 || (op >> 8 & 15) == 15)
                {
                    throw new RuntimeFault("invalid long multiply encoding");
                }
                uint a = Reg(rm);
                uint b = Reg((int)(op >> 8 & 15));
                ulong value = (op & (1 << 22)) != 0 ? (ulong)((long)(int)a * (int)b) : (ulong)a * b;
                if ((op & (1 << 21)) != 0)
                {
                    value += ((ulong)R[rn] << 32) | R[rd];
                }
                R[rd] = (uint)value;
                R[rn] = (uint)(value >> 32);
                if (set)
                {
                    N = (value >> 63) != 0;
                    Z = value == 0;
                }
                return;
            }
            // Plain data processing excludes the overlapping miscellaneous and
            // extra-transfer encodings, which retain the detailed decoders below.
            if ((op & 0x0c000000) == 0 && (op & 0x01900000) != 0x01000000 &&
                ((op & 0x02000000) != 0 || (op & 0x90) != 0x90))
            {
                ExecuteArmData(op, rd, rn, rm, set);
                return;
            }
            // Filter by the encoding families accepted by the existing decoders.
            // Common arithmetic and literal loads need none of their detailed scans.
            uint instructionClass = op & 0x0e000000;
            bool status = instructionClass <= 0x02000000 && (op & 0x01900000) == 0x01000000;
            bool media = instructionClass == 0x06000000 ||
                (op & 0x0fb00000) == 0x03000000 || (op & 0x0f900ff0) == 0x01000050;
            bool multiply = instructionClass == 0x06000000 ||
                instructionClass == 0 && (op & 0x80) != 0;
            bool exclusive = (op & 0x0f000080) == 0x01000080;
            bool floating = (op & 0x0c000e00) == 0x0c000a00;
            if (status && ExecuteArmStatus(op) || media && ExecuteArmMedia(op) ||
                multiply && ExecuteArmMultiply(op) || exclusive && ExecuteArmExclusive(op) ||
                floating && ExecuteVfp(op))
            {
                return;
            }
            if ((op & 0x0fff0ff0) == 0x016f0f10)
            {
                if (rd == 15 || rm == 15)
                {
                    throw new RuntimeFault("CLZ cannot use PC");
                }
                uint value = Reg(rm);
                uint zeros = 0;
                while (zeros < 32 && (value & 0x80000000) == 0)
                {
                    zeros++;
                    value <<= 1;
                }
                R[rd] = zeros;
                return;
            }
            if ((op & 0x0ff00090) == 0x01000080)
            {
                int source = (int)(op >> 8 & 15);
                if (rn == 15 || rd == 15 || rm == 15 || source == 15)
                {
                    throw new RuntimeFault("SMLAxy cannot use PC");
                }
                int left = (short)(Reg(rm) >> ((op & 0x20) != 0 ? 16 : 0));
                int right = (short)(Reg(source) >> ((op & 0x40) != 0 ? 16 : 0));
                long result = (long)left * right + (int)Reg(rd);
                Q |= result > int.MaxValue || result < int.MinValue;
                R[rn] = unchecked((uint)result);
                return;
            }
            if ((op & 0x0ff0f090) == 0x01600080)
            {
                int source = (int)(op >> 8 & 15);
                if (rn == 15 || rm == 15 || source == 15)
                {
                    throw new RuntimeFault("SMULxy cannot use PC");
                }
                int left = (short)(Reg(rm) >> ((op & 0x20) != 0 ? 16 : 0));
                int right = (short)(Reg(source) >> ((op & 0x40) != 0 ? 16 : 0));
                R[rn] = unchecked((uint)(left * right));
                return;
            }
            if ((op & 0x0e000090) == 0x00000090)
            {
                int kind = (int)(op >> 5 & 3);
                bool pair = !set && kind >= 2;
                bool writeback = (op & (1 << 24)) == 0 || (op & (1 << 21)) != 0;
                bool immediate = (op & (1 << 22)) != 0;
                if (kind == 0 || rd == 15 || pair && ((rd & 1) != 0 || rd >= 14) ||
                    pair && (op & 0x01200000) == 0x00200000 ||
                    pair && kind == 2 && !immediate && (rm == rd || rm == rd + 1) ||
                    !immediate && (rm == 15 || (op & 0xf00) != 0) ||
                    writeback && (rn == 15 || rn == rd || pair && rn == rd + 1))
                {
                    throw new RuntimeFault("invalid extra transfer encoding");
                }
                uint offset = immediate ? ((op >> 4) & 0xf0) | (op & 15) : Reg(rm);
                uint address = Address(op, rn, offset, out uint adjusted);
                if (pair)
                {
                    if ((address & 3) != 0)
                    {
                        throw new RuntimeFault("unaligned doubleword transfer");
                    }
                    CheckData(address, 8);
                    if (kind == 2)
                    {
                        R[rd] = Load32(address);
                        R[rd + 1] = Load32(address + 4);
                    }
                    else
                    {
                        Store32(address, R[rd]);
                        Store32(address + 4, R[rd + 1]);
                    }
                }
                else if (!set)
                {
                    Store16(address, Reg(rd));
                }
                else
                {
                    R[rd] = kind switch
                    {
                        1 => Load16(address),
                        2 => (uint)(sbyte)Load8(address),
                        _ => (uint)(short)Load16(address)
                    };
                }
                Writeback(op, rn, adjusted);
                return;
            }
            if ((op & 0x0c000000) == 0x04000000)
            {
                uint offset = op & 0xfff;
                if ((op & (1 << 25)) != 0)
                {
                    if ((op & 16) != 0)
                    {
                        throw new RuntimeFault("invalid transfer shift");
                    }
                    offset = Shift(Reg(rm), (int)(op >> 5 & 3), (int)(op >> 7 & 31), true, out _);
                }
                bool octet = (op & (1 << 22)) != 0;
                bool writeback = (op & (1 << 24)) == 0 || (op & (1 << 21)) != 0;
                if (octet && rd == 15 || writeback && (rn == 15 || rn == rd) ||
                    set && rd == 15 && (op & 0x01200000) == 0x00200000 ||
                    (op & (1 << 25)) != 0 && rm == 15)
                {
                    throw new RuntimeFault("invalid single transfer encoding");
                }
                uint address = Address(op, rn, offset, out uint adjusted);
                if (set && rd == 15 && (address & 3) != 0)
                {
                    throw new RuntimeFault("unaligned ARM PC load");
                }
                if (set)
                {
                    Dest(rd, octet ? Load8(address) : Load32(address));
                }
                else if (octet)
                {
                    Store8(address, Reg(rd));
                }
                else
                {
                    Store32(address, Reg(rd));
                }
                Writeback(op, rn, adjusted);
                return;
            }
            if ((op & 0x0e000000) == 0x08000000)
            {
                ExecuteBlockTransfer(op, rn, set, CountTransferRegisters(op));
                return;
            }
            if ((op & 0x0c000000) == 0)
            {
                ExecuteArmData(op, rd, rn, rm, set);
                return;
            }
            throw new RuntimeFault("unsupported ARM instruction");
        }

        private static int CountTransferRegisters(uint mask)
        {
            mask &= 65535;
            mask -= (mask >> 1) & 0x5555u;
            mask = (mask & 0x3333u) + ((mask >> 2) & 0x3333u);
            mask = (mask + (mask >> 4)) & 0x0f0fu;
            return (int)((mask + (mask >> 8)) & 31u);
        }

        private void ExecuteBlockTransfer(uint op, int rn, bool set, int count)
        {
            if ((op & (1 << 22)) != 0 || (op & 65535) == 0)
            {
                throw new RuntimeFault("unsupported block transfer mode");
            }
            bool up = (op & (1 << 23)) != 0;
            bool pre = (op & (1 << 24)) != 0;
            uint basis = Reg(rn);
            uint address = up ? basis + (pre ? 4u : 0) : basis - (uint)count * 4 + (pre ? 0u : 4);
            bool writeback = (op & (1 << 21)) != 0;
            if (rn == 15 || !AllowUnalignedBlockTransfers && (address & 3) != 0 ||
                writeback && (op & (1u << rn)) != 0 &&
                (set || (op & ((1u << rn) - 1)) != 0))
            {
                throw new RuntimeFault("invalid block transfer base or alignment");
            }
            if (set && (op & 0x8000) != 0)
            {
                CheckThumbBranchInIt();
            }
            CheckData(address, (uint)count * 4);
            if (set && (op & 0x8000) != 0)
            {
                CheckBranchTarget(Load32(address + (uint)(count - 1) * 4));
            }
            for (int i = 0; i < 16; i++)
            {
                if ((op & (1u << i)) == 0)
                {
                    continue;
                }
                if (set)
                {
                    Dest(i, Load32(address));
                }
                else
                {
                    Store32(address, Reg(i));
                }
                address += 4;
            }
            if ((op & (1 << 21)) != 0)
            {
                R[rn] = up ? basis + (uint)count * 4 : basis - (uint)count * 4;
            }
        }

        private void ExecuteArmData(uint op, int rd, int rn, int rm, bool set)
        {
            int operation = (int)(op >> 21 & 15);
            if (operation >= 8 && operation <= 11 && !set)
            {
                throw new RuntimeFault("unsupported status/miscellaneous instruction");
            }
            if ((op & 0x02000010) == 0x00000010
                && (rd == 15 || rn == 15 || rm == 15 || (op >> 8 & 15) == 15))
            {
                throw new RuntimeFault("invalid ARM register-shift PC operand");
            }
            if (set && rd == 15 && (operation < 8 || operation > 11))
            {
                throw new RuntimeFault("unsupported exception return");
            }
            bool carry;
            uint operand;
            if ((op & (1 << 25)) != 0)
            {
                operand = Shift(op & 255, 3, (int)(op >> 8 & 15) * 2, false, out carry);
            }
            else
            {
                bool immediate = (op & 16) == 0;
                int amount = immediate ? (int)(op >> 7 & 31) : (int)(Reg((int)(op >> 8 & 15)) & 255);
                operand = Shift(Reg(rm), (int)(op >> 5 & 3), amount, immediate, out carry);
            }
            uint left = Reg(rn);
            bool arithmetic = operation >= 2 && operation <= 7 || operation == 10 || operation == 11;
            uint result = operation switch
            {
                0 or 8 => left & operand,
                1 or 9 => left ^ operand,
                2 or 10 => Add(left, ~operand, true, set),
                3 => Add(operand, ~left, true, set),
                4 or 11 => Add(left, operand, false, set),
                5 => Add(left, operand, C, set),
                6 => Add(left, ~operand, C, set),
                7 => Add(operand, ~left, C, set),
                12 => left | operand,
                13 => operand,
                14 => left & ~operand,
                _ => ~operand
            };
            if (set && !arithmetic)
            {
                Nz(result);
                C = carry;
            }
            if (operation < 8 || operation > 11)
            {
                Dest(rd, result);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Address(uint op, int rn, uint offset, out uint adjusted)
        {
            uint basis = Reg(rn);
            adjusted = (op & (1 << 23)) != 0 ? basis + offset : basis - offset;
            bool pre = (op & (1 << 24)) != 0;
            return pre ? adjusted : basis;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Writeback(uint op, int rn, uint adjusted)
        {
            if ((op & (1 << 24)) == 0 || (op & (1 << 21)) != 0)
            {
                R[rn] = adjusted;
            }
        }

        private void ExecuteThumb(ushort op)
        {
            if (op == 0xdfab && SemihostingCall != null)
            {
                // SYS_WRITEC has no defined return value. Preserve registers and flags.
                SemihostingCall(R[0], R[1]);
                return;
            }
            int rd = op & 7;
            int rn = op >> 3 & 7;
            int immediate = op >> 6 & 31;
            bool flags = ITState == 0;
            if ((op & 0xff00) == 0xbf00)
            {
                if ((op & 15) == 0)
                {
                    // A6.2.5: allocated and unallocated hints complete without
                    // suspension in the single-thread model; IT needs a nonzero mask.
                }
                else
                {
                    int condition = op >> 4 & 15;
                    int mask = op & 15;
                    if (ITState != 0 || condition == 15 || condition == 14 && (mask & (mask - 1)) != 0)
                    {
                        throw new RuntimeFault("invalid Thumb IT encoding");
                    }
                    ITState = (byte)op;
                }
            }
            else if ((op & 0xe000) == 0 && (op & 0x1800) != 0x1800)
            {
                R[rd] = Shift(R[rn], op >> 11 & 3, immediate, true, out bool carry);
                if (flags)
                {
                    C = carry;
                    Nz(R[rd]);
                }
            }
            else if ((op & 0xf800) == 0x1800)
            {
                uint right = (op & 0x400) != 0 ? (uint)(op >> 6 & 7) : R[op >> 6 & 7];
                R[rd] = (op & 0x200) != 0 ? Add(R[rn], ~right, true, flags) : Add(R[rn], right, false, flags);
            }
            else if ((op & 0xe000) == 0x2000)
            {
                rd = op >> 8 & 7;
                uint right = (uint)(op & 255);
                int kind = op >> 11 & 3;
                if (kind == 0)
                {
                    R[rd] = right;
                    if (flags)
                    {
                        Nz(right);
                    }
                }
                else if (kind == 1)
                {
                    Add(R[rd], ~right, true, true);
                }
                else
                {
                    R[rd] = kind == 2 ? Add(R[rd], right, false, flags) : Add(R[rd], ~right, true, flags);
                }
            }
            else if ((op & 0xfc00) == 0x4000)
            {
                int kind = op >> 6 & 15;
                uint left = R[rd];
                uint right = R[rn];
                bool carry = C;
                uint value = kind switch
                {
                    0 or 8 => left & right,
                    1 => left ^ right,
                    2 or 3 or 4 => Shift(left, kind - 2, (int)(right & 255), false, out carry),
                    5 => Add(left, right, C, flags),
                    6 => Add(left, ~right, C, flags),
                    7 => Shift(left, 3, (int)(right & 255), false, out carry),
                    9 => Add(0, ~right, true, flags),
                    10 => Add(left, ~right, true, true),
                    11 => Add(left, right, false, true),
                    12 => left | right,
                    13 => left * right,
                    14 => left & ~right,
                    _ => ~right
                };
                if (flags || kind is 8 or 10 or 11)
                {
                    Nz(value);
                }
                if (flags && kind is 2 or 3 or 4 or 7)
                {
                    C = carry;
                }
                if (kind != 8 && kind != 10 && kind != 11)
                {
                    R[rd] = value;
                }
            }
            else if ((op & 0xfc00) == 0x4400)
            {
                rd |= op >> 4 & 8;
                rn = op >> 3 & 15;
                int kind = op >> 8 & 3;
                uint right = Reg(rn);
                if (kind == 3)
                {
                    if ((op & 7) != 0 || (op & 0x80) != 0 && rn == 15)
                    {
                        throw new RuntimeFault("invalid Thumb BX/BLX encoding");
                    }
                    Branch(right);
                    if ((op & 0x80) != 0)
                    {
                        R[14] = (pc + 2) | 1;
                    }
                }
                else if (kind == 1)
                {
                    if ((rd < 8 && rn < 8) || rd == 15 || rn == 15)
                    {
                        throw new RuntimeFault("invalid Thumb high-register CMP encoding");
                    }
                    Add(Reg(rd), ~right, true, true);
                }
                else
                {
                    if (kind == 0 && rd == 15 && rn == 15)
                    {
                        throw new RuntimeFault("invalid Thumb ADD PC operands");
                    }
                    uint value = kind == 0 ? Reg(rd) + right : right;
                    if (rd == 15)
                    {
                        CheckThumbBranchInIt();
                        R[15] = value & ~1u;
                    }
                    else
                    {
                        R[rd] = value;
                    }
                }
            }
            else if ((op & 0xf800) == 0x4800)
            {
                R[op >> 8 & 7] = Load32(((pc + 4) & ~3u) + (uint)(op & 255) * 4);
            }
            else if ((op & 0xf000) == 0x5000)
            {
                uint address = R[rn] + R[op >> 6 & 7];
                int kind = op >> 9 & 7;
                switch (kind)
                {
                    case 0:
                        Store32(address, R[rd]);
                        break;
                    case 1:
                        Store16(address, R[rd]);
                        break;
                    case 2:
                        Store8(address, R[rd]);
                        break;
                    case 3:
                        R[rd] = (uint)(sbyte)Load8(address);
                        break;
                    case 4:
                        R[rd] = Load32(address);
                        break;
                    case 5:
                        R[rd] = Load16(address);
                        break;
                    case 6:
                        R[rd] = Load8(address);
                        break;
                    case 7:
                        R[rd] = (uint)(short)Load16(address);
                        break;
                }
            }
            else if ((op & 0xe000) == 0x6000 || (op & 0xf000) == 0x8000)
            {
                int size = (op & 0xf000) == 0x8000 ? 2 : (op & 0x1000) != 0 ? 1 : 4;
                uint address = R[rn] + (uint)(immediate * size);
                if ((op & 0x800) != 0)
                {
                    R[rd] = size == 4 ? Load32(address) : size == 2 ? Load16(address) : Load8(address);
                }
                else if (size == 4)
                {
                    Store32(address, R[rd]);
                }
                else if (size == 2)
                {
                    Store16(address, R[rd]);
                }
                else
                {
                    Store8(address, R[rd]);
                }
            }
            else if ((op & 0xf000) == 0x9000)
            {
                uint address = R[13] + (uint)(op & 255) * 4;
                rd = op >> 8 & 7;
                if ((op & 0x800) != 0)
                {
                    R[rd] = Load32(address);
                }
                else
                {
                    Store32(address, R[rd]);
                }
            }
            else if ((op & 0xf000) == 0xa000)
            {
                R[op >> 8 & 7] = ((op & 0x800) != 0 ? R[13] : (pc + 4) & ~3u) + (uint)(op & 255) * 4;
            }
            else if ((op & 0xf500) == 0xb100)
            {
                if (ITState != 0)
                {
                    throw new RuntimeFault("CBZ/CBNZ cannot execute inside IT");
                }
                bool nonzero = (op & 0x800) != 0;
                if ((R[rd] != 0) == nonzero)
                {
                    uint offset = (uint)((op >> 3 & 31) << 1 | (op >> 9 & 1) << 6);
                    R[15] = pc + 4 + offset;
                }
            }
            else if ((op & 0xff00) == 0xb200)
            {
                R[rd] = (op >> 6 & 3) switch
                {
                    0 => unchecked((uint)(short)R[rn]),
                    1 => unchecked((uint)(sbyte)R[rn]),
                    2 => (ushort)R[rn],
                    _ => (byte)R[rn]
                };
            }
            else if ((op & 0xff00) == 0xba00)
            {
                int kind = op >> 6 & 3;
                if (kind == 2)
                {
                    throw new RuntimeFault("invalid Thumb reverse encoding");
                }
                ExecuteArmMedia(0x06bf0f30 | (uint)(kind & 1) << 7 | (uint)(kind >> 1) << 22 |
                    (uint)rd << 12 | (uint)rn);
            }
            else if ((op & 0xff00) == 0xb000)
            {
                uint amount = (uint)(op & 127) * 4;
                R[13] = (op & 128) != 0 ? R[13] - amount : R[13] + amount;
            }
            else if ((op & 0xfff7) == 0xb650)
            {
                if (ITState != 0)
                {
                    throw new RuntimeFault("invalid SETEND inside Thumb IT block");
                }
                if ((op & 8) != 0)
                {
                    throw new RuntimeFault("unsupported big-endian execution profile");
                }
            }
            else if ((op & 0xf600) == 0xb400)
            {
                bool pop = (op & 0x800) != 0;
                uint mask = (uint)(op & 255) | ((op & 0x100) != 0 ? 1u << (pop ? 15 : 14) : 0);
                ExecuteArm((pop ? 0xe8bd0000u : 0xe92d0000u) | mask);
            }
            else if ((op & 0xf000) == 0xc000)
            {
                rn = op >> 8 & 7;
                bool load = (op & 0x800) != 0;
                bool writeback = !load || (op & (1 << rn)) == 0;
                ExecuteArm(0xe8800000 | (load ? 0x00100000u : 0) | (writeback ? 0x00200000u : 0) |
                    (uint)rn << 16 | (uint)(op & 255));
            }
            else if ((op & 0xf000) == 0xd000 && (op >> 8 & 15) < 14)
            {
                if (ITState != 0)
                {
                    throw new RuntimeFault("conditional branch cannot execute inside IT");
                }
                if (Condition(op >> 8 & 15))
                {
                    R[15] = pc + 4 + (uint)((sbyte)op * 2);
                }
            }
            else if ((op & 0xf800) == 0xe000)
            {
                CheckThumbBranchInIt();
                R[15] = pc + 4 + (uint)((short)(op << 5) >> 4);
            }
            else
            {
                throw new RuntimeFault("unsupported Thumb instruction");
            }
        }
    }
}
