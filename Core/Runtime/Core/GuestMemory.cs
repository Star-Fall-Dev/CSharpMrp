using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CSharpMrp
{
    public sealed partial class GuestMemory
    {
        private static readonly bool SupportsUnalignedPixelVectors = Vector.IsHardwareAccelerated &&
            (RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64);
        private readonly byte[] bytes;
        private readonly uint guestSize;
        private readonly bool isolatedVmStorage;
        private readonly System.Collections.Generic.List<(uint Address, uint Length)> vmFree =
            new System.Collections.Generic.List<(uint, uint)>();
        private int vmFreeStart;
        private uint vmAvailable;
        private int vmSkippedBlocks;
        private uint vmSkippedMaximum;
        private List<uint>? initializingVmAllocations;
        private uint staticCursor = 0x2000;
        internal event Action<uint, uint>? Written;
        internal event Action<uint, uint>? CodeWritten;
        internal bool HasWriteObservers => Written != null;
        private byte[]? watchedCodePages;
        public uint HeapBase => 0x10000;
        public uint HeapEnd => guestSize - 1024 * 1024;
        public uint StackTop => guestSize - 16;
        public uint VmAvailable => vmAvailable;
        public uint MappedEnd => (uint)bytes.Length;
        internal bool IsVmStorageAddress(uint address) => address >= guestSize && address < MappedEnd;

        public GuestMemory(int size = 8 * 1024 * 1024, bool isolatedVmStorage = false)
        {
            if (size < 2 * 1024 * 1024 || size > 32 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }
            guestSize = (uint)size;
            this.isolatedVmStorage = isolatedVmStorage;
            vmAvailable = isolatedVmStorage ? 8 * 1024 * 1024u : 0;
            bytes = new byte[size + vmAvailable];
            if (isolatedVmStorage)
            {
                vmFree.Add((guestSize, vmAvailable));
            }
            InitializeHeap();
        }

        internal void WatchCodePage(int page)
        {
            watchedCodePages ??= new byte[(MappedEnd + 4095) / 4096];
            watchedCodePages[page] = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyWritten(uint address, uint size)
        {
            var codeWritten = CodeWritten;
            if (codeWritten != null && ContainsWatchedCode(address, size))
            {
                // Invalidate every CPU owner before ordinary observers can
                // synchronously invoke a CPU against the modified code.
                codeWritten(address, size);
            }
            Written?.Invoke(address, size);
        }

        private bool ContainsWatchedCode(uint address, uint size)
        {
            if (watchedCodePages == null || size == 0) return false;
            int first = (int)(address >> 12);
            int last = (int)(((ulong)address + size - 1) >> 12);
            for (int page = first; page <= last; page++)
            {
                if (watchedCodePages[page] != 0) return true;
            }
            return false;
        }

        private enum VmInitialization { Clear, Copy, String, EncodedText }

        internal uint AllocateVm(uint size) => isolatedVmStorage ? AllocateIsolatedVm(size) : Allocate(size);

        internal uint TryAllocateVm(uint size) => isolatedVmStorage ? TryAllocateIsolatedVm(size, default, VmInitialization.Clear, null) : TryAllocate(size);

        private uint AllocateIsolatedVm(uint size, ReadOnlySpan<byte> data = default,
            VmInitialization initialization = VmInitialization.Clear, string? text = null)
        {
            uint address = TryAllocateIsolatedVm(size, data, initialization, text);
            return address != 0 ? address : throw new RuntimeFault("guest memory: VM storage allocation limit");
        }

        private uint TryAllocateIsolatedVm(uint size, ReadOnlySpan<byte> data, VmInitialization initialization, string? text)
        {
            uint address = ReserveIsolatedVm(size, out uint length);
            if (address == 0) return 0;

            bool writesObserved = Written != null;
            bool observed = writesObserved || (CodeWritten != null && ContainsWatchedCode(address, length));
            if (!observed && (!data.IsEmpty || text != null))
            {
                Span<byte> destination = bytes.AsSpan((int)address, (int)length);
                if (text != null)
                {
                    EncodeVmText(text, destination.Slice(0, (int)size));
                    destination.Slice((int)size).Clear();
                }
                else
                {
                    data.CopyTo(destination);
                    destination.Slice(data.Length).Clear();
                }
            }
            else
            {
                Array.Clear(bytes, (int)address, (int)length);
                if (observed)
                {
                    if (initialization == VmInitialization.String && data.IsEmpty && !writesObserved)
                        initialization = VmInitialization.Clear;
                    NotifyVmInitialization(address, length, size, data, initialization, text);
                }
            }
            return address;
        }

        private uint ReserveIsolatedVm(uint size, out uint length)
        {
            ulong aligned = ((ulong)Math.Max(size, 1) + 7) & ~7ul;
            length = 0;
            if (aligned > vmAvailable) return 0;

            length = (uint)aligned;
            // Resume only when every earlier block is still too small for this request.
            int index = length > vmSkippedMaximum ? vmSkippedBlocks : 0;
            uint skippedMaximum = index == 0 ? 0 : vmSkippedMaximum;
            int blockCount = vmFree.Count - vmFreeStart;
            for (; index < blockCount; index++)
            {
                int slot = vmFreeStart + index;
                var block = vmFree[slot];
                if (length > block.Length)
                {
                    skippedMaximum = Math.Max(skippedMaximum, block.Length);
                    continue;
                }
                bool consumesBlock = length == block.Length;
                if (index != 0)
                {
                    vmSkippedBlocks = index;
                    vmSkippedMaximum = skippedMaximum;
                }
                else if (consumesBlock && vmSkippedBlocks != 0)
                {
                    // A first-block fit provides no new prefix; removing it shifts the old prefix left.
                    vmSkippedBlocks--;
                }
                if (consumesBlock)
                {
                    RemoveVmFreeBlock(slot);
                }
                else
                {
                    vmFree[slot] = (block.Address + length, block.Length - length);
                }
                vmAvailable -= length;
                allocationRequests.Add(block.Address, Math.Max(size, 1));
                return block.Address;
            }
            vmSkippedBlocks = index;
            vmSkippedMaximum = skippedMaximum;
            return 0;
        }

        internal uint CopyVm(byte[] data)
        {
            if (isolatedVmStorage) return AllocateIsolatedVm((uint)data.Length, data, VmInitialization.Copy);
            uint address = Allocate((uint)data.Length);
            Write(address, data);
            return address;
        }

        internal uint CopyVmText(string text)
        {
            if (!isolatedVmStorage) return CopyVm(ScriptChunk.Bytes.GetBytes(text + "\0"));

            uint size = (uint)ScriptChunk.Bytes.GetByteCount(text) + 1;
            return AllocateIsolatedVm(size, initialization: VmInitialization.EncodedText, text: text);
        }

        private static void EncodeVmText(string text, Span<byte> destination)
        {
            int terminator = destination.Length - 1;
            ScriptChunk.Bytes.GetBytes(text.AsSpan(), destination.Slice(0, terminator));
            destination[terminator] = 0;
        }

        internal uint AllocateVmString(uint length, ReadOnlySpan<byte> data)
        {
            if (isolatedVmStorage) return AllocateIsolatedVm(length + 1, data, VmInitialization.String);

            bool observed = HasWriteObservers;
            uint address = Allocate(length + 1);
            if (observed || !data.IsEmpty)
            {
                WriteVmStringContent(address, length, data);
                Put8(address + length, 0);
            }
            return address;
        }

        private void WriteVmStringContent(uint address, uint length, ReadOnlySpan<byte> data)
        {
            // A write observer can alter the allocation after it is cleared.
            if (data.IsEmpty && length != 0) Fill(address, length, 0);
            else Write(address, data);
        }

        private void NotifyVmInitialization(uint address, uint length, uint size, ReadOnlySpan<byte> data,
            VmInitialization initialization, string? text)
        {
            var initializing = TrackVmInitialization(address);
            int frame = initializing.Count - 1;
            try
            {
                NotifyWritten(address, length);
                if (initialization != VmInitialization.Clear)
                {
                    RequireVmInitialization(initializing[frame]);
                    if (initialization == VmInitialization.String)
                    {
                        WriteVmStringContent(address, size - 1, data);
                        RequireVmInitialization(initializing[frame]);
                        Put8(address + size - 1, 0);
                    }
                    else if (text != null)
                    {
                        EncodeVmText(text, bytes.AsSpan((int)address, (int)size));
                        NotifyWritten(address, size);
                    }
                    else Write(address, data);
                }
                RequireVmInitialization(initializing[frame]);
            }
            catch
            {
                // An observer may have released this block and allocated another at the same address.
                if (initializing[frame] != 0) Free(address);
                throw;
            }
            finally { initializing.RemoveAt(frame); }
        }

        private List<uint> TrackVmInitialization(uint address)
        {
            var initializing = initializingVmAllocations ??= new List<uint>();
            initializing.Add(address);
            return initializing;
        }

        private static void RequireVmInitialization(uint address)
        {
            if (address == 0) throw new RuntimeFault("guest memory: VM allocation released during initialization");
        }

        private void FreeVm(uint address, uint length)
        {
            int index = 0;
            int upper = vmFree.Count - vmFreeStart;
            while (index < upper)
            {
                int middle = index + (upper - index) / 2;
                if (vmFree[vmFreeStart + middle].Address < address) index = middle + 1;
                else upper = middle;
            }

            int slot = vmFreeStart + index;
            bool joinsRight = slot < vmFree.Count && address + length == vmFree[slot].Address;
            if (joinsRight) length += vmFree[slot].Length;
            if (index > 0)
            {
                var previous = vmFree[slot - 1];
                if (previous.Address + previous.Length == address)
                {
                    uint mergedLength = previous.Length + length;
                    UpdateSkippedVmBlocks(index - 1, mergedLength, joinsRight ? 2 : 1);
                    vmFree[slot - 1] = (previous.Address, mergedLength);
                    if (joinsRight) RemoveVmFreeBlock(slot);
                    return;
                }
            }
            UpdateSkippedVmBlocks(index, length, joinsRight ? 1 : 0);
            if (joinsRight) vmFree[slot] = (address, length);
            else if (index == 0 && vmFreeStart != 0) vmFree[--vmFreeStart] = (address, length);
            else vmFree.Insert(slot, (address, length));
        }

        private void RemoveVmFreeBlock(int slot)
        {
            if (slot == vmFreeStart) vmFreeStart++;
            else vmFree.RemoveAt(slot);

            int remaining = vmFree.Count - vmFreeStart;
            if (remaining == 0)
            {
                vmFree.Clear();
                vmFreeStart = 0;
            }
            else if (vmFreeStart >= 64 && vmFreeStart >= remaining)
            {
                // Compact in batches to keep retired slots bounded without shifting on each head removal.
                vmFree.RemoveRange(0, vmFreeStart);
                vmFreeStart = 0;
            }
        }

        private void UpdateSkippedVmBlocks(int index, uint length, int replacedBlocks)
        {
            if (index >= vmSkippedBlocks) return;
            if (length > vmSkippedMaximum)
            {
                vmSkippedBlocks = index;
                return;
            }
            // One small block replaces zero, one, or two blocks, but only old prefix entries count.
            vmSkippedBlocks += 1 - Math.Min(replacedBlocks, vmSkippedBlocks - index);
        }

        // Session-owned ABI globals occupy reserved memory, never guest heap nodes.
        internal uint AllocateStatic(uint size)
        {
            ulong length = ((ulong)Math.Max(size, 1) + 7) & ~7ul;
            if ((ulong)staticCursor + length > HeapBase)
            {
                throw new RuntimeFault("guest memory: static ABI storage limit");
            }
            uint address = staticCursor;
            staticCursor += (uint)length;
            return address;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Check(uint address, uint size)
        {
            if (!ContainsRange(address, size))
            {
                throw new RuntimeFault($"guest memory: invalid range 0x{address:x8}+{size}");
            }
        }

        internal bool ContainsRange(uint address, uint size) =>
            address >= 0x1000 && (ulong)address + size <= (ulong)bytes.Length;

        internal bool MatchesInstructions(uint address, byte[] instructions, ulong[] words)
        {
            if (address < 0x1000 || (ulong)address + (uint)instructions.Length > (ulong)bytes.Length)
            {
                return false;
            }
            int offset = (int)address;
            int index = 0;
            while (index + 8 <= instructions.Length)
            {
                if (BitConverter.ToUInt64(bytes, offset + index) != words[index / 8])
                {
                    return false;
                }
                index += 8;
            }
            while (index < instructions.Length)
            {
                if (bytes[offset + index] != instructions[index])
                {
                    return false;
                }
                index++;
            }
            return true;
        }

        // CPU data transfers may use the reserved, session-local zero page.
        // Public host-memory access and instruction fetches retain strict Check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CpuHalfWordMapped(uint address, bool allowLowPage)
        {
            if (address < 0x1000)
            {
                return allowLowPage && address <= 0xffe;
            }
            return (ulong)address + 2 <= (ulong)bytes.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadCpu16Pair(uint first, uint second, bool allowLowPage,
            out uint firstValue, out uint secondValue)
        {
            firstValue = 0;
            secondValue = 0;
            if (!CpuHalfWordMapped(first, allowLowPage) || !CpuHalfWordMapped(second, allowLowPage))
            {
                return false;
            }
            firstValue = (uint)(bytes[first] | bytes[first + 1] << 8);
            secondValue = (uint)(bytes[second] | bytes[second + 1] << 8);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CheckCpuData(uint address, uint size, bool allowLowPage)
        {
            if (address < 0x1000 && allowLowPage && (ulong)address + size <= 0x1000)
            {
                return;
            }
            Check(address, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint LoadCpuData(uint address, uint size, bool allowLowPage)
        {
            if (address >= 0x81080004 && TryReadKeypad(address, size, out uint keypad))
            {
                return keypad;
            }
            CheckCpuData(address, size, allowLowPage);
            switch (size)
            {
                case 1:
                    return bytes[address];
                case 2:
                    return (uint)(bytes[address] | bytes[address + 1] << 8);
                case 4:
                    return (uint)(bytes[address] | bytes[address + 1] << 8 |
                        bytes[address + 2] << 16 | bytes[address + 3] << 24);
            }
            uint value = 0;
            for (uint index = 0; index < size; index++)
            {
                value |= (uint)bytes[address + index] << (int)(index * 8);
            }
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void StoreCpuData(uint address, uint value, uint size, bool allowLowPage)
        {
            CheckCpuData(address, size, allowLowPage);
            NoteHeapMetadataWrite(address, size);
            switch (size)
            {
                case 1:
                    bytes[address] = (byte)value;
                    break;
                case 2:
                    bytes[address] = (byte)value;
                    bytes[address + 1] = (byte)(value >> 8);
                    break;
                case 4:
                    bytes[address] = (byte)value;
                    bytes[address + 1] = (byte)(value >> 8);
                    bytes[address + 2] = (byte)(value >> 16);
                    bytes[address + 3] = (byte)(value >> 24);
                    break;
                default:
                    for (uint index = 0; index < size; index++)
                    {
                        bytes[address + index] = (byte)(value >> (int)(index * 8));
                    }
                    break;
            }
            NotifyWritten(address, size);
        }

        public uint CopyIn(byte[] data)
        {
            uint address = Allocate((uint)data.Length);
            Write(address, data);
            return address;
        }

        public void Write(uint address, byte[] data)
        {
            Write(address, data, 0, data.Length);
        }

        internal int ReadMemoryFile(MemoryStream input, uint address, uint size)
        {
            Check(address, size);
            int count = input.Read(bytes, (int)address, (int)size);
            NoteHeapMetadataWrite(address, (uint)count);
            NotifyWritten(address, (uint)count);
            return count;
        }

        internal uint ReadWholeVmFile(Stream input, int length)
        {
            uint size = (uint)length + 1;
            uint alignedLength = 0;
            uint address = isolatedVmStorage ? ReserveIsolatedVm(size, out alignedLength) : Allocate(size);
            if (address == 0) throw new RuntimeFault("guest memory: VM storage allocation limit");

            // Other stream implementations can call back while reading and retain the original clear-first behavior.
            bool clearFirst = isolatedVmStorage && (Written != null ||
                (CodeWritten != null && ContainsWatchedCode(address, alignedLength)) ||
                (input.GetType() != typeof(FileStream) && input.GetType() != typeof(MemoryStream)));
            List<uint>? initializing = clearFirst ? TrackVmInitialization(address) : null;
            int frame = (initializing?.Count ?? 0) - 1;
            try
            {
                if (clearFirst)
                {
                    Array.Clear(bytes, (int)address, (int)alignedLength);
                    NotifyWritten(address, alignedLength);
                    RequireVmInitialization(initializing![frame]);
                }
                ReadVmFileBytes(input, address, length, initializing, frame);
                if (isolatedVmStorage && !clearFirst)
                    bytes.AsSpan((int)address + length, (int)alignedLength - length).Clear();
                NotifyWritten(address, (uint)length);
                if (initializing != null) RequireVmInitialization(initializing[frame]);
                return address;
            }
            catch
            {
                if (initializing == null || initializing[frame] != 0) Free(address);
                throw;
            }
            finally { initializing?.RemoveAt(frame); }
        }

        private void ReadVmFileBytes(Stream input, uint address, int length, List<uint>? initializing, int frame)
        {
            Span<byte> destination = bytes.AsSpan((int)address, length);
            while (!destination.IsEmpty)
            {
                // Cap the request for streams that implement Span reads via a pooled array.
                int count = input.Read(destination.Slice(0, Math.Min(destination.Length, 64 * 1024)));
                if (initializing != null) RequireVmInitialization(initializing[frame]);
                if (count == 0) throw new IOException("resource file shortened during read");
                destination = destination.Slice(count);
            }
        }

        internal void Write(uint address, byte[] data, int offset, int length) =>
            Write(address, data.AsSpan(offset, length));

        internal void Write(uint address, ReadOnlySpan<byte> data)
        {
            uint length = (uint)data.Length;
            Check(address, length);
            NoteHeapMetadataWrite(address, length);
            data.CopyTo(bytes.AsSpan((int)address, data.Length));
            NotifyWritten(address, length);
        }

        internal void WritePadded(uint address, uint size, ReadOnlySpan<byte> data)
        {
            Check(address, size);
            var destination = bytes.AsSpan((int)address, (int)size);
            int count = Math.Min(destination.Length, data.Length);
            NoteHeapMetadataWrite(address, size);
            data.Slice(0, count).CopyTo(destination);
            destination.Slice(count).Clear();
            NotifyWritten(address, size);
        }

        internal void WritePaddedPixels(uint address, uint size, ReadOnlySpan<byte> data, ushort pixel)
        {
            Check(address, size);
            NoteHeapMetadataWrite(address, size);
            Span<byte> destination = bytes.AsSpan((int)address, (int)size);
            data.CopyTo(destination);
            ushort stored = BitConverter.IsLittleEndian ? pixel : (ushort)(pixel >> 8 | pixel << 8);
            MemoryMarshal.Cast<byte, ushort>(destination.Slice(data.Length)).Fill(stored);
            NotifyWritten(address, size);
        }

        internal void WritePixelBytes(uint address, ReadOnlySpan<byte> data)
        {
            if (Written == null)
            {
                Write(address, data);
                return;
            }
            // Keep each halfword visible to the observer in write order.
            for (int i = 0; i < data.Length; i += 2)
                Put16(address + (uint)i, (uint)(data[i] | data[i + 1] << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GrayRgb565(uint color)
        {
            uint gray = ((color >> 11) * 60 + ((color >> 6) & 31) * 118 + (color & 31) * 22) / 25;
            return (gray & 248) << 8 | (gray & 252) << 3 | gray >> 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort RasterRgb565(ushort source, uint target, int operation) => unchecked((ushort)(operation switch
        {
            0 => source | target,
            1 => source ^ target,
            3 or 9 => ~(uint)source,
            4 => ~(uint)source | target,
            5 => ~(uint)source & target,
            6 => source,
            7 => source & target,
            8 => GrayRgb565(source),
            _ => throw new RuntimeFault("invalid bitmap operation")
        }));

        private static int WriteRasterVectors(ReadOnlySpan<ushort> source, Span<ushort> target, int operation)
        {
            ReadOnlySpan<Vector<ushort>> input = MemoryMarshal.Cast<ushort, Vector<ushort>>(source);
            Span<Vector<ushort>> output = MemoryMarshal.Cast<ushort, Vector<ushort>>(target);
            for (int index = 0; index < input.Length; index++)
            {
                Vector<ushort> value = input[index];
                Vector<ushort> previous = output[index];
                output[index] = operation switch
                {
                    0 => value | previous,
                    1 => value ^ previous,
                    3 => ~value,
                    4 => ~value | previous,
                    5 => ~value & previous,
                    7 => value & previous,
                    _ => throw new RuntimeFault("invalid unkeyed bitmap operation")
                };
            }
            return input.Length * Vector<ushort>.Count;
        }

        private static void WriteRasterWords(ReadOnlySpan<ushort> source, Span<ushort> target, int operation)
        {
            switch (operation)
            {
                case 0:
                    for (int i = 0; i < source.Length; i++) target[i] |= source[i];
                    break;
                case 1:
                    for (int i = 0; i < source.Length; i++) target[i] ^= source[i];
                    break;
                case 3:
                    for (int i = 0; i < source.Length; i++) target[i] = unchecked((ushort)~source[i]);
                    break;
                case 4:
                    for (int i = 0; i < source.Length; i++) target[i] |= unchecked((ushort)~source[i]);
                    break;
                case 5:
                    for (int i = 0; i < source.Length; i++) target[i] &= unchecked((ushort)~source[i]);
                    break;
                case 7:
                    for (int i = 0; i < source.Length; i++) target[i] &= source[i];
                    break;
                default:
                    throw new RuntimeFault("invalid unkeyed bitmap operation");
            }
        }

        // The caller supplies an aligned little-endian row outside heap metadata and batches only unobserved writes.
        internal void WriteRasterPixelRow(uint address, ReadOnlySpan<byte> data, int operation, ushort transparent, bool keyed)
        {
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(data);
            int first = 0;
            if (keyed)
            {
                while (first < source.Length && source[first] == transparent) first++;
                if (first == source.Length) return;
            }

            Check(address, (uint)data.Length);
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan((int)address, data.Length));
            if (!keyed)
            {
                int compared = SupportsUnalignedPixelVectors && source.Length >= Vector<ushort>.Count
                    ? WriteRasterVectors(source, target, operation) : 0;
                if (compared < source.Length)
                    WriteRasterWords(source.Slice(compared), target.Slice(compared), operation);
                NotifyWritten(address, (uint)data.Length);
                return;
            }

            int last = first;
            ushort invert = operation == 9 ? ushort.MaxValue : (ushort)0;
            for (int pixel = first; pixel < source.Length; pixel++)
            {
                ushort color = source[pixel];
                if (color == transparent) continue;
                target[pixel] = (ushort)(color ^ invert);
                last = pixel;
            }
            NotifyWritten(address + (uint)first * 2, (uint)(last - first + 1) * 2);
        }

        internal int CountRgb565CollisionVectors(uint source, uint target, int count, ushort transparent, ushort background,
            out uint collisions)
        {
            collisions = 0;
            uint size = (uint)count * 2;
            if (!SupportsUnalignedPixelVectors || count < Vector<ushort>.Count ||
                ((source | target) & 1) != 0 || (ulong)source + size > MappedEnd)
            {
                return 0;
            }

            ReadOnlySpan<Vector<ushort>> input = MemoryMarshal.Cast<byte, Vector<ushort>>(ReadSpan(source, size));
            ReadOnlySpan<Vector<ushort>> output = MemoryMarshal.Cast<byte, Vector<ushort>>(ReadSpan(target, size));
            var omittedInput = new Vector<ushort>(transparent);
            var omittedOutput = new Vector<ushort>(background);
            Vector<ushort> lanes = Vector<ushort>.Zero;
            for (int i = 0; i < input.Length; i++)
            {
                Vector<ushort> omitted = Vector.Equals(input[i], omittedInput) | Vector.Equals(output[i], omittedOutput);
                lanes += Vector.AndNot(Vector<ushort>.One, omitted);
            }
            int vectorWidth = Vector<ushort>.Count;
            for (int i = 0; i < vectorWidth; i++) collisions += lanes[i];
            return input.Length * vectorWidth;
        }

        // Used for aligned, unobserved RGB565 rows with nonconsecutive source pixels.
        internal void WriteStridedPixelRow(uint address, ReadOnlySpan<ushort> source, int start, int step, int count)
        {
            uint size = (uint)count * 2;
            Check(address, size);
            NoteHeapMetadataWrite(address, size);
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan((int)address, (int)size));
            for (int i = 0; i < target.Length; i++, start += step) target[i] = source[start];
            NotifyWritten(address, size);
        }

        // Bit 31 marks the first pixel. The caller supplies aligned little-endian pixels outside heap metadata.
        internal void WriteGlyphRow(uint address, int width, uint marks, ushort color)
        {
            uint size = (uint)width * 2;
            Check(address, size);
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan((int)address, (int)size));
            for (int i = 0; i < target.Length; i++, marks <<= 1)
            {
                if ((marks & 0x80000000u) != 0) target[i] = color;
            }
            NotifyWritten(address, size);
        }

        public byte[] Read(uint address, uint size)
        {
            Check(address, size);
            var result = new byte[size];
            Array.Copy(bytes, address, result, 0, size);
            return result;
        }

        internal ReadOnlySpan<byte> ReadSpan(uint address, uint size)
        {
            Check(address, size);
            return new ReadOnlySpan<byte>(bytes, (int)address, (int)size);
        }

        internal void Copy(uint destination, uint source, uint size, bool preserveOverlap)
        {
            if (size == 0)
            {
                return;
            }
            Check(source, size);
            Check(destination, size);
            NoteHeapMetadataWrite(destination, size);
            if (!preserveOverlap && destination > source && destination - source < size)
            {
                for (uint index = 0; index < size; index++)
                {
                    bytes[destination + index] = bytes[source + index];
                }
            }
            else
            {
                Array.Copy(bytes, source, bytes, destination, size);
            }
            NotifyWritten(destination, size);
        }

        public uint U8(uint address)
        {
            Check(address, 1);
            return bytes[address];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint U16(uint address)
        {
            Check(address, 2);
            return (uint)(bytes[address] | bytes[address + 1] << 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint U32(uint address)
        {
            Check(address, 4);
            return (uint)(bytes[address] | bytes[address + 1] << 8 |
                bytes[address + 2] << 16 | bytes[address + 3] << 24);
        }

        public void Put8(uint address, uint value)
        {
            Check(address, 1);
            NoteHeapMetadataWrite(address, 1);
            bytes[address] = (byte)value;
            NotifyWritten(address, 1);
        }

        public void Put16(uint address, uint value)
        {
            Check(address, 2);
            NoteHeapMetadataWrite(address, 2);
            bytes[address] = (byte)value;
            bytes[address + 1] = (byte)(value >> 8);
            NotifyWritten(address, 2);
        }

        public void Put32(uint address, uint value)
        {
            Check(address, 4);
            NoteHeapMetadataWrite(address, 4);
            bytes[address] = (byte)value;
            bytes[address + 1] = (byte)(value >> 8);
            bytes[address + 2] = (byte)(value >> 16);
            bytes[address + 3] = (byte)(value >> 24);
            NotifyWritten(address, 4);
        }

        internal void Fill(uint address, uint size, byte value)
        {
            if (size == 0) return;
            Check(address, size);
            NoteHeapMetadataWrite(address, size);
            bytes.AsSpan((int)address, (int)size).Fill(value);
            NotifyWritten(address, size);
        }

        internal void Fill16(uint address, int count, ushort value)
        {
            // Observers see each store as it happens. Odd addresses are also valid
            // guest memory and can be written bytewise on any host.
            if (Written != null || (address & 1) != 0 || !BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++) Put16(address + (uint)i * 2, value);
                return;
            }
            uint size = (uint)count * 2;
            Check(address, size);
            NoteHeapMetadataWrite(address, size);
            MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan((int)address, (int)size)).Fill(value);
            NotifyWritten(address, size);
        }

        private static ushort ScaleRgb565Value(uint value, int red, int green, int blue) =>
            unchecked((ushort)(((value & 0xf800) * (uint)red >> 8) & 0xf800 |
                ((value & 0x07e0) * (uint)green >> 8) & 0x07e0 |
                ((value & 0x001f) * (uint)blue >> 8) & 0x001f));

        internal void ScaleRgb565(uint address, int count, int red, int green, int blue)
        {
            // Observers can inspect or modify later pixels during each store.
            if (Written != null || (address & 1) != 0 || !BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++, address += 2)
                    Put16(address, ScaleRgb565Value(U16(address), red, green, blue));
                return;
            }

            uint size = (uint)count * 2;
            Check(address, size);
            NoteHeapMetadataWrite(address, size);
            Span<ushort> pixels = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan((int)address, (int)size));
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = ScaleRgb565Value(pixels[i], red, green, blue);
            NotifyWritten(address, size);
        }

        internal ReadOnlySpan<byte> CStringBytes(uint address, int limit = int.MaxValue)
        {
            if (limit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }
            Check(address, 1);
            int count = Math.Min(limit, bytes.Length - (int)address);
            var span = bytes.AsSpan((int)address, count);
            int end = span.IndexOf((byte)0);
            if (end >= 0)
            {
                return span.Slice(0, end);
            }
            throw new RuntimeFault("guest memory: unterminated string");
        }

        public string CString(uint address, int limit = int.MaxValue) =>
            ScriptChunk.Bytes.GetString(CStringBytes(address, limit));
    }
}
