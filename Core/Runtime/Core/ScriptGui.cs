using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void RegisterGuiGlobals()
        {
            var gui = new ScriptTable();
            foreach (var binding in new (string Name, int Slot)[]
            {
                ("m_create", 63), ("m_setItem", 64), ("m_show", 65), ("m_release", 67), ("m_update", 68),
                ("d_create", 69), ("d_release", 70), ("d_update", 71),
                ("t_create", 72), ("t_release", 73), ("t_update", 74),
                ("e_create", 75), ("e_release", 76), ("e_getText", 77), ("w_create", 78), ("w_release", 79)
            })
            {
                int slot = binding.Slot;
                gui[binding.Name] = new ScriptVm.Native(args => ScriptGuiCall(slot, args));
            }
            gui["m_focus"] = new ScriptVm.Native(_ =>
            {
                Record("reference gui.m_focus unavailable: GUI_RECREATE_MOD disabled");
                return new object?[] { null };
            });
            gui["m_setitem"] = gui["m_setItem"];
            gui["e_gettext"] = gui["e_getText"];
            Script.Globals["gui"] = gui;
        }

        private object?[] ScriptGuiCall(int slot, object?[] args)
        {
            Span<uint> values = stackalloc uint[] { 0, 0, 0, 0 };
            string? title = null;
            string? text = null;
            int titleIndex = 0;
            int textIndex = 1;
            if (slot == 63)
            {
                title = TextArgument(args, 0);
                values[1] = unchecked((uint)(short)Number(args, 1));
            }
            else if (slot == 64)
            {
                values[0] = unchecked((uint)Number(args, 0));
                text = TextArgument(args, 1);
                values[2] = unchecked((uint)Number(args, 2));
            }
            else if (slot == 69 || slot == 72 || slot == 75)
            {
                title = TextArgument(args, 0);
                text = TextArgument(args, 1);
                values[2] = unchecked((uint)Number(args, 2));
                if (slot == 75)
                {
                    values[3] = unchecked((uint)Number(args, 3, 68));
                }
            }
            else if (slot == 71 || slot == 74)
            {
                values[0] = unchecked((uint)Number(args, 0));
                title = TextArgument(args, 1);
                text = TextArgument(args, 2);
                titleIndex = 1;
                textIndex = 2;
                values[3] = slot == 71 ? unchecked((uint)Number(args, 3, -1)) : 0;
            }
            else if (slot != 78)
            {
                values[0] = unchecked((uint)Number(args, 0));
            }
            uint titlePointer = 0;
            uint textPointer = 0;
            try
            {
                if (title != null)
                {
                    titlePointer = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(title + "\0\0"));
                    values[titleIndex] = titlePointer;
                }
                if (text != null)
                {
                    textPointer = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(text + "\0\0"));
                    values[textIndex] = textPointer;
                }
                uint result = Cpu.Invoke(0xf0000000 + (uint)slot * 4, values);
                if (slot == 77)
                {
                    return new object?[] { result == 0 ? null : ScriptChunk.Bytes.GetString(Memory.ReadSpan(result, WideByteLength(result) + 2)) };
                }
                bool positive = slot == 63 || slot == 69 || slot == 71 || slot == 72 || slot == 74 || slot == 75 || slot == 78;
                return new object?[] { (positive ? unchecked((int)result) > 0 : result == 0) ? (object)unchecked((int)result) : null };
            }
            finally
            {
                if (textPointer != 0)
                {
                    Memory.Free(textPointer);
                }
                if (titlePointer != 0)
                {
                    Memory.Free(titlePointer);
                }
            }
        }
    }
}
