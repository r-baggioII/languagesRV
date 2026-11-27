using System;

// Minimal in-memory RIFF/WAVE reader: 16-bit PCM or 32-bit float, any channel
// count. Everything is bounds-checked because the bytes come from a network
// service; malformed input raises TtsException instead of crashing.
public static class WavDecoder
{
    private const int MaximumSamples = 24000 * 60 * 5;

    public sealed class Result
    {
        public float[] Samples;
        public int SampleRate;
        public int Channels;
        public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;
    }

    public static Result Decode(byte[] wav)
    {
        if (wav == null || wav.Length < 44)
        {
            throw new TtsException("Invalid WAV response (too short).");
        }

        if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE"))
        {
            throw new TtsException("Invalid WAV response (missing RIFF/WAVE header).");
        }

        int format = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        bool hasFormat = false;
        int dataOffset = -1;
        int dataLength = 0;

        int position = 12;
        while (position + 8 <= wav.Length)
        {
            uint declaredSize = BitConverter.ToUInt32(wav, position + 4);
            int bodyStart = position + 8;
            int available = wav.Length - bodyStart;
            // Streamed WAVs may declare 0xFFFFFFFF or overshoot: clamp.
            int size = declaredSize > (uint)available ? available : (int)declaredSize;

            if (Matches(wav, position, "fmt "))
            {
                if (size < 16)
                {
                    throw new TtsException("Invalid WAV response (fmt chunk too small).");
                }

                format = BitConverter.ToUInt16(wav, bodyStart);
                channels = BitConverter.ToUInt16(wav, bodyStart + 2);
                sampleRate = BitConverter.ToInt32(wav, bodyStart + 4);
                bitsPerSample = BitConverter.ToUInt16(wav, bodyStart + 14);

                // WAVE_FORMAT_EXTENSIBLE: real format is the sub-format GUID's
                // first two bytes.
                if (format == 0xFFFE && size >= 26)
                {
                    format = BitConverter.ToUInt16(wav, bodyStart + 24);
                }

                hasFormat = true;
            }
            else if (Matches(wav, position, "data"))
            {
                dataOffset = bodyStart;
                dataLength = size;
                break;
            }

            long next = (long)bodyStart + size + (size & 1);
            if (next > wav.Length)
            {
                break;
            }

            position = (int)next;
        }

        if (!hasFormat || dataOffset < 0)
        {
            throw new TtsException("Invalid WAV response (missing fmt or data chunk).");
        }

        if (channels < 1 || channels > 2 || sampleRate < 8000 || sampleRate > 96000)
        {
            throw new TtsException(
                $"Invalid WAV response (unsupported {channels} ch / {sampleRate} Hz).");
        }

        bool isPcm16 = format == 1 && bitsPerSample == 16;
        bool isFloat32 = format == 3 && bitsPerSample == 32;
        if (!isPcm16 && !isFloat32)
        {
            throw new TtsException(
                $"Invalid WAV response (unsupported format {format}, {bitsPerSample}-bit).");
        }

        int bytesPerSample = bitsPerSample / 8;
        int sampleCount = dataLength / bytesPerSample;
        sampleCount -= sampleCount % channels;

        if (sampleCount <= 0)
        {
            throw new TtsException("Invalid WAV response (no audio samples).");
        }

        if (sampleCount > MaximumSamples)
        {
            throw new TtsException("Invalid WAV response (audio is unreasonably long).");
        }

        var samples = new float[sampleCount];
        if (isPcm16)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = BitConverter.ToInt16(wav, dataOffset + i * 2) / 32768f;
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float value = BitConverter.ToSingle(wav, dataOffset + i * 4);
                samples[i] = float.IsNaN(value) ? 0f : Math.Max(-1f, Math.Min(1f, value));
            }
        }

        return new Result
        {
            Samples = samples,
            SampleRate = sampleRate,
            Channels = channels
        };
    }

    private static bool Matches(byte[] buffer, int offset, string tag)
    {
        if (offset + 4 > buffer.Length)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (buffer[offset + i] != tag[i])
            {
                return false;
            }
        }

        return true;
    }
}
