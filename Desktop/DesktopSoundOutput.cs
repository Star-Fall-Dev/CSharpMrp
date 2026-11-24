using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CSharpMrp.Desktop;

internal sealed class DesktopSoundOutput : ISoundOutput, IDisposable
{
    private static readonly MrpSoundDecoder Decoder = new();
    private WasapiOut? device;
    private SoundSamples? samples;
    private int playingType = -1;
    private bool paused;
    private bool disposed;

    public bool Play(int type, byte[] data, bool loop)
    {
        if (disposed || Decoder.Prepare(type, data) is not { } sound) return false;

        StopAll();
        samples = new SoundSamples(sound, loop);
        try
        {
            device = new WasapiOut(AudioClientShareMode.Shared, true, 72);
            device.Init(new SampleToWaveProvider16(samples));
            if (!paused) device.Play();
            playingType = type;
            return true;
        }
        catch (Exception error) when (IsDeviceError(error))
        {
            StopAll();
            return false;
        }
    }

    public bool Stop(int type)
    {
        if (device == null || type != playingType) return false;
        StopAll();
        return true;
    }

    public void Pause(bool value)
    {
        if (paused == value || disposed) return;
        paused = value;
        try
        {
            if (paused)
            {
                if (device?.PlaybackState == PlaybackState.Playing) device.Pause();
            }
            else
            {
                device?.Play();
            }
        }
        catch (Exception error) when (IsDeviceError(error))
        {
            StopAll();
        }
    }

    public void StopAll()
    {
        try
        {
            device?.Dispose();
        }
        catch (Exception error) when (IsDeviceError(error))
        {
            // The device may have disappeared since playback started.
        }
        finally
        {
            device = null;
            samples?.Dispose();
            samples = null;
            playingType = -1;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAll();
    }

    private static bool IsDeviceError(Exception error) => error is ExternalException ||
        error is InvalidOperationException || error is NotSupportedException;

    private sealed class SoundSamples(MrpSound sound, bool loop) : ISampleProvider, IDisposable
    {
        private readonly object gate = new();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sound.SampleRate, sound.Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            lock (gate)
            {
                return sound.Read(buffer, offset, count, loop);
            }
        }

        public void Dispose()
        {
            lock (gate) sound.Dispose();
        }
    }
}
