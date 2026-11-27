using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Talks to the local Kokoro HTTP server (kokoro-tts/server.py, run outside
// Unity). POST {input, voice, speed} -> audio/wav bytes, decoded in memory into
// an AudioClip. The defaults below are the single source of truth; the
// Inspector fields on RemyVoiceController override them.
public sealed class KokoroTtsService : ITtsService
{
    // The notebook host comes from VR Settings. Kokoro currently listens on 8000.
    public const string DefaultEndpoint = "";
    public const string DefaultVoice = "am_fenrir";
    public const float DefaultSpeed = 0.92f;
    public const float DefaultTimeoutSeconds = 30f;

    private const float HealthTimeoutSeconds = 3f;

    private readonly string endpointOverride;

    public KokoroTtsService(
        string endpoint = DefaultEndpoint,
        string voice = DefaultVoice,
        float speed = DefaultSpeed,
        float timeoutSeconds = DefaultTimeoutSeconds)
    {
        endpointOverride = string.IsNullOrWhiteSpace(endpoint)
            ? null : endpoint.Trim();
        Voice = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
        Speed = speed > 0f ? speed : DefaultSpeed;
        TimeoutSeconds = timeoutSeconds > 0f ? timeoutSeconds : DefaultTimeoutSeconds;
    }

    public string Endpoint => endpointOverride ?? LanguageBackend.KokoroDirectUrl();
    public string Voice { get; }
    public float Speed { get; }
    public float TimeoutSeconds { get; }

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

        string endpoint = Endpoint;
        if (endpoint == null)
            throw new TtsException(LanguageBackend.MissingConfigurationMessage());

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new SpeakRequest
        {
            input = text.Trim(),
            text = text.Trim(),
            voice = Voice,
            speed = Math.Round(Speed, 3)
        }));

        byte[] wav;
        using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "audio/wav");
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
                        $"Request timed out after {TimeoutSeconds:F0}s ({endpoint}).");
                }

                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new TtsException(
                    IsTimeout(request.error)
                        ? $"Request timed out ({endpoint})."
                        : $"Server unavailable at {endpoint}: {request.error}");
            }

            if (request.result == UnityWebRequest.Result.ProtocolError ||
                request.responseCode < 200 || request.responseCode >= 300)
            {
                throw new TtsException(
                    $"Server returned HTTP {request.responseCode}: " +
                    SafeErrorDetail(request));
            }

            if (request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new TtsException("Response processing failed: " + request.error);
            }

            wav = request.downloadHandler.data;
        }

        SpeechTimingLogger.LogTtsResponseReceived(timing, wav != null ? wav.Length : 0);

        WavDecoder.Result decoded = WavDecoder.Decode(wav);
        cancellationToken.ThrowIfCancellationRequested();

        AudioClip clip = AudioClip.Create(
            "Remy_KokoroTTS",
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

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string healthEndpoint = HealthEndpoint;
        if (healthEndpoint == null)
            return false;

        try
        {
            using (var request = UnityWebRequest.Get(healthEndpoint))
            {
                request.timeout = Mathf.CeilToInt(HealthTimeoutSeconds);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        return false;
                    }

                    await Task.Yield();
                }

                return request.result == UnityWebRequest.Result.Success &&
                       request.downloadHandler.text.Contains("\"ok\"");
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string HealthEndpoint => Endpoint == null ? null : DeriveHealthEndpoint(Endpoint);

    public TtsProvider Provider => TtsProvider.Kokoro;
    public string Model => null;
    public bool PrecachesGreeting => true;
    public string CacheKey => $"Kokoro|{Endpoint}|{Voice}|{Speed:F3}";

    private static string DeriveHealthEndpoint(string ttsEndpoint)
    {
        return Uri.TryCreate(ttsEndpoint, UriKind.Absolute, out Uri uri)
            ? new Uri(uri, "/health").ToString()
            : ttsEndpoint;
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

    [Serializable]
    private sealed class SpeakRequest
    {
        // Existing notebook implementations accept either input or text.
        public string input = string.Empty;
        public string text = string.Empty;
        public string voice = string.Empty;
        public double speed;
    }
}
