#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CSharpMrp.Unity
{
    /// <summary>The guest prepares audio on its worker. Unity clip and source operations run on the main thread.</summary>
    public sealed class UnitySoundOutput : ISoundOutput
    {
        private readonly MrpSoundDecoder decoder;
        private readonly GameObject owner;
        private readonly AudioSource source;
        private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        private readonly object gate = new object();
        private readonly Queue<Request> requests = new Queue<Request>();
        private ClipSamples? samples;
        private AudioClip? clip;
#if UNITY_WEBGL && !UNITY_EDITOR
        private WebGlPcmPlayback? webPlayback;
#endif
        private volatile int midiRate;
        private int playingType = -1;
        private bool paused;
        private bool closed;
        public AudioSource Source => source;

        public UnitySoundOutput(Transform parent, MrpSoundDecoder decoder)
        {
            this.decoder = decoder;
            owner = new GameObject("CSharpMrp audio");
            owner.transform.SetParent(parent, false);
            source = owner.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            if (Object.FindObjectOfType<AudioListener>() == null) owner.AddComponent<AudioListener>();
            midiRate = OutputRate();
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        public bool Play(int type, byte[] data, bool loop)
        {
            if (decoder.Prepare(type, data, midiRate) is not { } sound) return false;
            bool transferred = false;
            try
            {
                transferred = Send(() => StartClip(type, sound, loop));
                return transferred;
            }
            finally
            {
                if (!transferred) sound.Dispose();
            }
        }

        public bool Stop(int type) => Send(() =>
        {
            if (type != playingType || playingType < 0) return false;
            ClearClip();
            return true;
        });

        public void Pause(bool value) => Send(() =>
        {
            if (paused != value)
            {
                paused = value;
                if (playingType >= 0)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    if (samples != null)
                    {
                        webPlayback!.Pause(paused);
                        return true;
                    }
#endif
                    if (paused) source.Pause();
                    else source.UnPause();
                }
            }
            return true;
        });

        public void StopAll() => Send(() =>
        {
            ClearClip();
            return true;
        });

        private bool StartClip(int type, MrpSound sound, bool loop)
        {
            var buffered = sound.BufferedSamples;
            AudioClip? nextClip = null;
            ClipSamples? next = buffered == null ? new ClipSamples(sound) : null;
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (next != null)
                {
                    ClearClip();
                    webPlayback ??= owner.AddComponent<WebGlPcmPlayback>();
                    webPlayback.Configure(source, sound.SampleRate, sound.Channels, data => next.Read(data, loop));
                    webPlayback.Play();
                    if (paused) webPlayback.Pause(true);
                    samples = next;
                    playingType = type;
                    return true;
                }
#endif
                nextClip = next == null
                    ? AudioClip.Create("MRP sound", sound.Frames, sound.Channels, sound.SampleRate, false)
                    : AudioClip.Create("MRP stream", sound.Frames, sound.Channels, sound.SampleRate, true, next.Fill, next.SetPosition);
                if (nextClip == null) return false;
                if (buffered != null && !nextClip.SetData(buffered, 0))
                {
                    Object.Destroy(nextClip);
                    return false;
                }
            }
            catch (Exception error) when (error is UnityException || error is ArgumentException || error is InvalidOperationException)
            {
                if (nextClip != null) Object.Destroy(nextClip);
#if UNITY_WEBGL && !UNITY_EDITOR
                if (next != null) webPlayback?.Release();
#endif
                Debug.LogWarning("CSharpMrp audio could not create a clip. " + error.Message);
                return false;
            }

            ClearClip();
            clip = nextClip;
            samples = next;
            playingType = type;
            source.clip = clip;
            source.loop = loop;
            source.Play();
            if (paused) source.Pause();
            if (next == null) sound.Dispose();
            return true;
        }

        private void ClearClip()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            webPlayback?.Release();
#endif
            source.Stop();
            source.clip = null;
            if (clip != null) Object.Destroy(clip);
            samples?.Dispose();
            clip = null;
            samples = null;
            playingType = -1;
        }

        private static int OutputRate()
        {
            int rate = AudioSettings.outputSampleRate;
            return rate >= 8000 ? Math.Min(rate, 96000) : MrpSoundDecoder.DefaultMidiRate;
        }

        private void OnAudioConfigurationChanged(bool deviceChanged)
        {
            int rate = OutputRate();
            if (rate == midiRate) return;
            midiRate = rate;
            if (playingType == 0) ClearClip();
        }

        private bool Send(Func<bool> action)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThread) return !closed && action();

            var request = new Request(action);
            lock (gate)
            {
                if (closed) return false;
                requests.Enqueue(request);
            }
            return request.Result.Task.GetAwaiter().GetResult();
        }

        public void Pump()
        {
            lock (gate)
            {
                while (requests.Count != 0)
                {
                    Request request = requests.Dequeue();
                    try
                    {
                        request.Result.SetResult(request.Action());
                    }
                    catch (Exception error)
                    {
                        request.Result.SetException(error);
                    }
                }
            }
        }

        /// <summary>Close from the main thread before awaiting guest worker shutdown.</summary>
        public void Release()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                while (requests.Count != 0) requests.Dequeue().Result.SetResult(false);
            }
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            ClearClip();
            Object.Destroy(owner);
        }

        private sealed class Request
        {
            public readonly Func<bool> Action;
            public readonly TaskCompletionSource<bool> Result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Request(Func<bool> action) => Action = action;
        }

        private sealed class ClipSamples : IDisposable
        {
            private readonly object gate = new object();
            private readonly MrpSound sound;

            public ClipSamples(MrpSound sound) => this.sound = sound;

            public void Fill(float[] data) => Read(data, false);

            public int Read(float[] data, bool loop)
            {
                lock (gate)
                {
                    int read = sound.Read(data, 0, data.Length, loop);
                    Array.Clear(data, read, data.Length - read);
                    return read;
                }
            }

            public void SetPosition(int frame)
            {
                lock (gate) sound.Seek(frame);
            }

            public void Dispose()
            {
                lock (gate) sound.Dispose();
            }
        }
    }
}
