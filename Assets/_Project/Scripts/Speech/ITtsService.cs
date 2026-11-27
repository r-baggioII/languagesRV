using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public interface ITtsService
{
    TtsProvider Provider { get; }

    // Model name for diagnostics; null when the provider has no such concept.
    string Model { get; }

    // Identifies every setting that changes the audio (provider, model,
    // voice, speed). Cached clips are only reused under an identical key.
    string CacheKey { get; }

    // True when the greeting may be synthesised ahead of time at startup
    // (free local inference). Metered cloud providers return false so Play
    // Mode never spends credits unless Remy actually speaks.
    bool PrecachesGreeting { get; }

    // Converts Remy's final line into a playable clip. Must be called from
    // Unity's main thread (it drives UnityWebRequest). The caller owns the
    // returned clip. Throws TtsException on failure and
    // OperationCanceledException when cancelled.
    Task<AudioClip> SynthesizeAsync(
        string text,
        SpeechTimingLogger.Trace timing = null,
        CancellationToken cancellationToken = default);

    // Reachability probe. Never throws; false means "not usable right now".
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class TtsException : Exception
{
    public TtsException(string message) : base(message)
    {
    }

    public TtsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
