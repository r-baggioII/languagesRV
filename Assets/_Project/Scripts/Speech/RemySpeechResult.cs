using UnityEngine;

public sealed class RemySpeechResult
{
    private RemySpeechResult(
        bool requestSucceeded,
        bool clipCreated,
        bool playbackStarted,
        bool playbackCompleted,
        AudioClip clip,
        string safeError)
    {
        RequestSucceeded = requestSucceeded;
        ClipCreated = clipCreated;
        PlaybackStarted = playbackStarted;
        PlaybackCompleted = playbackCompleted;
        Clip = clip;
        SafeError = safeError ?? string.Empty;
    }

    public bool RequestSucceeded { get; }
    public bool ClipCreated { get; }
    public bool PlaybackStarted { get; }
    public bool PlaybackCompleted { get; }
    public AudioClip Clip { get; }
    public string SafeError { get; }

    public static RemySpeechResult Succeeded(AudioClip clip)
    {
        return new RemySpeechResult(
            true,
            clip != null,
            true,
            true,
            clip,
            string.Empty);
    }

    public static RemySpeechResult Failed(
        string safeError,
        bool requestSucceeded = false,
        AudioClip clip = null,
        bool playbackStarted = false)
    {
        return new RemySpeechResult(
            requestSucceeded,
            clip != null,
            playbackStarted,
            false,
            clip,
            safeError);
    }
}
