using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    public sealed partial class ScriptVm
    {
        private sealed class ObjectIdentity : IEqualityComparer<object>
        {
            internal static readonly ObjectIdentity Instance = new ObjectIdentity();

            public new bool Equals(object? left, object? right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        private readonly ConditionalWeakTable<Native, object?[]> nativeGcValues =
            new ConditionalWeakTable<Native, object?[]>();
        private readonly List<object?[]> activeNativeArguments = new List<object?[]>();
        private int traceCapacity;
        private int tracePendingCapacity;
        private int traceWithCellsCapacity;
        private int traceWithCellsPendingCapacity;
        private const int MaximumCachedTraceObjects = 8192;
        private HashSet<object>? cachedTraceObjects;
        private Stack<object>? cachedTracePending;
        private WeakReference<HashSet<object>>? largeTraceObjects;
        private WeakReference<Stack<object>>? largeTracePending;

        internal void ReleaseTracedObjects(HashSet<object> objects)
        {
            bool large = objects.Count > MaximumCachedTraceObjects ||
                objects.EnsureCapacity(0) > 2 * MaximumCachedTraceObjects;
            objects.Clear();
            if (large)
            {
                if (largeTraceObjects == null) largeTraceObjects = new WeakReference<HashSet<object>>(objects);
                else largeTraceObjects.SetTarget(objects);
            }
            else cachedTraceObjects ??= objects;
        }

        private HashSet<object> GetTraceObjects(int capacity)
        {
            if (capacity > MaximumCachedTraceObjects && largeTraceObjects != null &&
                largeTraceObjects.TryGetTarget(out HashSet<object>? large))
            {
                largeTraceObjects.SetTarget(null!);
                return large;
            }
            var objects = cachedTraceObjects ??
                new HashSet<object>(Math.Min(capacity, MaximumCachedTraceObjects), ObjectIdentity.Instance);
            cachedTraceObjects = null;
            return objects;
        }

        private Stack<object> GetTracePending(int capacity, out bool borrowedLarge)
        {
            if (capacity > MaximumCachedTraceObjects && largeTracePending != null &&
                largeTracePending.TryGetTarget(out Stack<object>? large))
            {
                largeTracePending.SetTarget(null!);
                borrowedLarge = true;
                return large;
            }
            borrowedLarge = false;
            var pending = cachedTracePending ?? new Stack<object>(Math.Min(capacity, MaximumCachedTraceObjects));
            cachedTracePending = null;
            return pending;
        }

        private static void PushTraceObject(Stack<object> pending, HashSet<object> visited, object value)
        {
            if (visited.Add(value)) pending.Push(value);
        }

        private static void PushTraceValue(Stack<object> pending, HashSet<object> visited, object? value)
        {
            if (value is not (null or int or uint or bool or long or double)) PushTraceObject(pending, visited, value);
        }

        internal void RetainNativeValues(Native function, params object?[] values)
        {
            nativeGcValues.Add(function, values);
            if (allocations.ContainsKey(function))
            {
                ResizeAllocation(function, 16L + 8L * values.Length);
                foreach (object? value in values)
                {
                    Account(value);
                }
            }
        }

        internal HashSet<object> TraceObjects(IEnumerable<object?> externalRoots,
            Func<object, object?> getNativeChild, bool recordCells = false)
        {
            HashSet<object> visited = GetTraceObjects(recordCells ? traceWithCellsCapacity : traceCapacity);
            try
            {
                TraceRoots(visited, externalRoots, getNativeChild, recordCells);
                return visited;
            }
            catch
            {
                ReleaseTracedObjects(visited);
                throw;
            }
        }

        private void TraceRoots(HashSet<object> visited, IEnumerable<object?> externalRoots,
            Func<object, object?> getNativeChild, bool recordCells)
        {
            int pendingHint = recordCells ? traceWithCellsPendingCapacity : tracePendingCapacity;
            var pending = GetTracePending(pendingHint, out bool borrowedLarge);
            PushTraceObject(pending, visited, Globals);
            PushTraceObject(pending, visited, Registry);
            if (currentCoroutine != null) PushTraceObject(pending, visited, currentCoroutine);
            foreach (object root in fixedAllocations)
            {
                PushTraceObject(pending, visited, root);
            }
            foreach (Cell[] frame in activeRegisterFrames)
            {
                PushTraceObject(pending, visited, frame);
            }
            for (int index = 0; index < environmentFrameCount; index++)
            {
                PushTraceValue(pending, visited, environmentFunctions[index]);
            }
            foreach (object?[] arguments in activeNativeArguments)
            {
                PushTraceObject(pending, visited, arguments);
            }
            foreach (object? root in externalRoots)
            {
                PushTraceValue(pending, visited, root);
            }
            int pendingCapacity = TracePending(pending, visited, getNativeChild, borrowedLarge, recordCells);
            if (recordCells)
            {
                traceWithCellsCapacity = visited.Count;
                traceWithCellsPendingCapacity = pendingCapacity;
            }
            else
            {
                traceCapacity = visited.Count;
                tracePendingCapacity = pendingCapacity;
            }
        }

        internal void MarkFinalizers(List<ScriptUserdata> finalizers, HashSet<object> visited,
            Func<object, object?> getNativeChild)
        {
            var pending = GetTracePending(tracePendingCapacity, out bool borrowedLarge);
            foreach (ScriptUserdata user in finalizers)
            {
                PushTraceObject(pending, visited, user);
            }
            int capacity = TracePending(pending, visited, getNativeChild, borrowedLarge);
            traceCapacity = visited.Count;
            tracePendingCapacity = Math.Max(tracePendingCapacity, capacity);
        }

        private int TracePending(Stack<object> pending, HashSet<object> visited, Func<object, object?> getNativeChild,
            bool borrowedLarge, bool recordCells = false)
        {
            int peakPending = pending.Count;
            try
            {
                return TracePendingObjects(pending, visited, getNativeChild, ref peakPending, recordCells);
            }
            finally
            {
                pending.Clear();
                if (borrowedLarge || peakPending > MaximumCachedTraceObjects)
                {
                    if (largeTracePending == null) largeTracePending = new WeakReference<Stack<object>>(pending);
                    else largeTracePending.SetTarget(pending);
                }
                else cachedTracePending ??= pending;
            }
        }

        private int TracePendingObjects(Stack<object> pending, HashSet<object> visited,
            Func<object, object?> getNativeChild, ref int peakPending, bool recordCells)
        {
            while (pending.Count != 0)
            {
                peakPending = Math.Max(peakPending, pending.Count);
                object value = pending.Pop();
                switch (value)
                {
                    case ScriptTable table:
                        bool weakKeys = false, weakValues = false;
                        if (table.Metatable is ScriptTable metatable)
                        {
                            PushTraceObject(pending, visited, metatable);
                            (weakKeys, weakValues) = metatable.ReadWeakMode();
                        }
                        foreach (KeyValuePair<object, object?> entry in table.Entries)
                        {
                            if (!weakKeys || !WeakCollectable(entry.Key))
                            {
                                PushTraceValue(pending, visited, entry.Key);
                            }
                            if (!weakValues || !WeakCollectable(entry.Value))
                            {
                                PushTraceValue(pending, visited, entry.Value);
                            }
                        }
                        break;
                    case ScriptUserdata userdata:
                        if (userdata.Metatable != null) PushTraceObject(pending, visited, userdata.Metatable);
                        break;
                    case Native native:
                        if (nativeGcValues.TryGetValue(native, out object?[]? captured))
                        {
                            PushTraceObject(pending, visited, captured);
                        }
                        break;
                    case Closure closure:
                        PushTraceObject(pending, visited, closure.Prototype);
                        PushTraceObject(pending, visited, closure.Environment);
                        PushTraceObject(pending, visited, closure.Upvalues);
                        break;
                    case Cell cell:
                        PushTraceValue(pending, visited, cell.Value);
                        break;
                    case Cell[] registers when !recordCells:
                        foreach (Cell register in registers)
                        {
                            PushTraceValue(pending, visited, register?.Value);
                        }
                        break;
                    case Cell[] cells:
                        foreach (Cell element in cells)
                        {
                            if (element != null) PushTraceObject(pending, visited, element);
                        }
                        break;
                    case ScriptPrototype prototype:
                        if (prototype.SharedDebugStrings != null)
                        {
                            PushTraceObject(pending, visited, prototype.SharedDebugStrings);
                        }
                        else
                        {
                            if (prototype.Source != null) PushTraceObject(pending, visited, prototype.Source);
                            foreach (ScriptLocal local in prototype.Locals)
                            {
                                if (local.Name != null) PushTraceObject(pending, visited, local.Name);
                            }
                            foreach (string? name in prototype.UpvalueNames)
                            {
                                if (name != null) PushTraceObject(pending, visited, name);
                            }
                        }
                        foreach (object? constant in prototype.Constants)
                        {
                            PushTraceValue(pending, visited, constant);
                        }
                        foreach (ScriptPrototype child in prototype.Children)
                        {
                            if (child != null) PushTraceObject(pending, visited, child);
                        }
                        break;
                    case Coroutine coroutine:
                        PushTraceValue(pending, visited, coroutine.EntryFunction);
                        if (coroutine.Frames != null)
                        {
                            foreach (CoroutineFrame frame in coroutine.Frames)
                            {
                                PushTraceObject(pending, visited, frame);
                            }
                        }
                        break;
                    case CoroutineFrame frame:
                        PushTraceObject(pending, visited, frame.Function);
                        PushTraceObject(pending, visited, frame.Registers);
                        break;
                    case object?[] array:
                        foreach (object? item in array)
                        {
                            PushTraceValue(pending, visited, item);
                        }
                        break;
                    case List<object?> list:
                        foreach (object? item in list)
                        {
                            PushTraceValue(pending, visited, item);
                        }
                        break;
                    case IEnumerable<object?> values:
                        foreach (object? item in values)
                        {
                            PushTraceValue(pending, visited, item);
                        }
                        break;
                }
                if (value is Native || value is Coroutine)
                {
                    PushTraceValue(pending, visited, getNativeChild(value));
                }
            }
            return peakPending;
        }

        internal static bool WeakCollectable(object? value)
        {
            // Reference MR GC keeps strings (including mutable strings) in weak tables.
            return value is ScriptTable || value is ScriptUserdata || value is Coroutine || IsFunction(value);
        }

        internal void ReplaceInternedString(string text, ScriptMutableString replacement,
            IEnumerable<object?> roots, Func<object, object?> getNativeChild)
        {
            HashSet<object> objects = TraceObjects(roots, getNativeChild, recordCells: true);
            try
            {
                ReplaceTracedString(text, replacement, objects);
            }
            finally
            {
                ReleaseTracedObjects(objects);
            }
        }

        private static void ReplaceTracedString(string text, ScriptMutableString replacement, HashSet<object> objects)
        {
            object? Replace(object? value)
            {
                return value is string candidate && candidate == text ? replacement : value;
            }
            foreach (object value in objects)
            {
                if (value is ScriptTable table)
                {
                    table.ReplaceString(text, replacement);
                }
                else if (value is Cell cell)
                {
                    object? changed = Replace(cell.Value);
                    if (!ReferenceEquals(changed, cell.Value))
                    {
                        cell.Value = changed;
                    }
                }
                else if (value is object?[] array)
                {
                    for (int index = 0; index < array.Length; index++)
                    {
                        array[index] = Replace(array[index]);
                    }
                }
                else if (value is List<object?> list)
                {
                    for (int index = 0; index < list.Count; index++)
                    {
                        list[index] = Replace(list[index]);
                    }
                }
                else if (value is ScriptPrototype prototype)
                {
                    for (int index = 0; index < prototype.Constants.Length; index++)
                    {
                        prototype.Constants[index] = Replace(prototype.Constants[index]);
                    }
                }
            }
        }

        internal void ClearWeakTables(HashSet<object> live, bool clearKeys, HashSet<string>? liveStrings = null)
        {
            List<object>? removed = TakeSweepObjects();
            foreach (object item in live)
            {
                if (item is ScriptTable table)
                {
                    table.ClearWeakEntries(live, clearKeys, ref removed);
                }
                else if (liveStrings != null && item is string text)
                {
                    liveStrings.Add(text);
                }
            }
            CacheSweepObjects(removed);
        }
    }
}
