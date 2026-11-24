using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint DrawMarkedBitmap(uint bitmap, uint destination, int stride, int destinationHeight,
            int sx, int sy, int width, int height, int x, int y)
        {
            if (bitmap == 0)
            {
                return uint.MaxValue;
            }
            Memory.Check(bitmap, 32);
            int sourceWidth = (int)Memory.U32(bitmap + 4);
            int sourceHeight = (int)Memory.U32(bitmap + 8);
            int sourceStride = (int)Memory.U32(bitmap + 12);
            int markCount = (int)Memory.U32(bitmap + 24);
            if (sourceWidth < 0 || sourceHeight < 0 || sourceStride < sourceWidth || sourceStride > 4096 ||
                sourceHeight > 4096 || stride <= 0 || destinationHeight < 0 || markCount < 0 || markCount > 4096 ||
                sx < 0 || sy < 0 || width < 0 || height < 0 || (long)sx + width > sourceWidth || (long)sy + height > sourceHeight)
            {
                throw new RuntimeFault("invalid marked bitmap geometry");
            }
            long firstColumn = Math.Max(0L, -(long)x);
            long firstRow = Math.Max(0L, -(long)y);
            long endColumn = Math.Min(width, (long)stride - x);
            long endRow = Math.Min(height, (long)destinationHeight - y);
            if (endColumn <= firstColumn || endRow <= firstRow || markCount == 0)
            {
                return 0;
            }
            uint source = Memory.U32(bitmap);
            uint marks = Memory.U32(bitmap + 20);
            Memory.Check(source, (uint)(sourceStride * sourceHeight * 2));
            ulong matrixLength = (ulong)markCount * (uint)sourceHeight * 4;
            ulong destinationLength = (ulong)(y + endRow - 1) * (uint)stride * 2 + (ulong)(x + endColumn) * 2;
            if (matrixLength > uint.MaxValue || destinationLength > uint.MaxValue)
            {
                throw new RuntimeFault("marked bitmap address overflow");
            }
            Memory.Check(marks, (uint)matrixLength);
            Memory.Check(destination, (uint)destinationLength);
            // Runs may share storage with destination pixels. Read each descriptor only when it is consumed.
            long sourceRow = source + ((long)sy + firstRow) * sourceStride * 2;
            long markRow = marks + ((long)sy + firstRow) * markCount * 4;
            for (int row = (int)firstRow; row < endRow; row++)
            {
                int tail = -1;
                for (int mark = 0; mark < ReadMarkCount(); mark++)
                {
                    uint entry = Address(markRow + (long)mark * 4, 4);
                    int start = (short)Memory.U16(entry);
                    if (start < 0)
                    {
                        break;
                    }
                    int length = (short)Memory.U16(entry + 2);
                    if (mark == ReadMarkCount() - 1)
                    {
                        bool hasTail = (length & 0x4000) != 0;
                        length &= 0x3fff;
                        tail = hasTail ? start + length : -1;
                    }
                    if (length < 0)
                    {
                        continue;
                    }
                    DrawRun(start, start + length, false);
                }
                if (tail >= 0)
                {
                    DrawRun(tail, sx + (int)endColumn, true);
                }

                void DrawRun(int start, int end, bool keyed)
                {
                    start = Math.Max(start, sx + (int)firstColumn);
                    end = Math.Min(end, sx + (int)endColumn);
                    if (end > start)
                    {
                        uint input = Address(sourceRow + (long)start * 2, (uint)(end - start) * 2);
                        uint output = destination + (uint)((y + row) * stride + x + start - sx) * 2;
                        if (!keyed)
                        {
                            uint bytes = (uint)(end - start) * 2;
                            if ((input & 3) != 0 && (output & 3) != 0)
                            {
                                Memory.Put16(output, Memory.U16(input));
                                input += 2;
                                output += 2;
                                bytes -= 2;
                            }
                            Memory.Copy(output, input, bytes, false);
                        }
                        else
                        {
                            for (int pixel = 0; pixel < end - start; pixel++)
                            {
                                uint color = Memory.U16(input + (uint)pixel * 2);
                                if (color != Memory.U16(bitmap + 16))
                                {
                                    Memory.Put16(output + (uint)pixel * 2, color);
                                }
                            }
                        }
                    }
                }
                sourceRow += (long)(int)Memory.U32(bitmap + 12) * 2;
                markRow += (long)ReadMarkCount() * 4;
            }
            return 0;

            int ReadMarkCount()
            {
                int current = (int)Memory.U32(bitmap + 24);
                if (current < 0 || current > 4096)
                {
                    throw new RuntimeFault("invalid marked bitmap live mark count");
                }
                return current;
            }

            uint Address(long pointer, uint size)
            {
                if (pointer < 0 || pointer > uint.MaxValue)
                {
                    throw new RuntimeFault("marked bitmap address overflow");
                }
                Memory.Check((uint)pointer, size);
                return (uint)pointer;
            }
        }

        private void DrawTransformedBitmap(uint sourceDescriptor, uint destinationDescriptor, int width, int height,
            uint matrix, ushort transparent)
        {
            Memory.Check(sourceDescriptor, 12);
            Memory.Check(destinationDescriptor, 12);
            Memory.Check(matrix, 10);
            int sourceWidth = (int)Memory.U16(sourceDescriptor + 4);
            int sourceHeight = (int)Memory.U16(sourceDescriptor + 6);
            int sx = (int)Memory.U16(sourceDescriptor + 8);
            int sy = (int)Memory.U16(sourceDescriptor + 10);
            int targetWidth = (int)Memory.U16(destinationDescriptor + 4);
            int targetHeight = (int)Memory.U16(destinationDescriptor + 6);
            int centerX = (short)(Memory.U16(destinationDescriptor + 8) + width / 2);
            int centerY = (short)(Memory.U16(destinationDescriptor + 10) + height / 2);
            int a = (short)Memory.U16(matrix);
            int b = (short)Memory.U16(matrix + 2);
            int c = (short)Memory.U16(matrix + 4);
            int d = (short)Memory.U16(matrix + 6);
            if (sx + width > sourceWidth || sy + height > sourceHeight || width > 4096 || height > 4096 ||
                (long)sourceWidth * sourceHeight * 2 > Package.MaxBytes)
            {
                throw new RuntimeFault("invalid transformed bitmap source");
            }
            int determinant = unchecked(a * d - b * c);
            if (determinant == 0)
            {
                throw new RuntimeFault("singular bitmap transform");
            }
            if (width == 0 || height == 0)
            {
                return;
            }
            uint destination = Memory.U32(destinationDescriptor);
            ulong targetBytes = (ulong)targetWidth * (uint)targetHeight * 2;
            if (targetBytes > uint.MaxValue)
            {
                throw new RuntimeFault("transformed bitmap destination size overflow");
            }
            Memory.Check(destination, (uint)targetBytes);
            uint source = Memory.U32(sourceDescriptor);
            uint sourceBytes = (uint)(sourceWidth * sourceHeight * 2);
            Memory.Check(source, sourceBytes);
            uint targetEnd = destination + (uint)targetBytes;
            // If stores can change either descriptor, keep reading fields as the reference does.
            bool liveSource = Memory.HasWriteObservers ||
                (sourceDescriptor < targetEnd && sourceDescriptor + 12 > destination) ||
                (destinationDescriptor < targetEnd && destinationDescriptor + 12 > destination);
            bool copyIdentityRows = !liveSource && a == 256 && d == 256 && b == 0 && c == 0 &&
                (source >= targetEnd || destination >= source + sourceBytes);
            int halfWidth = width / 2;
            int halfSourceHeight = height / 2;
            int widthExtent = unchecked(width * determinant) >> 9;
            int heightExtent = unchecked(height * determinant) >> 9;
            int horizontalStep = d << 8;
            int verticalStep = -c << 8;
            int halfHeight = unchecked((short)((Math.Abs(c) * width + Math.Abs(d) * height) >> 9));
            int firstRow = unchecked((short)Math.Max(unchecked((short)-halfHeight), -centerY));
            int lastRow = unchecked((short)Math.Min(halfHeight, targetHeight - centerY));
            if (firstRow < lastRow && (firstRow + centerY < 0 || lastRow + centerY > targetHeight))
            {
                throw new RuntimeFault("transformed bitmap clipped row overflow");
            }
            for (int row = firstRow; row < lastRow; row++)
            {
                int rowWidth = (int)Memory.U16(destinationDescriptor + 4);
                int firstColumn = int.MinValue;
                int lastColumn = int.MaxValue;
                Intersect(d, unchecked(b * row), widthExtent);
                Intersect(c, unchecked(a * row), heightExtent);
                firstColumn = unchecked((short)Math.Max(unchecked((short)firstColumn), -centerX));
                lastColumn = unchecked((short)Math.Min(unchecked((short)lastColumn), rowWidth - centerX));
                if (firstColumn < lastColumn && (firstColumn + centerX < 0 || lastColumn + centerX > rowWidth))
                {
                    throw new RuntimeFault("transformed bitmap clipped column overflow");
                }
                int operation = (int)Memory.U16(matrix + 8);
                if (operation != 2 && operation != 6)
                {
                    continue;
                }
                long rowAddress = Memory.U32(destinationDescriptor) + ((long)(row + centerY) * rowWidth + centerX) * 2;
                if (copyIdentityRows && operation == 2 && firstColumn < lastColumn)
                {
                    // With an identity matrix, the clipped screen row maps to consecutive source pixels.
                    long sourcePixel = (long)(sy + row + halfSourceHeight) * sourceWidth + sx + firstColumn + halfWidth;
                    Memory.Copy(PixelAddress(rowAddress + (long)firstColumn * 2),
                        PixelAddress(source + sourcePixel * 2), (uint)(lastColumn - firstColumn) * 2, preserveOverlap: true);
                    continue;
                }
                int horizontal = unchecked((d * firstColumn - b * row) << 8);
                int vertical = unchecked((a * row - c * firstColumn) << 8);
                for (int column = firstColumn; column < lastColumn;
                    column++, horizontal = unchecked(horizontal + horizontalStep), vertical = unchecked(vertical + verticalStep))
                {
                    int u = unchecked(Divide(horizontal, determinant) + halfWidth);
                    int v = unchecked(Divide(vertical, determinant) + halfSourceHeight);
                    if (u < 0 || v < 0 || u >= width || v >= height)
                    {
                        continue;
                    }
                    long sourceAddress = liveSource
                        ? Memory.U32(sourceDescriptor) + ((long)(Memory.U16(sourceDescriptor + 10) + v) *
                            Memory.U16(sourceDescriptor + 4) + Memory.U16(sourceDescriptor + 8) + u) * 2
                        : source + ((long)(sy + v) * sourceWidth + sx + u) * 2;
                    ushort color = (ushort)Memory.U16(PixelAddress(sourceAddress));
                    if (operation == 2 || color != transparent)
                    {
                        Memory.Put16(PixelAddress(rowAddress + (long)column * 2), color);
                    }
                }

                void Intersect(int divisor, int midpoint, int extent)
                {
                    if (divisor == 0)
                    {
                        firstColumn = Math.Max(firstColumn, -999);
                        lastColumn = Math.Min(lastColumn, 999);
                        return;
                    }
                    int first = Divide(unchecked(midpoint - extent), divisor);
                    int second = Divide(unchecked(midpoint + extent), divisor);
                    firstColumn = Math.Max(firstColumn, Math.Min(first, second));
                    lastColumn = Math.Min(lastColumn, Math.Max(first, second));
                }
            }

            // The wrapped quotient is also defined for int.MinValue / -1.
            static int Divide(int value, int divisor) => divisor == -1 ? unchecked(-value) : value / divisor;

            static uint PixelAddress(long address)
            {
                if (address < 0 || address > uint.MaxValue)
                {
                    throw new RuntimeFault("transformed bitmap live address overflow");
                }
                return (uint)address;
            }
        }
    }
}
