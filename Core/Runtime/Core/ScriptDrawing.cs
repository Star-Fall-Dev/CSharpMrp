using System;
using System.Buffers;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint scriptTimerName;
        private uint randomState = 0x6d727031;

        private static bool Boolean(object?[] args, int index)
        {
            return index < args.Length && args[index] != null && !(args[index] is bool flag && !flag);
        }

        private static ushort Rgb(int red, int green, int blue)
        {
            return (ushort)((red & 248) << 8 | (green & 252) << 3 | (blue & 255) >> 3);
        }

        private void DrawScriptLine(int x, int y, int targetX, int targetY, ushort color)
        {
            int dx = Math.Abs(targetX - x);
            int dy = -Math.Abs(targetY - y);
            int stepX = x < targetX ? 1 : -1;
            int stepY = y < targetY ? 1 : -1;
            int error = dx + dy;
            bool xMajor = dx >= -dy;
            int major = xMajor ? dx : -dy;
            int first = 0, last = major;
            var surface = DrawingSurface();
            bool stable = major != 0 && HasStableDrawingSurface(surface.Pointer);
            if (stable)
            {
                int origin = xMajor ? x : y;
                int limit = (xMajor ? surface.Width : surface.Height) - 1;
                int direction = xMajor ? stepX : stepY;
                first = Math.Max(0, direction > 0 ? -origin : origin - limit);
                last = Math.Min(major, direction > 0 ? limit - origin : origin);
                if (first > last) return;
                if (first != 0)
                {
                    // Keep the raster phase of the original line, including upward rounding at ties.
                    int minor = xMajor ? -dy : dx;
                    int minorSteps = (int)((2L * first * minor + major) / (2 * major));
                    int movedX = xMajor ? first : minorSteps;
                    int movedY = xMajor ? minorSteps : first;
                    x += movedX * stepX;
                    y += movedY * stepY;
                    error = (int)(error + (long)movedX * dy + (long)movedY * dx);
                }
            }

            for (int step = first; ; step++)
            {
                if (!stable && step != first) surface = DrawingSurface();
                if ((uint)x < (uint)surface.Width && (uint)y < (uint)surface.Height)
                {
                    Memory.Put16(surface.Pointer + (uint)(y * surface.Width + x) * 2, color);
                }
                if (step == last) break;
                int doubled = error * 2;
                if (doubled >= dy)
                {
                    error += dy;
                    x += stepX;
                }
                if (doubled <= dx)
                {
                    error += dx;
                    y += stepY;
                }
            }
        }

        private void RegisterDrawingGlobals()
        {
            Script.Globals["DrawLine"] = new ScriptVm.Native(args =>
            {
                int x = (short)Number(args, 0);
                int y = (short)Number(args, 1);
                int targetX = (short)Number(args, 2);
                int targetY = (short)Number(args, 3);
                ushort color = Rgb(Number(args, 4), Number(args, 5), Number(args, 6));
                DrawScriptLine(x, y, targetX, targetY, color);
                return Array.Empty<object?>();
            });
            Script.Globals["_drawLine"] = Script.Globals["DrawLine"];
            Script.Globals["ClearScreen"] = new ScriptVm.Native(args =>
            {
                var surface = DrawingSurface();
                Cpu.Invoke(0xf0000000 + 122 * 4, stackalloc uint[]
                {
                    0u, 0u, (uint)surface.Width, (uint)surface.Height, (uint)Number(args, 0),
                    (uint)Number(args, 1), (uint)Number(args, 2)
                });
                return Array.Empty<object?>();
            });
            Script.Globals["DrawRect"] = new ScriptVm.Native(args =>
            {
                Span<uint> values = stackalloc uint[7];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = unchecked((uint)Number(args, i));
                }
                Cpu.Invoke(0xf0000000 + 122 * 4, values);
                return Array.Empty<object?>();
            });
            Script.Globals["DispUpEx"] = new ScriptVm.Native(args =>
            {
                RefreshDrawingSurface(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3));
                Frames++;
                return Array.Empty<object?>();
            });
            Script.Globals["DrawText"] = new ScriptVm.Native(args =>
            {
                string value = TextArgument(args, 0);
                int x = Number(args, 1), y = Number(args, 2);
                ushort color = Rgb(Number(args, 3), Number(args, 4), Number(args, 5));
                bool unicode = Boolean(args, 6);
                uint selector = (uint)Number(args, 7, 1);
                ValidateDrawText(unicode, selector);
                byte[]? rented = null;
                try
                {
                    Span<byte> scratch = stackalloc byte[512];
                    var bytes = EncodeScriptDrawText(value, scratch, out rented);
                    var input = ReadScriptDrawText(bytes, unicode, 4096, "unterminated Unicode text");
                    DrawTextBytes(input, x, y, color, unicode, selector);
                }
                finally
                {
                    if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                }
                return Array.Empty<object?>();
            });
            Script.Globals["_textWidth"] = new ScriptVm.Native(args =>
            {
                if (font == null)
                {
                    throw new RuntimeFault("text metrics require an explicit bitmap font");
                }
                bool unicode = Boolean(args, 1);
                if (args.Length == 0)
                {
                    throw new RuntimeFault("text metrics require a string or character");
                }
                string text;
                if (args[0] is int characterCode)
                {
                    text = unicode ? ((char)characterCode).ToString() : DecodeGuestText(
                        characterCode < 128 ? new[] { (byte)characterCode } : new[] { (byte)(characterCode >> 8), (byte)characterCode });
                }
                else
                {
                    byte[] data = ScriptChunk.Bytes.GetBytes(ScriptVm.Text(args[0]));
                    if (unicode && data.Length % 2 != 0)
                    {
                        throw new RuntimeFault("odd Unicode text byte count");
                    }
                    text = unicode ? Encoding.BigEndianUnicode.GetString(data) : DecodeGuestText(data);
                }
                int width = 0;
                int height = 0;
                foreach (char character in text)
                {
                    if (character == 0)
                    {
                        break;
                    }
                    font.Measure(character, (uint)Number(args, 2, 1), out int w, out int h);
                    width += w;
                    height = Math.Max(height, h);
                }
                return new object?[] { width, height };
            });
            Script.Globals["TimerStart"] = new ScriptVm.Native(args =>
            {
                ushort delay = unchecked((ushort)Number(args, 1));
                string name = TextArgument(args, 2);
                uint state = Memory.U32(runtimeState);
                if (state != 1 && !(state == 2 && TimerRunsWhilePaused))
                {
                    return Array.Empty<object?>();
                }
                uint pointer;
                if (name.Length <= 128)
                {
                    Span<byte> bytes = stackalloc byte[128];
                    int length = ScriptChunk.Bytes.GetBytes(name.AsSpan(), bytes);
                    uint size = (uint)(length + 1);
                    pointer = Memory.Allocate(size);
                    Memory.WritePadded(pointer, size, bytes.Slice(0, length));
                }
                else
                {
                    pointer = Memory.CopyIn(ScriptChunk.Bytes.GetBytes(name + "\0"));
                }
                Memory.Free(scriptTimerName);
                scriptTimerName = pointer;
                Memory.Put32(timerFields, pointer);
                Cpu.Invoke(0xf0000000 + 31 * 4, stackalloc uint[] { (uint)delay });
                return Array.Empty<object?>();
            });
            Script.Globals["TimerStop"] = new ScriptVm.Native(_ =>
            {
                Cpu.Invoke(0xf0000000 + 32 * 4, Array.Empty<uint>());
                return Array.Empty<object?>();
            });
            Script.Globals["GetRand"] = new ScriptVm.Native(args =>
            {
                int range = Number(args, 0);
                if (range <= 0)
                {
                    throw new RuntimeFault("random range must be positive");
                }
                return new object?[] { (int)(NextRandom() % (uint)range) };
            });
            Script.Globals["mod"] = new ScriptVm.Native(args =>
            {
                int divisor = RawScriptNumber(args, 1);
                if (divisor == 0)
                {
                    throw new RuntimeFault("mod division by zero");
                }
                return new object?[] { (int)((long)RawScriptNumber(args, 0) % divisor) };
            });
            Script.Globals["_clearScr"] = Script.Globals["ClearScreen"];
            Script.Globals["_drawRect"] = Script.Globals["DrawRect"];
            Script.Globals["_dispUpEx"] = Script.Globals["DispUpEx"];
            Script.Globals["_drawText"] = Script.Globals["DrawText"];
            Script.Globals["_drawTextEx"] = new ScriptVm.Native(args =>
            {
                if (args.Length == 0 || args[0] == null)
                {
                    return new object?[] { 0 };
                }
                return new object?[] { (int)DrawScriptTextExtended(TextArgument(args, 0), (short)Number(args, 1), (short)Number(args, 2),
                    (ushort)Number(args, 5), (ushort)Number(args, 6),
                    Rgb(Number(args, 7), Number(args, 8), Number(args, 9)), Number(args, 10, 3), (ushort)Number(args, 11, 1)) };
            });
            Script.Globals["_timerStart"] = Script.Globals["TimerStart"];
            Script.Globals["_timerStop"] = Script.Globals["TimerStop"];
            Script.Globals["_rand"] = Script.Globals["GetRand"];
        }

        private uint DrawScriptTextExtended(string value, int x, int y, int width, int height, ushort color, int options, uint selector)
        {
            ValidateDrawTextExtended(selector);
            byte[]? encodedRental = null;
            try
            {
                Span<byte> scratch = stackalloc byte[512];
                var bytes = EncodeScriptDrawText(value, scratch, out encodedRental);
                bool unicode = (options & 1) != 0;
                const int unicodeLimit = 8192;
                const string limitError = "native text length limit";
                var input = ReadScriptDrawText(bytes, unicode, unicodeLimit, limitError);
                if (unicode) return DrawTextExtendedBytes(input, x, y, width, height, color, options, selector);

                Encoding decoder = RequireGbkEncoding("c2u requires an explicit CP936 encoding service");
                return DrawGbkTextExtended(input, decoder, x, y, width, height, color, options, selector);
            }
            finally
            {
                if (encodedRental != null) ArrayPool<byte>.Shared.Return(encodedRental, clearArray: true);
            }
        }

        private static ReadOnlySpan<byte> EncodeScriptDrawText(string value, Span<byte> scratch, out byte[]? rented)
        {
            int count = ScriptChunk.Bytes.GetByteCount(value);
            // An incomplete final UCS-2 unit is padded before the two-byte terminator.
            int length = checked(count + (count & 1) + 2);
            rented = null;
            Span<byte> bytes = length <= scratch.Length ? scratch.Slice(0, length) :
                length <= 64 * 1024 ? (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length) : new byte[length];
            int written = ScriptChunk.Bytes.GetBytes(value.AsSpan(), bytes);
            bytes.Slice(written).Clear();
            return bytes;
        }

        private static ReadOnlySpan<byte> ReadScriptDrawText(ReadOnlySpan<byte> bytes, bool unicode, int limit, string limitError)
        {
            if (!unicode) return bytes.Slice(0, bytes.IndexOf((byte)0));
            return ReadTerminatedUnicodeBytes(bytes, limit, limitError);
        }

        private string DecodeGuestText(ReadOnlySpan<byte> data)
        {
            if (textEncoding == null)
            {
                throw new RuntimeFault("GB text requires an explicit host encoding service");
            }
            try
            {
                return textEncoding.GetString(data);
            }
            catch (DecoderFallbackException)
            {
                throw new RuntimeFault("invalid GB text sequence");
            }
        }
    }
}
