using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using VectorMath = System.Numerics.Vector;

namespace CSharpMrp.Desktop;

internal sealed class DesktopFrame : Control, IFramePresenter
{
    internal Bitmap? FrameBitmap { get; private set; }

    public DesktopFrame()
    {
        BackColor = Color.Black;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
    }

    private Rectangle ImageBounds(Bitmap bitmap)
    {
        if (ClientSize.Width == 0 || ClientSize.Height == 0) return Rectangle.Empty;
        double scale = Math.Min((double)ClientSize.Width / bitmap.Width, (double)ClientSize.Height / bitmap.Height);
        int width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        int height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (FrameBitmap is not { } bitmap) return;
        Rectangle bounds = ImageBounds(bitmap);
        if (bounds.IsEmpty) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(bitmap, bounds, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
    }

    public bool TryMapTouch(Point position, bool clamp, out Point guest)
    {
        guest = default;
        if (FrameBitmap is not { } bitmap) return false;
        Rectangle bounds = ImageBounds(bitmap);
        if (bounds.IsEmpty || (!clamp && !bounds.Contains(position))) return false;
        int x = Math.Clamp(position.X, bounds.Left, bounds.Right - 1) - bounds.Left;
        int y = Math.Clamp(position.Y, bounds.Top, bounds.Bottom - 1) - bounds.Top;
        guest = new Point(x * bitmap.Width / bounds.Width, y * bitmap.Height / bounds.Height);
        // A drag beyond the last display pixel must reach the guest edge even when downscaled.
        if (position.X >= bounds.Right) guest.X = bitmap.Width - 1;
        if (position.Y >= bounds.Bottom) guest.Y = bitmap.Height - 1;
        return true;
    }

    public bool Present(byte[] pixels, int width, int height) => PresentRows(pixels, width, height, 0, height);

    internal unsafe bool PresentRows(byte[] pixels, int width, int height, int firstRow, int endRow)
    {
        if (FrameBitmap?.Size != new Size(width, height))
        {
            var replacement = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            FrameBitmap?.Dispose();
            FrameBitmap = replacement;
            firstRow = 0;
            endRow = height;
        }
        if (firstRow == endRow) return false;

        int rowCount = endRow - firstRow;
        ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(
            pixels.AsSpan(firstRow * width * 2, rowCount * width * 2));
        var bitmap = FrameBitmap!;
        var frame = bitmap.LockBits(new Rectangle(0, firstRow, width, rowCount),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * sizeof(uint);
            if (frame.Stride == rowBytes)
            {
                ConvertRgb565(source, new Span<uint>(frame.Scan0.ToPointer(), rowCount * width));
            }
            else
            {
                for (int row = 0; row < rowCount; row++)
                {
                    var target = new Span<uint>((byte*)frame.Scan0 + row * frame.Stride, width);
                    ConvertRgb565(source.Slice(row * width, width), target);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(frame);
        }
        Invalidate();
        return true;
    }

    private static void ConvertRgb565(ReadOnlySpan<ushort> source, Span<uint> target)
    {
        int i = 0;
        int batch = Vector<ushort>.Count;
        if (BitConverter.IsLittleEndian && VectorMath.IsHardwareAccelerated && source.Length >= batch)
        {
            // For the complete channel ranges, these products match floor(value * 255 / max)
            // while fitting in a 16-bit lane.
            var scale5 = new Vector<ushort>(1053);
            var scale6 = new Vector<ushort>(259);
            var bias6 = new Vector<ushort>(3);
            var mask5 = new Vector<ushort>(31);
            var mask6 = new Vector<ushort>(63);
            var opaque = new Vector<ushort>(0xff00);
            for (; i <= source.Length - batch; i += batch)
            {
                var rgb = new Vector<ushort>(source.Slice(i));
                var red = ((rgb >> 11) * scale5) >> 7;
                var green = ((((rgb >> 5) & mask6) * scale6) + bias6) >> 6;
                var blue = ((rgb & mask5) * scale5) >> 7;
                VectorMath.Widen(opaque | red, out var upperFirst, out var upperSecond);
                VectorMath.Widen((green << 8) | blue, out var lowerFirst, out var lowerSecond);
                ((upperFirst << 16) | lowerFirst).CopyTo(target.Slice(i));
                ((upperSecond << 16) | lowerSecond).CopyTo(target.Slice(i + Vector<uint>.Count));
            }
        }
        for (; i < source.Length; i++)
        {
            uint color = BitConverter.IsLittleEndian ? source[i] : BinaryPrimitives.ReverseEndianness(source[i]);
            target[i] = 0xff000000u | (color >> 11) * 255 / 31 << 16 |
                (color >> 5 & 63) * 255 / 63 << 8 | (color & 31) * 255 / 31;
        }
    }

    internal void Clear()
    {
        FrameBitmap?.Dispose();
        FrameBitmap = null;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FrameBitmap?.Dispose();
            FrameBitmap = null;
        }
        base.Dispose(disposing);
    }
}
