namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private (uint Pointer, int Width, int Height) DrawingSurface()
        {
            uint pointer = Memory.U32(Memory.U32(table + 91 * 4));
            uint width = Memory.U32(Memory.U32(table + 92 * 4));
            uint height = Memory.U32(Memory.U32(table + 93 * 4));
            uint depth = Memory.U32(Memory.U32(table + 94 * 4));
            if (width == 0 || height == 0 || width > 4096 || height > 4096 || depth != 16)
            {
                throw new RuntimeFault("invalid guest drawing surface dimensions or depth");
            }
            Memory.Check(pointer, width * height * 2);
            return (pointer, (int)width, (int)height);
        }

        private void RefreshDrawingSurface(int x, int y, int width, int height)
        {
            var surface = DrawingSurface();
            Blit(surface.Pointer, x, y, width, height, surface.Width, surface.Height);
        }

        private bool HasStableDrawingSurface(uint pointer)
        {
            // The ABI table is static, but the guest can redirect its display fields into the target.
            uint heap = Memory.HeapBase;
            if (Memory.HasWriteObservers || pointer < heap) return false;
            for (uint field = 91; field <= 94; field++)
            {
                if (Memory.U32(table + field * 4) > heap - 4) return false;
            }
            return true;
        }
    }
}
