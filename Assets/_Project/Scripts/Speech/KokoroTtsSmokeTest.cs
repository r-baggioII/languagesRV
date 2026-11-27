using System;
using UnityEngine;

// Developer-only check of Unity -> Kokoro server -> AudioClip -> Remy's
// AudioSource. Not part of normal runtime behaviour: leave runOnStart off.
public sealed class KokoroTtsSmokeTest : MonoBehaviour
{
    public const string SmokeTestLine =
        "Hi! What can I get for you today?";

    [SerializeField] private RemyVoiceController voiceController;
    [SerializeField] private bool runOnStart;

    public bool HasRun { get; private set; }
    public bool IsRunning { get; private set; }
    public bool RequestSucceeded { get; private set; }
    public bool AudioClipCreated { get; private set; }
    public bool AudioPlaybackStarted { get; private set; }
    public bool AudioPlaybackCompleted { get; private set; }
    public string ControlledError { get; private set; } = string.Empty;

    private void Start()
    {
        if (runOnStart)
        {
            RunSmokeTest();
        }
    }

    [ContextMenu("Run Kokoro TTS Smoke Test")]
    public async void RunSmokeTest()
    {
        if (IsRunning)
        {
            return;
        }

        HasRun = true;
        IsRunning = true;
        RequestSucceeded = false;
        AudioClipCreated = false;
        AudioPlaybackStarted = false;
        AudioPlaybackCompleted = false;
        ControlledError = string.Empty;

        if (voiceController == null)
        {
            ControlledError =
                "The RemyVoiceController reference is not assigned.";
            Debug.LogError(
                $"[KokoroTtsSmokeTest] Failed: {ControlledError}",
                this);
            IsRunning = false;
            return;
        }

        Debug.Log(
            $"[KokoroTtsSmokeTest] Starting with provider " +
            $"{voiceController.Provider} (Kokoro: voice " +
            $"{voiceController.Voice} via {voiceController.ServerUrl}).",
            this);

        try
        {
            RemySpeechResult result =
                await voiceController.SpeakAsync(SmokeTestLine);

            RequestSucceeded = result.RequestSucceeded;
            AudioClipCreated = result.ClipCreated &&
                               result.Clip != null &&
                               result.Clip.samples > 0 &&
                               result.Clip.length > 0f;
            AudioPlaybackStarted = result.PlaybackStarted;
            AudioPlaybackCompleted = result.PlaybackCompleted;
            ControlledError = result.SafeError;

            if (!RequestSucceeded || !AudioClipCreated ||
                !AudioPlaybackStarted || !AudioPlaybackCompleted)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(ControlledError)
                        ? "One or more TTS smoke-test assertions failed."
                        : ControlledError);
            }

            Debug.Log(
                $"[KokoroTtsSmokeTest] Request succeeded; AudioClip " +
                $"created ({result.Clip.frequency} Hz, " +
                $"{result.Clip.channels} channel, " +
                $"{result.Clip.length:F2}s); playback completed from Remy.",
                this);
        }
        catch (Exception exception)
        {
            ControlledError = exception.Message;
            Debug.LogError(
                $"[KokoroTtsSmokeTest] Failed: {ControlledError}",
                this);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
