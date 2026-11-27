using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Cartesia cloud TTS (https://docs.cartesia.ai/api-reference/tts/bytes), reached
// through the LAN backend that holds CARTESIA_API_KEY (see LanguageBackend).
// POST JSON -> complete WAV (PCM 16-bit, 44.1 kHz) decoded in memory into an
// AudioClip. Unity authenticates with the backend token only; no Cartesia key
// exists on the client.
public sealed class CartesiaTtsService : ITtsService
{
    public const string EndpointPath = "/api/cartesia/tts/bytes";
    public const string ApiVersion = "2026-08-14";
    public const string DefaultModel = "sonic-3.6";
    // Corey - Supportive Buddy
    public const string DefaultVoiceId = "630ed21c-2c5c-41cf-9d82-10a7fd668370";
    public const string DefaultLanguage = "en";
    public const float DefaultSpeed = 1f;
    public const float DefaultTimeoutSeconds = 30f;

    private const int OutputSampleRate = 44100;
    private const float MinimumSpeed = 0.6f;
    private const float MaximumSpeed = 1.5f;

    public CartesiaTtsService(
        string model = DefaultModel,
        string voiceId = DefaultVoiceId,
        string language = DefaultLanguage,
        float speed = DefaultSpeed,
        float timeoutSeconds = DefaultTimeoutSeconds)
    {
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        VoiceId = string.IsNullOrWhiteSpace(voiceId)
            ? DefaultVoiceId
            : voiceId.Trim();
        Language = string.IsNullOrWhiteSpace(language)
            ? DefaultLanguage
            : language.Trim();
        Speed = speed > 0f
            ? Mathf.Clamp(speed, MinimumSpeed, MaximumSpeed)
            : DefaultSpeed;
        TimeoutSeconds = timeoutSeconds > 0f ? timeoutSeconds : DefaultTimeoutSeconds;
    }

    public string Model { get; }
    public string VoiceId { get; }
    public string Language { get; }
    public float Speed { get; }
    public float TimeoutSeconds { get; }

    public TtsProvider Provider => TtsProvider.Cartesia;
    public bool PrecachesGreeting => false;
    public string CacheKey =>
        $"Cartesia|{LanguageBackend.BaseUrl}|{Model}|{VoiceId}|{Language}|{Speed:F3}";

    public async Task<AudioClip> SynthesizeAsync(
        string text,
        SpeechTimingLogger.Trace timing = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Speech text cannot be empty.", nameof(text));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string backendUrl = LanguageBackend.BaseUrl;
        string token = LanguageBackend.Token;
        if (backendUrl == null || token == null)
        {
            throw new TtsException(
                LanguageBackend.MissingConfigurationMessage() +
                " No request was sent.");
        }

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new SpeakRequest
        {
            model_id = Model,
            transcript = text.Trim(),
            voice = new VoiceSpec { id = VoiceId },
            language = Language,
            output_format = new OutputFormat
            {
                container = "wav",
                encoding = "pcm_s16le",
                sample_rate = OutputSampleRate
            },
            generation_config = new GenerationConfig
            {
                speed = Math.Round(Speed, 3)
            }
        }));

        byte[] wav;
        using (var request = new UnityWebRequest(
                   LanguageBackend.BuildUrl(backendUrl, EndpointPath),
                   UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            LanguageBackend.ApplyAuth(request, token);
            request.SetRequestHeader("Cartesia-Version", ApiVersion);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(TimeoutSeconds) + 5; // backstop only

            SpeechTimingLogger.LogTtsRequestSent(timing);
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            var elapsed = Stopwatch.StartNew();

            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elapsed.Elapsed.TotalSeconds > TimeoutSeconds)
                {
                    request.Abort();
                    throw new TtsException(
                        $"Cartesia request timed out after {TimeoutSeconds:F0}s.");
                }

                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new TtsException(
                    IsTimeout(request.error)
                        ? "Cartesia request timed out."
                        : "Language backend is unreachable (network error): " +
                          LanguageBackend.Redact(request.error, token));
            }

            long code = request.responseCode;
            if (request.result == UnityWebRequest.Result.ProtocolError ||
                code < 200 || code >= 300)
            {
                if (code == 401 || code == 403)
                {
                    LanguageBackend.RejectToken(token);
                }

                throw new TtsException(DescribeHttpError(code, request, token));
            }

            if (request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new TtsException("Cartesia response processing failed: " + request.error);
            }

            wav = request.downloadHandler.data;
        }

        SpeechTimingLogger.LogTtsResponseReceived(timing, wav != null ? wav.Length : 0);

        WavDecoder.Result decoded = WavDecoder.Decode(wav);
        cancellationToken.ThrowIfCancellationRequested();

        AudioClip clip = AudioClip.Create(
            "Remy_CartesiaTTS",
            decoded.FrameCount,
            decoded.Channels,
            decoded.SampleRate,
            false);

        if (clip == null || !clip.SetData(decoded.Samples, 0))
        {
            if (clip != null)
            {
                UnityEngine.Object.Destroy(clip);
            }

            throw new TtsException("Unity could not create the AudioClip.");
        }

        SpeechTimingLogger.LogTtsClipReady(timing, clip.length);
        return clip;
    }

    // No network call: Cartesia is metered, so "healthy" only means the
    // backend URL and token are configured.
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(LanguageBackend.IsConfigured);
    }

    private static string DescribeHttpError(long code, UnityWebRequest request, string token)
    {
        switch (code)
        {
            case 401:
            case 403:
                return $"Backend session rejected (HTTP {code}). Pair again in VR Settings.";
            case 429:
                return "Cartesia rate limit or quota reached (HTTP 429).";
            default:
                if (code >= 500)
                {
                    return $"Cartesia service error (HTTP {code}).";
                }

                return $"Cartesia returned HTTP {code}: " +
                    LanguageBackend.Redact(SafeErrorDetail(request), token);
        }
    }

    private static bool IsTimeout(string error)
    {
        return !string.IsNullOrEmpty(error) &&
               error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SafeErrorDetail(UnityWebRequest request)
    {
        string text = request.downloadHandler != null
            ? request.downloadHandler.text
            : string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(request.error)
                ? "Request rejected."
                : request.error;
        }

        text = text.Trim();
        return text.Length > 200 ? text.Substring(0, 200) : text;
    }

    // Field names match Cartesia's JSON exactly (JsonUtility serialisation).
    [Serializable]
    private sealed class SpeakRequest
    {
        public string model_id = string.Empty;
        public string transcript = string.Empty;
        public VoiceSpec voice;
        public string language = string.Empty;
        public OutputFormat output_format;
        public GenerationConfig generation_config;
    }

    [Serializable]
    private sealed class VoiceSpec
    {
        public string id = string.Empty;
    }

    [Serializable]
    private sealed class OutputFormat
    {
        public string container = string.Empty;
        public string encoding = string.Empty;
        public int sample_rate;
    }

    [Serializable]
    private sealed class GenerationConfig
    {
        public double speed;
    }
}
