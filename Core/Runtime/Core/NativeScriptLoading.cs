using System;
using System.IO;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint LoadScriptFromGuest(uint reader, uint context, uint name)
        {
            if (nativeStack.Count == NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            string chunkName = name == 0 ? "?" : Memory.CString(name);
            Memory.Check(reader & ~1u, 2);
            uint size = Memory.AllocateVm(4);
            using var bytes = new MemoryStream();
            try
            {
                ReadOnlySpan<uint> arguments = stackalloc uint[] { scriptState, context, size };
                int iterations = 0;
                while (true)
                {
                    if (++iterations > 100000)
                    {
                        throw new RuntimeFault("native script reader iteration limit");
                    }
                    Memory.Put32(size, 0);
                    uint data = Cpu.Invoke(reader, arguments);
                    uint length = Memory.U32(size);
                    if (data == 0 || length == 0)
                    {
                        break;
                    }
                    if (bytes.Length + length > Package.MaxBytes)
                    {
                        throw new RuntimeFault("native script reader size limit");
                    }
                    bytes.Write(Memory.ReadSpan(data, length));
                }
            }
            finally
            {
                Memory.Free(size);
            }
            try
            {
                StackPush(Script.Load(ScriptChunk.Load(bytes.ToArray(), chunkName)));
                return 0;
            }
            catch (RuntimeFault error)
            {
                StackPush(error.Message);
                return 3; // MRP_ERRSYNTAX
            }
        }

        private uint DumpScriptToGuest(uint writer, uint context)
        {
            ScriptPrototype? prototype = ScriptVm.FunctionPrototype(nativeStack[StackIndex(-1)]);
            if (prototype == null || prototype.Upvalues != 0)
            {
                return 0;
            }
            Memory.Check(writer & ~1u, 2);
            byte[] bytes = ScriptChunk.Write(prototype);
            uint data = Memory.CopyVm(bytes);
            try
            {
                uint status = Cpu.Invoke(writer, stackalloc uint[] { scriptState, data, (uint)bytes.Length, context });
                Record($"native script dump: {bytes.Length} bytes, writer status={status}");
                // The reference mrp_dump returns 1 for an eligible closure, independent of writer status.
                return 1;
            }
            finally
            {
                Memory.Free(data);
            }
        }

        private uint ProtectedGuestCall(uint function, uint data)
        {
            if (nativeStack.Count == NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            Memory.Check(function & ~1u, 2);
            try
            {
                ScriptVm.Native callback = args => InvokeGuestClosure(new GuestScriptClosure { Address = function }, args);
                Script.Call(callback, new LightUserdata(data));
                return 0;
            }
            catch (Exception error) when (error is GuestScriptError || error is RuntimeFault)
            {
                StackPush(error is GuestScriptError guest ? guest.Value : error.Message);
                return 1;
            }
        }

        private uint PushFormattedScript(uint format, uint arguments)
        {
            int firstWord = (int)((arguments & 7) / 4);
            uint alignedBase = arguments & ~7u;
            return PushFormattedScript(Memory.CString(format), n => Memory.U32(alignedBase + (uint)n * 4), firstWord);
        }

        private uint PushFormattedScript(string format, Func<int, uint> word, int next)
        {
            if (nativeStack.Count == NativeStackLimit)
            {
                throw new RuntimeFault("native script stack limit");
            }
            var text = new StringBuilder();
            for (int index = 0; index < format.Length; index++)
            {
                if (format[index] != '%')
                {
                    text.Append(format[index]);
                    continue;
                }
                if (++index == format.Length)
                {
                    throw new RuntimeFault("truncated script string format");
                }
                switch (format[index])
                {
                    case '%':
                        text.Append('%');
                        break;
                    case 's':
                        text.Append(Memory.CString(word(next++)));
                        break;
                    case 'c':
                        byte character = (byte)word(next++);
                        if (character != 0)
                        {
                            text.Append((char)character);
                        }
                        break;
                    case 'd':
                        text.Append(ScriptVm.Text((int)word(next++)));
                        break;
                    case 'f':
                        next = (next + 1) & ~1;
                        ulong bits = word(next++);
                        bits |= (ulong)word(next++) << 32;
                        double value = BitConverter.Int64BitsToDouble((long)bits);
                        if (double.IsNaN(value) || value < int.MinValue || value > int.MaxValue)
                        {
                            throw new RuntimeFault("script formatted number exceeds integer profile");
                        }
                        text.Append(ScriptVm.Text((int)value));
                        break;
                    default:
                        throw new RuntimeFault($"unsupported script format %{format[index]}");
                }
                if (text.Length > 4096)
                {
                    throw new RuntimeFault("script formatted string size limit");
                }
            }
            string result = text.ToString();
            uint pointer = ScriptStringPointer(result);
            StackPush(result);
            return pointer;
        }
    }
}
