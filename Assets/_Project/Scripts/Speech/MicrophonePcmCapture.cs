using System;
using UnityEngine;

// Captures the microphone with Unity's Microphone API and produces the audio
// format Gemini expects: 16-bit little-endian mono PCM at 16 kHz, in ~100 ms
// chunks. Pump() must be called from the main thread (e.g. Update).
public sealed class MicrophonePcmCapture
{
    public const int TargetSampleRate = GeminiLiveTranscriptionService.SampleRate;
    public const int ChunkMilliseconds = 100;

    private const int LoopLengthSeconds = 10;
    private const int ChunkSourceFramesMinimum = 256;

    private AudioClip clip;
    private int lastPosition;
    private int sourceRate;
    private int sourceChannels;
    private double resamplePhase;
    private float previousSample;
    private bool hasPreviousSample;

    private short[] pending = new short[TargetSampleRate];
    private int pendingCount;
    private byte[] chunkBytes;

    public string DeviceName { get; private set; } = string.Empty;
    public bool IsRecording { get; private set; }
    public int SourceSampleRate => sourceRate;
    public long TotalSamplesProduced { get; private set; }
    public float PeakLevel { get; private set; }

    public static string[] GetDevices()
    {
        return Microphone.devices;
    }

    public static string ResolveDeviceName(string preferredDevice)
    {
        string[] devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredDevice))
        {
            foreach (string device in devices)
            {
                if (device.IndexOf(
                        preferredDevice,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return device;
                }
            }
        }

        // Unity's first entry is the system default input.
        return devices[0];
    }

    public bool TryStart(string preferredDevice, out string error)
    {
        error = null;

        if (IsRecording)
        {
            return true;
        }

        string device = ResolveDeviceName(preferredDevice);
        if (device == null)
        {
            error = "No microphone was detected.";
            return false;
        }

        clip = Microphone.Start(device, true, LoopLengthSeconds, TargetSampleRate);
        if (clip == null)
        {
            error = $"Unity could not start the microphone '{device}'.";
            return false;
        }

        DeviceName = device;
        sourceRate = clip.frequency;
        sourceChannels = Mathf.Max(1, clip.channels);
        lastPosition = 0;
        resamplePhase = 0;
        hasPreviousSample = false;
        pendingCount = 0;
        TotalSamplesProduced = 0;
        PeakLevel = 0f;
        IsRecording = true;
        return true;
    }

    // Reads newly recorded audio; invokes onChunk(bytes, byteCount) for every
    // complete ~100 ms chunk. The byte buffer is reused between calls.
    public void Pump(Action<byte[], int> onChunk)
    {
        if (!IsRecording || clip == null)
        {
            return;
        }

        ReadAvailable(flushPartialChunk: false, onChunk);
    }

    // Reads whatever is left, emits the final partial chunk, and stops.
    public void Stop(Action<byte[], int> onChunk)
    {
        if (!IsRecording)
        {
            return;
        }

        if (clip != null && onChunk != null)
        {
            ReadAvailable(flushPartialChunk: true, onChunk);
        }

        Microphone.End(DeviceName);
        IsRecording = false;
        clip = null;
    }

    private void ReadAvailable(
        bool flushPartialChunk,
        Action<byte[], int> onChunk)
    {
        int clipFrames = clip.samples;
        int position = Microphone.GetPosition(DeviceName);

        if (position < 0 || position >= clipFrames)
        {
            position = 0;
        }

        int available = position - lastPosition;
        if (available < 0)
        {
            available += clipFrames;
        }

        if (available >= ChunkSourceFramesMinimum || flushPartialChunk)
        {
            int firstSegment = Math.Min(available, clipFrames - lastPosition);
            ConsumeSegment(lastPosition, firstSegment, onChunk);

            int secondSegment = available - firstSegment;
            if (secondSegment > 0)
            {
                ConsumeSegment(0, secondSegment, onChunk);
            }

            lastPosition = position;
        }

        if (flushPartialChunk && pendingCount > 0)
        {
            EmitChunk(pendingCount, onChunk);
        }
    }

    private void ConsumeSegment(
        int startFrame,
        int frameCount,
        Action<byte[], int> onChunk)
    {
        if (frameCount <= 0)
        {
            return;
        }

        var samples = new float[frameCount * sourceChannels];
        if (!clip.GetData(samples, startFrame))
        {
            return;
        }

        int targetChunkSamples = TargetSampleRate * ChunkMilliseconds / 1000;
        double step = (double)sourceRate / TargetSampleRate;

        for (int frame = 0; frame < frameCount; frame++)
        {
            // Channel 0 only: the app captures a single speaker.
            float current = samples[frame * sourceChannels];
            float magnitude = Mathf.Abs(current);
            if (magnitude > PeakLevel)
            {
                PeakLevel = magnitude;
            }

            if (sourceRate == TargetSampleRate)
            {
                AddSample(current, targetChunkSamples, onChunk);
                continue;
            }

            // Linear-interpolation resampler with a carried fractional phase.
            if (!hasPreviousSample)
            {
                previousSample = current;
                hasPreviousSample = true;
                continue;
            }

            while (resamplePhase < 1.0)
            {
                float interpolated = (float)(previousSample +
                    (current - previousSample) * resamplePhase);
                AddSample(interpolated, targetChunkSamples, onChunk);
                resamplePhase += step;
            }

            resamplePhase -= 1.0;
            previousSample = current;
        }
    }

    private void AddSample(
        float sample,
        int targetChunkSamples,
        Action<byte[], int> onChunk)
    {
        if (pendingCount >= pending.Length)
        {
            Array.Resize(ref pending, pending.Length * 2);
        }

        float clamped = Mathf.Clamp(sample, -1f, 1f);
        pending[pendingCount++] = (short)Mathf.RoundToInt(clamped * 32767f);

        if (pendingCount >= targetChunkSamples)
        {
            EmitChunk(pendingCount, onChunk);
        }
    }

    private void EmitChunk(int sampleCount, Action<byte[], int> onChunk)
    {
        int byteCount = sampleCount * 2;
        if (chunkBytes == null || chunkBytes.Length < byteCount)
        {
            chunkBytes = new byte[byteCount];
        }

        for (int i = 0; i < sampleCount; i++)
        {
            short value = pending[i];
            chunkBytes[i * 2] = (byte)(value & 0xFF);
            chunkBytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        TotalSamplesProduced += sampleCount;
        pendingCount = 0;
        onChunk?.Invoke(chunkBytes, byteCount);
    }
}
