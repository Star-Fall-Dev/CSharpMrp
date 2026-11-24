using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    internal sealed class ScriptUserdata
    {
        internal uint Pointer;
        internal uint Size;
        internal ScriptTable? Metatable;
        internal bool Finalized;
    }

    public sealed partial class RuntimeSession
    {
        private sealed class GuestScriptClosure
        {
            internal RuntimeSession? Owner;
            internal uint Address;
            internal object?[] Upvalues = Array.Empty<object?>();

            internal object?[] Invoke(object?[] arguments) =>
                (Owner ?? throw new ObjectDisposedException(nameof(RuntimeSession))).InvokeGuestClosure(this, arguments);
        }

        private sealed class GuestScriptError : Exception, IScriptRaisedError
        {
            internal readonly object? Value;
            object? IScriptRaisedError.Value => Value;
            internal GuestScriptError(object? value) : base(ScriptVm.IsString(value) ? ScriptVm.Text(value) : "guest script error")
            {
                Value = value;
            }
        }

        private GuestScriptClosure? activeScriptClosure;
        private List<object?>? cachedNativeCallStack;
        private const int MaximumCachedNativeCallStack = 256;
        private readonly ConditionalWeakTable<ScriptVm.Native, GuestScriptClosure> scriptClosures =
            new ConditionalWeakTable<ScriptVm.Native, GuestScriptClosure>();

        private static object?[] CopyStackRange(List<object?> stack, int start, int count)
        {
            if (count == 0)
            {
                return Array.Empty<object?>();
            }
            var values = new object?[count];
            stack.CopyTo(start, values, 0, count);
            return values;
        }

        private List<object?> RentNativeCallStack(int capacity)
        {
            List<object?> stack = cachedNativeCallStack ?? new List<object?>(capacity);
            cachedNativeCallStack = null;
            return stack;
        }

        private void ReturnNativeCallStack(List<object?> stack)
        {
            stack.Clear();
            if (!disposed && stack.Capacity <= MaximumCachedNativeCallStack)
            {
                cachedNativeCallStack = stack;
            }
        }

        private void RestoreNativeCallStack(List<object?> outer)
        {
            nativeStack = outer;
            nativeStackFrames.RemoveAt(nativeStackFrames.Count - 1);
            if (disposed) ReleaseNativeScriptStateWhenIdle();
        }

        private void ReleaseNativeScriptStateWhenIdle()
        {
            if (nativeStackFrames.Count != 0) return;
            Cpu.RunAfterInvocations(() =>
            {
                nativeStack.Clear();
                nativeStack.TrimExcess();
                foreach (var reference in coroutineHandles.Values)
                {
                    if (reference.TryGetTarget(out CoroutineNativeState? state))
                    {
                        state.Stack.Clear();
                        state.Stack.TrimExcess();
                    }
                }
                coroutineStates.Clear();
                coroutineHandles.Clear();
                coroutineHandles.TrimExcess();
                scriptIdentities.Clear();
                scriptIdentityHandles.Clear();
                scriptIdentityHandles.TrimExcess();
                foreach (var entry in scriptClosures)
                {
                    GuestScriptClosure closure = entry.Value;
                    closure.Owner = null;
                    Array.Clear(closure.Upvalues, 0, closure.Upvalues.Length);
                    closure.Upvalues = Array.Empty<object?>();
                }
                scriptClosures.Clear();
                resourceNames.Clear();
                resourceNames.TrimExcess();
                archivedScriptModules.Clear();
                archivedScriptModules.TrimExcess();
            });
        }

        private void SetStackValue(int index, object? value)
        {
            if (index == RegistryIndex || index == GlobalsIndex)
            {
                if (!(value is ScriptTable tableValue))
                {
                    throw new RuntimeFault("native script registry/globals must be a table");
                }
                if (index == RegistryIndex)
                {
                    scriptRegistry = tableValue;
                    Script.Registry = tableValue;
                }
                else
                {
                    Script.Globals = tableValue;
                }
            }
            else if (index < GlobalsIndex)
            {
                long upvalue = (long)GlobalsIndex - index - 1;
                if (activeScriptClosure == null || upvalue >= activeScriptClosure.Upvalues.Length)
                {
                    throw new RuntimeFault("native script invalid upvalue index");
                }
                activeScriptClosure.Upvalues[(int)upvalue] = value;
            }
            else
            {
                nativeStack[StackIndex(index)] = value;
            }
        }

        private void PushGuestClosure(uint address, int captures)
        {
            if (captures < 0 || captures > nativeStack.Count || captures > 255)
            {
                throw new RuntimeFault("native script invalid closure captures");
            }
            Memory.Check(address & ~1u, 2);
            CheckScriptAllocation(0);
            if (captures == 0 && nativeStack.Count == NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            var closure = new GuestScriptClosure
            {
                Owner = this,
                Address = address,
                Upvalues = CopyStackRange(nativeStack, nativeStack.Count - captures, captures)
            };
            ScriptVm.Native function = closure.Invoke;
            scriptClosures.Add(function, closure);
            Script.RetainNativeValues(function, closure.Upvalues);
            nativeStack.RemoveRange(nativeStack.Count - captures, captures);
            StackPush(function);
        }

        private object?[] InvokeGuestClosure(GuestScriptClosure closure, object?[] arguments)
        {
            Live();
            if (arguments.Length > NativeStackLimit)
            {
                throw new RuntimeFault("native script argument limit");
            }
            List<object?> outer = nativeStack;
            List<object?> stack = RentNativeCallStack(arguments.Length);
            stack.AddRange(arguments);
            GuestScriptClosure? caller = activeScriptClosure;
            uint outerState = activeGuestScriptState;
            nativeStackFrames.Add(outer);
            nativeStack = stack;
            activeScriptClosure = closure;
            try
            {
                activeGuestScriptState = Script.ActiveCoroutine is ScriptVm.Coroutine coroutine
                    ? CoroutineHandle(coroutine) : scriptState;
                uint count = Cpu.Invoke(closure.Address, stackalloc uint[] { activeGuestScriptState });
                if (count > nativeStack.Count)
                {
                    throw new RuntimeFault("native script invalid callback result count");
                }
                return CopyStackRange(nativeStack, nativeStack.Count - (int)count, (int)count);
            }
            finally
            {
                activeScriptClosure = caller;
                activeGuestScriptState = outerState;
                RestoreNativeCallStack(outer);
                ReturnNativeCallStack(stack);
            }
        }

        private uint CallScriptStack(int arguments, int results, int errorIndex, bool protect)
        {
            if (arguments < 0 || arguments >= nativeStack.Count || results < -1 || results > NativeStackLimit)
            {
                throw new RuntimeFault("native script invalid call arity");
            }
            int start = nativeStack.Count - arguments - 1;
            if (results >= 0 && start + results > NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            object? handler = errorIndex == 0 ? null : StackValue(errorIndex);
            if (errorIndex != 0 && ReferenceEquals(handler, MissingScriptValue))
            {
                throw new RuntimeFault("native script invalid error handler index");
            }
            object? function = nativeStack[start];
            object?[] values = CopyStackRange(nativeStack, start + 1, arguments);
            nativeStack.RemoveRange(start, arguments + 1);
            try
            {
                object?[] returned = Script.Call(function, values);
                int count = results < 0 ? returned.Length : results;
                if (start + count > NativeStackLimit)
                {
                    throw new RuntimeFault("native script stack limit");
                }
                for (int index = 0; index < count; index++)
                {
                    StackPush(index < returned.Length ? returned[index] : null);
                }
                return 0;
            }
            catch (Exception error) when (protect && (error is IScriptRaisedError || error is RuntimeFault))
            {
                object? value = error is IScriptRaisedError raised ? raised.Value : error.Message;
                uint status = 1; // MRP_ERRRUN (this runtime uses 1, not Lua 5.1's 2).
                if (errorIndex != 0)
                {
                    try
                    {
                        if (!ScriptVm.IsFunction(handler))
                        {
                            throw new RuntimeFault("native script error handler must be a function");
                        }
                        object?[] transformed = Script.Call(handler, value);
                        value = transformed.Length == 0 ? null : transformed[0];
                    }
                    catch (Exception handlerError) when (handlerError is IScriptRaisedError || handlerError is RuntimeFault)
                    {
                        value = "error in error handling";
                        status = 5;
                    }
                }
                StackPush(value);
                Record($"native protected script call returned error {status}");
                Record("native protected script error: " + error.Message);
                return status;
            }
        }
    }
}
