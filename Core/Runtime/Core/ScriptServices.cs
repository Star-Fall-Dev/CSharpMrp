using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void RegisterServiceGlobals()
        {
            Script.Globals["_exit"] = Script.Globals["Exit"];
            Script.Globals["TestCom1"] = Script.Globals["_strCom"];
            Script.Globals["_mod"] = Script.Globals["mod"];
            Script.Globals["_and"] = new ScriptVm.Native(args => new object?[] { RawScriptNumber(args, 0) & RawScriptNumber(args, 1) });
            Script.Globals["_or"] = new ScriptVm.Native(args => new object?[] { RawScriptNumber(args, 0) | RawScriptNumber(args, 1) });
            Script.Globals["_xor"] = new ScriptVm.Native(args => new object?[] { RawScriptNumber(args, 0) ^ RawScriptNumber(args, 1) });
            Script.Globals["_not"] = new ScriptVm.Native(args => new object?[] { RawScriptNumber(args, 0) == 0 ? 1 : 0 });
            Script.Globals["DrawPoint"] = new ScriptVm.Native(args =>
            {
                Pixel((short)RawScriptNumber(args, 0), (short)RawScriptNumber(args, 1),
                    Rgb(RawScriptNumber(args, 2), RawScriptNumber(args, 3), RawScriptNumber(args, 4)));
                return Array.Empty<object?>();
            });
            Script.Globals["_drawPoint"] = Script.Globals["DrawPoint"];
            Script.Globals["EffSetCon"] = new ScriptVm.Native(args =>
            {
                ApplyContrast((short)RawScriptNumber(args, 0), (short)RawScriptNumber(args, 1),
                    (short)RawScriptNumber(args, 2), (short)RawScriptNumber(args, 3),
                    (short)RawScriptNumber(args, 4), (short)RawScriptNumber(args, 5), (short)RawScriptNumber(args, 6));
                return Array.Empty<object?>();
            });
            Script.Globals["_effSetCon"] = Script.Globals["EffSetCon"];
            Script.Globals["LoadPack"] = new ScriptVm.Native(args =>
            {
                string name = TextArgument(args, 0);
                string old = Memory.CString(Memory.U32(table + 100 * 4));
                WriteRuntimeText(100, name);
                return new object?[] { old };
            });
            Script.Globals["_loadPack"] = Script.Globals["LoadPack"];
            Script.Globals["_plat"] = new ScriptVm.Native(args => new object?[]
            {
                unchecked((int)Cpu.Invoke(0xf0000000 + 37 * 4, stackalloc uint[]
                    { (uint)RawScriptNumber(args, 0), (uint)RawScriptNumber(args, 1) }))
            });
            Script.Globals["_platEx"] = new ScriptVm.Native(ScriptPlatformExtended);
            Script.Globals["Call"] = new ScriptVm.Native(args => ScriptPhoneService(60, args));
            Script.Globals["ConnectWAP"] = new ScriptVm.Native(args => ScriptPhoneService(62, args));
            Script.Globals["SendSms"] = new ScriptVm.Native(args => ScriptPhoneService(59, args));
            var phone = new ScriptTable();
            phone["call"] = Script.Globals["Call"];
            phone["wap"] = Script.Globals["ConnectWAP"];
            phone["sendSms"] = Script.Globals["SendSms"];
            phone["getNetID"] = Script.Globals["GetNetworkID"];
            Script.Globals["phone"] = phone;
        }

        private object?[] ScriptPhoneService(int slot, object?[] args)
        {
            string number = slot == 59 && args.Length == 0 ? "0" : TextArgument(args, 0);
            string content = slot == 59 ? (args.Length < 2 ? "0" : TextArgument(args, 1)) : "";
            uint first = 0;
            uint second = 0;
            try
            {
                first = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(number + "\0"));
                if (slot == 59)
                {
                    second = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(content + "\0"));
                }
                uint status = Cpu.Invoke(0xf0000000 + (uint)slot * 4,
                    stackalloc uint[] { first, second, (uint)RawScriptNumber(args, 2) });
                return slot == 59 ? new object?[] { unchecked((int)status) } : Array.Empty<object?>();
            }
            finally
            {
                Memory.Free(second);
                Memory.Free(first);
            }
        }

        private object?[] ScriptPlatformExtended(object?[] args)
        {
            int code = RawScriptNumber(args, 0);
            string input = TextArgument(args, 1);
            uint inputPointer = 0;
            uint outputs = 0;
            uint outputPointer = 0;
            uint outputLength = 0;
            uint release = 0;
            try
            {
                inputPointer = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(input + "\0\0"));
                outputs = Memory.Allocate(12);
                uint status = Cpu.Invoke(0xf0000000 + 38 * 4,
                    stackalloc uint[] { (uint)code, inputPointer, (uint)input.Length, outputs, outputs + 4, outputs + 8 });
                outputPointer = Memory.U32(outputs);
                outputLength = Memory.U32(outputs + 4);
                release = Memory.U32(outputs + 8);
                string result = outputPointer != 0 && outputLength != 0
                    ? ScriptChunk.Bytes.GetString(Memory.ReadSpan(outputPointer, outputLength)) : "";
                return new object?[] { result, unchecked((int)status) };
            }
            finally
            {
                try
                {
                    if (release != 0)
                    {
                        Cpu.Invoke(release, outputPointer, outputLength);
                    }
                    else if (code == 1207 && outputPointer != 0)
                    {
                        // Conversion allocates a result; the script string owns a copy.
                        Memory.NativeFree(outputPointer, outputLength);
                    }
                }
                finally
                {
                    Memory.Free(outputs);
                    Memory.Free(inputPointer);
                }
            }
        }

        private T RequiredScriptResource<T>(string name, ResourceDecoder<T> decode)
        {
            int terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name.Substring(0, terminator);
            }
            return TryReadResource(name, decode, out T result) ? result : throw new RuntimeFault("missing resource: " + name);
        }

        private (uint Pointer, uint Length, byte[]? Snapshot) ReadWholeScriptResource(string name) =>
            RequiredScriptResource(name, static (RuntimeSession session, uint address, uint size, ref bool owned) =>
                session.TryAdoptResource(address, size, ref owned)
                    ? (address, size, (byte[]?)null)
                    : (0u, size, session.Memory.Read(address, size)));

        private uint ResolveWholeScriptResource((uint Pointer, uint Length, byte[]? Snapshot) resource) =>
            resource.Pointer != 0 || resource.Length == 0 ? resource.Pointer : Memory.CopyIn(resource.Snapshot!);

        private void ReplaceScriptResourcePointer(uint slot, uint replacement, uint? previous = null)
        {
            try { Memory.Free(previous ?? Memory.U32(slot)); }
            catch
            {
                Memory.Free(replacement);
                throw;
            }
            Memory.Put32(slot, replacement);
        }
    }
}
