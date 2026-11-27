using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// High-level owner of Remy's voice: final line -> selected TTS provider
// (Kokoro local server or Cartesia cloud API) -> AudioClip -> Remy's
// AudioSource. Only the selected provider is ever contacted. TTS failure is never fatal: SpeakAsync
// reports it through RemySpeechResult and the conversation continues silently.
[RequireComponent(typeof(AudioSource))]
public sealed class RemyVoiceController : MonoBehaviour
{
    private const string ProviderPreference = "LanguageImmersion.VoiceProvider";
    private const string InitialGreeting =
        "Hi! What can I get for you today?";
    private const float OutputDrainSeconds = 0.15f;

    [SerializeField] private AudioSource audioSource;

    [Header("TTS provider")]
    [SerializeField] private TtsProvider provider = TtsProvider.Kokoro;

    [Header("Kokoro TTS (local server, run outside Unity)")]
    [SerializeField] private string serverUrl = KokoroTtsService.DefaultEndpoint;
    [SerializeField] private string voice = KokoroTtsService.DefaultVoice;
    [SerializeField, Range(0.5f, 1.5f)]
    private float speed = KokoroTtsService.DefaultSpeed;
    [SerializeField, Min(1f)]
    private float requestTimeoutSeconds = KokoroTtsService.DefaultTimeoutSeconds;

    [Header("Cartesia TTS (via configured LAN backend)")]
    [SerializeField] private string cartesiaModel = CartesiaTtsService.DefaultModel;
    [SerializeField] private string cartesiaVoiceId = CartesiaTtsService.DefaultVoiceId;
    [SerializeField] private string cartesiaLanguage = CartesiaTtsService.DefaultLanguage;
    [SerializeField, Range(0.6f, 1.5f)]
    private float cartesiaSpeed = CartesiaTtsService.DefaultSpeed;
    [SerializeField, Min(1f)]
    private float cartesiaTimeoutSeconds = CartesiaTtsService.DefaultTimeoutSeconds;

    private ITtsService ttsService;
    private TtsProvider appliedProvider;
    private string greetingKey;
    private CancellationTokenSource activeSpeechCancellation;
    private CancellationTokenSource lifetimeCancellation;
    private Task<AudioClip> greetingPreparation;
    private AudioClip greetingClip;
    private AudioClip generatedClip;

    public bool IsSpeaking =>
        audioSource != null && audioSource.isPlaying;

    public string Voice => voice;
    public string ServerUrl => string.IsNullOrWhiteSpace(serverUrl)
        ? LanguageBackend.KokoroDirectUrl() : serverUrl;
    public TtsProvider Provider => provider;

    // A change during a spoken line is deferred until the next line so the
    // current conversation state and audio are not interrupted.
    public void SetProvider(TtsProvider newProvider)
    {
        provider = newProvider;
        PlayerPrefs.SetInt(ProviderPreference, (int)newProvider);
        PlayerPrefs.Save();

        if (activeSpeechCancellation != null || IsSpeaking)
            return;

        ApplyPendingProvider();
    }

    private void ApplyPendingProvider()
    {
        if (ttsService == null || appliedProvider == provider)
        {
            return;
        }

        StopSpeaking();
        DestroyPreviousClip();
        DiscardGreeting();

        if (lifetimeCancellation != null)
        {
            lifetimeCancellation.Cancel();
            lifetimeCancellation.Dispose();
        }

        lifetimeCancellation = new CancellationTokenSource();
        ttsService = CreateService(provider);
        appliedProvider = provider;
        Debug.Log($"[TTS] Provider switched to {provider}.", this);
        StartProviderWarmUp();
    }

#if UNITY_EDITOR
    // Lets the Inspector dropdown switch providers during Play Mode.
    private void OnValidate()
    {
        if (Application.isPlaying && ttsService != null &&
            appliedProvider != provider)
        {
            SetProvider(provider);
        }
    }
#endif

    private ITtsService CreateService(TtsProvider which)
    {
        switch (which)
        {
            case TtsProvider.Cartesia:
                return new CartesiaTtsService(
                    cartesiaModel,
                    cartesiaVoiceId,
                    cartesiaLanguage,
                    cartesiaSpeed,
                    cartesiaTimeoutSeconds);
            default:
                return new KokoroTtsService(
                    serverUrl, voice, speed, requestTimeoutSeconds);
        }
    }

    private static string LogTag(ITtsService service)
    {
        return service.Provider == TtsProvider.Cartesia
            ? "CartesiaTTS"
            : "KokoroTTS";
    }

    private void Awake()
    {
        if (PlayerPrefs.HasKey(ProviderPreference))
        {
            int saved = PlayerPrefs.GetInt(ProviderPreference);
            if (Enum.IsDefined(typeof(TtsProvider), saved))
                provider = (TtsProvider)saved;
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        ttsService = CreateService(provider);
        appliedProvider = provider;
        lifetimeCancellation = new CancellationTokenSource();
    }

    private void Start()
    {
        StartProviderWarmUp();
    }

    // Kokoro (free, local): health check + greeting pre-cache. Cartesia
    // (metered): only a configuration check - no request is sent until Remy
    // actually speaks.
    private void StartProviderWarmUp()
    {
        ITtsService service = ttsService;
        Debug.Log(
            $"[TTS] Provider: {service.Provider}" +
            (service.Model != null ? $" (model {service.Model})" : string.Empty),
            this);

        if (service.PrecachesGreeting)
        {
            if (string.IsNullOrWhiteSpace(ServerUrl))
                return;
            greetingPreparation =
                WarmUpAsync(service, lifetimeCancellation.Token);
            return;
        }

        if (!LanguageBackend.IsConfigured)
        {
            Debug.LogWarning(
                $"[{LogTag(service)}] {LanguageBackend.MissingConfigurationMessage()} " +
                "Remy will be silent (subtitles, STT and the order " +
                "flow are unaffected). Enter them in VR Settings.",
                this);
        }
    }

    public async Task<RemySpeechResult> SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        CancelActiveSpeech();
        ApplyPendingProvider();

        if (audioSource == null)
        {
            const string Error = "Remy's AudioSource is not assigned.";
            Debug.LogError($"[RemyVoice] {Error}", this);
            return RemySpeechResult.Failed(Error);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return RemySpeechResult.Failed("Speech text is empty.");
        }

        var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        activeSpeechCancellation = linkedCancellation;
        CancellationToken token = linkedCancellation.Token;
        ITtsService service = ttsService;
        AudioClip clip = null;
        bool requestSucceeded = false;
        bool playbackStarted = false;

        try
        {
            DestroyPreviousClip();

            SpeechTimingLogger.Trace timing = null;
            bool cachedGreeting = false;

            if (IsInitialGreeting(text))
            {
                clip = await GetGreetingClipAsync(service, token);
                cachedGreeting = clip != null;
            }

            if (clip == null)
            {
                timing = SpeechTimingLogger.BeginTtsTrace(
                    service.Provider.ToString());
                var generation = System.Diagnostics.Stopwatch.StartNew();
                clip = await service.SynthesizeAsync(text, timing, token);
                generation.Stop();
                LogGeneration(service, generation.Elapsed.TotalMilliseconds, clip);

                // A provider switch/reset during the request cancels the token
                // and the clip is destroyed instead of cached or played.
                if (token.IsCancellationRequested)
                {
                    Destroy(clip);
                    clip = null;
                    token.ThrowIfCancellationRequested();
                }

                if (IsInitialGreeting(text) && !service.PrecachesGreeting)
                {
                    // Cloud greeting: generated once on demand, then reused
                    // (only under the same provider/voice/model key).
                    greetingClip = clip;
                    greetingKey = service.CacheKey;
                }
                else
                {
                    generatedClip = clip;
                }
            }

            requestSucceeded = true;

            // A reset/new conversation may have landed while the request was
            // in flight: stale audio must never start.
            token.ThrowIfCancellationRequested();

            audioSource.clip = clip;
            audioSource.Play();

            if (cachedGreeting)
            {
                SpeechTimingLogger.LogCachedPlaybackStarted();
            }
            else
            {
                SpeechTimingLogger.LogPlaybackStarted(timing);
            }

            if (!audioSource.isPlaying)
            {
                throw new TtsException(
                    "Remy's AudioSource did not start playback.");
            }

            playbackStarted = true;

            while (audioSource.isPlaying)
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            float drainUntil =
                Time.realtimeSinceStartup + OutputDrainSeconds;
            while (Time.realtimeSinceStartup < drainUntil)
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            audioSource.Stop();

            if (cachedGreeting)
            {
                SpeechTimingLogger.LogCachedPlaybackFinished();
            }
            else
            {
                SpeechTimingLogger.LogPlaybackFinished(timing);
            }

            return RemySpeechResult.Succeeded(clip);
        }
        catch (OperationCanceledException)
        {
            if (audioSource != null)
            {
                audioSource.Stop();
            }

            throw;
        }
        catch (TtsException exception)
        {
            Debug.LogWarning($"[{LogTag(service)}] {exception.Message}", this);
            return RemySpeechResult.Failed(
                exception.Message,
                requestSucceeded,
                clip,
                playbackStarted);
        }
        catch (Exception exception)
        {
            string safeError = SafeMessage(exception, service);
            Debug.LogError(
                $"[{LogTag(service)}] Unexpected failure: {safeError}", this);
            return RemySpeechResult.Failed(
                safeError,
                requestSucceeded,
                clip,
                playbackStarted);
        }
        finally
        {
            if (ReferenceEquals(activeSpeechCancellation, linkedCancellation))
            {
                activeSpeechCancellation = null;
            }

            linkedCancellation.Dispose();
        }
    }

    public void StopSpeaking()
    {
        CancelActiveSpeech();

        if (audioSource != null)
        {
            audioSource.Stop();
        }
    }

    // Startup: a failed health check is only a warning (STT, Gemini, subtitles
    // and order logic keep working). The greeting is cached only when the
    // server answers, so an offline server costs one warning, not two.
    private async Task<AudioClip> WarmUpAsync(
        ITtsService service,
        CancellationToken cancellationToken)
    {
        bool healthy = await service.CheckHealthAsync(cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!healthy)
        {
            Debug.LogWarning(
                $"[KokoroTTS] Server unavailable at {ServerUrl} (health check " +
                "failed). Remy will be silent until it is started " +
                "(subtitles, STT and the order flow are unaffected).",
                this);
            return null;
        }

        return await PrepareGreetingAsync(service, cancellationToken);
    }

    private async Task<AudioClip> GetGreetingClipAsync(
        ITtsService service,
        CancellationToken cancellationToken)
    {
        if (greetingClip != null && greetingKey == service.CacheKey)
        {
            return greetingClip;
        }

        // Cloud providers synthesise the greeting on demand (no pre-cache).
        if (!service.PrecachesGreeting)
        {
            return null;
        }

        if (greetingPreparation == null)
        {
            greetingPreparation =
                WarmUpAsync(service, lifetimeCancellation.Token);
        }

        while (!greetingPreparation.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        cancellationToken.ThrowIfCancellationRequested();
        greetingClip = await greetingPreparation;
        greetingKey = greetingClip != null ? service.CacheKey : null;
        return greetingClip;
    }

    private async Task<AudioClip> PrepareGreetingAsync(
        ITtsService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.SynthesizeAsync(
                InitialGreeting,
                SpeechTimingLogger.BeginTtsTrace(service.Provider.ToString()),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"[{LogTag(service)}] Initial greeting could not be pre-cached: " +
                SafeMessage(exception, service),
                this);
            return null;
        }
    }

    private static bool IsInitialGreeting(string text)
    {
        return string.Equals(
            text?.Trim(),
            InitialGreeting,
            StringComparison.Ordinal);
    }

    private static string SafeMessage(Exception exception, ITtsService service)
    {
        return exception == null ||
               string.IsNullOrWhiteSpace(exception.Message)
            ? $"{service.Provider} TTS failed for an unspecified reason."
            : exception.Message;
    }

    // Generation latency only; playback duration is logged by SpeechTimingLogger.
    private void LogGeneration(ITtsService service, double milliseconds, AudioClip clip)
    {
        string model = service.Model != null
            ? $"\n[TTS] Model: {service.Model}"
            : string.Empty;
        Debug.Log(
            $"[TTS] Provider: {service.Provider}{model}\n" +
            $"[TTS] Generation time: {milliseconds:F0} ms\n" +
            $"[TTS] Audio duration: {(clip != null ? clip.length : 0f):F2} s",
            this);
    }

    // Drops the cached greeting (and any in-flight pre-cache result).
    private void DiscardGreeting()
    {
        AudioClip stale = greetingClip;
        if (stale == null &&
            greetingPreparation != null &&
            greetingPreparation.IsCompleted &&
            !greetingPreparation.IsFaulted &&
            !greetingPreparation.IsCanceled)
        {
            stale = greetingPreparation.Result;
        }

        if (stale != null)
        {
            if (audioSource != null && audioSource.clip == stale)
            {
                audioSource.clip = null;
            }

            Destroy(stale);
        }

        greetingClip = null;
        greetingKey = null;
        greetingPreparation = null;
    }

    private void CancelActiveSpeech()
    {
        CancellationTokenSource cancellation = activeSpeechCancellation;
        activeSpeechCancellation = null;

        if (cancellation != null && !cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }
    }

    private void DestroyPreviousClip()
    {
        if (generatedClip == null)
        {
            return;
        }

        if (audioSource != null && audioSource.clip == generatedClip)
        {
            audioSource.clip = null;
        }

        Destroy(generatedClip);
        generatedClip = null;
    }

    private void OnDisable()
    {
        StopSpeaking();
    }

    private void OnDestroy()
    {
        StopSpeaking();

        if (lifetimeCancellation != null)
        {
            lifetimeCancellation.Cancel();
            lifetimeCancellation.Dispose();
            lifetimeCancellation = null;
        }

        DestroyPreviousClip();

        // The pre-cached greeting may never have been requested.
        DiscardGreeting();
    }
}
