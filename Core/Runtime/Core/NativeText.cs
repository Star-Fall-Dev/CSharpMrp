using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        public sealed class NativeTextWindow
        {
            public uint Handle
            {
                get;
            }
            public string Title
            {
                get;
            }
            public string Body
            {
                get;
            }
            public int Type
            {
                get;
            }
            public bool IsText
            {
                get;
            }
            public int FirstVisibleLine
            {
                get; internal set;
            }
            public int LineCount => Lines.Length;
            internal int RenderRevision;
            internal string[] Lines
            {
                get;
            }

            internal NativeTextWindow(uint handle, string title, string body, int type, bool isText, string[] lines)
            {
                Handle = handle;
                Title = title;
                Body = body;
                Type = type;
                IsText = isText;
                Lines = lines;
            }
        }

        public NativeTextWindow? NativeText
        {
            get; private set;
        }
        internal sealed class WindowHandleSequence
        {
            private uint next = 1;
            internal uint Allocate() => next++;
        }

        private WindowHandleSequence? windowHandles;
        internal WindowHandleSequence WindowHandles
        {
            get => windowHandles ??= new WindowHandleSequence();
            set => windowHandles = value;
        }
        private (uint Handle, int Key)? nativePressedKey;
        private (uint Handle, int Choice)? nativePressedTouch;
        private bool dispatchingNativeTextChoice;
        private Dictionary<uint, bool>? replacedNativeTextHandles;

        private sealed class NativeMenuWindow
        {
            internal uint Handle;
            internal string Title = "";
            internal string[] Items = Array.Empty<string>();
            internal int Focus;
        }

        private readonly Dictionary<uint, NativeMenuWindow> nativeMenus = new Dictionary<uint, NativeMenuWindow>();
        private NativeMenuWindow? visibleMenu;
        private NativeTextWindow? nativeMenuView;

        private uint CreateNativeMenu(uint title, int count)
        {
            if (count < 1 || count > 128 || nativeMenus.Count >= 4 || font == null || Width < 40 || Height < 64)
            {
                return uint.MaxValue;
            }
            string decodedTitle = ReadUnicode(title);
            if (decodedTitle.Length > 256)
            {
                return uint.MaxValue;
            }
            var menu = new NativeMenuWindow
            {
                Handle = WindowHandles.Allocate(),
                Title = decodedTitle,
                Items = new string[count]
            };
            Array.Fill(menu.Items, "");
            nativeMenus.Add(menu.Handle, menu);
            Record($"native menu created: {decodedTitle}, items={count}");
            return menu.Handle;
        }

        private uint SetNativeMenuItem(uint handle, uint text, int index)
        {
            if (!nativeMenus.TryGetValue(handle, out var menu) || index < 0 || index >= menu.Items.Length)
            {
                return uint.MaxValue;
            }
            string label = ReadUnicode(text);
            if (label.Length > 256)
            {
                return uint.MaxValue;
            }
            menu.Items[index] = label;
            return 0;
        }

        private uint ShowNativeMenu(uint handle)
        {
            if (!nativeMenus.TryGetValue(handle, out var menu) || NativeEdit != null || NativeText != null)
            {
                return uint.MaxValue;
            }
            visibleMenu = menu;
            nativePressedKey = null;
            nativePressedTouch = null;
            RefreshNativeMenuView();
            Record($"native menu shown: {menu.Title}");
            return 0;
        }

        private uint RefreshNativeMenu(uint handle)
        {
            if (!nativeMenus.ContainsKey(handle))
            {
                return uint.MaxValue;
            }
            if (visibleMenu?.Handle == handle)
            {
                RefreshNativeMenuView();
            }
            return 0;
        }

        private uint ReleaseNativeMenu(uint handle)
        {
            if (!nativeMenus.Remove(handle))
            {
                return uint.MaxValue;
            }
            if (visibleMenu?.Handle == handle)
            {
                visibleMenu = null;
                nativeMenuView = null;
                nativePressedKey = null;
                nativePressedTouch = null;
                Frames++;
            }
            Record("native menu released");
            return 0;
        }

        private void RefreshNativeMenuView()
        {
            NativeMenuWindow menu = visibleMenu!;
            var lines = new string[menu.Items.Length];
            for (int index = 0; index < lines.Length; index++)
            {
                lines[index] = (index == menu.Focus ? "> " : "  ") + menu.Items[index];
            }
            font!.Measure('点', 1, out _, out int height);
            int visible = Math.Max(1, (Height - 60) / (height + 3));
            int first = nativeMenuView?.Handle == menu.Handle ? nativeMenuView.FirstVisibleLine : 0;
            first = Math.Min(first, menu.Focus);
            first = Math.Max(first, menu.Focus - visible + 1);
            nativeMenuView = new NativeTextWindow(menu.Handle, menu.Title, "", 1, false, lines)
            {
                FirstVisibleLine = first
            };
            Frames++;
        }

        private void DispatchNativeMenu(bool returning)
        {
            int index = returning ? 0 : visibleMenu!.Focus;
            int code = returning ? 5 : 4;
            Record($"native menu event {code}:{index}");
            uint status = InvokeGuestHook(eventHook, stackalloc uint[] { (uint)code, (uint)index, 0u });
            if (Started && status == 1 && ScriptVm.IsFunction(Script.Globals["dealevent"]))
            {
                RunGuest(() => Script.Call(Script.Globals["dealevent"], code, index, 0));
            }
        }

        private void MoveNativeMenuFocus(int focus)
        {
            NativeMenuWindow menu = visibleMenu!;
            if (menu.Focus == focus)
            {
                return;
            }
            NativeTextWindow view = nativeMenuView!;
            view.Lines[menu.Focus] = "  " + menu.Items[menu.Focus];
            menu.Focus = focus;
            view.Lines[focus] = "> " + menu.Items[focus];
            font!.Measure('点', 1, out _, out int height);
            int visible = Math.Max(1, (Height - 60) / (height + 3));
            view.FirstVisibleLine = Math.Max(Math.Min(view.FirstVisibleLine, focus), focus - visible + 1);
            view.RenderRevision++;
            Frames++;
        }

        private bool HandleNativeMenuKey(int type, int key)
        {
            if (visibleMenu == null || NativeText != null || NativeEdit != null)
            {
                return false;
            }
            if (type == 0)
            {
                nativePressedKey = (visibleMenu.Handle, key);
            }
            else if (type == 1)
            {
                var pressed = nativePressedKey;
                nativePressedKey = null;
                if (pressed == null || pressed.Value.Handle != visibleMenu.Handle || pressed.Value.Key != key)
                {
                    return true;
                }
                if (key == 12 || key == 13)
                {
                    MoveNativeMenuFocus(Math.Max(0, Math.Min(visibleMenu.Items.Length - 1,
                        visibleMenu.Focus + (key == 12 ? -1 : 1))));
                }
                else if (key == 17 || key == 20 || key == 18)
                {
                    DispatchNativeMenu(key == 18);
                }
            }
            return true;
        }

        private bool HandleNativeMenuTouch(int type, int x, int y)
        {
            if (visibleMenu == null || NativeText != null || NativeEdit != null)
            {
                return false;
            }
            font!.Measure('点', 1, out _, out int height);
            int choice = -2;
            if (y >= Height - 26)
            {
                choice = x >= Width / 2 ? -1 : visibleMenu.Focus;
            }
            else if (y >= 34)
            {
                int row = (y - 34) / (height + 3);
                int index = nativeMenuView!.FirstVisibleLine + row;
                if (34 + row * (height + 3) + height <= Height - 26 && index < visibleMenu.Items.Length)
                {
                    choice = index;
                }
            }
            if (type == 2)
            {
                nativePressedTouch = choice >= -1 ? (visibleMenu.Handle, choice) : null;
                if (choice >= 0)
                {
                    MoveNativeMenuFocus(choice);
                }
            }
            else if (type == 3)
            {
                var pressed = nativePressedTouch;
                nativePressedTouch = null;
                if (pressed != null && pressed.Value.Handle == visibleMenu.Handle && pressed.Value.Choice == choice)
                {
                    DispatchNativeMenu(choice == -1);
                }
            }
            return true;
        }

        private uint ConvertNativeText(uint source, uint error, uint size)
        {
            if (error != 0)
            {
                Memory.Check(error, 4);
            }
            if (size != 0)
            {
                Memory.Check(size, 4);
            }
            if (source == 0)
            {
                if (error != 0)
                {
                    Memory.Put32(error, uint.MaxValue);
                }
                return 0;
            }
            Encoding decoder = RequireGbkEncoding("c2u requires an explicit CP936 encoding service");
            ReadOnlySpan<byte> input = Memory.CStringBytes(source);
            int outputSize = ConvertedTextSize(input, out _);
            uint header = size == 0 ? 4u : 0u;
            uint allocationSize = (uint)outputSize + header;
            uint allocation = 0;
            byte[]? rented = null;
            try
            {
                Span<byte> output = outputSize <= 512 ? stackalloc byte[outputSize] :
                    outputSize <= 64 * 1024 ? (rented = ArrayPool<byte>.Shared.Rent(outputSize)).AsSpan(0, outputSize) :
                    new byte[outputSize];
                ConvertTextBytes(input, output, decoder);
                allocation = Memory.NativeAllocate(allocationSize);
                if (error != 0)
                {
                    Memory.Put32(error, uint.MaxValue);
                }
                if (size != 0)
                {
                    Memory.Put32(size, (uint)outputSize);
                }
                if (allocation == 0)
                {
                    return 0;
                }
                if (header != 0)
                {
                    Memory.Put32(allocation, (uint)outputSize);
                }
                uint result = allocation + header;
                Memory.Write(result, output);
                allocation = 0;
                return result;
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                if (allocation != 0) Memory.NativeFree(allocation, allocationSize);
            }
        }

        private static readonly DecoderFallback UnmappedGbk = new DecoderReplacementFallback("\ufffd");
        private Encoding? nativeGbkEncoding;

        private Encoding RequireGbkEncoding(string error)
        {
            if (nativeGbkEncoding != null) return nativeGbkEncoding;
            if (textEncoding == null || textEncoding.CodePage != 936) throw new RuntimeFault(error);

            var encoding = (Encoding)textEncoding.Clone();
            encoding.DecoderFallback = UnmappedGbk;
            encoding.EncoderFallback = UnmappedUcs2;
            return nativeGbkEncoding = encoding;
        }

        private static int ConvertedTextSize(ReadOnlySpan<byte> input, out int consumed, int unitLimit = int.MaxValue)
        {
            if ((long)input.Length * 2 + 2 > Package.MaxBytes)
            {
                throw new RuntimeFault("c2u output size limit");
            }
            int units = 0;
            int index = 0;
            for (; index < input.Length && units < unitLimit; index += input[index] > 128 ? 2 : 1)
            {
                units++;
            }
            consumed = Math.Min(index, input.Length);
            return (units + 1) * 2;
        }

        private static void ConvertTextBytes(ReadOnlySpan<byte> input, Span<byte> output, Encoding decoder)
        {
            Span<char> pair = stackalloc char[2];
            int written = 0;
            for (int index = 0; index < input.Length; index++)
            {
                byte lead = input[index];
                char character;
                if (lead < 128)
                {
                    character = (char)lead;
                }
                else if (lead == 128)
                {
                    character = '\u20ac';
                }
                else
                {
                    character = '\ufffd';
                    if (index + 1 < input.Length)
                    {
                        int code = lead << 8 | input[index + 1];
                        if (code >= 0x8140 && code <= 0xfe4f &&
                            decoder.GetChars(input.Slice(index, 2), pair) == 1 &&
                            (pair[0] < '\ue000' || pair[0] > '\uf8ff'))
                        {
                            character = pair[0];
                        }
                        index++;
                    }
                }
                output[written++] = (byte)(character >> 8);
                output[written++] = (byte)character;
            }
            output[written++] = 0;
            output[written] = 0;
        }

        private ReadOnlySpan<byte> ReadUnicodeBytes(uint pointer, uint limit = 8192, string limitError = "native text length limit")
        {
            Memory.Check(pointer, 1);
            var bytes = Memory.ReadSpan(pointer, Math.Min(limit, Memory.MappedEnd - pointer));
            int end = UnicodeTerminatorOffset(bytes);
            if (end >= 0) return bytes.Slice(0, end);
            if (bytes.Length < limit) Memory.Check(pointer + (uint)(bytes.Length & ~1), 2);
            throw new RuntimeFault(limitError);
        }

        private static ReadOnlySpan<byte> ReadTerminatedUnicodeBytes(ReadOnlySpan<byte> bytes, int limit, string limitError)
        {
            int end = UnicodeTerminatorOffset(bytes.Slice(0, Math.Min(bytes.Length, limit)));
            if (end < 0) throw new RuntimeFault(limitError);
            return bytes.Slice(0, end);
        }

        private static int UnicodeTerminatorOffset(ReadOnlySpan<byte> bytes)
        {
            for (int offset = 0; offset + 1 < bytes.Length; offset += 2)
            {
                if ((bytes[offset] | bytes[offset + 1]) == 0)
                {
                    return offset;
                }
            }
            return -1;
        }

        private string ReadUnicode(uint pointer)
        {
            int length = ReadUnicodeBytes(pointer).Length / 2;
            return string.Create(length, (Memory, pointer), static (characters, source) =>
            {
                var input = source.Memory.ReadSpan(source.pointer, (uint)characters.Length * 2);
                for (int index = 0; index < characters.Length; index++)
                {
                    characters[index] = (char)(input[index * 2] << 8 | input[index * 2 + 1]);
                }
            });
        }

        private static void CheckDialogType(int type)
        {
            if (type != 0 && type != 1 && type != 2 && type != 100)
            {
                throw new RuntimeFault($"unsupported native text button type {type}");
            }
        }

        private uint CreateNativeText(uint title, uint body, int type, bool isText)
        {
            CheckDialogType(type);
            if ((NativeText != null && !dispatchingNativeTextChoice) ||
                NativeEdit != null || font == null || Width < 40 || Height < 64)
            {
                throw new RuntimeFault("native text requires a font and no existing window");
            }
            string decodedTitle = ReadUnicode(title);
            string decodedBody = ReadUnicode(body);
            var window = new NativeTextWindow(WindowHandles.Allocate(), decodedTitle, decodedBody, type, isText, WrapNativeText(decodedBody));
            if (NativeText != null)
            {
                (replacedNativeTextHandles ??= new Dictionary<uint, bool>()).Add(NativeText.Handle, NativeText.IsText);
            }
            NativeText = window;
            nativePressedKey = null;
            nativePressedTouch = null;
            Record($"native text created: {decodedTitle}: {decodedBody}");
            Frames++;
            return window.Handle;
        }

        private uint ReleaseNativeText(uint handle, bool isText)
        {
            if (replacedNativeTextHandles != null &&
                replacedNativeTextHandles.TryGetValue(handle, out bool replacedIsText) && replacedIsText == isText)
            {
                replacedNativeTextHandles.Remove(handle);
                return 0;
            }
            RequireNativeText(handle, isText);
            NativeText = null;
            nativePressedKey = null;
            nativePressedTouch = null;
            Frames++;
            return 0;
        }

        private NativeTextWindow RequireNativeText(uint handle, bool isText)
        {
            return NativeText != null && NativeText.Handle == handle && NativeText.IsText == isText
                ? NativeText : throw new RuntimeFault("invalid native text handle");
        }

        public void DialogChoice(int choice)
        {
            Live();
            if (!Started || NativeText == null || (choice != 0 && choice != 1) ||
                NativeText.Type == 100 || (NativeText.Type == 0 && choice != 0) ||
                (NativeText.Type == 2 && choice != 1))
            {
                throw new RuntimeFault("invalid native text choice");
            }
            uint state = Memory.U32(runtimeState);
            if (state == 3 || state == 2 && !TimerRunsWhilePaused)
            {
                return;
            }
            Record($"native text choice {choice}");
            // The choice handler can display successive windows before returning.
            bool wasDispatchingChoice = dispatchingNativeTextChoice;
            dispatchingNativeTextChoice = true;
            try
            {
                uint status = InvokeGuestHook(eventHook, stackalloc uint[] { 6u, (uint)choice, 0u });
                if (Started && status == 1 && ScriptVm.IsFunction(Script.Globals["dealevent"]))
                {
                    RunGuest(() => Script.Call(Script.Globals["dealevent"], 6, choice, 0));
                }
            }
            finally
            {
                dispatchingNativeTextChoice = wasDispatchingChoice;
                if (!wasDispatchingChoice)
                {
                    replacedNativeTextHandles?.Clear();
                }
            }
        }

        private bool HandleNativeKey(int type, int key)
        {
            if (NativeEdit != null)
            {
                return HandleEditKey(type, key);
            }
            if (NativeText == null)
            {
                nativePressedKey = null;
                return false;
            }
            if (type == 0)
            {
                nativePressedKey = (NativeText.Handle, key);
            }
            else if (type == 1)
            {
                var pressed = nativePressedKey;
                nativePressedKey = null;
                if (pressed == null || pressed.Value.Handle != NativeText.Handle || pressed.Value.Key != key)
                {
                    return true;
                }
                if (key == 12 || key == 13)
                {
                    font!.Measure('点', 1, out _, out int height);
                    int visible = Math.Max(1, (Height - 60) / (height + 3));
                    int first = NativeText.FirstVisibleLine;
                    int next = Math.Max(0, Math.Min(Math.Max(0, NativeText.LineCount - visible),
                        first + (key == 12 ? -1 : 1)));
                    if (next != first)
                    {
                        NativeText.FirstVisibleLine = next;
                        NativeText.RenderRevision++;
                    }
                    Frames++;
                    return true;
                }
                int choice = key == 18 ? 1 : key == 17 || key == 20 ? NativeText.Type == 2 ? 1 : 0 : -1;
                if (NativeText.Type != 100 && choice >= 0 &&
                    (NativeText.Type == 1 || NativeText.Type == 0 && choice == 0 || NativeText.Type == 2 && choice == 1))
                {
                    DialogChoice(choice);
                }
            }
            return true;
        }

        private bool HandleNativeTouch(int type, int x, int y)
        {
            if (NativeEdit != null)
            {
                return true;
            }
            if (NativeText == null)
            {
                nativePressedTouch = null;
                return false;
            }
            int choice = NativeText.Type == 2 ? 1 : NativeText.Type == 1 && x >= Width / 2 ? 1 : 0;
            if (type == 2)
            {
                nativePressedTouch = y >= Height - 26 && NativeText.Type != 100 ? (NativeText.Handle, choice) : null;
            }
            else if (type == 3)
            {
                var pressed = nativePressedTouch;
                nativePressedTouch = null;
                if (pressed != null && pressed.Value.Handle == NativeText.Handle && pressed.Value.Choice == choice && y >= Height - 26)
                {
                    DialogChoice(choice);
                }
            }
            return true;
        }

        private string[] WrapNativeText(string text)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            int width = 0;
            foreach (char character in text)
            {
                if (character == '\r')
                {
                    continue;
                }
                font!.Measure(character, 1, out int advance, out _);
                if (character == '\n' || width + advance > Width - 12)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    width = 0;
                }
                if (character != '\n')
                {
                    line.Append(character);
                    width += advance;
                }
            }
            lines.Add(line.ToString());
            return lines.ToArray();
        }

        private void RenderNativeText(byte[] pixels)
        {
            NativeTextWindow? window = NativeText ?? nativeMenuView;
            if (window == null)
            {
                return;
            }
            Array.Fill(pixels, (byte)255);
            void Text(string text, int startY, int bottom, int startX = 6, bool wrap = true)
            {
                int x = startX;
                int y = startY;
                foreach (char character in text)
                {
                    if (character == '\r')
                    {
                        continue;
                    }
                    var glyph = font!.GlyphRows(character, 1, out int width, out int height, out int stride);
                    if (character == '\n' || x + width > Width - 6)
                    {
                        if (!wrap)
                        {
                            break;
                        }
                        x = startX;
                        y += height + 3;
                        if (character == '\n')
                        {
                            continue;
                        }
                    }
                    for (int row = 0; row < height && y + row < bottom; row++)
                    {
                        for (int column = 0; column < width && x + column < Width; column++)
                        {
                            if (y + row >= Height || y + row < 0)
                            {
                                continue;
                            }
                            if ((glyph[row * stride + column / 8] & (128 >> (column % 8))) != 0)
                            {
                                int index = ((y + row) * Width + x + column) * 2;
                                pixels[index] = 0;
                                pixels[index + 1] = 0;
                            }
                        }
                    }
                    x += width;
                }
            }
            Text(window.Title, 6, 28);
            font!.Measure('点', 1, out _, out int lineHeight);
            int bodyY = 34;
            for (int line = window.FirstVisibleLine; line < window.LineCount && bodyY + lineHeight <= Height - 26; line++)
            {
                Text(window.Lines[line], bodyY, Height - 26, wrap: window != nativeMenuView);
                bodyY += lineHeight + 3;
            }
            string buttons = window.Type == 0 || window.Type == 1 ? "确定"
                : window.Type == 2 ? "取消" : "";
            Text(buttons, Height - 20, Height);
            if (window.Type == 1)
            {
                font!.Measure('取', 1, out int width, out _);
                Text("取消", Height - 20, Height, Width - width * 2 - 6);
            }
        }
    }
}
