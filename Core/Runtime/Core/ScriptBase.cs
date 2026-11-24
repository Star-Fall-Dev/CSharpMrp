using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static void RequireScriptArguments(object?[] args, int count)
        {
            if (args.Length < count)
            {
                throw new RuntimeFault($"script missing argument {count}");
            }
        }

        private void RegisterBaseGlobals()
        {
            Script.Globals["_error"] = new ScriptVm.Native(args =>
            {
                int level = args.Length < 2 || args[1] == null ? 1 : ScriptVm.Number(args[1]);
                RequireScriptArguments(args, 1);
                object? value = args[0];
                if (level != 0 && (ScriptVm.IsString(value) || value is int))
                {
                    value = Script.ErrorLocation(level) + ScriptVm.Text(value);
                }
                throw new GuestScriptError(value);
            });
            Script.Globals["_str"] = new ScriptVm.Native(ScriptString);
            Script.Globals["tostring"] = new ScriptVm.Native(ScriptString);
            Script.Globals["_assert"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                if (ScriptVm.Truth(args[0]))
                {
                    return new object?[] { args[0] };
                }
                string message = args.Length < 2 || args[1] == null ? "assert() meet false or nil." : ScriptVm.Text(args[1]);
                int terminator = message.IndexOf('\0');
                throw new GuestScriptError(terminator < 0 ? message : message.Substring(0, terminator));
            });
            Script.Globals["pcall"] = new ScriptVm.Native(args => ScriptProtectedCall(args, false));
            Script.Globals["_pCall"] = new ScriptVm.Native(args => ScriptProtectedCall(args, false));
            Script.Globals["_pCallEx"] = new ScriptVm.Native(args => ScriptProtectedCall(args, true));
            Script.Globals["_num"] = new ScriptVm.Native(ScriptNumber);
            Script.Globals["tonumber"] = new ScriptVm.Native(ScriptNumber);
            Script.Globals["type"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                return new object?[] { ScriptTypeName(ScriptType(args[0]), false) };
            });
            Script.Globals["_t"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                return new object?[] { ScriptTypeName(ScriptType(args[0]), true) };
            });
            Script.Globals["_rawEq"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 2);
                return new object?[] { ScriptVm.RawEqual(args[0], args[1]) };
            });
            Script.Globals["next"] = new ScriptVm.Native(ScriptVm.IterateTable);
            Script.Globals["_iPairs"] = new ScriptVm.Native(ScriptIPairs);
            Script.Globals["_loads"] = new ScriptVm.Native(ScriptLoadBuffer);
            Script.Globals["_loadBuf"] = new ScriptVm.Native(ScriptLoadBuffer);
            Script.Globals["loadfile"] = new ScriptVm.Native(args => ScriptLoadFile(args, false));
            Script.Globals["_loadFile"] = new ScriptVm.Native(args => ScriptLoadFile(args, false));
            Script.Globals["dofile"] = new ScriptVm.Native(args => ScriptLoadFile(args, true));
            Script.Globals["_execFile"] = new ScriptVm.Native(args => ScriptLoadFile(args, true));
        }

        private object?[] ScriptLoadFile(object?[] args, bool execute)
        {
            if (args.Length == 0 || args[0] == null)
            {
                throw new RuntimeFault("script loadfile requires a resource name");
            }
            string name = ScriptVm.Text(args[0]);
            int terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name.Substring(0, terminator);
            }
            object function;
            try
            {
                function = ReadScriptFile(name, execute);
            }
            catch (RuntimeFault error) when (!execute)
            {
                return new object?[] { null, error.Message };
            }
            return execute ? Script.Call(function) : new object?[] { function };
        }

        private object ReadScriptFile(string name, bool execute)
        {
            byte[]? bytes = ReadResourceBytes(name);
            if (bytes == null)
            {
                throw new RuntimeFault($"cannot read {name}: missing resource or allocation failure");
            }
            Record($"{(execute ? "dofile" : "loadfile")} {name} bytes={bytes.Length}");
            return Script.Load(ScriptChunk.Load(bytes, "@" + name));
        }

        private object?[] ScriptLoadBuffer(object?[] args)
        {
            RequireScriptArguments(args, 1);
            string source = ScriptVm.Text(args[0]);
            string name = args.Length < 2 || args[1] == null ? source : ScriptVm.Text(args[1]);
            int terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name.Substring(0, terminator);
            }
            try
            {
                return new object?[] { Script.Load(ScriptChunk.Load(ScriptChunk.Bytes.GetBytes(source), name)) };
            }
            catch (RuntimeFault error)
            {
                return new object?[] { null, error.Message };
            }
        }

        private object?[] ScriptIPairs(object?[] args)
        {
            if (args.Length == 0 || !(args[0] is ScriptTable tableValue))
            {
                throw new RuntimeFault("ipairs requires a table");
            }
            if (args.Length == 1)
            {
                return new object?[] { Script.Globals["_iPairs"], tableValue, 0 };
            }
            ScriptInteger(args[1], out int previous);
            int index = unchecked(previous + 1);
            object? value = tableValue[index];
            return value == null ? Array.Empty<object?>() : new object?[] { index, value };
        }

        private object?[] ScriptString(object?[] args)
        {
            RequireScriptArguments(args, 1);
            object? value = args[0];
            object? method = ScriptVm.Metatable(value)?["__str"];
            if (method != null)
            {
                object?[] results = Script.Call(method, value);
                return new object?[] { results.Length == 0 ? null : results[0] };
            }
            string text = value == null ? "nil" : ScriptVm.IsString(value) || value is int ? ScriptVm.Text(value) :
                value is bool || value is ScriptBoolean ? ScriptVm.Truth(value) ? "true" : "false" :
                ScriptTypeName(ScriptType(value), false) + ": " + ScriptPointer(value).ToString("X8", CultureInfo.InvariantCulture);
            return new object?[] { text };
        }

        private object?[] ScriptProtectedCall(object?[] args, bool extended)
        {
            RequireScriptArguments(args, extended ? 2 : 1);
            if (!extended && args.Length > NativeStackLimit)
            {
                throw new RuntimeFault("native script argument limit");
            }
            List<object?> outer = nativeStack;
            List<object?> stack = RentNativeCallStack(extended ? 2 : args.Length);
            if (extended)
            {
                stack.Add(args[1]);
                stack.Add(args[0]);
            }
            else
            {
                stack.AddRange(args);
            }
            nativeStackFrames.Add(outer);
            nativeStack = stack;
            try
            {
                uint status = CallScriptStack(extended ? 0 : args.Length - 1, -1, extended ? 1 : 0, true);
                int prefix = extended ? 1 : 0;
                int count = nativeStack.Count - prefix;
                if (count >= NativeStackLimit)
                {
                    throw new RuntimeFault("native script result limit");
                }
                var results = new object?[count + 1];
                results[0] = status == 0;
                nativeStack.CopyTo(prefix, results, 1, count);
                return results;
            }
            finally
            {
                RestoreNativeCallStack(outer);
                ReturnNativeCallStack(stack);
            }
        }

        private object?[] ScriptNumber(object?[] args)
        {
            int radix = args.Length < 2 || args[1] == null ? 10 : ScriptVm.Number(args[1]);
            RequireScriptArguments(args, 1);
            if (radix == 10)
            {
                return new object?[] { ScriptInteger(args[0], out int number) ? number : null };
            }
            string text = ScriptVm.Text(args[0]);
            if (radix < 2 || radix > 36)
            {
                throw new RuntimeFault("script number base out of range");
            }
            uint Read(uint index) => index < text.Length ? text[(int)index] : 0u;
            uint value = ParseUnsigned(null, text, 0, (uint)radix, out uint end);
            if (end == 0)
            {
                return new object?[] { null };
            }
            while (Read(end) == 32 || Read(end) >= 9 && Read(end) <= 13)
            {
                end++;
            }
            return new object?[] { Read(end) == 0 ? unchecked((int)value) : null };
        }
    }
}
