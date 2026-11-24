#nullable enable
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace CSharpMrp.Unity
{
    public sealed class UnityFrame : IFramePresenter, IDisposable
    {
        private static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("MRP.Frame.Apply");
        // Blue occupies the low five bits of the guest's little-endian RGB565 word.
        private const GraphicsFormat PackedFormat = GraphicsFormat.B5G6R5_UNormPack16;

        public Texture2D? Texture { get; private set; }
        public bool PreferRgb565 { get; set; }
        public static bool SupportsRgb565 => BitConverter.IsLittleEndian &&
            SystemInfo.SupportsTextureFormat(TextureFormat.RGB565) &&
            SystemInfo.IsFormatSupported(PackedFormat, FormatUsage.Sample);

        public bool NeedsResize(int width, int height) =>
            Texture == null || Texture.width != width || Texture.height != height;

        public bool Present(byte[] pixels, int width, int height)
            => PresentRows(pixels, width, height, 0, height);

        public bool PresentRows(byte[] pixels, int width, int height, int firstRow, int endRow)
            => PresentRows(pixels, width, height, firstRow, endRow, rowsChanged: false);

        // The worker already compared this range with the consumer's previous RGB565 snapshot.
        public bool PresentChangedRows(byte[] pixels, int width, int height, int firstRow, int endRow)
            => PresentRows(pixels, width, height, firstRow, endRow, rowsChanged: true);

        private bool PresentRows(byte[] pixels, int width, int height, int firstRow, int endRow, bool rowsChanged)
        {
            if (firstRow < 0 || endRow < firstRow || endRow > height)
                throw new ArgumentOutOfRangeException(nameof(firstRow));
            bool recreate = NeedsResize(width, height);
            if (recreate)
            {
                Dispose();
                Texture = CreateTexture(width, height);
                firstRow = 0;
                endRow = height;
            }
            if (firstRow == endRow) return false;
            bool writeAll = recreate || rowsChanged;
            var texture = Texture!;
            ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
            bool upload = texture.format == TextureFormat.RGB565
                ? WritePackedRows(texture, words, width, height, firstRow, endRow, writeAll)
                : WriteRgbaRows(texture, words, width, height, firstRow, endRow, writeAll);
            if (upload)
            {
                using (ApplyMarker.Auto())
                    texture.Apply(false);
            }
            return upload;
        }

        private Texture2D CreateTexture(int width, int height)
        {
            if (PreferRgb565 && SupportsRgb565)
            {
                try
                {
                    var packed = new Texture2D(width, height, TextureFormat.RGB565, false, linear: true);
                    if (packed.format == TextureFormat.RGB565 && packed.graphicsFormat == PackedFormat)
                        return SetSampling(packed);
                    UnityEngine.Object.Destroy(packed);
                    Debug.LogWarning("MRP RGB565 is unavailable in the requested native format. Using RGBA32.");
                }
                catch (UnityException error)
                {
                    Debug.LogWarning("MRP RGB565 texture creation failed. Using RGBA32. " + error.Message);
                }
                PreferRgb565 = false;
            }
            return SetSampling(new Texture2D(width, height, TextureFormat.RGBA32, false));
        }

        private static Texture2D SetSampling(Texture2D texture)
        {
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private static bool WritePackedRows(Texture2D texture, ReadOnlySpan<ushort> words,
            int width, int height, int firstRow, int endRow, bool writeAll)
        {
            var target = texture.GetRawTextureData<ushort>().AsSpan();
            bool upload = false;
            for (int y = firstRow; y < endRow; y++)
            {
                var sourceRow = words.Slice(y * width, width);
                var targetRow = target.Slice((height - 1 - y) * width, width);
                if (writeAll || !sourceRow.SequenceEqual(targetRow))
                {
                    sourceRow.CopyTo(targetRow);
                    upload = true;
                }
            }
            return upload;
        }

        private static bool WriteRgbaRows(Texture2D texture, ReadOnlySpan<ushort> words,
            int width, int height, int firstRow, int endRow, bool writeAll)
        {
            bool upload = writeAll;
            var colors = texture.GetRawTextureData<uint>();
            for (int y = firstRow; y < endRow; y++)
            {
                ReadOnlySpan<ushort> sourceRow = words.Slice(y * width, width);
                int destinationRow = (height - 1 - y) * width;
                if (writeAll)
                {
                    for (int x = 0; x < sourceRow.Length; x++)
                        colors[destinationRow + x] = Decode(sourceRow[x]);
                }
                else
                {
                    for (int x = 0; x < sourceRow.Length; x++)
                    {
                        uint rgba = Decode(sourceRow[x]);
                        if (colors[destinationRow + x] != rgba)
                        {
                            colors[destinationRow + x] = rgba;
                            upload = true;
                        }
                    }
                }
            }
            return upload;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Decode(ushort word)
        {
            int rgb565 = BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
            // These multiply-and-shift pairs reproduce floor(channel * 255 / 31 or 63).
            uint red = (uint)((rgb565 >> 11) * 1053 >> 7);
            uint green = (uint)((rgb565 >> 5 & 63) * 4145 >> 10);
            uint blue = (uint)((rgb565 & 31) * 1053 >> 7);
            return BitConverter.IsLittleEndian
                ? red | green << 8 | blue << 16 | 0xff000000u
                : red << 24 | green << 16 | blue << 8 | 0xffu;
        }

        public void Dispose()
        {
            if (Texture != null)
            {
                UnityEngine.Object.Destroy(Texture);
                Texture = null;
            }
        }
    }
}
