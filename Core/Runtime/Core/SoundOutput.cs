using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NLayer;

namespace CSharpMrp
{
    public interface ISoundOutput
    {
        bool Play(int type, byte[] data, bool loop);
        bool Stop(int type);
        void Pause(bool paused);
        void StopAll();
    }

    public sealed class PcmSound
    {
        private static readonly bool UsePcmVectors = Vector.IsHardwareAccelerated &&
            (RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64);
        public readonly float[] Samples;
        public readonly int Channels;
        public readonly int SampleRate;

        private PcmSound(float[] samples, int channels, int sampleRate)
        {
            Samples = samples;
            Channels = channels;
            SampleRate = sampleRate;
        }

        public static bool TryDecode(int type, byte[] data, out PcmSound? sound)
        {
            sound = null;
            if (type == 3)
            {
                return DecodePcm(data, 0, data.Length, 1, 8000, 16, out sound);
            }
            if (type == 2)
            {
                return DecodeMp3(data, out sound);
            }
            if (type != 1 || data.Length < 44 || !Tag(data, 0, "RIFF") || !Tag(data, 8, "WAVE") ||
                (long)U32(data, 4) + 8 > data.Length)
            {
                return false;
            }
            int channels = 0, rate = 0, bits = 0, format = 0, offset = -1, length = 0;
            long end = (long)U32(data, 4) + 8;
            for (long cursor = 12; cursor + 8 <= end;)
            {
                uint rawSize = U32(data, (int)cursor + 4);
                if (rawSize > int.MaxValue)
                {
                    return false;
                }
                int size = (int)rawSize;
                long next = cursor + 8L + size;
                if (next > end)
                {
                    return false;
                }
                if (Tag(data, (int)cursor, "fmt ") && size >= 16)
                {
                    format = U16(data, (int)cursor + 8);
                    channels = U16(data, (int)cursor + 10);
                    uint rawRate = U32(data, (int)cursor + 12);
                    if (rawRate > int.MaxValue)
                    {
                        return false;
                    }
                    rate = (int)rawRate;
                    bits = U16(data, (int)cursor + 22);
                }
                else if (Tag(data, (int)cursor, "data") && offset < 0)
                {
                    offset = (int)cursor + 8;
                    length = size;
                }
                cursor = next + (size & 1);
            }
            return format == 1 && offset >= 0 &&
                DecodePcm(data, offset, length, channels, rate, bits, out sound);
        }

        private static bool DecodeMp3(byte[] data, out PcmSound? sound)
        {
            sound = null;
            if (data.Length == 0 || data.Length > 8 * 1024 * 1024) return false;
            try
            {
                using var input = new MemoryStream(data, false);
                using var decoder = new MpegFile(input);
                return TryDecodeMp3(decoder, out sound);
            }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException ||
                error is ArgumentException || error is NotSupportedException)
            {
                return false;
            }
        }

        // Borrows a freshly opened decoder positioned at the beginning of the stream.
        public static bool TryDecodeMp3(MpegFile decoder, out PcmSound? sound)
        {
            sound = null;
            try
            {
                long bytes = decoder.Length;
                if (decoder.Channels < 1 || decoder.Channels > 2 || decoder.SampleRate < 4000 ||
                    decoder.SampleRate > 192000 || bytes <= 0 || bytes > 16 * 1024 * 1024 ||
                    bytes % (4 * decoder.Channels) != 0)
                {
                    return false;
                }
                var samples = new float[(int)(bytes / 4)];
                int decoded = 0;
                while (decoded < samples.Length)
                {
                    int count = decoder.ReadSamples(samples, decoded, samples.Length - decoded);
                    if (count <= 0) return false;
                    decoded += count;
                }
                sound = new PcmSound(samples, decoder.Channels, decoder.SampleRate);
                return true;
            }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException ||
                error is ArgumentException || error is NotSupportedException)
            {
                return false;
            }
        }

        private static bool DecodePcm(byte[] data, int offset, int length, int channels, int rate, int bits,
            out PcmSound? sound)
        {
            sound = null;
            int bytesPerSample = bits / 8;
            if (channels < 1 || channels > 2 || rate < 4000 || rate > 192000 || length <= 0 ||
                length > 8 * 1024 * 1024 || (bits != 8 && bits != 16 && bits != 24 && bits != 32) ||
                length % (channels * bytesPerSample) != 0 ||
                offset < 0 || (long)offset + length > data.Length)
            {
                return false;
            }
            var samples = new float[length / bytesPerSample];
            ReadOnlySpan<byte> input = data.AsSpan(offset, length);
            switch (bits)
            {
                case 8:
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = (input[i] - 128) / 128f;
                    break;
                case 16:
                    DecodePcm16(input, samples);
                    break;
                case 24:
                    for (int i = 0, cursor = 0; i < samples.Length; i++, cursor += 3)
                    {
                        int value = input[cursor] | input[cursor + 1] << 8 | input[cursor + 2] << 16;
                        samples[i] = (value << 8 >> 8) / 8388608f;
                    }
                    break;
                default:
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(i * 4)) / 2147483648f;
                    break;
            }
            sound = new PcmSound(samples, channels, rate);
            return true;
        }

        private static void DecodePcm16(ReadOnlySpan<byte> input, Span<float> samples)
        {
            const float scale = 1f / 32768f;
            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(input.Slice(i * 2)) * scale;
                return;
            }

            ReadOnlySpan<short> source = MemoryMarshal.Cast<byte, short>(input);
            int decoded = 0;
            if (UsePcmVectors)
            {
                var vectors = MemoryMarshal.Cast<short, Vector<short>>(source);
                var output = MemoryMarshal.Cast<float, Vector<float>>(samples);
                var normalization = new Vector<float>(scale);
                for (int i = 0; i < vectors.Length; i++)
                {
                    Vector.Widen(vectors[i], out var lower, out var upper);
                    output[i * 2] = Vector.ConvertToSingle(lower) * normalization;
                    output[i * 2 + 1] = Vector.ConvertToSingle(upper) * normalization;
                }
                decoded = vectors.Length * Vector<short>.Count;
            }
            for (int i = decoded; i < samples.Length; i++)
                samples[i] = source[i] * scale;
        }

        private static int U16(byte[] data, int offset) => data[offset] | data[offset + 1] << 8;
        private static uint U32(byte[] data, int offset) => (uint)(data[offset] | data[offset + 1] << 8 |
            data[offset + 2] << 16 | data[offset + 3] << 24);
        private static bool Tag(byte[] data, int offset, string tag) =>
            data[offset] == tag[0] && data[offset + 1] == tag[1] &&
            data[offset + 2] == tag[2] && data[offset + 3] == tag[3];
    }
}
