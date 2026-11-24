using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    public sealed partial class GuestMemory
    {
        // Reserved ABI cells below the allocatable heap. Nodes contain next offset + length.
        public const uint HeapHead = 0x1000;
        public const uint HeapCells = 0x1008;
        public const uint HeapMinimum = 0x1018;
        public const uint HeapTop = 0x101c;
        private readonly Dictionary<uint, uint> managed = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> allocationRequests = new Dictionary<uint, uint>();
        private readonly HashSet<uint> nativeAllocationRequests = new HashSet<uint>();
        private readonly SortedList<uint, uint> externalRam = new SortedList<uint, uint>();
        private bool heapMetadataMutation;
        internal Action<string>? CompatibilityDiagnostic;
        public uint Available => U32(HeapCells + 12);
        public uint MinimumAvailable => U32(HeapMinimum);
        public uint HighWater => U32(HeapTop);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NoteHeapMetadataWrite(uint address, uint length)
        {
            if (heapMetadataMutation || length == 0 || address >= HeapTop + 4 ||
                (ulong)address + length <= HeapHead)
            {
                return;
            }
            foreach (uint nativeAddress in nativeAllocationRequests)
            {
                allocationRequests.Remove(nativeAddress);
            }
            nativeAllocationRequests.Clear();
        }

        private void InitializeHeap()
        {
            uint capacity = HeapEnd - HeapBase;
            Put32(HeapHead, 0);
            Put32(HeapHead + 4, 0);
            Put32(HeapCells, HeapBase);
            Put32(HeapCells + 4, capacity);
            Put32(HeapCells + 8, HeapEnd);
            Put32(HeapCells + 12, capacity);
            Put32(HeapMinimum, capacity);
            Put32(HeapTop, 0);
            Put32(HeapBase, capacity);
            Put32(HeapBase + 4, capacity);
        }

        // Loaders temporarily reorder nodes. Validate ranges/cycles independently of order.
        private uint ValidateHeap(bool restoreOrder = false)
        {
            uint end = U32(HeapCells + 8);
            if (U32(HeapCells) != HeapBase || U32(HeapCells + 4) != HeapEnd - HeapBase ||
                end < HeapBase || end > HeapEnd || (end & 3) != 0 || Available > HeapEnd - HeapBase)
            {
                throw new RuntimeFault("guest heap: invalid metadata");
            }
            uint limit = end - HeapBase;
            uint offset = U32(HeapHead);
            for (int count = 0; offset != limit; count++)
            {
                var (next, length) = ReadFreeNode(offset, limit, count);
                if (next < offset + length)
                {
                    ValidateUnorderedHeap(limit);
                    if (restoreOrder) RestoreFreeNodeOrder(limit);
                    break;
                }
                offset = next;
            }
            return limit;
        }

        private (uint Next, uint Length) ReadFreeNode(uint offset, uint limit, int count)
        {
            if (count >= 65536 || (offset & 3) != 0 || (ulong)offset + 8 > limit)
                throw new RuntimeFault("guest heap: invalid free-list node");
            uint next = U32(HeapBase + offset);
            uint length = U32(HeapBase + offset + 4);
            if (length < 8 || (length & 3) != 0 || (ulong)offset + length > limit || next > limit)
                throw new RuntimeFault($"guest heap: overlapping, cyclic or out-of-range free list " +
                    $"node={HeapBase + offset:x8} next={next:x8} length={length} limit={limit:x8}");
            ProtectManaged(HeapBase + offset, length, false);
            return (next, length);
        }

        private void ValidateUnorderedHeap(uint limit)
        {
            uint offset = U32(HeapHead);
            var visited = new HashSet<uint>();
            var ranges = new List<(uint Start, uint Length)>();
            while (offset != limit)
            {
                if (!visited.Add(offset))
                {
                    throw new RuntimeFault("guest heap: invalid free-list node");
                }
                var (next, length) = ReadFreeNode(offset, limit, visited.Count - 1);
                ranges.Add((offset, length));
                offset = next;
            }
            ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
            for (int index = 1; index < ranges.Count; index++)
            {
                if ((ulong)ranges[index - 1].Start + ranges[index - 1].Length > ranges[index].Start)
                {
                    if (!isolatedVmStorage)
                    {
                        throw new RuntimeFault("guest heap: overlapping free nodes");
                    }
                    NormalizeFreeNodes(ranges, limit);
                    return;
                }
            }
        }

        private void NormalizeFreeNodes(List<(uint Start, uint Length)> ranges, uint limit)
        {
            var originalOrder = new List<uint>();
            uint offset = U32(HeapHead);
            while (offset != limit)
            {
                originalOrder.Add(offset);
                offset = U32(HeapBase + offset);
            }
            var merged = new List<(uint Start, uint Length)>();
            uint overlap = 0;
            foreach (var range in ranges)
            {
                if (merged.Count != 0 && (ulong)merged[merged.Count - 1].Start + merged[merged.Count - 1].Length > range.Start)
                {
                    var previous = merged[merged.Count - 1];
                    uint end = Math.Max(previous.Start + previous.Length, range.Start + range.Length);
                    overlap += previous.Length + range.Length - (end - previous.Start);
                    merged[merged.Count - 1] = (previous.Start, end - previous.Start);
                }
                else
                {
                    merged.Add(range);
                }
            }
            merged.Sort((left, right) => originalOrder.FindIndex(value => value >= left.Start && value < left.Start + left.Length)
                .CompareTo(originalOrder.FindIndex(value => value >= right.Start && value < right.Start + right.Length)));
            if (overlap > Available)
            {
                throw new RuntimeFault("guest heap: overlapping free nodes exceed available accounting");
            }
            heapMetadataMutation = true;
            try
            {
                Put32(HeapHead, merged[0].Start);
                for (int index = 0; index < merged.Count; index++)
                {
                    Put32(HeapBase + merged[index].Start, index + 1 < merged.Count ? merged[index + 1].Start : limit);
                    Put32(HeapBase + merged[index].Start + 4, merged[index].Length);
                }
                Put32(HeapCells + 12, Available - overlap);
            }
            finally
            {
                heapMetadataMutation = false;
            }
            CompatibilityDiagnostic?.Invoke($"compatibility heap normalization: overlapping free nodes unioned, " +
                $"duplicate bytes={overlap}; first-fit component order preserved");
        }

        private void ProtectManaged(uint address, uint length, bool releasing)
        {
            foreach (var owned in managed)
            {
                if (address < (ulong)owned.Key + owned.Value && owned.Key < (ulong)address + length &&
                    !(releasing && owned.Key == address && owned.Value == length))
                {
                    throw new RuntimeFault($"guest heap: operation overlaps a live managed allocation " +
                        $"range={address:x8}+{length}, owned={owned.Key:x8}+{owned.Value}");
                }
            }
        }

        private uint AllocateHeap(uint size, bool native)
        {
            uint limit = ValidateHeap();
            ulong aligned = ((ulong)(native ? size : Math.Max(size, 1)) + 7) & ~7ul;
            if (aligned == 0 || aligned > Available || (native && aligned == Available))
            {
                return 0;
            }
            uint length = (uint)aligned;
            uint previous = HeapHead;
            uint offset = U32(previous);
            while (offset != limit)
            {
                uint address = HeapBase + offset;
                uint sizeHere = U32(address + 4);
                if (FitsFreeNode(sizeHere, length))
                {
                    uint following = U32(address);
                    if (sizeHere > length)
                    {
                        // Preserve the allocated node bytes: native loaders inspect them.
                        Put32(address + length, following);
                        Put32(address + length + 4, sizeHere - length);
                        following = offset + length;
                    }
                    heapMetadataMutation = true;
                    try
                    {
                        Put32(previous, following);
                        Put32(HeapCells + 12, Available - length);
                        Put32(HeapMinimum, Math.Min(MinimumAvailable, Available));
                        Put32(HeapTop, Math.Max(HighWater, following));
                        if (!native)
                        {
                            managed.Add(address, length);
                            Array.Clear(bytes, (int)address, (int)length);
                            NotifyWritten(address, length);
                        }
                        // Guest code can manage the free list itself and return a previously
                        // registered address to us without going through NativeFree.
                        ForgetExternalRam(address, length);
                        allocationRequests.Add(address, native ? size : Math.Max(size, 1));
                        if (native)
                        {
                            nativeAllocationRequests.Add(address);
                        }
                    }
                    finally
                    {
                        heapMetadataMutation = false;
                    }
                    return address;
                }
                previous = address;
                offset = U32(previous);
            }
            return 0;
        }

        private static bool FitsFreeNode(uint available, uint length) =>
            available == length || (available > length && available - length >= 8);

        public uint Allocate(uint size)
        {
            uint address = TryAllocate(size);
            return address != 0 ? address : throw new RuntimeFault("guest memory: allocation limit");
        }

        internal uint TryAllocate(uint size)
        {
            return AllocateHeap(size, false);
        }

        public uint NativeAllocate(uint size)
        {
            return AllocateHeap(size, true);
        }

        internal bool TryAdoptNativeAllocation(uint address, uint size)
        {
            if (!nativeAllocationRequests.Contains(address) || !allocationRequests.TryGetValue(address, out uint requested) ||
                requested != size)
            {
                return false;
            }
            managed.Add(address, (size + 7) & ~7u);
            nativeAllocationRequests.Remove(address);
            return true;
        }

        internal uint AllocateExternalRam(uint size)
        {
            uint address = NativeAllocate(size);
            if (address != 0) externalRam.Add(address, size);
            return address;
        }

        internal bool ReleaseExternalRam(uint address, uint size)
        {
            return externalRam.TryGetValue(address, out uint requested) && requested == size &&
                NativeFree(address, size, false);
        }

        private void ForgetExternalRam(uint address, uint length)
        {
            if (externalRam.Count == 0) return;
            var starts = externalRam.Keys;
            uint end = address + length;
            int low = 0, high = starts.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (starts[middle] < end) low = middle + 1;
                else high = middle;
            }
            // Registered blocks do not overlap. Stop at the first block below this range.
            for (int i = low - 1; i >= 0; i--)
            {
                if (starts[i] + ((externalRam.Values[i] + 7) & ~7u) <= address) break;
                externalRam.RemoveAt(i);
            }
        }

        internal bool TryGetAllocationRemaining(uint address, out uint remaining)
        {
            if (allocationRequests.TryGetValue(address, out remaining)) return true;
            foreach (var allocation in allocationRequests)
            {
                if (address >= allocation.Key && (ulong)address < (ulong)allocation.Key + allocation.Value)
                {
                    remaining = allocation.Value - (address - allocation.Key);
                    return true;
                }
            }
            foreach (var allocation in allocationRequests)
            {
                ulong aligned = ((ulong)allocation.Value + 7) & ~7ul;
                if (address >= allocation.Key && (ulong)address <= (ulong)allocation.Key + aligned)
                {
                    remaining = 0;
                    return true;
                }
            }
            remaining = 0;
            return false;
        }

        internal bool ExceedsNativeAllocation(uint address, uint length)
        {
            if (nativeAllocationRequests.Contains(address))
                return length > ((allocationRequests[address] + 7) & ~7u);
            foreach (uint start in nativeAllocationRequests)
            {
                ulong size = ((ulong)allocationRequests[start] + 7) & ~7ul;
                if (address >= start && (ulong)address < (ulong)start + size)
                {
                    return (ulong)address + length > (ulong)start + size;
                }
            }
            return false;
        }

        public void Free(uint address)
        {
            if (address == 0)
            {
                return;
            }
            if (isolatedVmStorage && address >= guestSize)
            {
                if (!allocationRequests.Remove(address, out uint requested))
                    throw new RuntimeFault($"guest memory: invalid free 0x{address:x8}");
                if (initializingVmAllocations is { Count: > 0 } initializing)
                {
                    int pending = initializing.LastIndexOf(address);
                    if (pending >= 0) initializing[pending] = 0;
                }
                uint length = (requested + 7) & ~7u;
                vmAvailable += length;
                FreeVm(address, length);
                return;
            }
            if (!managed.TryGetValue(address, out uint size))
                throw new RuntimeFault($"guest memory: invalid free 0x{address:x8}");
            NativeFree(address, size);
        }

        private (uint Limit, uint Length) ValidateFree(uint address, uint size)
        {
            uint limit = ValidateHeap();
            ulong aligned = ((ulong)size + 7) & ~7ul;
            if (address < HeapBase || (address & 3) != 0 || (ulong)address + aligned > HeapBase + limit ||
                (ulong)Available + aligned > HeapEnd - HeapBase)
            {
                throw new RuntimeFault("guest heap: invalid free range");
            }
            uint length = (uint)aligned;
            ProtectManaged(address, length, true);
            uint offset = U32(HeapHead);
            while (offset != limit)
            {
                uint node = HeapBase + offset;
                uint following = U32(node);
                if (following <= offset)
                {
                    throw new RuntimeFault("guest heap: restore ordered free list before freeing");
                }
                if (address < (ulong)node + U32(node + 4) && node < (ulong)address + length)
                {
                    throw new RuntimeFault($"guest heap: double free or overlapping free range " +
                        $"range={address:x8}+{length}, node={node:x8}+{U32(node + 4)}");
                }
                offset = following;
            }
            return (limit, length);
        }

        public void NativeFree(uint address, uint size)
        {
            NativeFree(address, size, false);
        }

        internal bool NativeFree(uint address, uint size, bool ignoreExactFreeNode)
        {
            if (address == 0 || size == 0)
            {
                return false;
            }
            return ignoreExactFreeNode ? FreeCompatibleRange(address, size) : FreeNativeAllocation(address, size, 0);
        }

        internal bool TryFreeForNativeReplacement(uint address, uint size, uint replacementSize) =>
            FreeNativeAllocation(address, size, replacementSize);

        private bool FreeNativeAllocation(uint address, uint size, uint replacementSize)
        {
            var (limit, length) = ValidateFree(address, size);
            uint previous = HeapHead;
            uint next = HeapBase + U32(previous);
            while (next < address)
            {
                previous = next;
                next = HeapBase + U32(previous);
            }
            if ((ulong)address + length > next ||
                (previous != HeapHead && (ulong)previous + U32(previous + 4) > address))
            {
                throw new RuntimeFault("guest heap: double free or overlapping free range");
            }
            uint merged = address;
            uint mergedLength = length;
            if (previous != HeapHead && previous + U32(previous + 4) == address)
            {
                merged = previous;
                mergedLength += U32(previous + 4);
            }
            uint following = next - HeapBase;
            if (next < HeapBase + limit && address + length == next)
            {
                following = U32(next);
                mergedLength += U32(next + 4);
            }
            if (replacementSize != 0 && !CanAllocateAfterFree(merged, mergedLength, length, replacementSize, limit))
            {
                return false;
            }
            heapMetadataMutation = true;
            try
            {
                Put32(previous, merged - HeapBase);
                Put32(merged, following);
                Put32(merged + 4, mergedLength);
                Put32(HeapCells + 12, Available + length);
                ForgetExternalRam(address, length);
                managed.Remove(address);
                allocationRequests.Remove(address);
                nativeAllocationRequests.Remove(address);
            }
            finally
            {
                heapMetadataMutation = false;
            }
            return true;
        }

        private bool CanAllocateAfterFree(uint merged, uint mergedLength, uint released, uint requested, uint limit)
        {
            ulong length = ((ulong)requested + 7) & ~7ul;
            // Native allocation leaves some free memory even if a node fits exactly.
            if (length >= (ulong)Available + released) return false;
            if (FitsFreeNode(mergedLength, (uint)length)) return true;

            uint mergedEnd = merged + mergedLength;
            for (uint offset = U32(HeapHead); offset != limit; offset = U32(HeapBase + offset))
            {
                uint node = HeapBase + offset;
                if ((node < merged || node >= mergedEnd) && FitsFreeNode(U32(node + 4), (uint)length)) return true;
            }
            return false;
        }

        private bool FreeCompatibleRange(uint address, uint size)
        {
            ulong aligned = ((ulong)size + 7) & ~7ul;
            if (address < HeapBase || (address & 3) != 0 || (ulong)address + aligned > HeapEnd)
            {
                throw new RuntimeFault("guest heap: invalid free range");
            }
            uint length = (uint)aligned;
            ProtectManaged(address, length, true);
            // C mr_free returns for the current head before reading its node fields.
            // Loaders may still be using that node's bytes as decompression scratch.
            if ((ulong)HeapBase + U32(HeapHead) == address)
            {
                return false;
            }
            uint limit = ValidateHeap(restoreOrder: true);
            uint previous = HeapHead;
            uint next = HeapBase + U32(previous);
            while (next < HeapBase + limit && (ulong)next + U32(next + 4) < address)
            {
                uint following = U32(next);
                if (HeapBase + following <= next)
                {
                    throw new RuntimeFault("guest heap: restore ordered free list before freeing");
                }
                previous = next;
                next = HeapBase + following;
            }
            uint start = address;
            uint end = address + length;
            uint duplicate = 0;
            while (next < HeapBase + limit && next <= end)
            {
                if (next == address)
                {
                    return false;
                }
                uint nodeEnd = next + U32(next + 4);
                uint overlapStart = Math.Max(address, next);
                uint overlapEnd = Math.Min(address + length, nodeEnd);
                if (overlapEnd > overlapStart)
                {
                    duplicate += overlapEnd - overlapStart;
                }
                start = Math.Min(start, next);
                end = Math.Max(end, nodeEnd);
                uint following = U32(next);
                if (HeapBase + following <= next)
                {
                    throw new RuntimeFault("guest heap: restore ordered free list before freeing");
                }
                next = HeapBase + following;
            }
            if (duplicate == length)
            {
                return false;
            }
            uint added = length - duplicate;
            if ((ulong)Available + added > HeapEnd - HeapBase)
            {
                throw new RuntimeFault("guest heap: invalid free range");
            }
            heapMetadataMutation = true;
            try
            {
                Put32(previous, start - HeapBase);
                Put32(start, next - HeapBase);
                Put32(start + 4, end - start);
                Put32(HeapCells + 12, Available + added);
                ForgetExternalRam(address, length);
                managed.Remove(address);
                allocationRequests.Remove(address);
                nativeAllocationRequests.Remove(address);
            }
            finally
            {
                heapMetadataMutation = false;
            }
            if (duplicate != 0)
            {
                CompatibilityDiagnostic?.Invoke($"compatibility mr_free overlap: range={address:x8}+{length}, " +
                    $"already free={duplicate}, newly free={added}; accounting uses union");
            }
            return true;
        }

        private void RestoreFreeNodeOrder(uint limit)
        {
            var offsets = new List<uint>();
            uint offset = U32(HeapHead);
            bool ordered = true;
            while (offset != limit)
            {
                if (offsets.Count != 0 && offset < offsets[offsets.Count - 1])
                {
                    ordered = false;
                }
                offsets.Add(offset);
                offset = U32(HeapBase + offset);
            }
            if (ordered)
            {
                return;
            }
            offsets.Sort();
            heapMetadataMutation = true;
            try
            {
                Put32(HeapHead, offsets[0]);
                for (int index = 0; index < offsets.Count; index++)
                {
                    Put32(HeapBase + offsets[index], index + 1 < offsets.Count ? offsets[index + 1] : limit);
                }
            }
            finally
            {
                heapMetadataMutation = false;
            }
            CompatibilityDiagnostic?.Invoke("compatibility mr_free: validated free nodes restored to address order before coalescing");
        }

        public uint NativeReallocate(uint address, uint oldSize, uint size)
        {
            if (address == 0 || oldSize == 0)
            {
                return NativeAllocate(size);
            }
            if (size == 0)
            {
                NativeFree(address, oldSize);
                return 0;
            }
            ValidateFree(address, oldSize);
            uint replacement = NativeAllocate(size);
            if (replacement != 0)
            {
                Copy(replacement, address, Math.Min(oldSize, size), true);
                NativeFree(address, oldSize);
            }
            return replacement;
        }
    }
}
