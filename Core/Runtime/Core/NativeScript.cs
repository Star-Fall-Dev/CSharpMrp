using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private const int NativeStackLimit = ScriptVm.StackLimit;
        private const int StackHandleSweepCapacity = 32;
        private const int RegistryIndex = -10000;
        private const int GlobalsIndex = -10001;
        private static readonly object MissingScriptValue = new object();
        private uint scriptState;
        private uint activeGuestScriptState;
        private ScriptVm.Coroutine? nativeAllocationCoroutine;
        private long? gcThresholdBytes;
        private ScriptTable scriptRegistry = null!;
        private List<object?> nativeStack = new List<object?>();
        private readonly List<List<object?>> nativeStackFrames = new List<List<object?>>();
        private readonly List<ScriptUserdata> scriptUsers = new List<ScriptUserdata>();
        private readonly List<ScriptMutableString> mutableScriptStrings = new List<ScriptMutableString>();
        private readonly HashSet<string> permanentScriptStrings = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, WeakReference<ScriptMutableString>> internedScriptStrings =
            new Dictionary<string, WeakReference<ScriptMutableString>>(StringComparer.Ordinal);
        private bool collectingScript;
        private bool closingScript;
        private const int MaximumCachedGcStrings = 8192;
        private HashSet<string>? cachedGcStrings;
        private WeakReference<HashSet<string>>? largeGcStrings;
        private int gcStringCount;
        private WeakReference<string>? recentDecodedScriptString;
        private WeakReference<string>? previousDecodedScriptString;
        private ConditionalWeakTable<string, byte[]>? recentDecodedScriptBytes;

        private void CheckScriptAllocation(uint bytes)
        {
            if (collectingScript || closingScript)
            {
                return;
            }
            GcThreshold();
            if (ScriptBytes() >= gcThresholdBytes!.Value)
            {
                CollectScriptObjects();
            }
        }

        private ScriptUserdata NewScriptUserdata(uint pointer, uint size, ScriptTable? metatable = null)
        {
            var user = new ScriptUserdata { Pointer = pointer, Size = size, Metatable = metatable };
            scriptUsers.Add(user);
            Script.Account(user);
            return user;
        }

        private ScriptMutableString NewMutableScriptString(int length, ReadOnlySpan<byte> bytes = default)
        {
            PrepareMutableStringAllocation(length);
            var value = new ScriptMutableString(Memory, length, bytes);
            mutableScriptStrings.Add(value);
            Script.Account(value);
            return value;
        }

        private void PrepareMutableStringAllocation(int length)
        {
            if ((uint)length >= Package.MaxBytes)
            {
                throw new RuntimeFault("mutable string size limit");
            }
            CheckScriptAllocation((uint)length);
        }

        private IEnumerable<object?> StringAliasRoots()
        {
            foreach (object? root in NativeGcRoots())
            {
                yield return root;
            }
            foreach (ScriptUserdata user in scriptUsers)
            {
                yield return user;
            }
        }

        private ScriptMutableString InternedScriptString(string text)
        {
            if (!internedScriptStrings.TryGetValue(text, out WeakReference<ScriptMutableString>? reference) ||
                !reference.TryGetTarget(out ScriptMutableString? value) || !value.TextEquals(text))
            {
                uint pointer;
                if (permanentScriptStrings.Contains(text))
                {
                    CheckScriptAllocation((uint)text.Length + 1);
                    pointer = Memory.CopyVmText(text);
                }
                else
                {
                    pointer = ScriptStringPointer(text);
                    stringPointers.Remove(text);
                }
                value = new ScriptMutableString(Memory, pointer, text);
                mutableScriptStrings.Add(value);
                internedScriptStrings[text] = new WeakReference<ScriptMutableString>(value);
                Script.PromoteAllocationString(text, value);
            }
            Script.ReplaceInternedString(text, value, StringAliasRoots(), ScriptGcChildren);
            return value;
        }

        private object CanonicalScriptString(string text)
        {
            if (internedScriptStrings.TryGetValue(text, out WeakReference<ScriptMutableString>? reference))
            {
                if (reference.TryGetTarget(out ScriptMutableString? value) && value.TextEquals(text))
                {
                    return value;
                }
                return InternedScriptString(text);
            }
            return text;
        }

        private string DecodeScriptBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 512 || bytes.Length > 64 * 1024)
                return ScriptChunk.Bytes.GetString(bytes);
            string? text = ReuseDecodedScriptString(bytes, recentDecodedScriptString);
            if (text != null) return text;

            text = ReuseDecodedScriptString(bytes, previousDecodedScriptString);
            if (text == null)
            {
                text = ScriptChunk.Bytes.GetString(bytes);
                if (previousDecodedScriptString == null)
                {
                    previousDecodedScriptString = new WeakReference<string>(text);
                }
                else
                {
                    if (previousDecodedScriptString.TryGetTarget(out string? evicted))
                        recentDecodedScriptBytes?.Remove(evicted);
                    previousDecodedScriptString.SetTarget(text);
                }
            }
            (recentDecodedScriptString, previousDecodedScriptString) = (previousDecodedScriptString, recentDecodedScriptString);
            return text;
        }

        private string? ReuseDecodedScriptString(ReadOnlySpan<byte> bytes, WeakReference<string>? reference)
        {
            if (reference == null || !reference.TryGetTarget(out string? text) || text.Length != bytes.Length)
                return null;
            byte[]? snapshot = null;
            recentDecodedScriptBytes?.TryGetValue(text, out snapshot);
            if (!(snapshot == null ? ScriptChunk.BytesEqualText(bytes, text.AsSpan()) : bytes.SequenceEqual(snapshot)))
                return null;

            // Allocate the sample only on reuse and tie its lifetime to the decoded string.
            if (snapshot == null)
                (recentDecodedScriptBytes ??= new ConditionalWeakTable<string, byte[]>()).Add(text, bytes.ToArray());
            return text;
        }

        private sealed class CoroutineNativeState
        {
            internal uint Handle;
            internal WeakReference<ScriptVm.Coroutine> Coroutine = null!;
            internal List<object?> Stack = new List<object?>();
        }

        private readonly ConditionalWeakTable<ScriptVm.Coroutine, CoroutineNativeState> coroutineStates =
            new ConditionalWeakTable<ScriptVm.Coroutine, CoroutineNativeState>();
        private readonly Dictionary<uint, WeakReference<CoroutineNativeState>> coroutineHandles =
            new Dictionary<uint, WeakReference<CoroutineNativeState>>();
        private int nextCoroutineSweep = 256;

        private bool TryCoroutineState(uint handle, out CoroutineNativeState state, out ScriptVm.Coroutine coroutine)
        {
            coroutineHandles.TryGetValue(handle, out var reference);
            return TryCoroutineState(reference, out state, out coroutine);
        }

        private static bool TryCoroutineState(WeakReference<CoroutineNativeState>? reference,
            out CoroutineNativeState state, out ScriptVm.Coroutine coroutine)
        {
            if (reference != null &&
                reference.TryGetTarget(out CoroutineNativeState? found) &&
                found.Coroutine.TryGetTarget(out ScriptVm.Coroutine? owner))
            {
                state = found;
                coroutine = owner;
                return true;
            }
            state = null!;
            coroutine = null!;
            return false;
        }

        private ref struct ExpiredHandleBuffer
        {
            private Span<uint> handles;
            private uint[]? rented;
            private int count;

            internal ExpiredHandleBuffer(Span<uint> handles)
            {
                this.handles = handles;
                rented = null;
                count = 0;
            }

            internal ReadOnlySpan<uint> Handles => handles.Slice(0, count);

            internal void Add(uint handle)
            {
                if (count == handles.Length)
                {
                    uint[] larger = ArrayPool<uint>.Shared.Rent(handles.Length * 2);
                    handles.CopyTo(larger);
                    Dispose();
                    rented = larger;
                    handles = larger;
                }
                handles[count++] = handle;
            }

            internal void Dispose()
            {
                if (rented != null) ArrayPool<uint>.Shared.Return(rented);
            }
        }

        private void SweepCoroutineHandles(HashSet<object>? live = null)
        {
            var expired = new ExpiredHandleBuffer(stackalloc uint[StackHandleSweepCapacity]);
            try
            {
                foreach (var entry in coroutineHandles)
                {
                    if (!TryCoroutineState(entry.Value, out _, out ScriptVm.Coroutine coroutine) ||
                        live != null && !live.Contains(coroutine))
                    {
                        expired.Add(entry.Key);
                    }
                }
                foreach (uint handle in expired.Handles)
                {
                    // Invalidate only the handle being released so later mappings survive a failure.
                    coroutineHandles.Remove(handle, out var reference);
                    if (TryCoroutineState(reference, out _, out ScriptVm.Coroutine coroutine))
                    {
                        coroutineStates.Remove(coroutine);
                    }
                    Memory.Free(handle);
                }
            }
            finally { expired.Dispose(); }
            nextCoroutineSweep = Math.Max(256, coroutineHandles.Count * 2);
        }

        private IEnumerable<object?> NativeGcRoots()
        {
            yield return scriptRegistry;
            yield return nativeStack;
            foreach (List<object?> frame in nativeStackFrames)
            {
                yield return frame;
            }
            if (activeScriptClosure != null)
            {
                yield return activeScriptClosure.Upvalues;
            }
            yield return pendingInitTable;
            yield return pendingDnsTable;
            yield return activeNetworkLibrary;
            foreach (string text in permanentScriptStrings)
            {
                yield return text;
            }
            uint timerName = Memory.U32(timerFields);
            if (!Memory.IsVmStorageAddress(timerName)) yield break;
            foreach (KeyValuePair<string, uint> entry in stringPointers)
            {
                if (entry.Value == timerName)
                {
                    yield return entry.Key;
                }
            }
            foreach (ScriptMutableString value in mutableScriptStrings)
            {
                if (value.Pointer == timerName)
                {
                    yield return value;
                }
            }
        }

        private Func<object, object?>? cachedScriptGcChild;
        private Func<object, object?> ScriptGcChildren => cachedScriptGcChild ??= ScriptGcChild;

        private object? ScriptGcChild(object value)
        {
            if (value is ScriptVm.Native native && scriptClosures.TryGetValue(native, out GuestScriptClosure? closure))
            {
                return closure.Upvalues;
            }
            if (value is ScriptVm.Coroutine coroutine && coroutineStates.TryGetValue(coroutine, out CoroutineNativeState? state))
            {
                return state.Stack;
            }
            return null;
        }

        private HashSet<string> TakeGcStrings()
        {
            if (gcStringCount > MaximumCachedGcStrings && largeGcStrings != null &&
                largeGcStrings.TryGetTarget(out HashSet<string>? large))
            {
                largeGcStrings.SetTarget(null!);
                return large;
            }
            var strings = cachedGcStrings ?? new HashSet<string>(Math.Min(gcStringCount, MaximumCachedGcStrings),
                StringComparer.Ordinal);
            cachedGcStrings = null;
            return strings;
        }

        private void CacheGcStrings(HashSet<string> strings)
        {
            gcStringCount = strings.Count;
            bool large = gcStringCount > MaximumCachedGcStrings ||
                strings.EnsureCapacity(0) > 2 * MaximumCachedGcStrings;
            strings.Clear();
            if (large)
            {
                if (largeGcStrings == null) largeGcStrings = new WeakReference<HashSet<string>>(strings);
                else largeGcStrings.SetTarget(strings);
            }
            else cachedGcStrings = strings;
        }

        private void SweepExpiredStrings<T>(Dictionary<string, T> index, HashSet<string> live,
            Action<RuntimeSession, T>? release = null)
        {
            List<object>? expired = null;
            try
            {
                foreach (string text in index.Keys)
                {
                    if (!live.Contains(text))
                        (expired ??= Script.TakeSweepObjects() ?? new List<object>()).Add(text);
                }
                if (expired == null) return;
                foreach (string text in expired)
                {
                    // Commit each removal before releasing its address so a later failure can be retried.
                    index.Remove(text, out var value);
                    release?.Invoke(this, value!);
                }
            }
            finally { Script.CacheSweepObjects(expired); }
        }

        private void CollectScriptObjects()
        {
            if (collectingScript || closingScript)
            {
                return;
            }
            collectingScript = true;
            HashSet<object>? live = null;
            HashSet<string>? liveStrings = null;
            try
            {
                foreach (var reference in coroutineHandles.Values)
                {
                    if (TryCoroutineState(reference, out CoroutineNativeState coroutineState, out ScriptVm.Coroutine coroutine))
                    {
                        Script.UpdateNativeAllocationTop(coroutine, coroutineState.Stack.Count);
                    }
                }
                // A running callback owns a temporary stack, not the saved coroutine stack.
                Script.UpdateNativeAllocationTop(nativeAllocationCoroutine ?? Script.ActiveCoroutine, nativeStack.Count);
                live = Script.TraceObjects(NativeGcRoots(), ScriptGcChildren);
                // Only userdata finalizers can extend this mark set before the final weak sweep.
                if (scriptUsers.Count != 0) Script.ClearWeakTables(live, clearKeys: false);
                List<ScriptUserdata>? finalizers = null;
                for (int index = scriptUsers.Count - 1; index >= 0; index--)
                {
                    ScriptUserdata user = scriptUsers[index];
                    if (!live.Contains(user) && !user.Finalized && user.Metatable?["__gc"] != null)
                    {
                        (finalizers ??= new List<ScriptUserdata>()).Add(user);
                    }
                }
                // The reference extends the current marks with finalizable userdata.
                // Keys already marked through a cleared weak-value entry survive this cycle.
                if (finalizers != null)
                {
                    Script.MarkFinalizers(finalizers, live, ScriptGcChildren);
                }
                liveStrings = TakeGcStrings();
                Script.ClearWeakTables(live, clearKeys: true, liveStrings);
                for (int index = scriptUsers.Count - 1; index >= 0; index--)
                {
                    ScriptUserdata user = scriptUsers[index];
                    if (!live.Contains(user))
                    {
                        networkUsers.Remove(user);
                        Memory.Free(user.Pointer);
                        scriptUsers.RemoveAt(index);
                    }
                }
                for (int index = mutableScriptStrings.Count - 1; index >= 0; index--)
                {
                    ScriptMutableString value = mutableScriptStrings[index];
                    if (!live.Contains(value))
                    {
                        Memory.Free(value.Pointer);
                        mutableScriptStrings.RemoveAt(index);
                        if (value.InternedText != null &&
                            internedScriptStrings.TryGetValue(value.InternedText, out WeakReference<ScriptMutableString>? reference) &&
                            reference.TryGetTarget(out ScriptMutableString? current) && ReferenceEquals(current, value))
                        {
                            internedScriptStrings.Remove(value.InternedText);
                        }
                    }
                }
                SweepExpiredStrings(stringPointers, liveStrings, static (session, pointer) => session.Memory.Free(pointer));
                Script.SweepAllocations(live, liveStrings);
                if (resourceNames.Count != 0)
                {
                    // subV can promote a resource to an addressable string. Its original
                    // source is still useful if the guest edits and restores its bytes.
                    foreach (ScriptMutableString mutable in mutableScriptStrings)
                    {
                        if (mutable.InternedText != null)
                            liveStrings.Add(mutable.InternedText);
                    }
                    SweepExpiredStrings(resourceNames, liveStrings);
                }
                long pendingBytes = 0;
                if (finalizers != null)
                {
                    foreach (ScriptUserdata user in finalizers) pendingBytes += 16L + user.Size;
                }
                gcThresholdBytes = Math.Min(uint.MaxValue, ScriptBytes() * 2 - pendingBytes);
                // Sweep the marked graph before callbacks can publish new handles.
                SweepCoroutineHandles(live);
                SweepScriptIdentityHandles(live);
                if (finalizers != null)
                {
                    foreach (ScriptUserdata user in finalizers)
                    {
                        FinalizeScriptUserdata(user);
                    }
                }
            }
            finally
            {
                if (live != null) Script.ReleaseTracedObjects(live);
                if (liveStrings != null) CacheGcStrings(liveStrings);
                collectingScript = false;
            }
        }

        private void FinalizeScriptObjects()
        {
            // mrp_close drains finalizers after each protected failure. Record every error.
            int finalized = 0;
            int first = 0;
            int next = scriptUsers.Count;
            Stack<(int First, int Next)>? pendingRanges = null;
            while (true)
            {
                if (next == first)
                {
                    if (pendingRanges == null || pendingRanges.Count == 0) return;
                    (first, next) = pendingRanges.Pop();
                }
                ScriptUserdata pending = scriptUsers[--next];
                if (pending.Finalized) continue;
                if (++finalized > 100000)
                {
                    throw new RuntimeFault("script close finalizer work limit");
                }
                int previousCount = scriptUsers.Count;
                try
                {
                    FinalizeScriptUserdata(pending);
                }
                catch (Exception error) when (error is IScriptRaisedError || error is RuntimeFault)
                {
                    Record("script close finalizer error: " + error.Message);
                }

                // GC is suspended during shutdown. Visit appended userdata before resuming this range.
                if (scriptUsers.Count > previousCount)
                {
                    if (next > first)
                        (pendingRanges ??= new Stack<(int, int)>()).Push((first, next));
                    first = previousCount;
                    next = scriptUsers.Count;
                }
            }
        }

        private void FinalizeScriptUserdata(ScriptUserdata user)
        {
            user.Finalized = true;
            // An earlier finalizer may have replaced or removed this method.
            if (user.Metatable?["__gc"] is object function) Script.Call(function, user);
        }

        private void ReleaseScriptAllocationTracking()
        {
            Script.ReleaseAllocations();
            scriptUsers.Clear();
            scriptUsers.TrimExcess();
            mutableScriptStrings.Clear();
            mutableScriptStrings.TrimExcess();
            internedScriptStrings.Clear();
            internedScriptStrings.TrimExcess();
            stringPointers.Clear();
            stringPointers.TrimExcess();
            permanentScriptStrings.Clear();
            permanentScriptStrings.TrimExcess();
            cachedGcStrings = null;
            largeGcStrings = null;
            recentDecodedScriptString = null;
            previousDecodedScriptString = null;
            recentDecodedScriptBytes = null;
            cachedNativeCallStack = null;
            cachedPatternBuffers = null;
        }

        private long ScriptBytes()
        {
            return Script.AllocatedBytes;
        }

        private uint GcThreshold()
        {
            gcThresholdBytes ??= Math.Min(uint.MaxValue, Script.InitialGcThreshold);
            return (uint)(gcThresholdBytes.Value >> 10);
        }

        private uint SetGcThreshold(int kilobytes)
        {
            gcThresholdBytes = kilobytes < 0 || kilobytes > (uint.MaxValue >> 10)
                ? uint.MaxValue : (long)kilobytes << 10;
            long bytes = ScriptBytes();
            if (bytes >= gcThresholdBytes.Value)
            {
                CollectScriptObjects();
            }
            return 0;
        }

        private uint CoroutineHandle(ScriptVm.Coroutine coroutine)
        {
            if (coroutineStates.TryGetValue(coroutine, out CoroutineNativeState? state))
            {
                return state.Handle;
            }
            if (coroutineHandles.Count >= nextCoroutineSweep)
            {
                SweepCoroutineHandles();
            }
            uint handle = Memory.AllocateVm(4);
            state = new CoroutineNativeState
            {
                Handle = handle,
                Coroutine = new WeakReference<ScriptVm.Coroutine>(coroutine)
            };
            coroutineStates.Add(coroutine, state);
            coroutineHandles.Add(handle, new WeakReference<CoroutineNativeState>(state));
            return handle;
        }
        private sealed class ScriptIdentityHandle
        {
            internal uint Handle;
            internal WeakReference<object> Owner = null!;
        }

        private readonly ConditionalWeakTable<object, ScriptIdentityHandle> scriptIdentities =
            new ConditionalWeakTable<object, ScriptIdentityHandle>();
        private readonly Dictionary<uint, WeakReference<ScriptIdentityHandle>> scriptIdentityHandles =
            new Dictionary<uint, WeakReference<ScriptIdentityHandle>>();
        private int nextIdentitySweep = 256;

        private void SweepScriptIdentityHandles(HashSet<object>? live = null)
        {
            var expired = new ExpiredHandleBuffer(stackalloc uint[StackHandleSweepCapacity]);
            try
            {
                foreach (KeyValuePair<uint, WeakReference<ScriptIdentityHandle>> entry in scriptIdentityHandles)
                {
                    if (!entry.Value.TryGetTarget(out ScriptIdentityHandle? state) ||
                        !state.Owner.TryGetTarget(out object? owner) ||
                        live != null && !live.Contains(owner))
                    {
                        expired.Add(entry.Key);
                    }
                }
                foreach (uint handle in expired.Handles)
                {
                    scriptIdentityHandles.Remove(handle, out var reference);
                    if (reference!.TryGetTarget(out ScriptIdentityHandle? state) && state.Owner.TryGetTarget(out object? owner))
                    {
                        scriptIdentities.Remove(owner);
                    }
                    Memory.Free(handle);
                }
            }
            finally { expired.Dispose(); }
            nextIdentitySweep = Math.Max(256, scriptIdentityHandles.Count * 2);
        }

        internal readonly struct LightUserdata
        {
            internal readonly uint Pointer;
            internal LightUserdata(uint pointer)
            {
                Pointer = pointer;
            }
        }

        private int StackIndex(int index)
        {
            long position = index > 0 ? (long)index - 1 : (long)nativeStack.Count + index;
            if (index == 0 || index <= RegistryIndex || position < 0 || position >= nativeStack.Count)
            {
                throw new RuntimeFault($"native script stack index {index} out of range");
            }
            return (int)position;
        }

        private object? StackValue(int index)
        {
            if (index == RegistryIndex)
            {
                return scriptRegistry;
            }
            if (index == GlobalsIndex)
            {
                return Script.Globals;
            }
            if (index < GlobalsIndex)
            {
                long capture = (long)GlobalsIndex - index - 1;
                return activeScriptClosure != null && capture < activeScriptClosure.Upvalues.Length
                    ? activeScriptClosure.Upvalues[(int)capture] : MissingScriptValue;
            }
            long position = index > 0 ? (long)index - 1 : (long)nativeStack.Count + index;
            return index == 0 || index <= RegistryIndex || position < 0 || position >= nativeStack.Count
                ? MissingScriptValue : nativeStack[(int)position];
        }

        private void StackPush(object? value)
        {
            if (nativeStack.Count == NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            nativeStack.Add(ReferenceEquals(value, MissingScriptValue) ? null : value);
            if (disposed) return;
            if (value is string text)
            {
                nativeStack[nativeStack.Count - 1] = CanonicalScriptString(text);
            }
            Script.Account(nativeStack[nativeStack.Count - 1]);
            if (nativeAllocationCoroutine == null)
            {
                Script.EnsureAllocationStack(nativeStack.Count + 1);
            }
            else
            {
                Script.EnsureCoroutineAllocationStack(nativeAllocationCoroutine, nativeStack.Count + 1);
            }
        }

        private int ScriptType(object? value)
        {
            return ReferenceEquals(value, MissingScriptValue) ? -1 : value == null ? 0 : value is bool || value is ScriptBoolean ? 1 :
                value is LightUserdata ? 2 : value is int ? 3 : ScriptVm.IsString(value) ? 4 : value is ScriptTable ? 5 :
                ScriptVm.IsFunction(value) ? 6 : value is ScriptUserdata ? 7 : value is ScriptVm.Coroutine ? 8 :
                throw new RuntimeFault("unmapped native script value type");
        }

        private static bool ScriptInteger(object? value, out int number)
        {
            return ScriptVm.TryNumber(value, out number);
        }

        private static string ScriptTypeName(int type, bool shortName)
        {
            return type switch
            {
                -1 => "no value",
                0 => "nil",
                1 => shortName ? "bool" : "boolean",
                2 or 7 => shortName ? "obj" : "object",
                3 => shortName ? "num" : "number",
                4 => shortName ? "str" : "string",
                5 => shortName ? "tab" : "table",
                6 => shortName ? "func" : "function",
                8 => shortName ? "co" : "thread",
                _ => throw new RuntimeFault("invalid native script type tag")
            };
        }

        private uint ScriptPointer(object? identity)
        {
            if (identity is LightUserdata pointer)
            {
                return pointer.Pointer;
            }
            if (identity is ScriptUserdata userdata)
            {
                return userdata.Pointer;
            }
            if (identity is ScriptVm.Coroutine coroutine)
            {
                return CoroutineHandle(coroutine);
            }
            if (!(identity is ScriptTable) && !ScriptVm.IsFunction(identity))
            {
                return 0;
            }
            if (!scriptIdentities.TryGetValue(identity!, out ScriptIdentityHandle? state))
            {
                if (scriptIdentityHandles.Count >= nextIdentitySweep)
                {
                    SweepScriptIdentityHandles();
                }
                state = new ScriptIdentityHandle
                {
                    Handle = Memory.AllocateVm(4),
                    Owner = new WeakReference<object>(identity!)
                };
                scriptIdentities.Add(identity!, state);
                scriptIdentityHandles.Add(state.Handle, new WeakReference<ScriptIdentityHandle>(state));
            }
            return state.Handle;
        }

        private string? StackString(int index)
        {
            object? value = StackValue(index);
            if (value is int number)
            {
                string text = number.ToString(CultureInfo.InvariantCulture);
                SetStackValue(index, text);
                return text;
            }
            return ScriptVm.IsString(value) ? ScriptVm.Text(value) : null;
        }

        private uint ScriptStringPointer(string text, bool permanent = false)
        {
            Script.Account(text);
            if (permanent)
            {
                permanentScriptStrings.Add(text);
            }
            if (!stringPointers.TryGetValue(text, out uint pointer))
            {
                CheckScriptAllocation((uint)text.Length + 1);
                pointer = Memory.CopyVmText(text);
                stringPointers.Add(text, pointer);
            }
            return pointer;
        }

        private ScriptTable StackTable(int index)
        {
            return StackValue(index) as ScriptTable ?? throw new RuntimeFault("native script expected table");
        }

        private uint InvokeScriptCommand(uint state, int operation, int value)
        {
            // The platform glyph probe returns before the reference touches L or its stack.
            if (operation == 7)
            {
                return unchecked((uint)value);
            }
            if (state != 0)
            {
                if (state != scriptState && (activeGuestScriptState == 0 || state != activeGuestScriptState))
                {
                    throw new RuntimeFault($"invalid native script state 0x{state:x8}");
                }
                if (nativeStack.Count == NativeStackLimit)
                {
                    throw new RuntimeFault("native script stack limit");
                }
            }
            int result = Command(operation, value);
            if (state == 0)
            {
                return 0;
            }
            StackPush(result);
            return 1;
        }

        private string? TransformGuestString(int operation, uint input, uint length)
        {
            return TransformStringCommand(operation, length,
                () => length == 0 ? Array.Empty<byte>() : Memory.Read(input, length));
        }

        private uint InvokeNativeStringCommand(uint state, int operation, uint input, uint length)
        {
            if (operation == 9)
            {
                Memory.Check(input, length);
                ApplySdkCapacityCompatibility(input, length);
                ApplyOriginalSdkCompatibility(input, length);
                Record($"cache sync range {input:x8}+{length}");
                return 0;
            }
            if (operation != 2 && operation != 3 && operation != 4 && operation != 5 &&
                operation != 6 && operation != 100 && operation != 200 && operation != 300 &&
                operation != 500 && operation != 501 && operation != 502 && operation != 600 &&
                operation != 601 && operation != 602 && operation != 603 &&
                operation != 700 && operation != 701 && operation != 800 && operation != 801 &&
                operation != 802 && operation != 900)
            {
                throw new RuntimeFault($"unsupported native _strCom {operation}");
            }
            if (state != 0 || operation == 3 || operation == 5 || operation == 6 ||
                operation == 100 || operation == 300 ||
                operation >= 500)
            {
                if (state != scriptState && (activeGuestScriptState == 0 || state != activeGuestScriptState))
                {
                    throw new RuntimeFault($"invalid native script state 0x{state:x8}");
                }
                if (nativeStack.Count > NativeStackLimit - (operation == 603 || operation == 801 ? 2 : 1))
                {
                    throw new RuntimeFault("native script stack limit");
                }
            }
            if (operation == 2)
            {
                ReplaceRamPackage(input, length);
                if (state == 0)
                {
                    return 0;
                }
                StackPush(0);
                return 1;
            }
            if (operation == 5 || operation == 6)
            {
                byte[] bytes = length == 0 ? Array.Empty<byte>() : Memory.Read(input, length);
                if (operation == 5)
                {
                    int end = Array.IndexOf(bytes, (byte)0);
                    guestExceptionText = ScriptChunk.Bytes.GetString(bytes, 0, end < 0 ? bytes.Length : end);
                }
                else
                {
                    guestExceptionText = null;
                }
                StackPush(0);
                return 1;
            }
            if (operation == 300)
            {
                byte[] packed = length == 0 ? Array.Empty<byte>() : Memory.Read(input, length);
                StackPush(ScriptChunk.Bytes.GetString(Package.ReadPackedBytes(packed, Record)));
                return 1;
            }
            if (operation == 100)
            {
                byte[] bytes = length == 0 ? Array.Empty<byte>() : Memory.Read(input, length);
                int end = Array.IndexOf(bytes, (byte)0);
                string name = ScriptChunk.Bytes.GetString(bytes, 0, end < 0 ? bytes.Length : end);
                object?[] dnsResults = StringCommand(new object?[] { 100, name });
                foreach (object? value in dnsResults)
                {
                    StackPush(value);
                }
                return (uint)dnsResults.Length;
            }
            if (operation == 700)
            {
                Memory.Check(input, length);
                byte[] oldImsi = new byte[16];
                uint copied = Math.Min(length, (uint)oldImsi.Length);
                if (copied != 0)
                {
                    Memory.Read(input, copied).CopyTo(oldImsi, 0);
                }
                uint temporary = Memory.CopyVm(oldImsi);
                try
                {
                    ScriptInteger(StackValue(3), out int type);
                    StackPush(unchecked((int)NotifySimChange(unchecked((short)type), temporary)));
                }
                finally
                {
                    Memory.Free(temporary);
                }
                return 1;
            }
            if (operation == 701)
            {
                byte[] content = length == 0 ? Array.Empty<byte>() : Memory.Read(input, length);
                string sender = StackString(3) ?? throw new RuntimeFault("native SMS sender must be a string");
                ScriptInteger(StackValue(4), out int encoding);
                int smsResult = ReceiveSms(ScriptChunk.Bytes.GetString(content), sender, encoding);
                if (exitRequested || disposed)
                {
                    return 0;
                }
                StackPush(smsResult);
                return 1;
            }
            if (operation == 900)
            {
                Memory.Check(input, length);
                StackPush(unchecked((int)Cpu.Invoke(0xf0000000 + 38 * 4,
                    stackalloc uint[] { 200001u, Memory.U32(table + 24 * 4), 16u, 0u, 0u, 0u })));
                return 1;
            }
            if (operation == 800)
            {
                if (length < 12)
                {
                    throw new RuntimeFault("EXT native-loaded.ext: truncated header");
                }
                Memory.Check(input, length);
                ScriptInteger(StackValue(3), out int loadCode);
                StackPush(unchecked((int)Initialize(new Module
                {
                    Name = "native-loaded.ext", Address = input, Size = length
                }, loadCode)));
                return 1;
            }
            if (operation == 801)
            {
                Memory.Check(input, length);
                ScriptInteger(StackValue(3), out int helperCode);
                object?[] values = Helper(helperCode, input, length);
                StackPush(values[0]);
                StackPush(values[1]);
                return 2;
            }
            if (operation == 802)
            {
                if (!ScriptInteger(StackValue(4), out int externalPatch))
                {
                    throw new RuntimeFault("EXT external patch pointer is required");
                }
                ScriptInteger(StackValue(3), out int loadCode);
                byte[] ext = length == 0 ? Array.Empty<byte>() : Memory.Read(input, length);
                StackPush(unchecked((int)LoadExtWithPatch(ext, "native-external.ext", loadCode,
                    unchecked((uint)externalPatch))));
                return 1;
            }
            if (operation == 600)
            {
                ScriptInteger(StackValue(3), out int offset);
                ScriptInteger(StackValue(4), out int count);
                uint first = Memory.U8(input);
                uint index = first == '*' ? Memory.U8(input + 1) - 'A' : 0;
                string? bytes = ReadResidentBytes(first, index, offset, count);
                if (bytes == null)
                {
                    return 0;
                }
                StackPush(bytes);
                return 1;
            }
            if (operation is 601 or 602 or 603)
            {
                object?[] results = ResourceStringCommand(operation, Memory.CString(input));
                foreach (object? value in results)
                {
                    StackPush(value);
                }
                return (uint)results.Length;
            }
            if (operation == 3 || operation == 4)
            {
                string start = "start.mr";
                if (operation == 3 && StackValue(3) != null && !ReferenceEquals(StackValue(3), MissingScriptValue))
                {
                    start = StackString(3) ?? throw new RuntimeFault("native script expected string");
                }
                var bytes = new List<byte>(127);
                if (input != 0)
                {
                    for (uint index = 0; index < 127; index++)
                    {
                        byte value = (byte)Memory.U8(input + index);
                        if (value == 0)
                        {
                            break;
                        }
                        bytes.Add(value);
                    }
                }
                WriteRuntimeText(operation == 3 ? 102u : 138u, ScriptChunk.Bytes.GetString(bytes.ToArray()));
                if (operation == 3)
                {
                    WriteRuntimeText(103, start);
                }
                if (state == 0)
                {
                    return 0;
                }
                StackPush(0);
                return 1;
            }
            if (operation == 501 || operation == 502)
            {
                string? output = TransformGuestString(operation, input, length);
                if (output == null)
                {
                    return 0;
                }
                StackPush(output);
                return 1;
            }
            if (operation == 500)
            {
                // md5_append takes a signed length and ignores non-positive lengths.
                ReadOnlySpan<byte> bytes = (int)length <= 0 ? ReadOnlySpan<byte>.Empty : Memory.ReadSpan(input, length);
                StackPush(ScriptChunk.Bytes.GetString(GuestMd5.Digest(bytes)));
                return 1;
            }
            if (input != 0 && length != 0)
            {
                Memory.Check(input, length);
            }
            UpdateCrc(0, 0);
            uint result = UpdateCrc(input, length);
            if (state == 0)
            {
                return 0;
            }
            StackPush(unchecked((int)result));
            return 1;
        }

        private uint DispatchInternal(uint address, ArmCpu cpu)
        {
            Live();
            int slot = (int)(address - 0xf0010000) / 4;
            uint A(int index) => cpu.ReadArgument(index);
            if (slot == 12)
            {
                return SetSmsBytes((int)A(0), A(1), (int)A(2));
            }
            if (slot == 13)
            {
                return AddSmsNumber((int)A(0), Memory.CString(A(1)));
            }
            if (slot == 15)
            {
                return CheckDeviceRecord(A(0));
            }
            if (slot == 14)
            {
                return NotifySimChange(unchecked((short)A(0)), A(1));
            }
            uint scriptHandle = A(0);
            if (scriptHandle == scriptState || scriptHandle == activeGuestScriptState && activeGuestScriptState != 0)
            {
                return DispatchScriptInternal(slot, cpu);
            }
            if (!TryCoroutineState(scriptHandle, out CoroutineNativeState coroutineState, out ScriptVm.Coroutine coroutine))
            {
                throw new RuntimeFault($"invalid native script state 0x{A(0):x8}");
            }
            List<object?> outerStack = nativeStack;
            ScriptVm.Coroutine? outerAllocationCoroutine = nativeAllocationCoroutine;
            nativeAllocationCoroutine = coroutine;
            nativeStackFrames.Add(outerStack);
            nativeStack = coroutineState.Stack;
            try
            {
                return DispatchScriptInternal(slot, cpu);
            }
            finally
            {
                coroutineState.Stack = nativeStack;
                nativeAllocationCoroutine = outerAllocationCoroutine;
                RestoreNativeCallStack(outerStack);
            }
        }

        private uint DispatchScriptInternal(int slot, ArmCpu cpu)
        {
            uint A(int index) => cpu.ReadArgument(index);
            int index = (int)A(1);
            switch (slot)
            {
                case 16:
                    return (uint)nativeStack.Count;
                case 17:
                    long count = index >= 0 ? index : (long)nativeStack.Count + index + 1;
                    if (count < 0 || count > NativeStackLimit)
                    {
                        throw new RuntimeFault("native script invalid stack size");
                    }
                    if (count < nativeStack.Count)
                    {
                        nativeStack.RemoveRange((int)count, nativeStack.Count - (int)count);
                    }
                    while (nativeStack.Count < count)
                    {
                        StackPush(null);
                    }
                    return 0;
                case 18:
                    StackPush(StackValue(index));
                    return 0;
                case 19:
                    nativeStack.RemoveAt(StackIndex(index));
                    return 0;
                case 20:
                    int position = StackIndex(index);
                    object? moved = nativeStack[StackIndex(-1)];
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    nativeStack.Insert(position, moved);
                    return 0;
                case 21:
                    SetStackValue(index, nativeStack[StackIndex(-1)]);
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    return 0;
                case 22:
                    return ScriptInteger(StackValue(index), out _) ? 1u : 0u;
                case 23:
                    return StackValue(index) is int || ScriptVm.IsString(StackValue(index)) ? 1u : 0u;
                case 24:
                    return StackValue(index) is ScriptVm.Native ? 1u : 0u;
                case 25:
                    return StackValue(index) is LightUserdata || StackValue(index) is ScriptUserdata ? 1u : 0u;
                case 26:
                    return unchecked((uint)ScriptType(StackValue(index)));
                case 27:
                case 28:
                    return ScriptStringPointer(ScriptTypeName(index, slot == 28), true);
                case 29:
                case 30:
                case 31:
                    object? first = StackValue(index);
                    object? second = StackValue((int)A(2));
                    if (ReferenceEquals(first, MissingScriptValue) || ReferenceEquals(second, MissingScriptValue))
                    {
                        return 0;
                    }
                    return (slot == 29 ? Script.EqualValue(first, second) : slot == 30 ? ScriptVm.RawEqual(first, second)
                        : Script.CompareValue(first, second, false)) ? 1u : 0u;
                case 32:
                    return ScriptInteger(StackValue(index), out int integer) ? unchecked((uint)integer) : 0;
                case 33:
                    object? boolean = StackValue(index);
                    return boolean == null || ReferenceEquals(boolean, MissingScriptValue) || boolean is bool b && !b ? 0u : 1u;
                case 34:
                case 35:
                case 36:
                case 37:
                    if ((slot == 36 || slot == 37) && StackValue(index) is ScriptTable pair)
                    {
                        return ScriptInteger(pair[slot == 36 ? 1 : 2], out int raw) ? (uint)raw : 0;
                    }
                    if (StackValue(index) is ScriptMutableString mutable)
                    {
                        return slot == 35 || slot == 37 ? (uint)mutable.Length : mutable.Pointer;
                    }
                    string? converted = StackString(index);
                    return converted == null ? 0 : slot == 35 || slot == 37 ? (uint)converted.Length : InternedScriptString(converted).Pointer;
                case 38:
                    return StackValue(index) is ScriptVm.Native native && scriptClosures.TryGetValue(native, out var callable)
                        ? callable.Address : 0;
                case 40:
                    return StackValue(index) is ScriptVm.Coroutine thread ? CoroutineHandle(thread) : 0;
                case 39:
                    return StackValue(index) is LightUserdata light ? light.Pointer :
                        StackValue(index) is ScriptUserdata user ? user.Pointer : 0;
                case 41:
                    return ScriptPointer(StackValue(index));
                case 42:
                    StackPush(null);
                    return 0;
                case 43:
                    StackPush(ScriptVm.BoxInteger(index));
                    return 0;
                case 44:
                    CheckScriptAllocation(0);
                    StackPush(A(2) == 0 ? "" : DecodeScriptBytes(Memory.ReadSpan(A(1), A(2))));
                    return 0;
                case 45:
                    CheckScriptAllocation(0);
                    StackPush(A(1) == 0 ? null : Memory.CString(A(1)));
                    return 0;
                case 46:
                    return PushFormattedScript(A(1), A(2));
                case 47:
                    return PushFormattedScript(Memory.CString(A(1)), cpu.ReadArgument, 2);
                case 48:
                    StackPush(ScriptBoolean.Box(A(1) != 0));
                    return 0;
                case 49:
                    PushGuestClosure(A(1), (int)A(2));
                    return 0;
                case 50:
                case 51:
                    object? source = StackValue(index);
                    object? lookupKey = nativeStack[StackIndex(-1)];
                    object? found = slot == 50 ? Script.GetValue(source, lookupKey) : StackTable(index)[lookupKey];
                    nativeStack[StackIndex(-1)] = found;
                    return 0;
                case 52:
                    StackPush(StackTable(index)[(int)A(2)]);
                    return 0;
                case 53:
                    CheckScriptAllocation(0);
                    StackPush(Script.NewTable());
                    return 0;
                case 54:
                    ScriptTable? meta = ScriptVm.Metatable(StackValue(index));
                    if (meta == null)
                    {
                        return 0;
                    }
                    StackPush(meta);
                    return 1;
                case 55:
                case 56:
                    object? destination = StackValue(index);
                    object? key = nativeStack[StackIndex(-2)];
                    object? assigned = nativeStack[StackIndex(-1)];
                    if (slot == 55)
                    {
                        Script.SetValue(destination, key, assigned);
                    }
                    else
                    {
                        StackTable(index)[key] = assigned;
                    }
                    nativeStack.RemoveRange(nativeStack.Count - 2, 2);
                    return 0;
                case 57:
                    ScriptTable integerDestination = StackTable(index);
                    integerDestination[(int)A(2)] = nativeStack[StackIndex(-1)];
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    return 0;
                case 58:
                case 59:
                    return CallScriptStack(index, (int)A(2), slot == 59 ? (int)A(3) : 0, slot == 59);
                case 60:
                    return LoadScriptFromGuest(A(1), A(2), A(3));
                case 61:
                    return GcThreshold();
                case 62:
                    return SetGcThreshold(index);
                case 63:
                    throw new GuestScriptError(nativeStack[StackIndex(-1)]);
                case 64:
                    return index >= 0 && (long)nativeStack.Count + index <= NativeStackLimit ? 1u : 0u;
                case 65:
                    if (nativeStack.Count == NativeStackLimit)
                    {
                        throw new RuntimeFault("native script stack limit");
                    }
                    CheckScriptAllocation(A(1));
                    uint storage = Memory.AllocateVm(A(1));
                    StackPush(NewScriptUserdata(storage, A(1)));
                    return storage;
                case 66:
                    StackPush(Script.FunctionEnvironment(StackValue(index)));
                    return 0;
                case 67:
                    object? changed = StackValue(index);
                    ScriptTable environment = StackTable(-1);
                    bool updated = Script.SetFunctionEnvironment(changed, environment);
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    return updated ? 1u : 0u;
                case 68:
                    object? target = StackValue(index);
                    object? metatable = nativeStack[StackIndex(-1)];
                    if (metatable != null && !(metatable is ScriptTable))
                    {
                        throw new RuntimeFault("native script metatable must be nil/table");
                    }
                    bool set = ScriptVm.SetMetatable(target, metatable as ScriptTable);
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    return set ? 1u : 0u;
                case 69:
                    return ProtectedGuestCall(A(1), A(2));
                case 70:
                    ScriptTable iterated = StackTable(index);
                    bool foundNext = iterated.TryNext(nativeStack[StackIndex(-1)], out object? nextKey, out object? nextValue);
                    if (foundNext && nativeStack.Count == NativeStackLimit)
                    {
                        throw new RuntimeFault("native script stack limit");
                    }
                    nativeStack.RemoveAt(nativeStack.Count - 1);
                    if (!foundNext)
                    {
                        return 0;
                    }
                    StackPush(nextKey);
                    StackPush(nextValue);
                    return 1;
                case 71:
                    if (index < 0 || index > nativeStack.Count)
                    {
                        throw new RuntimeFault("invalid native concat count");
                    }
                    object? joined = index == 0 ? "" : Script.Concatenate(nativeStack.Count - index, nativeStack.Count - 1,
                        nativeStack);
                    nativeStack.RemoveRange(nativeStack.Count - index, index);
                    StackPush(joined);
                    return 0;
                case 72:
                    StackPush(new LightUserdata(A(1)));
                    return 0;
                case 73:
                    return (uint)Math.Min(int.MaxValue, ScriptBytes() >> 10);
                case 74:
                    return DumpScriptToGuest(A(1), A(2));
                case 75:
                    if (index < 0 || index > nativeStack.Count)
                    {
                        throw new RuntimeFault("native coroutine invalid yield result count");
                    }
                    Script.YieldFromGuest(CopyStackRange(nativeStack, nativeStack.Count - index, index));
                    return uint.MaxValue;
                case 76:
                    if (!TryCoroutineState(A(0), out CoroutineNativeState resumed, out ScriptVm.Coroutine coroutine))
                    {
                        throw new RuntimeFault("native coroutine resume requires a thread state");
                    }
                    if (index < 0 || index > nativeStack.Count)
                    {
                        throw new RuntimeFault("native coroutine invalid resume argument count");
                    }
                    object?[] result = Script.ResumeExternal(coroutine,
                        CopyStackRange(nativeStack, nativeStack.Count - index, index));
                    nativeStack.Clear();
                    for (int valueIndex = 1; valueIndex < result.Length; valueIndex++)
                    {
                        StackPush(result[valueIndex]);
                    }
                    return ScriptVm.Truth(result[0]) ? 0u : 1u;
                default:
                    throw new RuntimeFault($"unsupported internal script slot {slot}");
            }
        }
    }
}
