using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint lcdOrientation;

        private uint SetLcdOrientation(uint orientation)
        {
            if (orientation > 3) return 1;
            // The host displays the oriented logical canvas and sends input in
            // that same coordinate system. The SDK may then repeat the dimensions
            // through commands 401/406; both screen-info APIs must already agree.
            if (((orientation ^ lcdOrientation) & 1) != 0)
            {
                int frames = Frames;
                SetResolution(Height, Width);
                Frames = frames; // Changing orientation alone is not a guest frame.
            }
            lcdOrientation = orientation;
            Record($"LCD orientation={orientation}, canvas={Width}x{Height}");
            return 0;
        }

        // Called on the host thread between guest invocations. Never restarts the VM.
        public void SetResolution(int width, int height)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeSession));
            }
            if (!MrpScreenResolution.IsValid(width, height))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            if (width == Width && height == Height)
            {
                return;
            }

            uint bitmap = Memory.U32(table + 95 * 4) + 30 * 16;
            uint type = Memory.U32(bitmap + 8);
            var surface = DrawingSurface();
            uint pointer = Memory.U32(bitmap + 12);
            uint capacity;
            if (type == 0 && pointer == Framebuffer && surface.Pointer == Framebuffer)
            {
                capacity = framebufferCapacity;
            }
            else if (type == 1 && pointer == surface.Pointer &&
                Memory.TryGetAllocationRemaining(pointer, out uint remaining))
            {
                capacity = Math.Min(remaining, Memory.U32(bitmap + 4));
            }
            else
            {
                throw new InvalidOperationException("当前游戏的绘图缓冲区不支持安全切换分辨率。");
            }
            uint length = (uint)(width * height * 2);
            if (length > capacity || (long)surface.Width * surface.Height * 2 > capacity)
            {
                throw new InvalidOperationException("当前游戏借用的屏幕缓冲区容量不足，已保留原分辨率。");
            }
            Memory.Check(pointer, length);
            uint[] cells = new uint[4];
            for (uint index = 0; index < cells.Length; index++)
            {
                cells[index] = Memory.U32(table + (91 + index) * 4);
                Memory.Check(cells[index], 4);
            }
            Memory.Check(bitmap, 16);

            // Complete allocations and validation before touching guest state.
            byte[] nextDrawing = new byte[length];
            byte[] nextDisplay = new byte[length];
            CopyOverlap(Memory.ReadSpan(pointer, (uint)(surface.Width * surface.Height * 2)),
                surface.Width, surface.Height, nextDrawing, width, height);
            CopyOverlap(displayedPixels, Width, Height, nextDisplay, width, height);

            Memory.Write(pointer, nextDrawing);
            Memory.Put32(cells[0], pointer);
            Memory.Put32(cells[1], (uint)width);
            Memory.Put32(cells[2], (uint)height);
            Memory.Put32(cells[3], 16);
            Memory.Put16(bitmap, (uint)width);
            Memory.Put16(bitmap + 2, (uint)height);
            Memory.Put32(bitmap + 4, capacity);
            Width = width;
            Height = height;
            displayedPixels = nextDisplay;
            firstDirtyRow = 0;
            endDirtyRow = height;
            Frames++;
        }

        private static void CopyOverlap(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
            byte[] destination, int width, int height)
        {
            int bytes = Math.Min(sourceWidth, width) * 2;
            for (int row = 0; row < Math.Min(sourceHeight, height); row++)
            {
                source.Slice(row * sourceWidth * 2, bytes).CopyTo(destination.AsSpan(row * width * 2, bytes));
            }
        }
    }
}
