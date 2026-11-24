#nullable enable
using System;
using System.IO;
using AudioSynthesis;
using AudioSynthesis.Bank;
using AudioSynthesis.Midi;
using AudioSynthesis.Sequencer;
using AudioSynthesis.Synthesis;
using NLayer;

namespace CSharpMrp
{
    /// <summary>Prepares guest audio outside the host's audio callback.</summary>
    public sealed class MrpSoundDecoder
    {
        public const int DefaultMidiRate = 44100;
        private const string BankResource = "CSharpMrp.Audio.GMBank.bank";
        private const int MidiChannels = 2;
        private const int MidiBlockSize = 1024;
        private readonly Lazy<PatchBank> bank;

        public MrpSoundDecoder() : this(() => typeof(MrpSoundDecoder).Assembly.GetManifestResourceStream(BankResource)) { }

        public MrpSoundDecoder(Func<Stream?> openMidiBank)
        {
            bank = new Lazy<PatchBank>(() =>
            {
                using var stream = openMidiBank() ?? throw new InvalidDataException("GMBank.bank is missing.");
                var patches = new PatchBank(new SoundResource("GMBank.bank", stream));
                if (patches.GetPatch(0, 0) == null || patches.GetPatch(128, 0) == null)
                    throw new InvalidDataException("GMBank.bank is missing GM instruments or drums.");
                return patches;
            });
        }

        public void PrepareBank() => _ = bank.Value;

        public MrpSound? Prepare(int type, byte[] data, int midiRate = DefaultMidiRate)
        {
            try
            {
                switch (type)
                {
                    case 0:
                        return PrepareMidi(data, midiRate);
                    case 2:
                        return PrepareMp3(data);
                    default:
                        return PcmSound.TryDecode(type, data, out var pcm) && pcm != null ? new BufferedSound(pcm) : null;
                }
            }
            catch (Exception error) when (IsMediaError(error))
            {
                return null;
            }
        }

        private MrpSound? PrepareMidi(byte[] data, int rate)
        {
            if (data.Length < 14 || data.Length > 1024 * 1024 ||
                data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd' ||
                data[4] != 0 || data[5] != 0 || data[6] != 0 || data[7] != 6 ||
                data[10] >= 128 || (data[10] == 0 && data[11] == 0))
                return null;

            using var stream = new MemoryStream(data, false);
            var midi = new MidiFile(new SoundResource("sound.mid", stream));
            bool hasEvents = false;
            foreach (var track in midi.Tracks)
            {
                if (track.EndTime < 0) return null;
                hasEvents |= track.MidiEvents.Length != 0;
            }
            if (!hasEvents || midi.Division == 0) return null;

            var synth = new Synthesizer(rate, MidiChannels, MidiBlockSize, 1);
            synth.LoadBank(bank.Value);
            var sequencer = new MidiFileSequencer(synth);
            if (!sequencer.LoadMidi(midi) || sequencer.EndTime <= 0) return null;

            int blockFrames = synth.WorkingBuffer.Length / MidiChannels;
            int frames = checked((int)(((long)sequencer.EndTime + blockFrames - 1) / blockFrames * blockFrames));
            return new MidiSound(synth, sequencer, frames);
        }

        private static MrpSound? PrepareMp3(byte[] data)
        {
            if (data.Length == 0 || data.Length > 32 * 1024 * 1024) return null;
            MemoryStream? stream = new MemoryStream(data, false);
            MpegFile? decoder = null;
            try
            {
                decoder = new MpegFile(stream);
                int channels = decoder.Channels;
                int rate = decoder.SampleRate;
                long bytes = decoder.Length;
                if (channels < 1 || channels > 2 || rate < 4000 || rate > 192000 || bytes <= 0 ||
                    bytes % (4 * channels) != 0 || bytes / (4 * channels) > int.MaxValue)
                    return null;

                if (bytes <= 16 * 1024 * 1024 && data.Length <= 8 * 1024 * 1024)
                    return PcmSound.TryDecodeMp3(decoder, out var pcm) && pcm != null ? new BufferedSound(pcm) : null;

                // Confirm that the stream can produce samples before replacing the current sound.
                var firstFrame = new float[channels];
                if (decoder.ReadSamples(firstFrame, 0, channels) != channels) return null;
                decoder.Position = 0;
                var sound = new Mp3Sound(decoder, stream, rate, channels, (int)(bytes / (4 * channels)));
                decoder = null;
                stream = null;
                return sound;
            }
            finally
            {
                decoder?.Dispose();
                stream?.Dispose();
            }
        }

        // CSharpSynth reports several malformed MIDI structures as plain Exception.
        internal static bool IsMediaError(Exception error) => error.GetType() == typeof(Exception) ||
            error is IOException || error is InvalidDataException || error is ArgumentException || error is NotSupportedException ||
            error is InvalidOperationException || error is IndexOutOfRangeException || error is OverflowException;

        private sealed class SoundResource : IResource
        {
            private readonly string name;
            private readonly Stream stream;

            public SoundResource(string name, Stream stream)
            {
                this.name = name;
                this.stream = stream;
            }

            public bool ReadAllowed() => true;
            public bool WriteAllowed() => false;
            public bool DeleteAllowed() => false;
            public string GetName() => name;
            public Stream OpenResourceForRead() => stream;
            public Stream OpenResourceForWrite() => throw new NotSupportedException();
            public void DeleteResource() => throw new NotSupportedException();
        }

        private sealed class BufferedSound : MrpSound
        {
            public override float[] BufferedSamples { get; }
            private int position;

            public BufferedSound(PcmSound pcm) : base(pcm.SampleRate, pcm.Channels, pcm.Samples.Length / pcm.Channels)
            {
                BufferedSamples = pcm.Samples;
                ApplyGain(BufferedSamples, 0, BufferedSamples.Length);
            }

            protected override int ReadCore(float[] destination, int offset, int count)
            {
                int read = Math.Min(count, BufferedSamples.Length - position);
                Array.Copy(BufferedSamples, position, destination, offset, read);
                position += read;
                return read;
            }

            protected override void RewindCore() => position = 0;
        }

        private sealed class Mp3Sound : MrpSound
        {
            private readonly MpegFile decoder;
            private readonly Stream stream;

            public Mp3Sound(MpegFile decoder, Stream stream, int rate, int channels, int frames) : base(rate, channels, frames)
            {
                this.decoder = decoder;
                this.stream = stream;
            }

            protected override int ReadCore(float[] destination, int offset, int count) =>
                decoder.ReadSamples(destination, offset, count);

            protected override void RewindCore() => decoder.Position = 0;

            protected override bool SeekCore(int frame)
            {
                decoder.Position = (long)frame * Channels * sizeof(float);
                return true;
            }

            protected override void Release()
            {
                decoder.Dispose();
                stream.Dispose();
            }
        }

        private sealed class MidiSound : MrpSound
        {
            private readonly Synthesizer synth;
            private readonly MidiFileSequencer sequencer;
            private int bufferPosition;

            public MidiSound(Synthesizer synth, MidiFileSequencer sequencer, int frames) : base(synth.SampleRate, MidiChannels, frames)
            {
                this.synth = synth;
                this.sequencer = sequencer;
                RewindCore();
            }

            protected override int ReadCore(float[] destination, int offset, int count)
            {
                float[] buffer = synth.WorkingBuffer;
                int written = 0;
                while (written < count)
                {
                    if (bufferPosition == buffer.Length)
                    {
                        sequencer.FillMidiEventQueue();
                        if (!sequencer.IsPlaying) break;
                        synth.GetNext();
                        bufferPosition = 0;
                    }
                    int length = Math.Min(count - written, buffer.Length - bufferPosition);
                    Array.Copy(buffer, bufferPosition, destination, offset + written, length);
                    bufferPosition += length;
                    written += length;
                }
                return written;
            }

            protected override void RewindCore()
            {
                sequencer.Stop();
                synth.NoteOffAll(true);
                synth.ResetPrograms();
                synth.ResetSynthControls(useDefaultDrumBank: false);
                bufferPosition = synth.WorkingBuffer.Length;
                sequencer.Play();
            }
        }
    }

    /// <summary>Interleaved float samples. The output owns and serializes reads, rewinds and disposal.</summary>
    public abstract class MrpSound : IDisposable
    {
        public int SampleRate { get; }
        public int Channels { get; }
        public int Frames { get; }
        /// <summary>Complete normalized PCM for buffered formats. Hosts may upload it directly.</summary>
        public virtual float[]? BufferedSamples => null;
        private bool failed;
        private bool disposed;

        private protected MrpSound(int rate, int channels, int frames)
        {
            SampleRate = rate;
            Channels = channels;
            Frames = frames;
        }

        public int Read(float[] destination, int offset, int count, bool loop = false)
        {
            if (disposed || failed) return 0;

            int written = 0;
            try
            {
                bool rewound = false;
                while (written < count)
                {
                    int length = ReadCore(destination, offset + written, count - written);
                    if (length == 0)
                    {
                        if (!loop || rewound) break;
                        RewindCore();
                        rewound = true;
                        continue;
                    }
                    if (BufferedSamples == null) ApplyGain(destination, offset + written, length);
                    written += length;
                    rewound = false;
                }
            }
            catch (Exception error) when (MrpSoundDecoder.IsMediaError(error))
            {
                failed = true;
            }
            return written;
        }

        protected static void ApplyGain(float[] samples, int offset, int count)
        {
            for (int index = offset; index < offset + count; index++)
            {
                float sample = samples[index];
                if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new InvalidDataException("Invalid audio sample.");
                float bounded = Math.Max(-1f, Math.Min(1f, sample));
                samples[index] = 2f * bounded / (1f + Math.Abs(bounded));
            }
        }

        public bool Rewind() => Seek(0);

        public bool Seek(int frame)
        {
            if (disposed || failed || frame < 0 || frame > Frames) return false;

            try
            {
                if (frame != 0) return SeekCore(frame);
                RewindCore();
                return true;
            }
            catch (Exception error) when (MrpSoundDecoder.IsMediaError(error))
            {
                failed = true;
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release();
        }

        protected abstract int ReadCore(float[] destination, int offset, int count);
        protected abstract void RewindCore();
        protected virtual bool SeekCore(int frame) => false;
        protected virtual void Release() { }
    }
}
