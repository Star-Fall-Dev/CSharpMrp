using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    // Guest ABI sizes, independent of CLR object sizes and the host pointer width.
    internal sealed class ScriptTableAllocation
    {
        private struct Slot
        {
            internal object? Key;
            internal bool Used;
            internal int Next;
        }

        private Slot[] slots = Array.Empty<Slot>();
        private static readonly object DeadKey = new object();
        private int free;
        private bool mayHavePrunableKeys;
        private readonly HashSet<int> array = new HashSet<int>();
        internal int ArraySize { get; private set; }
        internal int TraversalCapacity => ArraySize + slots.Length;
        internal long Bytes => 32L + 8L * ArraySize + 20L * slots.Length;

        internal ScriptTableAllocation(int arraySize, int hashBits)
        {
            ArraySize = arraySize;
            MakeSlots(hashBits == 0 ? 0 : 1 << hashBits);
        }

        private void MakeSlots(int size)
        {
            if (slots.Length == size)
            {
                Array.Clear(slots, 0, size);
            }
            else
            {
                slots = size == 0 ? Array.Empty<Slot>() : new Slot[size];
            }
            for (int index = 0; index < size; index++)
            {
                slots[index].Next = -1;
            }
            free = size - 1;
            mayHavePrunableKeys = false;
        }

        private static int ArrayIndex(object? key)
        {
            return key is int number && number >= 1 && number <= 1 << 24 ? number : -1;
        }

        private int Position(object key)
        {
            if (key is int number)
            {
                return NumericPosition(unchecked((uint)(number + 1)));
            }
            if (ScriptVm.IsString(key))
            {
                uint hash = key is ScriptMutableString mutable ? mutable.AllocationHash : HashText(ScriptVm.Text(key));
                return (int)(hash & (uint)(slots.Length - 1));
            }
            if (key is bool boolean)
            {
                return (boolean ? 1 : 0) & (slots.Length - 1);
            }
            if (key is ScriptBoolean rawBoolean)
            {
                return (int)(rawBoolean.AllocationHash & (uint)(slots.Length - 1));
            }
            uint numeric = key is RuntimeSession.LightUserdata light ? light.Pointer
                : unchecked((uint)RuntimeHelpers.GetHashCode(key));
            return NumericPosition(numeric);
        }

        private int NumericPosition(uint hash) => (int)(hash % (uint)((slots.Length - 1) | 1));

        internal bool HasTraversalPosition(object key)
        {
            int index = ArrayIndex(key);
            return (index > 0 && index <= ArraySize) || (slots.Length != 0 && FindSlot(Position(key), key) >= 0);
        }

        private int FindSlot(int position, object key)
        {
            for (int current = position; current >= 0; current = slots[current].Next)
            {
                if (slots[current].Key != null && ScriptVm.RawEqual(slots[current].Key, key))
                {
                    return current;
                }
            }
            return -1;
        }

        internal static uint HashText(string text)
        {
            uint hash = (uint)text.Length;
            int step = (text.Length >> 5) + 1;
            for (int length = text.Length; length >= step; length -= step)
            {
                hash ^= (hash << 5) + (hash >> 2) + (byte)text[length - 1];
            }
            return hash;
        }

        internal int FindStringReplacementSlot(string text, ScriptMutableString replacement, out bool replacementExists)
        {
            replacementExists = false;
            if (slots.Length == 0) return -1;

            // The interned key retains the original string's hash even after guest writes.
            int home = (int)(replacement.AllocationHash & (uint)(slots.Length - 1));
            int match = -1;
            for (int index = home; index >= 0; index = slots[index].Next)
            {
                if (ReferenceEquals(slots[index].Key, replacement))
                {
                    replacementExists = true;
                    return -1;
                }
                if (slots[index].Key is string original && original == text)
                {
                    match = index;
                }
            }
            return match;
        }

        internal void ReplaceStringKeyAt(int index, ScriptMutableString replacement) => slots[index].Key = replacement;

        internal void PruneDeadKeys(HashSet<object> live, HashSet<string> liveStrings)
        {
            if (!mayHavePrunableKeys) return;
            mayHavePrunableKeys = false;
            for (int index = 0; index < slots.Length; index++)
            {
                object? key = slots[index].Key;
                if (slots[index].Used || key == null) continue;
                bool marked;
                if (key is string text) marked = liveStrings.Contains(text);
                else if (key is ScriptMutableString || ScriptVm.WeakCollectable(key)) marked = live.Contains(key);
                else continue;

                if (marked)
                {
                    // A later collection may be the one that releases the old key.
                    mayHavePrunableKeys = true;
                }
                else
                {
                    // A dead key still occupies its slot until rehash, but must not retain CLR values.
                    slots[index].Key = DeadKey;
                }
            }
        }

        internal void Set(object key, bool used)
        {
            int index = ArrayIndex(key);
            if (index > 0 && index <= ArraySize)
            {
                if (used)
                {
                    array.Add(index);
                }
                else
                {
                    array.Remove(index);
                }
                return;
            }
            if (slots.Length == 0)
            {
                ResizeForKey(key, used);
                return;
            }
            int main = Position(key);
            int existing = FindSlot(main, key);
            if (!used && key is not int && !mayHavePrunableKeys)
            {
                object? deleted = existing >= 0 ? slots[existing].Key : key;
                mayHavePrunableKeys = ScriptVm.IsString(deleted) || ScriptVm.WeakCollectable(deleted);
            }
            if (existing >= 0)
            {
                slots[existing].Used = used;
                return;
            }
            int destination = main;
            if (slots[main].Used)
            {
                int original = Position(slots[main].Key!);
                if (original == main)
                {
                    destination = free;
                    slots[destination].Next = slots[main].Next;
                    slots[main].Next = destination;
                }
                else
                {
                    int predecessor = original;
                    while (slots[predecessor].Next != main)
                    {
                        predecessor = slots[predecessor].Next;
                    }
                    slots[predecessor].Next = free;
                    slots[free] = slots[main];
                    slots[main] = new Slot { Next = -1 };
                }
            }
            slots[destination].Key = key;
            slots[destination].Used = true;
            while (free >= 0 && slots[free].Key != null)
            {
                free--;
            }
            if (free < 0)
            {
                ResizeForKey(key, used);
            }
            else
            {
                slots[destination].Used = used;
            }
        }

        private void ResizeForKey(object key, bool used)
        {
            int[]? rentedIndices = null;
            try
            {
                int arrayCount = array.Count;
                Span<int> indices = arrayCount <= 128 ? stackalloc int[arrayCount] :
                    arrayCount <= 8192 ? (rentedIndices = ArrayPool<int>.Shared.Rent(arrayCount)).AsSpan(0, arrayCount) :
                    new int[arrayCount];
                int index = 0;
                foreach (int element in array) indices[index++] = element;
                Resize(indices, slots, slots.Length == 0 ? key : null);
            }
            finally
            {
                if (rentedIndices != null) ArrayPool<int>.Shared.Return(rentedIndices);
            }
            Set(key, used);
        }

        private static int ArrayBucket(int index)
        {
            // The bit width of index - 1 is the smallest power-of-two bucket that contains index.
            int value = index - 1;
            int bits = 0;
            if (value >= 1 << 16)
            {
                value >>= 16;
                bits += 16;
            }
            if (value >= 1 << 8)
            {
                value >>= 8;
                bits += 8;
            }
            if (value >= 1 << 4)
            {
                value >>= 4;
                bits += 4;
            }
            if (value >= 1 << 2)
            {
                value >>= 2;
                bits += 2;
            }
            return bits + (value >= 2 ? 2 : value);
        }

        private void Resize(ReadOnlySpan<int> indices, Slot[] oldSlots, object? pendingKey)
        {
            static int CountArrayKey(object? key, Span<int> slices)
            {
                int index = ArrayIndex(key);
                if (index < 0) return 0;
                slices[ArrayBucket(index)]++;
                return 1;
            }

            Span<int> slices = stackalloc int[25];
            slices.Clear();
            foreach (int index in indices) slices[ArrayBucket(index)]++;
            int integerCount = indices.Length + CountArrayKey(pendingKey, slices);
            int hashKeyCount = pendingKey == null ? 0 : 1;
            foreach (Slot slot in oldSlots)
            {
                if (slot.Used)
                {
                    hashKeyCount++;
                    integerCount += CountArrayKey(slot.Key, slices);
                }
            }
            int size = slices[0] == 0 ? 0 : 1;
            int accepted = slices[0];
            int total = accepted;
            for (int bit = 1; total < integerCount && integerCount >= 1 << (bit - 1); bit++)
            {
                total += slices[bit];
                if (slices[bit] > 0 && total >= 1 << (bit - 1))
                {
                    size = 1 << bit;
                    accepted = total;
                }
            }
            int hashCount = indices.Length + hashKeyCount - accepted;
            int hashSize = hashCount == 0 ? 0 : 2;
            while (hashSize <= hashCount && hashSize != 0)
            {
                hashSize <<= 1;
            }
            object[]? snapshot = null;
            bool rented = false;
            try
            {
                // MakeSlots clears the old array only when the hash capacity is reused.
                if (hashSize != 0 && hashSize == oldSlots.Length)
                {
                    rented = hashKeyCount <= 256;
                    snapshot = rented ? ArrayPool<object>.Shared.Rent(hashKeyCount) : new object[hashKeyCount];
                    int next = 0;
                    for (int slot = oldSlots.Length - 1; slot >= 0; slot--)
                    {
                        if (oldSlots[slot].Used) snapshot[next++] = oldSlots[slot].Key!;
                    }
                }

                ArraySize = size;
                array.Clear();
                MakeSlots(hashSize);
                foreach (int index in indices)
                {
                    if (index <= ArraySize) array.Add(index);
                    else Set(index, true);
                }
                if (snapshot != null)
                {
                    for (int i = 0; i < hashKeyCount; i++) Set(snapshot[i], true);
                }
                else
                {
                    for (int slot = oldSlots.Length - 1; slot >= 0; slot--)
                    {
                        if (oldSlots[slot].Used) Set(oldSlots[slot].Key!, true);
                    }
                }
                if (pendingKey != null) Set(pendingKey, true);
            }
            finally
            {
                if (rented && snapshot != null) ArrayPool<object>.Shared.Return(snapshot, clearArray: true);
            }
        }
    }

    public sealed partial class ScriptVm
    {
        private sealed class AllocationThread
        {
            internal int StackSize = 45;
            internal int Calls = 8;
            internal int Top = 1;
            internal int NativeTop = 1;
            internal int Depth;
            internal int ReservedTop = 21;
            internal long Bytes => 88L + 8L * StackSize + 24L * Calls;
        }

        private readonly Dictionary<object, long> allocations = new Dictionary<object, long>(new ObjectIdentity());
        private readonly Dictionary<string, object> allocationStrings = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly HashSet<object> fixedAllocations = new HashSet<object>(new ObjectIdentity());
        private readonly Dictionary<Coroutine, AllocationThread> allocationThreads = new Dictionary<Coroutine, AllocationThread>();
        private const int MaximumCachedSweepObjects = 1024;
        private List<object>? cachedSweepObjects;
        private WeakReference<List<object>>? largeSweepObjects;
        private readonly AllocationThread mainAllocationThread = new AllocationThread();
        internal ScriptTable Registry = new ScriptTable(4, 4);
        private readonly ScriptTable defaultAllocationMeta = new ScriptTable();
        private int stringHashSize = 1024;
        private int allocatedStrings;
        private int stringBufferSize;
        private long allocatedObjectBytes;
        internal Action? CollectionRequested;
        internal long InitialGcThreshold { get; private set; }
        internal long AllocatedBytes => 144L + mainAllocationThread.Bytes + 4L * stringHashSize
            + stringBufferSize + allocatedObjectBytes;

        private void InitializeAllocations()
        {
            Account(defaultAllocationMeta);
            Account(Globals);
            Account(Registry);
            fixedAllocations.Add(defaultAllocationMeta);
            string[] fixedStrings =
            {
                "__index", "__newindex", "__gc", "__mode", "__eq", "__add", "__sub", "__mul",
                "__div", "__op", "__unm", "__lt", "__le", "__concat", "__call", "&&", "break",
                "do", "else", "elif", "end", "false", "for", "def", "if", "in", "local", "nil",
                "!", "||", "repeat", "return", "then", "true", "until", "while", "not enough memory"
            };
            foreach (string text in fixedStrings)
            {
                fixedAllocations.Add(Account(text)!);
            }
            InitialGcThreshold = AllocatedBytes * 4;
        }

        private void ResizeAllocation(object value, long bytes)
        {
            if (allocations.TryGetValue(value, out long previous) && previous == bytes)
            {
                return;
            }
            allocations[value] = bytes;
            allocatedObjectBytes += bytes - previous;
        }

        internal object? Account(object? value)
        {
            if (value == null || value is int || value is bool || value is ScriptBoolean)
            {
                return value;
            }
            if (value is string text)
            {
                if (allocationStrings.TryGetValue(text, out object? interned))
                {
                    if (!(interned is ScriptMutableString changed) || changed.TextEquals(text))
                    {
                        return interned;
                    }
                    allocationStrings.Remove(text);
                }
                allocationStrings.Add(text, text);
                ResizeAllocation(text, 17L + text.Length);
                allocatedStrings++;
                GrowStringHash();
                return text;
            }
            if (allocations.ContainsKey(value))
            {
                return value;
            }
            switch (value)
            {
                case ScriptTable table:
                    ResizeAllocation(table, table.Allocation.Bytes);
                    table.AllocationOwner = this;
                    Account(table.Metatable);
                    foreach (KeyValuePair<object, object?> entry in table.Entries)
                    {
                        Account(entry.Key);
                        Account(entry.Value);
                    }
                    break;
                case Native native:
                    int count = nativeGcValues.TryGetValue(native, out object?[]? captured) ? captured.Length : 0;
                    ResizeAllocation(native, 16L + 8L * count);
                    if (captured != null)
                    {
                        foreach (object? child in captured)
                        {
                            Account(child);
                        }
                    }
                    break;
                case Closure closure:
                    ResizeAllocation(closure, 24L + 4L * closure.Upvalues.Length);
                    Account(closure.Prototype);
                    Account(closure.Environment);
                    foreach (Cell cell in closure.Upvalues)
                    {
                        ResizeAllocation(cell, 20);
                        Account(cell.Value);
                    }
                    break;
                case ScriptPrototype prototype:
                    ResizeAllocation(prototype, 72L + 4L * prototype.Code.Length + 4L * prototype.Lines.Length
                        + 8L * prototype.Constants.Length + 4L * prototype.Children.Length
                        + 12L * prototype.Locals.Length + 4L * prototype.UpvalueNames.Length);
                    // The source compiler's constant index table survives until the next GC.
                    Account(prototype.CompilationTable);
                    prototype.CompilationTable = null;
                    Account(prototype.Source);
                    foreach (ScriptLocal local in prototype.Locals)
                    {
                        Account(local.Name);
                    }
                    foreach (string? name in prototype.UpvalueNames)
                    {
                        Account(name);
                    }
                    foreach (object? constant in prototype.Constants)
                    {
                        Account(constant);
                    }
                    foreach (ScriptPrototype child in prototype.Children)
                    {
                        Account(child);
                    }
                    break;
                case ScriptUserdata user:
                    ResizeAllocation(user, 16L + user.Size);
                    Account(user.Metatable);
                    break;
                case ScriptMutableString mutable:
                    ResizeAllocation(mutable, 17L + mutable.Length);
                    allocatedStrings++;
                    GrowStringHash();
                    break;
                case Coroutine coroutine:
                    sweptCoroutines?.Remove(coroutine);
                    var thread = new AllocationThread();
                    allocationThreads.Add(coroutine, thread);
                    ResizeAllocation(coroutine, thread.Bytes);
                    Account(coroutine.EntryFunction);
                    break;
            }
            return value;
        }

        private void GrowStringHash()
        {
            if (allocatedStrings > stringHashSize)
            {
                stringHashSize *= 2;
            }
        }

        internal void PromoteAllocationString(string text, ScriptMutableString mutable)
        {
            object original = Account(text)!;
            if (allocations.TryGetValue(original, out long bytes))
            {
                allocations.Remove(original);
                allocatedObjectBytes -= bytes;
                bool wasFixed = fixedAllocations.Remove(original);
                ResizeAllocation(mutable, bytes);
                if (wasFixed)
                {
                    fixedAllocations.Add(mutable);
                }
            }
            allocationStrings[text] = mutable;
        }

        internal ScriptTable NewTable(int arraySize = 0, int hashBits = 0)
        {
            if (arraySize < 0 || arraySize > 1 << 24 || hashBits < 0 || hashBits > 24)
            {
                throw new RuntimeFault("script table allocation size limit");
            }
            var table = new ScriptTable(arraySize, hashBits);
            Account(table);
            return table;
        }

        internal void AccountTableWrite(ScriptTable table, object key, object? value)
        {
            Account(key);
            Account(value);
            ResizeAllocation(table, table.Allocation.Bytes);
        }

        private AllocationThread CurrentAllocationThread()
        {
            if (currentCoroutine == null)
            {
                return mainAllocationThread;
            }
            Account(currentCoroutine);
            return allocationThreads[currentCoroutine];
        }

        internal void EnsureAllocationStack(int required)
        {
            AllocationThread thread = CurrentAllocationThread();
            if (required >= thread.StackSize - 5)
            {
                thread.StackSize = required - thread.Top <= thread.StackSize
                    ? thread.StackSize * 2 : thread.StackSize + required - thread.Top + 5;
            }
            if (currentCoroutine != null)
            {
                ResizeAllocation(currentCoroutine, thread.Bytes);
            }
        }

        internal void EnsureCoroutineAllocationStack(Coroutine coroutine, int required)
        {
            Account(coroutine);
            AllocationThread thread = allocationThreads[coroutine];
            if (required >= thread.StackSize - 5)
            {
                thread.StackSize = required - thread.Top <= thread.StackSize
                    ? thread.StackSize * 2 : thread.StackSize + required - thread.Top + 5;
            }
            ResizeAllocation(coroutine, thread.Bytes);
        }

        internal void UpdateNativeAllocationTop(Coroutine? coroutine, int count)
        {
            AllocationThread thread = mainAllocationThread;
            if (coroutine != null && !allocationThreads.TryGetValue(coroutine, out thread!))
            {
                // A prior failing finalizer may leave a handle after its thread was swept.
                return;
            }
            thread.NativeTop = count + 1;
        }

        private int EnterAllocationCall(object? function, int arguments)
        {
            AllocationThread thread = CurrentAllocationThread();
            int previous = thread.Top;
            thread.Depth++;
            if (thread.Depth >= thread.Calls)
            {
                thread.Calls *= 2;
            }
            thread.Top += arguments + 1;
            EnsureAllocationStack(thread.Top + (function is Closure closure ? closure.Prototype.Stack : 20));
            thread.Top = previous + 1 + Math.Max(arguments, function is Closure frame ? frame.Prototype.Stack : 0);
            return previous;
        }

        internal void ReserveAllocationStack(int required)
        {
            EnsureAllocationStack(required);
            AllocationThread thread = CurrentAllocationThread();
            thread.ReservedTop = Math.Max(thread.ReservedTop, required);
        }

        private void LeaveAllocationCall(int previous)
        {
            AllocationThread thread = CurrentAllocationThread();
            thread.Depth--;
            thread.Top = previous;
        }

        internal void EnsureStringBuffer(int size)
        {
            if (size > stringBufferSize)
            {
                stringBufferSize = Math.Max(1024, size);
            }
        }

        internal void ReleaseAllocations()
        {
            CloseCoroutines();
            releaseAllocationsOnReturn = depth != 0;
            cachedRegisterFrames = null;
            cachedTraceObjects = null;
            cachedTracePending = null;
            largeTraceObjects = null;
            largeTracePending = null;
            cachedSweepObjects = null;
            largeSweepObjects = null;
            foreach (object value in allocations.Keys)
            {
                if (value is ScriptTable table && ReferenceEquals(table.AllocationOwner, this))
                    table.AllocationOwner = null;
            }
            allocations.Clear();
            allocations.TrimExcess();
            allocationStrings.Clear();
            allocationStrings.TrimExcess();
            allocationThreads.Clear();
            allocationThreads.TrimExcess();
            fixedAllocations.Clear();
            fixedAllocations.TrimExcess();
            allocatedObjectBytes = 0;
            allocatedStrings = 0;
        }

        private bool releaseAllocationsOnReturn;

        internal List<object>? TakeSweepObjects()
        {
            if (largeSweepObjects != null && largeSweepObjects.TryGetTarget(out List<object>? large))
            {
                largeSweepObjects.SetTarget(null!);
                return large;
            }
            List<object>? objects = cachedSweepObjects;
            cachedSweepObjects = null;
            return objects;
        }

        internal void CacheSweepObjects(List<object>? objects)
        {
            if (objects == null) return;
            objects.Clear();
            if (objects.Capacity > MaximumCachedSweepObjects)
            {
                if (largeSweepObjects == null) largeSweepObjects = new WeakReference<List<object>>(objects);
                else largeSweepObjects.SetTarget(objects);
            }
            else cachedSweepObjects ??= objects;
        }

        internal void SweepAllocations(HashSet<object> live, HashSet<string> liveStrings)
        {
            foreach (object value in live)
            {
                if (value is ScriptTable table)
                {
                    table.PruneAllocationHistory(live, liveStrings);
                }
            }
            List<object>? dead = TakeSweepObjects();
            foreach (object value in allocations.Keys)
            {
                if (!live.Contains(value) && !fixedAllocations.Contains(value)
                    && !(value is string text && liveStrings.Contains(text)))
                {
                    (dead ??= new List<object>()).Add(value);
                }
            }
            if (dead != null)
            {
                foreach (object value in dead)
                {
                    allocatedObjectBytes -= allocations[value];
                    allocations.Remove(value);
                    if (value is ScriptTable table && ReferenceEquals(table.AllocationOwner, this))
                        table.AllocationOwner = null;
                    if (value is string text)
                    {
                        allocationStrings.Remove(text);
                        allocatedStrings--;
                    }
                    else if (value is ScriptMutableString mutable)
                    {
                        allocatedStrings--;
                        if (mutable.InternedText != null
                            && allocationStrings.TryGetValue(mutable.InternedText, out object? current)
                            && ReferenceEquals(current, mutable))
                        {
                            allocationStrings.Remove(mutable.InternedText);
                        }
                    }
                    else if (value is Coroutine coroutine)
                    {
                        allocationThreads.Remove(coroutine);
                        // Host code may still hold this coroutine after the script stops referencing it.
                        if (!coroutine.Dead)
                            (sweptCoroutines ??= new ConditionalWeakTable<Coroutine, object>()).Add(coroutine, coroutineOwnerToken);
                    }
                }
                CacheSweepObjects(dead);
            }
            if (allocatedStrings < stringHashSize / 4 && stringHashSize > 2048)
            {
                stringHashSize /= 2;
            }
            if (stringBufferSize > 2048)
            {
                stringBufferSize /= 2;
            }
            ShrinkAllocationStack(mainAllocationThread);
            foreach (KeyValuePair<Coroutine, AllocationThread> entry in allocationThreads)
            {
                if (!entry.Key.Running)
                {
                    entry.Value.Top = 1;
                    if (entry.Key.Frames != null)
                    {
                        foreach (CoroutineFrame frame in entry.Key.Frames)
                        {
                            entry.Value.Top += 1 + frame.Registers.Length;
                        }
                    }
                }
                ShrinkAllocationStack(entry.Value);
                ResizeAllocation(entry.Key, entry.Value.Bytes);
            }
        }

        private static void ShrinkAllocationStack(AllocationThread thread)
        {
            if (4 * thread.Depth < thread.Calls && thread.Calls > 16)
            {
                thread.Calls /= 2;
            }
            int used = Math.Max(thread.NativeTop, Math.Max(thread.ReservedTop, thread.Top));
            if (4 * used < thread.StackSize && thread.StackSize > 90)
            {
                thread.StackSize /= 2;
            }
        }
    }
}
