using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void DrawEncodedSprite(uint source, int x, int y, int width, int height, int operation, ushort transparent)
        {
            int raster = operation & 0x3ff;
            if (raster != 2 && raster != 6)
            {
                if (raster >= 0 && raster <= 9)
                {
                    Record($"encoded sprite raster {raster}: reference performs no drawing");
                    return;
                }
                throw new RuntimeFault("unknown encoded sprite raster operation");
            }
            if (width < 0 || height < 0 || width > 640 || height > 640)
            {
                throw new RuntimeFault("invalid encoded sprite dimensions");
            }
            if (width == 0 || height == 0)
            {
                return;
            }
            var surface = DrawingSurface();
            int left = Math.Max(0, -x), top = Math.Max(0, -y);
            int right = Math.Min(width, surface.Width - x), bottom = Math.Min(height, surface.Height - y);
            if (right <= left || bottom <= top)
            {
                return;
            }
            int mode = raster == 6 ? 0 : (operation >> 11) & 3;
            bool flip = raster != 6 && (operation & (4 << 11)) != 0;
            (int X, int Y) Source(int u, int v)
            {
                int column = mode == 0 ? u : mode == 3 ? v : width - 1 - (mode == 1 ? v : u);
                int row = mode == 0 ? v : mode == 1 ? u : mode == 2 ? height - 1 - v : height - 1 - u;
                return (column, flip ? height - 1 - row : row);
            }
            // The legacy API keeps destination w/h even for a quarter turn. Reject the
            // resulting invalid rectangular source accesses instead of reading adjacent RAM.
            void CheckCorner(int column, int row)
            {
                var corner = Source(column, row);
                if (corner.X < 0 || corner.X >= width || corner.Y < 0 || corner.Y >= height)
                {
                    throw new RuntimeFault("encoded sprite rotation exceeds source rectangle");
                }
            }
            CheckCorner(left, top);
            CheckCorner(right - 1, top);
            CheckCorner(left, bottom - 1);
            CheckCorner(right - 1, bottom - 1);

            int SourceOffset(int column, int row)
            {
                var point = Source(column, row);
                return (point.Y * width + point.X) * 2;
            }
            int start = SourceOffset(left, top);
            int columnStep = SourceOffset(left + 1, top) - start;
            int rowStep = SourceOffset(left, top + 1) - start;
            uint rowBytes = (uint)surface.Width * 2;
            uint firstTarget = surface.Pointer + (uint)((y + top) * surface.Width + x + left) * 2;
            int columns = right - left, rows = bottom - top;
            uint endTarget = firstTarget + (uint)(rows - 1) * rowBytes + (uint)columns * 2;
            int lastColumnOffset = (columns - 1) * columnStep;
            int lastRowOffset = (rows - 1) * rowStep;
            int firstSource = start + Math.Min(0, lastColumnOffset) + Math.Min(0, lastRowOffset);
            uint visibleBytes = (uint)(Math.Abs(lastColumnOffset) + Math.Abs(lastRowOffset) + 2);
            uint sourceBytes = (uint)(width * height * 2);
            // Validate the declared sprite even when only a smaller window needs a snapshot.
            if (visibleBytes != sourceBytes) Memory.Check(source, sourceBytes);
            int sourceStride = width * 2;
            bool quarterTurn = (mode & 1) != 0;
            int visibleRowBytes = (quarterTurn ? rows : columns) * 2;
            var pixels = ReadBitmapPixels(source + (uint)firstSource, visibleBytes, firstTarget, endTarget,
                visibleRowBytes, sourceStride, out byte[]? snapshot, forceSnapshot: Memory.HasWriteObservers);
            start -= firstSource;
            if (pixels.Length != visibleBytes)
            {
                // The snapshot drops gaps between source rows, leaving their order intact.
                start = start / sourceStride * visibleRowBytes + start % sourceStride;
                if (quarterTurn) columnStep = Math.Sign(columnStep) * visibleRowBytes;
                else rowStep = Math.Sign(rowStep) * visibleRowBytes;
            }
            bool writeRows = !Memory.HasWriteObservers && BitConverter.IsLittleEndian &&
                ((source | firstTarget) & 1) == 0 && firstTarget >= Memory.HeapBase;
            ReadOnlySpan<ushort> stridedPixels = writeRows && columnStep != 2
                ? MemoryMarshal.Cast<byte, ushort>(pixels) : default;
            try
            {
                for (int row = top; row < bottom; row++, start += rowStep, firstTarget += rowBytes)
                {
                    if (writeRows)
                    {
                        if (columnStep != 2)
                        {
                            Memory.WriteStridedPixelRow(firstTarget, stridedPixels, start / 2, columnStep / 2, columns);
                        }
                        else
                        {
                            var rowPixels = pixels.Slice(start, columns * 2);
                            if (raster == 2) Memory.WritePixelBytes(firstTarget, rowPixels);
                            else Memory.WriteRasterPixelRow(firstTarget, rowPixels, raster, transparent, keyed: true);
                        }
                        continue;
                    }
                    int offset = start;
                    uint target = firstTarget;
                    for (int column = left; column < right; column++, offset += columnStep, target += 2)
                    {
                        uint color = (uint)(pixels[offset] | pixels[offset + 1] << 8);
                        if (raster != 6 || color != transparent) Memory.Put16(target, color);
                    }
                }
            }
            finally
            {
                if (snapshot != null) ArrayPool<byte>.Shared.Return(snapshot);
            }
        }
    }
}
