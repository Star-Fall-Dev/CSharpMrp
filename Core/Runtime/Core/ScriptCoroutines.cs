using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    internal interface IScriptRaisedError
    {
        object? Value { get; }
    }

    internal sealed class ScriptRaisedError : Exception, IScriptRaisedError
    {
        private readonly object? value;
        object? IScriptRaisedError.Value => value;

        internal ScriptRaisedError(object? value) : base(ScriptVm.IsString(value) ? ScriptVm.Text(value) : "script error")
        {
            this.value = value;
        }
    }

    public sealed partial class ScriptVm
    {
        internal sealed class Coroutine
        {
            internal readonly object OwnerToken;
            internal object? EntryFunction;
            internal List<CoroutineFrame>? Frames;
            internal bool Running;
            internal bool Dead;

            internal Coroutine(ScriptVm owner, object function)
            {
                OwnerToken = owner.coroutineOwnerToken;
                Dead = owner.coroutinesClosed;
                if (!Dead) EntryFunction = function;
            }

            internal void Finish()
            {
                Dead = true;
                EntryFunction = null;
                Frames = null;
            }
        }

        internal class CoroutineFrame
        {
            internal readonly Closure Function;
            internal readonly Cell[] Registers;
            internal readonly int Pc;
            internal readonly int Top;
            internal readonly int ResultBase;
            internal readonly int ResultField;
            internal readonly bool TailCall;

            internal CoroutineFrame(Closure function, Cell[] registers, int pc, int top,
                int resultBase, int resultField, bool tailCall)
            {
                Function = function;
                Registers = registers;
                Pc = pc;
                Top = top;
                ResultBase = resultBase;
                ResultField = resultField;
                TailCall = tailCall;
            }
        }

        private sealed class CapturedCoroutineFrame : CoroutineFrame
        {
            internal readonly RegisterCaptures Captures;

            internal CapturedCoroutineFrame(Closure function, Cell[] registers, int pc, int top,
                int resultBase, int resultField, bool tailCall, RegisterCaptures captures)
                : base(function, registers, pc, top, resultBase, resultField, tailCall)
            {
                Captures = captures;
            }
        }

        private sealed class CoroutineYield : Exception
        {
            internal readonly object?[] Values;
            internal readonly List<CoroutineFrame> Frames = new List<CoroutineFrame>();

            internal CoroutineYield(object?[] values)
            {
                Values = values;
            }
        }

        private sealed class CoroutineWrapperOwner
        {
            internal ScriptVm? Vm;
        }

        private readonly object coroutineOwnerToken = new object();
        private CoroutineWrapperOwner? coroutineWrapperOwner;
        private ConditionalWeakTable<Coroutine, object>? sweptCoroutines;
        private bool coroutinesClosed;
        private Coroutine? currentCoroutine;
        internal Coroutine? ActiveCoroutine => currentCoroutine;

        internal object?[] ResumeExternal(Coroutine coroutine, object?[] args)
        {
            if (coroutine.OwnerToken != coroutineOwnerToken)
            {
                throw new RuntimeFault("coroutine belongs to another script state");
            }
            return ResumeCoroutine(coroutine, args);
        }

        internal void YieldFromGuest(object?[] values)
        {
            if (currentCoroutine == null || nativeBoundaryDepth != 1)
            {
                throw new RuntimeFault("yield across metamethod or C-call boundary");
            }
            throw new CoroutineYield(values);
        }

        private void RegisterCoroutines()
        {
            var library = new ScriptTable();
            library["create"] = new Native(args =>
            {
                if (args.Length == 0 || FunctionPrototype(args[0]) == null)
                {
                    throw new RuntimeFault("coroutine create requires a Mythroad function");
                }
                CollectionRequested?.Invoke();
                var coroutine = new Coroutine(this, args[0]!);
                Account(coroutine);
                return new object?[] { coroutine };
            });
            library["resume"] = new Native(args =>
            {
                Coroutine coroutine = RequireCoroutine(args);
                object?[] values = args.Length == 1 ? Array.Empty<object?>() : args.AsSpan(1).ToArray();
                return ResumeCoroutine(coroutine, values);
            });
            library["status"] = new Native(args =>
            {
                Coroutine coroutine = RequireCoroutine(args);
                return new object?[] { ReferenceEquals(currentCoroutine, coroutine) ? "running" :
                    coroutine.Dead ? "dead" : "suspended" };
            });
            library["wrap"] = new Native(args =>
            {
                if (args.Length == 0 || FunctionPrototype(args[0]) == null)
                {
                    throw new RuntimeFault("coroutine wrap requires a Mythroad function");
                }
                CollectionRequested?.Invoke();
                var coroutine = new Coroutine(this, args[0]!);
                Account(coroutine);
                var owner = coroutineWrapperOwner ??= new CoroutineWrapperOwner { Vm = coroutinesClosed ? null : this };
                Native wrapper = WrapCoroutine(owner, coroutine);
                RetainNativeValues(wrapper, coroutine);
                return new object?[] { wrapper };
            });
            library["yield"] = new Native(args =>
            {
                YieldFromGuest(args);
                return Array.Empty<object?>();
            });
            Globals["_co"] = library;
        }

        private static Native WrapCoroutine(CoroutineWrapperOwner owner, Coroutine coroutine) => values =>
        {
            ScriptVm vm = owner.Vm ?? throw new ScriptRaisedError("cannot resume dead coroutine");
            object?[]? result = vm.ResumeCoroutine(coroutine, values, out object? error);
            if (result == null)
            {
                if (error is string message)
                {
                    error = vm.CurrentScriptLocation() + message;
                }
                throw new ScriptRaisedError(error);
            }
            // A resumed tail call can return the caller's own argument array.
            return result.Length != 0 && ReferenceEquals(result, values) ? (object?[])result.Clone() : result;
        };

        private Coroutine RequireCoroutine(object?[] args)
        {
            if (args.Length == 0 || !(args[0] is Coroutine coroutine) || coroutine.OwnerToken != coroutineOwnerToken)
            {
                throw new RuntimeFault("coroutine expected");
            }
            return coroutine;
        }

        private object?[] ResumeCoroutine(Coroutine coroutine, object?[] args)
        {
            object?[]? values = ResumeCoroutine(coroutine, args, out object? error);
            return values == null ? new object?[] { false, error } : PrependSuccess(values);
        }

        private object?[]? ResumeCoroutine(Coroutine coroutine, object?[] args, out object? errorValue)
        {
            errorValue = null;
            if (coroutine.Running)
            {
                errorValue = "cannot resume non-suspended coroutine";
                return null;
            }
            if (coroutine.Dead || coroutinesClosed)
            {
                errorValue = "cannot resume dead coroutine";
                return null;
            }
            Coroutine? previous = currentCoroutine;
            int previousNativeDepth = nativeBoundaryDepth;
            currentCoroutine = coroutine;
            nativeBoundaryDepth = 0;
            coroutine.Running = true;
            try
            {
                // The active or suspended frames own the function once execution starts.
                object? entry = coroutine.EntryFunction;
                coroutine.EntryFunction = null;
                object?[] values = entry != null ? Call(entry, args) : ResumeFrames(coroutine.Frames, args);
                coroutine.Finish();
                return values;
            }
            catch (CoroutineYield signal)
            {
                if (signal.Frames.Count == 0)
                {
                    coroutine.Finish();
                    errorValue = "yield without a resumable script frame";
                    return null;
                }
                coroutine.Frames = signal.Frames;
                return signal.Values;
            }
            catch (RuntimeFault error)
            {
                coroutine.Finish();
                errorValue = error.Message;
                return null;
            }
            catch (Exception error) when (error is IScriptRaisedError)
            {
                coroutine.Finish();
                errorValue = ((IScriptRaisedError)error).Value;
                return null;
            }
            finally
            {
                if (coroutinesClosed) coroutine.Finish();
                coroutine.Running = false;
                currentCoroutine = previous;
                nativeBoundaryDepth = previousNativeDepth;
            }
        }

        private void CloseCoroutines()
        {
            if (coroutinesClosed) return;
            coroutinesClosed = true;
            if (coroutineWrapperOwner != null) coroutineWrapperOwner.Vm = null;
            foreach (Coroutine coroutine in allocationThreads.Keys)
            {
                if (!coroutine.Running) coroutine.Finish();
            }
            if (sweptCoroutines != null)
            {
                foreach (var entry in sweptCoroutines)
                {
                    if (!entry.Key.Running) entry.Key.Finish();
                }
                sweptCoroutines = null;
            }
        }

        private object?[] ResumeFrames(List<CoroutineFrame>? frames, object?[] args)
        {
            if (frames == null)
            {
                throw new RuntimeFault("cannot resume dead coroutine");
            }
            object?[] values = args;
            for (int index = 0; index < frames.Count; index++)
            {
                try
                {
                    values = ResumeFrame(frames[index], values);
                }
                catch (CoroutineYield signal)
                {
                    for (int outer = index + 1; outer < frames.Count; outer++)
                    {
                        signal.Frames.Add(frames[outer]);
                    }
                    throw;
                }
            }
            return values;
        }

        private object?[] ResumeFrame(CoroutineFrame frame, object?[] values)
        {
            if (depth >= 64)
            {
                throw new RuntimeFault("script call depth exceeded");
            }
            depth++;
            int environmentFrame = environmentFrameCount++;
            environmentFunctions[environmentFrame] = frame.Function;
            environmentTailCalls[environmentFrame] = 0;
            environmentInstructions[environmentFrame] = frame.Pc;
            int allocationTop = EnterAllocationCall(frame.Function, values.Length);
            try
            {
                object?[] result = RunFrame(frame.Function, Array.Empty<object?>(),
                    out Closure? tail, out object?[] tailArguments, frame, values);
                if (tail != null)
                {
                    environmentFunctions[environmentFrame] = tail;
                    environmentTailCalls[environmentFrame]++;
                    return Run(tail, tailArguments);
                }
                return result;
            }
            finally
            {
                environmentFunctions[environmentFrame] = null;
                environmentTailCalls[environmentFrame] = 0;
                environmentInstructions[environmentFrame] = 0;
                environmentFrameCount = environmentFrame;
                depth--;
                LeaveAllocationCall(allocationTop);
            }
        }

        private static object?[] PrependSuccess(object?[] values)
        {
            var result = new object?[values.Length + 1];
            result[0] = true;
            Array.Copy(values, 0, result, 1, values.Length);
            return result;
        }
    }
}
