using System;
using System.Buffers;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private ReadOnlySpan<byte> ReadBitmapPixels(uint source, uint size, uint firstTarget, uint endTarget,
            int visibleRowBytes, int sourceStride, out byte[]? snapshot, bool forceSnapshot = false)
        {
            ReadOnlySpan<byte> pixels = Memory.ReadSpan(source, size);
            if (!forceSnapshot && (source >= endTarget || (ulong)source + size <= firstTarget))
            {
                snapshot = null;
                return pixels;
            }
            const int poolLimit = 512 * 1024;
            int snapshotLength = pixels.Length;
            int packedRows = 0;
            if (visibleRowBytes < sourceStride && size > visibleRowBytes)
            {
                int rows = (pixels.Length - visibleRowBytes) / sourceStride + 1;
                int packedLength = rows * visibleRowBytes;
                if (packedLength <= poolLimit && (size > poolLimit || packedLength <= pixels.Length / 2))
                {
                    snapshotLength = packedLength;
                    packedRows = rows;
                }
            }

            // Keep normal screen-sized copies reusable without pooling oversized images.
            if (snapshotLength > poolLimit)
            {
                snapshot = null;
                return pixels.ToArray();
            }
            snapshot = ArrayPool<byte>.Shared.Rent(snapshotLength);
            if (packedRows == 0)
            {
                pixels.CopyTo(snapshot);
            }
            else
            {
                for (int row = 0; row < packedRows; row++)
                {
                    pixels.Slice(row * sourceStride, visibleRowBytes)
                        .CopyTo(snapshot.AsSpan(row * visibleRowBytes, visibleRowBytes));
                }
            }
            return snapshot.AsSpan(0, snapshotLength);
        }

        private void DrawBitmap(uint source, int x, int y, int width, int height, int operation,
            ushort transparent, int sourceX, int sourceY, int stride)
        {
            if (operation > 0x400)
            {
                DrawEncodedSprite(source, x, y, width, height, operation, transparent);
                return;
            }
            if (operation == 20)
            {
                Record("bitmap operation 20 ignored: reference switch has no drawing case; source and destination unchanged");
                return;
            }
            if (operation < 0 || operation > 9)
            {
                throw new RuntimeFault($"unsupported bitmap operation {operation}");
            }
            if (width == 0 || height == 0)
            {
                return;
            }
            var surface = DrawingSurface();
            int firstRow = Math.Max(0, -y);
            int lastRow = Math.Min(height, surface.Height - y);
            int firstColumn = Math.Max(0, -x);
            int lastColumn = Math.Min(width, surface.Width - x);
            if (firstRow >= lastRow || firstColumn >= lastColumn)
            {
                return;
            }
            // The reference clips the destination before accessing the source bitmap.
            if (width > 4096 || height > 4096 || stride <= 0 || stride > 4096)
            {
                throw new RuntimeFault($"invalid bitmap source rectangle: source=0x{source:x8}, " +
                    $"destination={x},{y}, size={width},{height}, sourceXY={sourceX},{sourceY}, stride={stride}");
            }
            // The reference treats sourceX as a linear offset, including scanline crossings.
            long firstPixel = ((long)sourceY + firstRow) * stride + sourceX + firstColumn;
            long lastPixel = ((long)sourceY + lastRow - 1) * stride + sourceX + lastColumn;
            if (firstPixel < 0)
            {
                throw new RuntimeFault("invalid bitmap source rectangle: negative visible source offset");
            }
            Memory.Check(source, 0);
            ulong readAddress = (ulong)source + (ulong)firstPixel * 2;
            uint size = checked((uint)((lastPixel - firstPixel) * 2));
            if (readAddress > uint.MaxValue)
            {
                throw new RuntimeFault("guest memory: invalid range for bitmap source");
            }
            uint rowBytes = (uint)surface.Width * 2;
            uint firstTarget = surface.Pointer + (uint)((y + firstRow) * surface.Width + x + firstColumn) * 2;
            int columns = lastColumn - firstColumn;
            int rows = lastRow - firstRow;
            int visibleRowBytes = columns * 2;
            int sourceStride = stride * 2;
            uint endTarget = firstTarget + (uint)(rows - 1) * rowBytes + (uint)visibleRowBytes;
            bool observed = Memory.HasWriteObservers;
            if (operation == 2 && !observed && firstTarget >= Memory.HeapBase && sourceStride == rowBytes &&
                readAddress < endTarget && readAddress + size > firstTarget)
            {
                CopyOverlappingBitmapRows((uint)readAddress, firstTarget, size, rows, visibleRowBytes, sourceStride);
                return;
            }
            // An observer can write elsewhere in the drawing surface during a store.
            uint snapshotStart = observed ? surface.Pointer : firstTarget;
            uint snapshotEnd = observed ? surface.Pointer + rowBytes * (uint)surface.Height : endTarget;
            ReadOnlySpan<byte> pixels = ReadBitmapPixels((uint)readAddress, size, snapshotStart, snapshotEnd,
                visibleRowBytes, sourceStride, out byte[]? snapshot);
            if (pixels.Length != size) sourceStride = visibleRowBytes;
            bool writeRasterRows = operation != 8 && !observed && BitConverter.IsLittleEndian &&
                ((readAddress | firstTarget) & 1) == 0 && firstTarget >= Memory.HeapBase;
            try
            {
                if (operation == 2 && columns == stride && columns == surface.Width)
                {
                    Memory.WritePixelBytes(firstTarget, pixels);
                    return;
                }
                bool keyed = operation is 6 or 8 or 9;
                bool usesTarget = operation is 0 or 1 or 4 or 5 or 7;
                for (int row = 0; row < rows; row++)
                {
                    ReadOnlySpan<byte> rowPixels = pixels.Slice(row * sourceStride, visibleRowBytes);
                    uint target = firstTarget + (uint)row * rowBytes;
                    if (operation == 2)
                    {
                        Memory.WritePixelBytes(target, rowPixels);
                        continue;
                    }
                    if (writeRasterRows)
                    {
                        Memory.WriteRasterPixelRow(target, rowPixels, operation, transparent, keyed);
                        continue;
                    }
                    for (int index = 0; index < rowPixels.Length; index += 2, target += 2)
                    {
                        ushort value = (ushort)(rowPixels[index] | rowPixels[index + 1] << 8);
                        if (keyed && value == transparent) continue;
                        uint previous = usesTarget ? Memory.U16(target) : 0;
                        Memory.Put16(target, GuestMemory.RasterRgb565(value, previous, operation));
                    }
                }
            }
            finally
            {
                if (snapshot != null) ArrayPool<byte>.Shared.Return(snapshot);
            }
        }

        private void CopyOverlappingBitmapRows(uint source, uint target, uint size, int rows, int rowBytes, int stride)
        {
            if (rowBytes == stride)
            {
                Memory.Copy(target, source, size, preserveOverlap: true);
                return;
            }

            Memory.Check(source, size);
            // Equal strides keep the address difference constant across rows.
            int row = target > source ? rows - 1 : 0;
            int step = target > source ? -1 : 1;
            for (int copied = 0; copied < rows; copied++, row += step)
            {
                uint offset = (uint)(row * stride);
                Memory.Copy(target + offset, source + offset, (uint)rowBytes, preserveOverlap: true);
            }
        }

        private void DrawText(uint source, int x, int y, ushort color, bool unicode, uint selector)
        {
            if (source == 0)
            {
                Record($"DrawText x={x}: text is null; reference returns 0 without drawing");
                return;
            }
            ValidateDrawText(unicode, selector);
            ReadOnlySpan<byte> input = unicode
                ? ReadUnicodeBytes(source, 4096, "unterminated Unicode text") : Memory.CStringBytes(source);
            DrawTextBytes(input, x, y, color, unicode, selector);
        }

        private void ValidateDrawText(bool unicode, uint selector)
        {
            if (font == null || selector > 2)
            {
                throw new RuntimeFault("DrawText requires a bitmap font and a supported selector");
            }
            if (!unicode && textEncoding == null)
            {
                throw new RuntimeFault("GB text requires an explicit host encoding service");
            }
        }

        private void DrawTextBytes(ReadOnlySpan<byte> input, int x, int y, ushort color, bool unicode, uint selector)
        {
            const int maxPooledChars = 4096;
            int capacity = unicode ? input.Length / 2 : input.Length;
            if (!unicode && capacity <= maxPooledChars) capacity = textEncoding!.GetMaxCharCount(capacity);
            if (capacity > maxPooledChars)
            {
                DrawDecodedText(DecodeGuestText(input), x, y, color, selector);
                return;
            }

            char[]? rented = null;
            Span<char> text = capacity <= 256 ? stackalloc char[capacity] :
                (rented = ArrayPool<char>.Shared.Rent(capacity)).AsSpan(0, capacity);
            try
            {
                // Decode before drawing can overwrite the guest source, including during write callbacks.
                if (unicode)
                {
                    for (int i = 0; i < text.Length; i++) text[i] = (char)(input[i * 2] << 8 | input[i * 2 + 1]);
                }
                else
                {
                    try { text = text.Slice(0, textEncoding!.GetChars(input, text)); }
                    catch (DecoderFallbackException) { throw new RuntimeFault("invalid GB text sequence"); }
                }
                DrawDecodedText(text, x, y, color, selector);
            }
            finally
            {
                if (rented != null) ArrayPool<char>.Shared.Return(rented, clearArray: true);
            }
        }

        private void DrawDecodedText(ReadOnlySpan<char> text, int x, int y, ushort color, uint selector)
        {
            if (Trace.Count < TraceLimit) Trace.Add($"text x={x} y={y} font={selector} value={text.ToString()}");
            foreach (char character in text)
            {
                x += DrawCharacter(character, x, y, color, selector);
            }
        }

        private uint DrawTextExtended(uint source, int x, int y, int width, int height, ushort color, int options, uint selector)
        {
            if (source == 0)
            {
                return 0;
            }
            ValidateDrawTextExtended(selector);
            if ((options & 1) == 0)
            {
                Encoding decoder = RequireGbkEncoding("c2u requires an explicit CP936 encoding service");
                return DrawGbkTextExtended(Memory.CStringBytes(source), decoder, x, y, width, height, color, options, selector);
            }

            byte[]? rented = null;
            try
            {
                ReadOnlySpan<byte> input = ReadUnicodeBytes(source);
                Span<byte> text = input.Length <= 512 ? stackalloc byte[input.Length] :
                    (rented = ArrayPool<byte>.Shared.Rent(input.Length)).AsSpan(0, input.Length);
                // Drawing may overwrite the guest buffer that supplied the text.
                input.CopyTo(text);
                return DrawTextExtendedBytes(text, x, y, width, height, color, options, selector);
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        private uint DrawGbkTextExtended(ReadOnlySpan<byte> input, Encoding decoder, int x, int y, int width, int height,
            ushort color, int options, uint selector)
        {
            const int unicodeLimit = 8192;
            // A full prefix keeps its terminator beyond the draw limit, so it is still rejected.
            int size = ConvertedTextSize(input, out int consumed, unicodeLimit / 2);
            byte[]? rented = null;
            try
            {
                Span<byte> converted = size <= 512 ? stackalloc byte[size] :
                    (rented = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size);
                // Complete the conversion before the drawing surface can overwrite the input.
                ConvertTextBytes(input.Slice(0, consumed), converted, decoder);
                return DrawTextExtendedBytes(ReadTerminatedUnicodeBytes(converted, unicodeLimit, "native text length limit"),
                    x, y, width, height, color, options, selector);
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        private void ValidateDrawTextExtended(uint selector)
        {
            if (font == null || selector > 2)
            {
                throw new RuntimeFault("DrawTextEx requires a bitmap font and a supported selector");
            }
        }

        private uint DrawTextExtendedBytes(ReadOnlySpan<byte> text, int x, int y, int width, int height,
            ushort color, int options, uint selector)
        {
            bool wrap = (options & 2) != 0;
            int penX = (ushort)x;
            int penY = (ushort)y;
            int lineHeight = 0;
            int offset = 0;
            uint accepted = 0;
            for (; offset < text.Length; offset += 2)
            {
                char character = (char)(text[offset] << 8 | text[offset + 1]);
                bool control = character == '\n' || character == '\r';
                var glyph = font!.GlyphRows(control ? ' ' : character, selector,
                    out int glyphWidth, out int glyphHeight, out int stride);
                if (wrap)
                {
                    if (penX + glyphWidth > x + width || character == '\n')
                    {
                        if (penY + lineHeight < y + height)
                        {
                            accepted = (uint)offset;
                        }
                        penX = (ushort)x;
                        penY = (ushort)(penY + lineHeight + 2);
                        lineHeight = 0;
                        if (penY > y + height)
                        {
                            break;
                        }
                    }
                    lineHeight = Math.Max(lineHeight, glyphHeight);
                }
                else
                {
                    if (penX > x + width || character == '\n')
                    {
                        break;
                    }
                    if (penX + glyphWidth > x + width)
                    {
                        accepted = (uint)offset;
                    }
                }
                if (!control)
                {
                    // MR_PLAT_DRAWTEXT clips to the screen, not to rect. rect.x/y are ignored.
                    DrawGlyph(glyph, glyphWidth, glyphHeight, stride, penX, penY, color);
                    penX = (ushort)(penX + glyphWidth);
                }
            }
            if (offset == text.Length && (wrap ? penY + lineHeight < y + height : penX <= x + width))
            {
                accepted = (uint)offset;
            }
            return accepted;
        }

        private uint CheckBitmap(uint source, int x, int y, int width, int height, ushort transparent, ushort background)
        {
            var surface = DrawingSurface();
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(surface.Width, x + width);
            int bottom = Math.Min(surface.Height, y + height);
            if (left >= right || top >= bottom) return 0;

            uint collisions = 0;
            uint targetStride = (uint)surface.Width * 2;
            uint target = surface.Pointer + (uint)top * targetStride + (uint)left * 2;
            ulong input = source + ((ulong)(top - y) * (uint)width + (uint)(left - x)) * 2;
            ulong inputStride = (ulong)width * 2;
            int columns = right - left;
            for (int row = top; row < bottom; row++, input += inputStride, target += targetStride)
            {
                if (input > uint.MaxValue) throw new RuntimeFault("bitmap address overflow");
                int compared = Memory.CountRgb565CollisionVectors((uint)input, target, columns, transparent, background,
                    out uint vectorCollisions);
                collisions += vectorCollisions;
                uint sourcePixel = (uint)input + (uint)compared * 2;
                uint targetPixel = target + (uint)compared * 2;
                // Also report the first invalid halfword if the visible row is truncated.
                for (int column = compared; column < columns; column++, sourcePixel += 2, targetPixel += 2)
                {
                    if (Memory.U16(sourcePixel) != transparent && Memory.U16(targetPixel) != background) collisions++;
                }
            }
            return collisions;
        }

        private void ApplyContrast(int x, int y, int width, int height, int red, int green, int blue)
        {
            var surface = DrawingSurface();
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(surface.Width, x + width);
            int bottom = Math.Min(surface.Height, y + height);
            if (left >= right || top >= bottom) return;

            uint rowBytes = (uint)surface.Width * 2;
            uint address = surface.Pointer + (uint)top * rowBytes + (uint)left * 2;
            int columns = right - left;
            if (columns == surface.Width)
            {
                Memory.ScaleRgb565(address, columns * (bottom - top), red, green, blue);
                return;
            }
            for (int row = top; row < bottom; row++, address += rowBytes)
                Memory.ScaleRgb565(address, columns, red, green, blue);
        }

        private void FillRectangle(int x, int y, int width, int height, ushort color)
        {
            var surface = DrawingSurface();
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(surface.Width, x + width);
            int bottom = Math.Min(surface.Height, y + height);
            if (left >= right || top >= bottom) return;

            uint rowBytes = (uint)surface.Width * 2;
            uint address = surface.Pointer + (uint)top * rowBytes + (uint)left * 2;
            int columns = right - left;
            if (columns == surface.Width)
            {
                Memory.Fill16(address, columns * (bottom - top), color);
                return;
            }
            for (int row = top; row < bottom; row++, address += rowBytes)
                Memory.Fill16(address, columns, color);
        }

        private int DrawCharacter(char character, int x, int y, ushort color, uint selector)
        {
            if (font == null || selector > 2)
            {
                throw new RuntimeFault("DrawText requires a bitmap font and a supported selector");
            }
            var bits = font.GlyphRows(character, selector, out int width, out int height, out int stride);
            DrawGlyph(bits, width, height, stride, x, y, color);
            return width;
        }

        private void DrawGlyph(ReadOnlySpan<byte> bits, int width, int height, int stride, int x, int y, ushort color)
        {
            (uint Pointer, int Width, int Height) surface = default;
            int firstColumn = 0, visibleWidth = 0;
            uint visibleMask = 0;
            bool writeRows = false;
            int glyphMask = 0xffff << (16 - width);
            for (int row = 0; row < height; row++)
            {
                int rowStart = row * stride;
                int rowBits = bits[rowStart] << 8;
                if (stride == 2) rowBits |= bits[rowStart + 1];
                if ((rowBits & glyphMask) == 0) continue;

                // Empty glyphs do not access the guest drawing surface.
                if (surface.Width == 0)
                {
                    surface = DrawingSurface();
                    if (x <= -width || x >= surface.Width || y <= -height || y >= surface.Height) return;
                    firstColumn = Math.Max(0, -x);
                    visibleWidth = Math.Min(width, surface.Width - x) - firstColumn;
                    visibleMask = uint.MaxValue << (32 - visibleWidth);
                    writeRows = !Memory.HasWriteObservers && BitConverter.IsLittleEndian &&
                        surface.Pointer >= Memory.HeapBase && (surface.Pointer & 1) == 0;
                }
                int screenY = y + row;
                if ((uint)screenY >= (uint)surface.Height) continue;
                uint marks = ((uint)rowBits << (16 + firstColumn)) & visibleMask;
                if (marks == 0) continue;
                uint address = surface.Pointer + (uint)(screenY * surface.Width + x + firstColumn) * 2;
                if (writeRows)
                {
                    Memory.WriteGlyphRow(address, visibleWidth, marks, color);
                    continue;
                }
                for (; marks != 0; address += 2, marks <<= 1)
                {
                    if ((marks & 0x80000000u) != 0) Memory.Put16(address, color);
                }
            }
        }
    }
}
