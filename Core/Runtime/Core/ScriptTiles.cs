using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void RegisterTileGlobals()
        {
            Script.Globals["TileSet"] = new ScriptVm.Native(args =>
            {
                int index = Number(args, 0);
                uint slot = ResourceSlot(96, index, 3, 20);
                uint map = ResourceSlot(97, index, 3, 4);
                int width = Number(args, 3);
                int height = Number(args, 4);
                int tileHeight = Number(args, 5);
                if (width < 0 || height < 0 || width > 1024 || height > 1024 ||
                    (long)width * height > 65536 || tileHeight <= 0 || tileHeight > 640)
                {
                    throw new RuntimeFault("invalid tile map dimensions");
                }
                int originX = Number(args, 1);
                int originY = Number(args, 2);
                Memory.Check(slot, 20);
                int cells = width * height;
                uint pointer = Memory.U32(map);
                if (pointer == 0 || Memory.U16(slot + 4) * Memory.U16(slot + 6) != cells)
                {
                    uint replacement = cells == 0 ? 0 : Memory.Allocate((uint)(cells * 2));
                    ReplaceScriptResourcePointer(map, replacement, pointer);
                }
                Memory.Put16(slot, (uint)originX);
                Memory.Put16(slot + 2, (uint)originY);
                Memory.Put16(slot + 4, (uint)width);
                Memory.Put16(slot + 6, (uint)height);
                Memory.Put16(slot + 18, (uint)tileHeight);
                return Array.Empty<object?>();
            });
            Script.Globals["TileSetRect"] = new ScriptVm.Native(args =>
            {
                uint rect = ResourceSlot(96, Number(args, 0), 3, 20) + 8;
                uint left = (uint)Number(args, 1);
                uint top = (uint)Number(args, 2);
                uint right = (uint)Number(args, 3);
                uint bottom = (uint)Number(args, 4);
                Memory.Check(rect, 8);
                Memory.Put16(rect, left);
                Memory.Put16(rect + 2, top);
                Memory.Put16(rect + 4, right);
                Memory.Put16(rect + 6, bottom);
                return Array.Empty<object?>();
            });
            Script.Globals["GetTile"] = new ScriptVm.Native(args =>
                new object?[] { (int)(short)Memory.U16(TileCell(Number(args, 0), Number(args, 1), Number(args, 2))) });
            Script.Globals["SetTile"] = new ScriptVm.Native(args =>
            {
                Memory.Put16(TileCell(Number(args, 0), Number(args, 1), Number(args, 2)), (uint)Number(args, 3));
                return Array.Empty<object?>();
            });
            Script.Globals["TileLoad"] = new ScriptVm.Native(args =>
            {
                int index = Number(args, 0);
                uint slot = ResourceSlot(96, index, 3, 20);
                uint map = ResourceSlot(97, index, 3, 4);
                var resource = ReadWholeScriptResource(TextArgument(args, 1));
                try
                {
                    if (resource.Length == 0 || resource.Length != (ulong)Memory.U16(slot + 4) * Memory.U16(slot + 6) * 2)
                    {
                        throw new RuntimeFault("tile resource dimensions mismatch");
                    }
                }
                catch
                {
                    Memory.Free(resource.Pointer);
                    throw;
                }
                ReplaceScriptResourcePointer(map, ResolveWholeScriptResource(resource));
                return Array.Empty<object?>();
            });
            Script.Globals["TileDraw"] = new ScriptVm.Native(args =>
            {
                DrawTiles(Number(args, 0));
                return Array.Empty<object?>();
            });
            Script.Globals["TileShift"] = new ScriptVm.Native(args =>
            {
                int index = (ushort)RawScriptNumber(args, 0);
                uint slot = ResourceSlot(96, index, 3, 20);
                int mode = (ushort)RawScriptNumber(args, 1);
                if (mode >= 0 && mode <= 3)
                {
                    int width = (int)Memory.U16(slot + 4);
                    int height = (int)Memory.U16(slot + 6);
                    if (width == 0 || height == 0)
                    {
                        throw new RuntimeFault("TileShift requires a nonempty map");
                    }
                    uint pointer = TileCell(index, 0, 0);
                    uint rowBytes = (uint)width * 2;
                    Memory.Check(pointer, rowBytes * (uint)height);
                    if (mode <= 1)
                    {
                        Memory.Copy(pointer + (mode == 1 ? rowBytes : 0),
                            pointer + (mode == 0 ? rowBytes : 0), rowBytes * (uint)(height - 1), true);
                    }
                    else
                    {
                        uint shiftedBytes = (uint)(width - 1) * 2;
                        for (uint row = 0; row < height; row++)
                        {
                            uint start = pointer + row * rowBytes;
                            Memory.Copy(start + (mode == 3 ? 2u : 0u),
                                start + (mode == 2 ? 2u : 0u), shiftedBytes, true);
                        }
                    }
                }
                return Array.Empty<object?>();
            });
        }

        private uint TileCell(int index, int x, int y)
        {
            uint slot = ResourceSlot(96, index, 3, 20);
            int width = (int)Memory.U16(slot + 4);
            int height = (int)Memory.U16(slot + 6);
            uint pointer = Memory.U32(ResourceSlot(97, index, 3, 4));
            if (pointer == 0 || (long)width * height > 65536 || x < 0 || y < 0 || x >= width || y >= height)
            {
                throw new RuntimeFault($"tile coordinate outside map: index={index}, cell={x},{y}, map={width}x{height}");
            }
            return pointer + (uint)(y * width + x) * 2;
        }

        private void DrawTiles(int index)
        {
            uint slot = ResourceSlot(96, index, 3, 20);
            uint bitmap = ResourceSlot(95, index, 30, 16);
            int tileWidth = (int)Memory.U16(bitmap);
            int tileHeight = (int)Memory.U16(slot + 18);
            if (tileWidth == 0 || tileHeight == 0)
            {
                throw new RuntimeFault("tile drawing requires bitmap and tile dimensions");
            }
            int width = (int)Memory.U16(slot + 4);
            int height = (int)Memory.U16(slot + 6);
            if ((long)width * height > 65536)
            {
                throw new RuntimeFault("tile map dimensions exceed limit");
            }
            var surface = DrawingSurface();
            if (width == 0 || height == 0) return;

            uint map = tileWidth <= 4096 && tileHeight <= 4096
                ? StableTileMap(index, slot, surface.Pointer, (uint)(width * height * 2)) : 0;
            int originX = 0, originY = 0;
            int firstColumn = 0, endColumn = width;
            int firstRow = 0, endRow = height;
            if (map != 0)
            {
                originX = (short)Memory.U16(slot);
                originY = (short)Memory.U16(slot + 2);
                int left = (short)Memory.U16(slot + 8), top = (short)Memory.U16(slot + 10);
                int right = (short)Memory.U16(slot + 12), bottom = (short)Memory.U16(slot + 14);
                // The viewport excludes tiles ending at zero, while the tile rect includes edge contact.
                firstColumn = TileBound(Math.Max(1, left) - tileWidth, originX, tileWidth, width);
                firstRow = TileBound(Math.Max(1, top) - tileHeight, originY, tileHeight, height);
                endColumn = TileBound(Math.Min(surface.Width, right), originX, tileWidth, width);
                endRow = TileBound(Math.Min(surface.Height, bottom), originY, tileHeight, height);
            }

            ulong length = (ulong)(uint)tileWidth * (uint)tileHeight * 2;
            for (int row = firstRow; row < endRow; row++)
            {
                for (int column = firstColumn; column < endColumn; column++)
                {
                    uint cell = map != 0 ? map + (uint)(row * width + column) * 2 : TileCell(index, column, row);
                    int tile = (int)Memory.U16(cell);
                    if ((tile & 1023) == 1023)
                    {
                        continue;
                    }
                    int x = (map != 0 ? originX : (short)Memory.U16(slot)) + column * tileWidth;
                    int y = (map != 0 ? originY : (short)Memory.U16(slot + 2)) + row * tileHeight;
                    if (map == 0 && (x >= surface.Width || y >= surface.Height || x + tileWidth <= 0 || y + tileHeight <= 0 ||
                        x + tileWidth < (short)Memory.U16(slot + 8) || x >= (short)Memory.U16(slot + 12) ||
                        y + tileHeight < (short)Memory.U16(slot + 10) || y >= (short)Memory.U16(slot + 14)))
                    {
                        continue;
                    }
                    uint source = Memory.U32(bitmap + 12);
                    ulong offset = (uint)(tile & 1023) * length;
                    if (source == 0 || offset + length > Memory.U32(bitmap + 4) || source + offset + length > uint.MaxValue)
                    {
                        throw new RuntimeFault("tile frame exceeds bitmap storage");
                    }
                    DrawBitmap(source + (uint)offset, (short)x, (short)y, tileWidth, tileHeight,
                        (tile & 0xfc00) + ((tile & 1024) == 0 ? 2 : 6), (ushort)Memory.U16(source), 0, 0, tileWidth);
                }
            }
        }

        private uint StableTileMap(int index, uint tileSlot, uint surface, uint size)
        {
            uint heap = Memory.HeapBase;
            if (tileSlot > heap - 20 || !Memory.ContainsRange(tileSlot, 20) || !HasStableDrawingSurface(surface))
            {
                return 0;
            }
            uint mapSlot = ResourceSlot(97, index, 3, 4);
            if (mapSlot > heap - 4) return 0;
            uint map = Memory.U32(mapSlot);
            // Keep per-cell faults in their original order if the map extends outside guest memory.
            return Memory.ContainsRange(map, size) ? map : 0;
        }

        private static int TileBound(int pixel, int origin, int step, int count) =>
            Math.Min(count, (Math.Max(0, pixel - origin) + step - 1) / step);
    }
}
