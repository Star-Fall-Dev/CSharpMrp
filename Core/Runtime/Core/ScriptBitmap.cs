using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static int RawScriptNumber(object?[] args, int index, int fallback = 0)
        {
            if (index >= args.Length)
            {
                return fallback;
            }
            ScriptInteger(args[index], out int value);
            return value;
        }

        private uint ScriptBitmapSlot(object?[] args, int argument = 0)
        {
            return ResourceSlot(95, (ushort)RawScriptNumber(args, argument), 31, 16);
        }

        private void RegisterBitmapGlobals()
        {
            Script.Globals["BitmapNew"] = new ScriptVm.Native(args =>
            {
                ResizeScriptBitmap(ScriptBitmapSlot(args), (ushort)RawScriptNumber(args, 1), (ushort)RawScriptNumber(args, 2), false);
                return Array.Empty<object?>();
            });
            Script.Globals["_bmpNew"] = Script.Globals["BitmapNew"];
            Script.Globals["_bmpInfo"] = new ScriptVm.Native(args =>
            {
                uint slot = ScriptBitmapSlot(args);
                return new object?[] { unchecked((int)Memory.U32(slot + 12)), unchecked((int)Memory.U32(slot + 4)),
                    (int)Memory.U16(slot), (int)Memory.U16(slot + 2), unchecked((int)Memory.U32(slot + 8)) };
            });
            Script.Globals["BmGetScr"] = new ScriptVm.Native(args =>
            {
                uint slot = ResourceSlot(95, (ushort)RawScriptNumber(args, 0), 30, 16);
                var surface = DrawingSurface();
                ResizeScriptBitmap(slot, surface.Width, surface.Height, true);
                Memory.Copy(Memory.U32(slot + 12), surface.Pointer, (uint)(surface.Width * surface.Height * 2), false);
                return Array.Empty<object?>();
            });
            Script.Globals["_bmpGetScr"] = Script.Globals["BmGetScr"];
            Script.Globals["_dispUp"] = new ScriptVm.Native(args =>
            {
                int x = (short)RawScriptNumber(args, 0);
                int y = (short)RawScriptNumber(args, 1);
                int width = (ushort)RawScriptNumber(args, 2);
                int height = (ushort)RawScriptNumber(args, 3);
                uint slot = ResourceSlot(95, (ushort)Number(args, 4, 30), 31, 16);
                uint pointer = Memory.U32(slot + 12);
                // Preserve the reference entry point's height-based pointer offset.
                long offset = ((long)y * Memory.U16(slot + 2) + x) * 2;
                int right = Math.Min(Width, x + width);
                int bottom = Math.Min(Height, y + height);
                long end = right > Math.Max(0, x) && bottom > Math.Max(0, y)
                    ? ((long)(bottom - 1) * Width + right) * 2 : 0;
                if (pointer == 0 || offset < 0 || offset + end > Memory.U32(slot + 4) ||
                    pointer + offset + end > uint.MaxValue)
                {
                    throw new RuntimeFault("bitmap display exceeds resource storage");
                }
                Blit(pointer + (uint)offset, x, y, width, height);
                Frames++;
                return Array.Empty<object?>();
            });
            Script.Globals["_bmpShowEx"] = new ScriptVm.Native(args =>
            {
                uint source = unchecked((uint)RawScriptNumber(args, 0));
                DrawBitmap(source, (short)RawScriptNumber(args, 1), (short)RawScriptNumber(args, 2),
                    (short)RawScriptNumber(args, 4), (short)RawScriptNumber(args, 5), (ushort)Number(args, 6, 2),
                    (ushort)Memory.U16(source), (short)Number(args, 7), (short)Number(args, 8), (short)RawScriptNumber(args, 3));
                return Array.Empty<object?>();
            });
            Script.Globals["BitmapDraw"] = new ScriptVm.Native(DrawScriptBitmap);
            Script.Globals["_bmpDraw"] = Script.Globals["BitmapDraw"];
            Script.Globals["SpriteDrawEx"] = new ScriptVm.Native(args =>
            {
                var sprite = ScriptSprite(args, true);
                var surface = DrawingSurface();
                TransformScriptBitmap(sprite.Pointer, sprite.Width, sprite.Height, 0, 0,
                    surface.Pointer, surface.Width, surface.Height, (short)RawScriptNumber(args, 2), (short)RawScriptNumber(args, 3),
                    sprite.Width, sprite.Height, args, 4, 6, sprite.Transparent);
                return Array.Empty<object?>();
            });
            Script.Globals["SpriteCheck"] = new ScriptVm.Native(args =>
            {
                var sprite = ScriptSprite(args, false);
                uint color = unchecked((uint)RawScriptNumber(args, 4));
                return new object?[] { unchecked((int)CheckBitmap(sprite.Pointer, (ushort)RawScriptNumber(args, 2),
                    (ushort)RawScriptNumber(args, 3), sprite.Width, sprite.Height, sprite.Transparent,
                    Rgb((int)(color >> 16), (int)(color >> 8), (int)color))) };
            });
        }

        private void ResizeScriptBitmap(uint slot, int width, int height, bool always)
        {
            long size = (long)width * height * 2;
            if (size > Package.MaxBytes)
            {
                throw new RuntimeFault("script bitmap allocation size limit");
            }
            uint oldSize = Memory.U32(slot + 4);
            if (always || oldSize != size || (size != 0 && Memory.U32(slot + 12) == 0))
            {
                if (size == 0 || !ReleaseScriptBitmap(slot, (uint)size))
                {
                    throw new RuntimeFault("script bitmap allocation limit");
                }
                uint replacement = Memory.NativeAllocate((uint)size);
                if (replacement == 0)
                {
                    throw new RuntimeFault("script bitmap allocation limit");
                }
                Memory.Fill(replacement, (uint)size, 0);
                Memory.Put32(slot + 12, replacement);
            }
            Memory.Put32(slot + 4, (uint)size);
            Memory.Put16(slot, (uint)width);
            Memory.Put16(slot + 2, (uint)height);
        }

        private bool ReleaseScriptBitmap(uint slot, uint replacementSize = 0)
        {
            uint pointer = Memory.U32(slot + 12);
            if (pointer != 0 && (pointer == Framebuffer || pointer == Memory.U32(Memory.U32(table + 91 * 4))))
            {
                throw new RuntimeFault("cannot free the active drawing surface through a bitmap resource");
            }
            uint size = Memory.U32(slot + 4);
            if (replacementSize != 0 && pointer != 0 && size != 0)
            {
                if (!Memory.TryFreeForNativeReplacement(pointer, size, replacementSize)) return false;
            }
            else Memory.NativeFree(pointer, size);
            Memory.Put32(slot + 12, 0);
            return true;
        }

        private (uint Pointer, int Width, int Height, ushort Transparent) ScriptSprite(object?[] args, bool masked)
        {
            int index = (ushort)RawScriptNumber(args, 0);
            uint sprite = ResourceSlot(99, index, 10, 2);
            uint bitmap = ResourceSlot(95, index, 30, 16);
            int width = (int)Memory.U16(bitmap);
            int height = (int)Memory.U16(sprite);
            int frame = (ushort)RawScriptNumber(args, 1);
            if (masked)
            {
                frame &= 1023;
            }
            uint source = Memory.U32(bitmap + 12);
            ulong offset = (ulong)(uint)frame * (uint)width * (uint)height * 2;
            ulong size = (ulong)(uint)width * (uint)height * 2;
            if (source == 0 || offset + size > Memory.U32(bitmap + 4) || source + offset + size > uint.MaxValue)
            {
                throw new RuntimeFault("sprite frame exceeds bitmap storage");
            }
            Memory.Check(source + (uint)offset, (uint)size);
            return (source + (uint)offset, width, height, (ushort)Memory.U16(source));
        }

        private object?[] DrawScriptBitmap(object?[] args)
        {
            uint destination = ScriptBitmapSlot(args);
            uint source = ScriptBitmapSlot(args, 3);
            uint sourcePointer = Memory.U32(source + 12);
            uint destinationPointer = Memory.U32(destination + 12);
            if (sourcePointer == 0 || destinationPointer == 0 ||
                (ulong)Memory.U16(source) * Memory.U16(source + 2) * 2 > Memory.U32(source + 4) ||
                (ulong)Memory.U16(destination) * Memory.U16(destination + 2) * 2 > Memory.U32(destination + 4))
            {
                throw new RuntimeFault("bitmap transform requires valid resource storage");
            }
            TransformScriptBitmap(sourcePointer, (int)Memory.U16(source), (int)Memory.U16(source + 2),
                (short)RawScriptNumber(args, 4), (short)RawScriptNumber(args, 5), destinationPointer,
                (int)Memory.U16(destination), (int)Memory.U16(destination + 2),
                (short)RawScriptNumber(args, 1), (short)RawScriptNumber(args, 2),
                (ushort)RawScriptNumber(args, 6), (ushort)RawScriptNumber(args, 7), args, 8,
                (ushort)RawScriptNumber(args, 12, 2), (ushort)Memory.U16(sourcePointer));
            return Array.Empty<object?>();
        }

        private void TransformScriptBitmap(uint source, int sourceWidth, int sourceHeight, int sx, int sy,
            uint destination, int destinationWidth, int destinationHeight, int dx, int dy,
            int width, int height, object?[] args, int coefficients, int operation, ushort transparent)
        {
            uint temporary = Memory.Allocate(36);
            try
            {
                void Descriptor(uint address, uint pointer, int w, int h, int x, int y)
                {
                    Memory.Put32(address, pointer);
                    Memory.Put16(address + 4, (uint)w);
                    Memory.Put16(address + 6, (uint)h);
                    Memory.Put16(address + 8, (uint)x);
                    Memory.Put16(address + 10, (uint)y);
                }
                Descriptor(temporary, source, sourceWidth, sourceHeight, sx, sy);
                Descriptor(temporary + 12, destination, destinationWidth, destinationHeight, dx, dy);
                for (int index = 0; index < 4; index++)
                {
                    Memory.Put16(temporary + 24 + (uint)index * 2, (uint)RawScriptNumber(args, coefficients + index));
                }
                Memory.Put16(temporary + 32, (uint)operation);
                DrawTransformedBitmap(temporary, temporary + 12, width, height, temporary + 24, transparent);
            }
            finally
            {
                Memory.Free(temporary);
            }
        }
    }
}
