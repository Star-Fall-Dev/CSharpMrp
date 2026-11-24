using System;
using System.Buffers;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static int Number(object?[] args, int index, int fallback = 0)
        {
            return index < args.Length && args[index] != null ? ScriptVm.Number(args[index]) : fallback;
        }

        private static string TextArgument(object?[] args, int index)
        {
            if (index >= args.Length)
            {
                throw new RuntimeFault($"missing string argument {index + 1}");
            }
            return ScriptVm.Text(args[index]);
        }

        private uint ResourceSlot(int tableSlot, int index, int count, int size)
        {
            if (index < 0 || index >= count)
            {
                throw new RuntimeFault($"resource index {index} outside table {tableSlot}");
            }
            return Memory.U32(table + (uint)tableSlot * 4) + (uint)(index * size);
        }

        private void RegisterResourceGlobals()
        {
            RegisterBitmapGlobals();
            Script.Globals["BitmapLoad"] = new ScriptVm.Native(LoadBitmap);
            Script.Globals["_bmpLoad"] = Script.Globals["BitmapLoad"];
            Script.Globals["BitmapShow"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(95, Number(args, 0), 31, 16);
                ShowBitmap(slot, Number(args, 1), Number(args, 2), Number(args, 3, 2),
                    Number(args, 4), Number(args, 5), Number(args, 6, -1), Number(args, 7, -1));
                return Array.Empty<object?>();
            });
            Script.Globals["_bmpShow"] = Script.Globals["BitmapShow"];
            Script.Globals["SpriteSet"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(99, (ushort)RawScriptNumber(args, 0), 10, 2);
                Memory.Put16(slot, (ushort)RawScriptNumber(args, 1));
                return Array.Empty<object?>();
            });
            Script.Globals["SpriteDraw"] = new ScriptVm.Native(args =>
            {
                var sprite = ScriptSprite(args, false);
                DrawBitmap(sprite.Pointer, (short)RawScriptNumber(args, 2), (short)RawScriptNumber(args, 3),
                    sprite.Width, sprite.Height, (ushort)Number(args, 4, 6), sprite.Transparent, 0, 0, sprite.Width);
                return Array.Empty<object?>();
            });
            Script.Globals["SoundSet"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(98, Number(args, 0), 5, 12);
                string name = TextArgument(args, 1);
                int type = Number(args, 2, 1);
                if (type < 0 || type > 6)
                {
                    throw new RuntimeFault("invalid sound type");
                }
                (uint Pointer, uint Length, byte[]? Snapshot) resource = name.StartsWith("*", StringComparison.Ordinal)
                    ? default : ReadWholeScriptResource(name);
                ReplaceScriptResourcePointer(slot, ResolveWholeScriptResource(resource));
                Memory.Put32(slot + 4, resource.Length);
                Memory.Put32(slot + 8, (uint)type);
                if (Trace.Count < TraceLimit)
                    Trace.Add($"sound resource {Number(args, 0)}: {name} bytes={resource.Length}, type={type}");
                return Array.Empty<object?>();
            });
            Script.Globals["SoundPlay"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(98, Number(args, 0), 5, 12);
                if (Memory.U32(runtimeState) != 1 || Memory.U8(Memory.U32(table + 106 * 4)) == 0)
                {
                    Record("audio gated: script SoundPlay requires running state and sound switch");
                    return Array.Empty<object?>();
                }
                PlaySound((int)Memory.U32(slot + 8), Memory.U32(slot), Memory.U32(slot + 4),
                    args.Length > 1 && args[1] != null && !Equals(args[1], false));
                return Array.Empty<object?>();
            });
            Script.Globals["SoundStop"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(98, Number(args, 0), 5, 12);
                StopSound((int)Memory.U32(slot + 8));
                return Array.Empty<object?>();
            });
            Script.Globals["BgMusicSet"] = new ScriptVm.Native(args =>
            {
                Script.Call(Script.Globals["SoundSet"], 0, TextArgument(args, 0), Number(args, 1));
                return Array.Empty<object?>();
            });
            Script.Globals["BgMusicStart"] = new ScriptVm.Native(args =>
            {
                if (Memory.U32(runtimeState) == 1 && Memory.U8(Memory.U32(table + 106 * 4)) != 0)
                {
                    uint slot = ResourceSlot(98, 0, 5, 12);
                    bool loop = args.Length == 0 || args[0] != null && !Equals(args[0], false);
                    PlaySound((int)Memory.U32(slot + 8), Memory.U32(slot), Memory.U32(slot + 4), loop);
                }
                return Array.Empty<object?>();
            });
            Script.Globals["BgMusicStop"] = new ScriptVm.Native(_ =>
            {
                uint slot = ResourceSlot(98, 0, 5, 12);
                StopSound((int)Memory.U32(slot + 8));
                return Array.Empty<object?>();
            });
        }

        private uint PlaySound(int type, uint pointer, uint length, bool loop)
        {
            if (length == 0 || soundOutput == null)
            {
                if (Trace.Count < TraceLimit) Trace.Add($"audio unavailable: play type={type} bytes={length}, MR_FAILED=-1");
                return uint.MaxValue;
            }
            byte[] data = Memory.Read(pointer, length);
            bool playing = soundOutput.Play(type, data, loop);
            if (Trace.Count < TraceLimit)
                Trace.Add($"audio {(playing ? "playing" : "unsupported")}: type={type} bytes={length}, loop={loop}");
            return playing ? 0u : uint.MaxValue;
        }

        private uint StopSound(int type)
        {
            if (soundOutput == null)
            {
                if (Trace.Count < TraceLimit) Trace.Add($"audio unavailable: stop type={type}, MR_FAILED=-1");
                return uint.MaxValue;
            }
            bool stopped = soundOutput.Stop(type);
            if (Trace.Count < TraceLimit) Trace.Add($"audio {(stopped ? "stopped" : "not playing")}: type={type}");
            return stopped ? 0u : uint.MaxValue;
        }

        private object?[] LoadBitmap(object?[] args)
        {
            uint slot = ResourceSlot(95, Number(args, 0), 30, 16);
            string name = TextArgument(args, 1);
            uint pointer = 0;
            int width = 0;
            int height = 0;
            int bufferLength = 0;
            if (!name.StartsWith("*", StringComparison.Ordinal))
            {
                int x = Number(args, 2);
                int y = Number(args, 3);
                width = Number(args, 4);
                height = Number(args, 5);
                int stride = Number(args, 6, width);
                if (x < 0 || y < 0 || width <= 0 || height <= 0 || stride <= 0 ||
                    width > 4096 || height > 4096 || stride > 4096 || (long)x + width > stride)
                {
                    throw new RuntimeFault("invalid bitmap load rectangle");
                }
                (pointer, bufferLength) = LoadBitmapPixels(name, x, y, width, height, stride);
            }
            try
            {
                ReleaseScriptBitmap(slot);
                Memory.Put16(slot, (uint)width);
                Memory.Put16(slot + 2, (uint)height);
                Memory.Put32(slot + 4, (uint)bufferLength);
                Memory.Put32(slot + 8, 0);
            }
            catch
            {
                Memory.Free(pointer);
                throw;
            }
            Memory.Put32(slot + 12, pointer);
            if (Trace.Count < TraceLimit)
            {
                Record($"bitmap resource {Number(args, 0)}: {name} {width}x{height}");
            }
            return Array.Empty<object?>();
        }

        private (uint Pointer, int Length) LoadBitmapPixels(string name, int x, int y, int width, int height, int stride)
        {
            const int maximumBitmapBytes = 4 * 1024 * 1024;
            const int maximumPooledBytes = 256 * 1024;
            int rowBytes = width * 2;
            int croppedLength = rowBytes * height;
            long needed = (((long)y + height - 1) * stride + x + width) * 2;
            bool direct = x == 0 && y == 0 && stride == width;
            byte[]? rented = null;
            try
            {
                var loaded = RequiredScriptResource(name, (RuntimeSession session, uint address, uint size, ref bool owned) =>
                {
                    if ((!direct && needed > size) || croppedLength > maximumBitmapBytes)
                        throw new RuntimeFault("bitmap resource dimensions exceed data or size limit");
                    if (direct && (size < 2 || size > maximumBitmapBytes || size % 2 != 0))
                        throw new RuntimeFault("bitmap resource byte size limit or alignment");

                    int copiedLength = direct ? (int)size : croppedLength;
                    if (direct && copiedLength >= croppedLength && session.TryAdoptResource(address, size, ref owned))
                    {
                        return (Pointer: address, Length: copiedLength, Snapshot: (byte[]?)null);
                    }
                    byte[] buffer = copiedLength <= maximumPooledBytes
                        ? (rented = ArrayPool<byte>.Shared.Rent(copiedLength)) : new byte[copiedLength];
                    ReadOnlySpan<byte> source = session.Memory.ReadSpan(address, size);
                    if (direct)
                    {
                        source.CopyTo(buffer);
                    }
                    else
                    {
                        int offset = (y * stride + x) * 2;
                        for (int row = 0; row < height; row++, offset += stride * 2)
                            source.Slice(offset, rowBytes).CopyTo(buffer.AsSpan(row * rowBytes, rowBytes));
                    }
                    return (Pointer: 0u, Length: copiedLength, Snapshot: buffer);
                });
                if (loaded.Pointer != 0) return (loaded.Pointer, loaded.Length);

                // The resource has been released before allocating the destination in the guest heap.
                int copiedLength = loaded.Length;
                int bufferLength = Math.Max(croppedLength, copiedLength);
                uint pointer = Memory.Allocate((uint)bufferLength);
                try
                {
                    ReadOnlySpan<byte> pixels = loaded.Snapshot.AsSpan(0, copiedLength);
                    if (bufferLength > copiedLength)
                    {
                        ushort transparent = (ushort)(pixels[0] | pixels[1] << 8);
                        Memory.WritePaddedPixels(pointer, (uint)bufferLength, pixels, transparent);
                        if (Trace.Count < TraceLimit)
                            Trace.Add($"compatibility bitmap padding: {name}, bytes={copiedLength}->{bufferLength}; " +
                                "missing pixels use the resource transparency key");
                    }
                    else
                    {
                        Memory.Write(pointer, pixels);
                    }
                    return (pointer, bufferLength);
                }
                catch
                {
                    Memory.Free(pointer);
                    throw;
                }
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void ShowBitmap(uint slot, int x, int y, int operation, int sourceX, int sourceY, int width, int height)
        {
            uint pointer = Memory.U32(slot + 12);
            int sourceWidth = (int)Memory.U16(slot);
            int sourceHeight = (int)Memory.U16(slot + 2);
            width = width == -1 ? sourceWidth : width;
            height = height == -1 ? sourceHeight : height;
            var surface = DrawingSurface();
            int firstRow = Math.Max(0, -y);
            int lastRow = Math.Min(height, surface.Height - y);
            int firstColumn = Math.Max(0, -x);
            int lastColumn = Math.Min(width, surface.Width - x);
            bool visible = firstRow < lastRow && firstColumn < lastColumn;
            // _DrawBitmap clips to the destination before reading source pixels.
            long required = visible ?
                (((long)sourceY + lastRow - 1) * sourceWidth + sourceX + lastColumn) * 2 : 0;
            if (pointer == 0 || width < 0 || height < 0 || sourceX < 0 || sourceY < 0 ||
                visible && ((long)sourceX + lastColumn > sourceWidth || required > Memory.U32(slot + 4)))
            {
                throw new RuntimeFault($"bitmap show rectangle exceeds loaded resource: source={sourceWidth}x{sourceHeight}, " +
                    $"rect={sourceX},{sourceY},{width},{height}, pointer=0x{pointer:x8}");
            }
            DrawBitmap(pointer, x, y, width, height, operation, (ushort)Memory.U16(pointer), sourceX, sourceY, sourceWidth);
        }
    }
}
