using System;

namespace CSharpMrp
{
    // IGF1: LE height/count, then 65536 records of width + 32 bitmap bytes.
    // IGF2: LE face count=3, then IGF1 faces at 12/14/16px. Rows are byte-aligned, MSB first.
    public sealed class BitmapFont
    {
        private const int FaceBytes = 12 + 65536 * 33;
        private readonly byte[] data;
        private readonly bool bundled;
        private readonly bool uc2;

        // The vmrp platform profile uses gb16 for every selector, including ASCII at 8px.
        public static BitmapFont FromUc2(byte[] source)
        {
            return new BitmapFont(source, copy: true);
        }

        // Only MythroadFiles may supply its private, read-only mounted storage.
        internal static BitmapFont FromImmutableUc2(byte[] source)
        {
            return new BitmapFont(source, copy: false);
        }

        private BitmapFont(byte[] source, bool copy)
        {
            if (source.Length != 65536 * 32)
            {
                throw new RuntimeFault("invalid gb16.uc2 font length");
            }
            uc2 = true;
            data = copy ? (byte[])source.Clone() : source;
        }

        public BitmapFont(byte[] source)
        {
            if (source.Length >= 4 && source[0] == 'I' && source[1] == 'G' && source[2] == 'F' && source[3] == '2')
            {
                if (source.Length != 8 + 3 * FaceBytes || source[4] != 3 || source[5] != 0 || source[6] != 0 || source[7] != 0)
                {
                    throw new RuntimeFault("invalid IGF2 font bundle");
                }
                for (int index = 0; index < 3; index++)
                {
                    var face = source.AsSpan(8 + index * FaceBytes, FaceBytes);
                    ValidateFace(face);
                    if (face[4] != 12 + index * 2)
                    {
                        throw new RuntimeFault("invalid IGF2 face order");
                    }
                }
                bundled = true;
            }
            else ValidateFace(source);
            data = (byte[])source.Clone();
        }

        private static void ValidateFace(ReadOnlySpan<byte> source)
        {
            if (source.Length != FaceBytes || source[0] != 'I' || source[1] != 'G' ||
                source[2] != 'F' || source[3] != '1' || (source[4] != 12 && source[4] != 14 && source[4] != 16) || source[5] != 0 ||
                source[6] != 0 || source[7] != 0 || source[8] != 0 || source[9] != 0 ||
                source[10] != 1 || source[11] != 0)
            {
                throw new RuntimeFault("invalid IGF1 bitmap font");
            }
            for (int character = 0; character < 65536; character++)
            {
                byte width = source[12 + character * 33];
                if (width != source[4] / 2 && width != source[4])
                {
                    throw new RuntimeFault("invalid bitmap font glyph width");
                }
            }
        }

        internal void Measure(ushort character, uint selector, out int width, out int height)
        {
            GlyphRows(character, selector, out width, out height, out _);
        }

        public byte[] Glyph(ushort character, out int width, out int height)
        {
            return Glyph(character, 2, out width, out height);
        }

        public byte[] Glyph(ushort character, uint selector, out int width, out int height)
        {
            var rows = GlyphRows(character, selector, out width, out height, out int stride);
            if (stride == (width + 7) / 8) return rows.ToArray();
            // A narrow UC2 glyph uses the first byte of each two-byte row.
            var packed = new byte[height];
            for (int row = 0; row < height; row++) packed[row] = rows[row * stride];
            return packed;
        }

        internal ReadOnlySpan<byte> GlyphRows(ushort character, uint selector, out int width, out int height, out int stride)
        {
            if (selector > 2) throw new RuntimeFault("unsupported font selector");
            if (uc2)
            {
                width = character < 128 ? 8 : 16;
                height = 16;
                stride = 2;
                return data.AsSpan(character * 32, 32);
            }
            int face = bundled ? 8 + (int)selector * FaceBytes : 0;
            int offset = face + 12 + character * 33;
            width = data[offset];
            height = data[face + 4];
            stride = (width + 7) / 8;
            return data.AsSpan(offset + 1, height * stride);
        }
    }
}
