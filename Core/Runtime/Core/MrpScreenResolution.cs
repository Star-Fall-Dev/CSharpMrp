#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CSharpMrp
{
    public sealed class MrpScreenResolution
    {
        public const int DefaultWidth = 240;
        public const int DefaultHeight = 320;

        public static readonly IReadOnlyList<MrpScreenResolution> SupportedResolutions = Array.AsReadOnly(new[]
        {
            new MrpScreenResolution(DefaultWidth, DefaultHeight, "session"),
            new MrpScreenResolution(320, 480, "session"),
            new MrpScreenResolution(176, 220, "session"),
            new MrpScreenResolution(240, 400, "session"),
            new MrpScreenResolution(128, 160, "session"),
            new MrpScreenResolution(128, 128, "session"),
            new MrpScreenResolution(480, 800, "session"),
            new MrpScreenResolution(272, 480, "session")
        });

        public int Width { get; }
        public int Height { get; }
        public string Source { get; }
        public bool IsKnown => Source != "default-unverified";

        public MrpScreenResolution(int width, int height, string source)
        {
            Width = width;
            Height = height;
            Source = source;
        }

        public override string ToString() => $"{Width} × {Height}";

        public static bool IsValid(int width, int height) =>
            width > 0 && width <= 1024 && height > 0 && height <= 1024;

        public static bool IsSupported(int width, int height)
        {
            foreach (var size in SupportedResolutions)
            {
                if (size.Width == width && size.Height == height) return true;
            }
            return false;
        }

        public static (int Width, int Height) ReadHeaderDimensions(ReadOnlySpan<byte> header)
        {
            if (header.Length < 240) return (0, 0);
            return (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(204, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(206, 2)));
        }

        public static MrpScreenResolution Resolve(int declaredWidth, int declaredHeight, string? originalName = null)
        {
            if (IsValid(declaredWidth, declaredHeight))
            {
                return new MrpScreenResolution(declaredWidth, declaredHeight, "package-header");
            }
            if (!string.IsNullOrEmpty(originalName))
            {
                string name = Path.GetFileNameWithoutExtension(originalName.Replace('\\', '/'));
                foreach (var size in SupportedResolutions)
                {
                    string pattern = "(?<![0-9])" + size.Width + "[xX*]?" + size.Height + "(?![0-9])";
                    if (Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant))
                    {
                        return new MrpScreenResolution(size.Width, size.Height, "filename");
                    }
                }
            }
            return new MrpScreenResolution(DefaultWidth, DefaultHeight, "default-unverified");
        }
    }
}
