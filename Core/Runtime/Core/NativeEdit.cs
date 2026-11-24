using System;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        public sealed class NativeEditWindow
        {
            public uint Handle
            {
                get;
            }
            public string Title
            {
                get;
            }
            public string Text
            {
                get; internal set;
            }
            public int Type
            {
                get;
            }
            public int MaximumLength
            {
                get;
            }
            public bool Submitted
            {
                get; internal set;
            }
            internal uint Buffer
            {
                get;
            }

            internal NativeEditWindow(uint handle, string title, string text, int type, int maximumLength, uint buffer)
            {
                Handle = handle;
                Title = title;
                Text = text;
                Type = type;
                MaximumLength = maximumLength;
                Buffer = buffer;
            }
        }

        public NativeEditWindow? NativeEdit
        {
            get; private set;
        }

        private uint CreateNativeEdit(uint title, uint text, int type, int maximumLength)
        {
            if (type < 0 || type > 3 || maximumLength < 0 || maximumLength > 4095)
            {
                throw new RuntimeFault($"unsupported native edit type or capacity: type={type}, capacity={maximumLength}");
            }
            if (NativeEdit != null || NativeText != null)
            {
                return uint.MaxValue;
            }
            string decodedTitle = title == 0 ? "" : ReadUnicode(title);
            string decodedText = text == 0 ? "" : ReadUnicode(text);
            if (decodedText.Length > maximumLength)
            {
                decodedText = decodedText.Substring(0, maximumLength);
            }
            uint buffer = Memory.AllocateVm((uint)(maximumLength + 1) * 2);
            Memory.Write(buffer, Encoding.BigEndianUnicode.GetBytes(decodedText + "\0"));
            NativeEdit = new NativeEditWindow(WindowHandles.Allocate(), decodedTitle, decodedText, type, maximumLength, buffer);
            nativePressedKey = null;
            nativePressedTouch = null;
            Frames++;
            Record($"native edit created: type={type}, capacity={maximumLength}");
            return NativeEdit.Handle;
        }

        private uint ReleaseNativeEdit(uint handle)
        {
            if (NativeEdit == null || NativeEdit.Handle != handle)
            {
                return uint.MaxValue;
            }
            Memory.Free(NativeEdit.Buffer);
            NativeEdit = null;
            nativePressedKey = null;
            Frames++;
            Record("native edit released");
            return 0;
        }

        public bool EditText(uint handle, string text)
        {
            Live();
            NativeEditWindow edit = RequireNativeEdit(handle);
            if (edit.Submitted || text.Length > edit.MaximumLength)
            {
                return false;
            }
            foreach (char character in text)
            {
                if (character == '\0' || char.IsSurrogate(character) ||
                    edit.Type == 1 && (character < '0' || character > '9'))
                {
                    return false;
                }
            }
            Memory.Write(edit.Buffer, Encoding.BigEndianUnicode.GetBytes(text + "\0"));
            edit.Text = text;
            Frames++;
            return true;
        }

        private NativeEditWindow RequireNativeEdit(uint handle)
        {
            return NativeEdit != null && NativeEdit.Handle == handle ? NativeEdit :
                throw new RuntimeFault("invalid native edit handle");
        }

        public void EditChoice(uint handle, int choice)
        {
            Live();
            NativeEditWindow edit = RequireNativeEdit(handle);
            if (!Started || edit.Submitted || (choice != 0 && choice != 1))
            {
                throw new RuntimeFault("invalid native edit choice");
            }
            uint state = Memory.U32(runtimeState);
            if (state == 3 || state == 2 && !TimerRunsWhilePaused)
            {
                return;
            }
            edit.Submitted = true;
            Record($"native edit choice {choice}");
            uint status = InvokeGuestHook(eventHook, stackalloc uint[] { 6u, (uint)choice, 0u });
            if (Started && status == 1 && ScriptVm.IsFunction(Script.Globals["dealevent"]))
            {
                RunGuest(() => Script.Call(Script.Globals["dealevent"], 6, choice, 0));
            }
        }

        private bool HandleEditKey(int type, int key)
        {
            NativeEditWindow edit = NativeEdit!;
            if (type == 0)
            {
                nativePressedKey = (edit.Handle, key);
            }
            else if (type == 1)
            {
                var pressed = nativePressedKey;
                nativePressedKey = null;
                if (pressed != null && pressed.Value.Handle == edit.Handle && pressed.Value.Key == key && !edit.Submitted)
                {
                    int choice = key == 18 ? 1 : key == 17 || key == 20 ? 0 : -1;
                    if (choice >= 0)
                    {
                        EditChoice(edit.Handle, choice);
                    }
                }
            }
            return true;
        }
    }
}
