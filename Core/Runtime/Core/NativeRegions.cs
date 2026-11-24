using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void DrawRegion(uint context, uint image, int sx, int sy, int width, int height,
            int transform, int x, int y, int anchor)
        {
            if (context == 0 || image == 0)
            {
                return;
            }
            Memory.Check(context, 72);
            Memory.Check(image, 12);
            int sourceWidth = (int)Memory.U16(image + 4);
            int sourceHeight = (int)Memory.U16(image + 6);
            if (width < 0 || height < 0 || sx < 0 || sy < 0 ||
                (long)sx + width > sourceWidth || (long)sy + height > sourceHeight || transform < 0 || transform > 7)
            {
                throw new RuntimeFault("invalid image region or transform");
            }
            if (width == 0 || height == 0)
            {
                return;
            }
            int targetWidth = transform < 4 ? width : height;
            int targetHeight = transform < 4 ? height : width;
            int originX = unchecked(x + (int)Memory.U32(context + 24));
            int originY = unchecked(y + (int)Memory.U32(context + 28));
            if (anchor != 0)
            {
                switch (anchor & 13)
                {
                    case 4:
                        break;
                    case 1:
                        originX = unchecked(originX - targetWidth / 2);
                        break;
                    case 8:
                        originX = unchecked(originX - targetWidth);
                        break;
                    default:
                        throw new RuntimeFault("invalid image horizontal anchor");
                }
                switch (anchor & 114)
                {
                    case 16:
                        break;
                    case 2:
                        originY = unchecked(originY - targetHeight / 2);
                        break;
                    case 32:
                        originY = unchecked(originY - targetHeight);
                        break;
                    case 64:
                        originY = unchecked(originY - targetHeight / 2 - 3);
                        break;
                    default:
                        throw new RuntimeFault("invalid image vertical anchor");
                }
            }
            long left = Math.Max(originX, (int)Memory.U32(context));
            long top = Math.Max(originY, (int)Memory.U32(context + 4));
            long right = Math.Min(unchecked(originX + targetWidth), (int)Memory.U32(context + 16));
            long bottom = Math.Min(unchecked(originY + targetHeight), (int)Memory.U32(context + 20));
            if (right <= left || bottom <= top)
            {
                return;
            }
            int stride = (int)Memory.U32(context + 48);
            int surfaceHeight = (int)Memory.U32(context + 52);
            int rowStep = (int)Memory.U32(context + 64);
            if (left < 0 || top < 0 || right > stride || bottom > surfaceHeight || rowStep <= 0)
            {
                throw new RuntimeFault("image region outside destination surface");
            }
            uint destination = Memory.U32(context + 44);
            long lastPixel = top * stride + left + (bottom - top - 1) * rowStep + right - left;
            if (lastPixel > uint.MaxValue / 2)
            {
                throw new RuntimeFault("image destination address overflow");
            }
            Memory.Check(destination, (uint)lastPixel * 2);
            long sourceBytes = (long)sourceWidth * sourceHeight * 2;
            if (sourceBytes > Package.MaxBytes)
            {
                throw new RuntimeFault("image source size limit");
            }
            uint source = Memory.U32(image);
            Memory.Check(source, (uint)sourceBytes);
            int u = (int)(left - originX);
            int v = (int)(top - originY);
            int sourceX = transform < 4 ? u : v;
            int sourceY = transform < 4 ? v : u;
            int horizontalDirection = (transform & 2) == 0 ? 1 : -1;
            int verticalDirection = (transform & 1) == 0 ? 1 : -1;
            if (horizontalDirection < 0)
            {
                sourceX = width - 1 - sourceX;
            }
            if (verticalDirection < 0)
            {
                sourceY = height - 1 - sourceY;
            }
            long firstSourcePixel = (long)(sy + sourceY) * sourceWidth + sx + sourceX;
            int sourceColumnStep = transform < 4 ? horizontalDirection : verticalDirection * (short)sourceWidth;
            int sourceRowStep = transform < 4 ? verticalDirection * (short)sourceWidth : horizontalDirection;
            long rowExtent = (bottom - top - 1) * sourceRowStep;
            long columnExtent = (right - left - 1) * sourceColumnStep;
            long minimumSource = firstSourcePixel + Math.Min(0, rowExtent) + Math.Min(0, columnExtent);
            long maximumSource = firstSourcePixel + Math.Max(0, rowExtent) + Math.Max(0, columnExtent);
            if (minimumSource < 0 || maximumSource >= sourceBytes / 2)
            {
                throw new RuntimeFault("image source stride outside image");
            }
            uint transparency = Memory.U8(image + 8);
            ushort key = transparency == 1 ? (ushort)Memory.U16(source) : (ushort)Memory.U16(image + 10);
            for (long row = top; row < bottom; row++)
            {
                if (transparency == 0 && transform < 2)
                {
                    uint input = source + (uint)(firstSourcePixel + (row - top) * sourceRowStep) * 2;
                    uint output = destination + (uint)(top * stride + (row - top) * rowStep + left) * 2;
                    Memory.Copy(output, input, (uint)(right - left) * 2, false);
                    continue;
                }
                for (long column = left; column < right; column++)
                {
                    long sourcePixel = firstSourcePixel + (row - top) * sourceRowStep + (column - left) * sourceColumnStep;
                    ushort color = (ushort)Memory.U16(source + (uint)sourcePixel * 2);
                    if (transparency == 0 || color != key)
                    {
                        uint address = destination + (uint)(top * stride + (row - top) * rowStep + column) * 2;
                        Memory.Put16(address, color);
                    }
                }
            }
        }
    }
}
