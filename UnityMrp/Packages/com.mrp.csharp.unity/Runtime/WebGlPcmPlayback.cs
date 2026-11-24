#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using UnityEngine;

/// <summary>Two non-streamed PCM blocks, rendered and recycled by the Unity main thread.</summary>
internal sealed class WebGlPcmPlayback : MonoBehaviour
{
    private const double LeadSeconds = .1;
    private readonly AudioSource[] sources = new AudioSource[2];
    private readonly AudioClip[] clips = new AudioClip[2];
    private readonly double[] starts = new double[2];
    private readonly double[] ends = new double[2];
    private float[] samples;
    private Func<float[], int> render;
    private bool playing;
    private bool paused;
    private bool exhausted;
    private int samplesPerSecond;
    private double pauseTime;
    private double nextStart;

    public void Configure(AudioSource primary, int sampleRate, int channels, Func<float[], int> renderer)
    {
        Release();
        sources[0] = primary;
        sources[1] = gameObject.AddComponent<AudioSource>();
        samplesPerSecond = checked(sampleRate * channels);
        samples = new float[samplesPerSecond];
        render = renderer;
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i].playOnAwake = false;
            sources[i].spatialBlend = 0;
            sources[i].loop = false;
            clips[i] = AudioClip.Create("MRP WebGL PCM " + i, sampleRate, channels, sampleRate, false);
            sources[i].clip = clips[i];
        }
    }

    public void Play()
    {
        Stop();
        playing = true;
        paused = false;
        nextStart = AudioSettings.dspTime + LeadSeconds;
        try
        {
            for (int i = 0; i < sources.Length && !exhausted; i++)
            {
                Schedule(i);
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void Schedule(int index)
    {
        int count = render(samples);
        if (count == 0)
        {
            exhausted = true;
            return;
        }
        if (!clips[index].SetData(samples, 0))
        {
            Stop();
            throw new InvalidOperationException("WebGL PCM buffer upload failed.");
        }
        starts[index] = nextStart;
        ends[index] = nextStart + count / (double)samplesPerSecond;
        sources[index].volume = sources[0].volume;
        sources[index].mute = sources[0].mute;
        sources[index].PlayScheduled(nextStart);
        if (count < samples.Length)
        {
            sources[index].SetScheduledEndTime(ends[index]);
            exhausted = true;
        }
        nextStart = ends[index];
    }

    private void Update()
    {
        if (!playing || paused)
        {
            return;
        }
        double now = AudioSettings.dspTime;
        if (exhausted)
        {
            if (now >= nextStart) playing = false;
            return;
        }
        nextStart = Math.Max(nextStart, now + LeadSeconds);
        try
        {
            for (int i = 0; i < sources.Length && !exhausted; i++)
            {
                if (now >= ends[i])
                {
                    Schedule(i);
                }
            }
        }
        catch (Exception error)
        {
            Stop();
            Debug.LogException(error);
        }
    }

    public void Pause(bool value)
    {
        if (!playing || value == paused)
        {
            return;
        }
        paused = value;
        if (value)
        {
            pauseTime = AudioSettings.dspTime;
            foreach (var source in sources)
            {
                source.Pause();
            }
        }
        else
        {
            double delay = AudioSettings.dspTime - pauseTime;
            for (int i = 0; i < sources.Length; i++)
            {
                if (starts[i] >= pauseTime)
                {
                    sources[i].SetScheduledStartTime(starts[i] + delay);
                }
                sources[i].UnPause();
                starts[i] += delay;
                ends[i] += delay;
            }
            nextStart += delay;
        }
    }

    public void Stop()
    {
        playing = false;
        paused = false;
        exhausted = false;
        Array.Clear(starts, 0, starts.Length);
        Array.Clear(ends, 0, ends.Length);
        foreach (var source in sources)
        {
            if (source)
            {
                source.Stop();
            }
        }
    }

    public void Release()
    {
        Stop();
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i])
            {
                sources[i].clip = null;
            }
            if (clips[i])
            {
                Destroy(clips[i]);
            }
            clips[i] = null;
        }
        if (sources[1])
        {
            Destroy(sources[1]);
        }
        sources[0] = null;
        sources[1] = null;
        samples = null;
        render = null;
    }

    private void OnDestroy()
    {
        Release();
    }
}
#endif
