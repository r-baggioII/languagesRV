using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public enum VoiceInputPhase
{
    Unavailable,
    Ready,
    Listening,
    Finalizing
}

// Push-to-talk speech input. Microphone -> Gemini Transcribe Live -> final
// transcript -> DialogueManager.SubmitPlayerMessage(). DialogueManager stays the
// only authority over conversation state; this component only observes it.
public sealed class VoiceInputController : MonoBehaviour
{
    private const int SampleRate = MicrophonePcmCapture.TargetSampleRate;

    [SerializeField] private DialogueManager dialogueManager;
    [SerializeField] private RemyVoiceController remyVoiceController;
    [SerializeField] private string preferredMicrophone = string.Empty;
    [SerializeField, Range(0f, 0.5f)] private float releaseTailSeconds = 0.15f;
    [SerializeField, Min(0.1f)] private float minimumSpeechSeconds = 0.3f;
    [SerializeField, Min(1f)] private float maximumSpeechSeconds = 30f;

    private ISttService sttService;
    private ISttSession session;
    private MicrophonePcmCapture capture;
    private SpeechTimingLogger.SttTrace trace;
    private CancellationTokenSource lifetime;
    private DialogueManager subscribedDialogueManager;
    private float listeningStartedAt;
    private int turnVersion;
    private volatile string pendingPartial;
    private string publishedPartial = string.Empty;

    public VoiceInputPhase Phase { get; private set; } = VoiceInputPhase.Unavailable;
    public string LastDeviceName => capture != null ? capture.DeviceName : string.Empty;
    public string PushToTalkKeyLabel => VrPushToTalkController.ControlLabel;

    public event Action<VoiceInputPhase> PhaseChanged;
    public event Action<string> PartialTranscriptChanged;
    public event Action<string> StatusMessage;
    public event Action<string> TranscriptSubmitted;

    // Microphone input is allowed only while the conversation waits for the
    // learner, and never while Remy's voice is audible (his own speech would
    // otherwise be transcribed as the learner's).
    public bool CanBeginListening =>
        Phase == VoiceInputPhase.Ready &&
        dialogueManager != null &&
        dialogueManager.CurrentState == ConversationState.WaitingForUser &&
        !IsRemyAudible;

    private bool IsRemyAudible =>
        remyVoiceController != null && remyVoiceController.IsSpeaking;

    private void Awake()
    {
        capture = new MicrophonePcmCapture();
        sttService = new GeminiLiveTranscriptionService();
    }

    private void OnEnable()
    {
        lifetime = new CancellationTokenSource();

        if (dialogueManager == null)
        {
            Debug.LogError(
                "[VoiceInput] DialogueManager reference is not assigned.",
                this);
            return;
        }

        subscribedDialogueManager = dialogueManager;
        subscribedDialogueManager.StateChanged += HandleStateChanged;
        SetPhase(PhaseForState(dialogueManager.CurrentState));
    }

    private void OnDisable()
    {
        if (subscribedDialogueManager != null)
        {
            subscribedDialogueManager.StateChanged -= HandleStateChanged;
            subscribedDialogueManager = null;
        }

        CancelListening();
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        SetPhase(VoiceInputPhase.Unavailable);
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // A key/button release outside the window would never reach us.
        if (!hasFocus)
        {
            CancelListening();
        }
    }

    private void Update()
    {
        PublishPartialTranscript();

        if (!capture.IsRecording)
        {
            return;
        }

        if (!IsListeningAllowed())
        {
            CancelListening();
            return;
        }

        capture.Pump(SendChunk);

        if (session != null && session.IsFailed)
        {
            ReportFailureAndReset("Voice input lost its connection.");
            return;
        }

        if (Phase == VoiceInputPhase.Listening &&
            Time.realtimeSinceStartup - listeningStartedAt > maximumSpeechSeconds)
        {
            EndListening();
        }
    }

    // Press / hold: opens the microphone and a transcription session.
    public bool BeginListening()
    {
        if (!CanBeginListening)
        {
            return false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
            StatusMessage?.Invoke("Allow microphone access, then press left Trigger again.");
            return false;
        }
#endif

        if (!capture.TryStart(preferredMicrophone, out string error))
        {
            Debug.LogWarning($"[VoiceInput] {error}", this);
            StatusMessage?.Invoke("No microphone available.");
            return false;
        }

        turnVersion++;
        trace = SpeechTimingLogger.LogSttMicrophoneStarted(capture.DeviceName);

        try
        {
            session = sttService.StartSession(trace, lifetime.Token);
        }
        catch (SttException exception)
        {
            capture.Stop(null);
            trace = null;
            Debug.LogWarning($"[VoiceInput] {exception.Message}", this);
            StatusMessage?.Invoke("Voice input is not configured.");
            return false;
        }

        session.PartialTranscriptReceived += HandlePartialTranscript;
        pendingPartial = string.Empty;
        listeningStartedAt = Time.realtimeSinceStartup;
        SetPhase(VoiceInputPhase.Listening);
        return true;
    }

    // Release: ends the speech turn and submits the final transcript.
    public void EndListening()
    {
        if (Phase != VoiceInputPhase.Listening)
        {
            return;
        }

        SetPhase(VoiceInputPhase.Finalizing);
        _ = FinishTurnAsync(turnVersion);
    }

    // Drops the turn without submitting anything.
    public void CancelListening()
    {
        if (Phase != VoiceInputPhase.Listening &&
            Phase != VoiceInputPhase.Finalizing &&
            !capture.IsRecording)
        {
            return;
        }

        turnVersion++;
        StopMicrophone(flush: false);
        DropSession();
        ClearPartial();

        SetPhase(dialogueManager != null
            ? PhaseForState(dialogueManager.CurrentState)
            : VoiceInputPhase.Unavailable);
    }

    private async Task FinishTurnAsync(int version)
    {
        // Keep the microphone open a moment: Unity's capture buffer lags the
        // real input, and clipping the last word is worse than a short tail.
        if (releaseTailSeconds > 0f)
        {
            await Task.Delay(Mathf.RoundToInt(releaseTailSeconds * 1000f));
        }

        if (!IsCurrentTurn(version))
        {
            return;
        }

        StopMicrophone(flush: true);

        float capturedSeconds =
            capture.TotalSamplesProduced / (float)SampleRate;
        Debug.Log(
            $"[VoiceInput] Captured {capturedSeconds:F2}s from " +
            $"'{capture.DeviceName}' (peak level {capture.PeakLevel:F3}).",
            this);

        ISttSession activeSession = session;
        SpeechTimingLogger.SttTrace activeTrace = trace;

        if (activeSession == null)
        {
            return;
        }

        if (capturedSeconds < minimumSpeechSeconds)
        {
            DropSession();
            ClearPartial();
            StatusMessage?.Invoke("Hold the microphone button while you speak.");
            SetPhase(PhaseForState(dialogueManager.CurrentState));
            return;
        }

        SttResult result = await activeSession.CompleteAsync();

        if (!IsCurrentTurn(version) ||
            dialogueManager.CurrentState != ConversationState.WaitingForUser)
        {
            // The conversation moved on while we were transcribing.
            Debug.Log("[VoiceInput] Discarded transcript: conversation state changed.", this);
            return;
        }

        session = null;
        trace = null;
        ClearPartial();
        SetPhase(VoiceInputPhase.Ready);

        if (!result.HasFinalTranscript)
        {
            HandleNoFinalTranscript(result);
            return;
        }

        SubmitTranscript(result.FinalTranscript, activeTrace);
    }

    private void HandleNoFinalTranscript(SttResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            Debug.LogWarning($"[VoiceInput] {result.Error}", this);
        }

        // A partial is only a hypothesis: it is never submitted.
        StatusMessage?.Invoke("I didn't catch that. Please try again.");
    }

    private void SubmitTranscript(
        string transcript,
        SpeechTimingLogger.SttTrace activeTrace)
    {
        Debug.Log($"[VoiceInput] Final transcript: \"{transcript}\"", this);
        SpeechTimingLogger.LogSttTranscriptSubmitted(activeTrace);

        dialogueManager.SubmitPlayerMessage(transcript);

        TranscriptSubmitted?.Invoke(transcript);
    }

    private void HandleStateChanged(
        ConversationState previousState,
        ConversationState newState)
    {
        if (newState != ConversationState.WaitingForUser)
        {
            // Stop capturing immediately when leaving WaitingForUser.
            CancelListening();
        }

        SetPhase(PhaseForState(newState));
    }

    private void HandlePartialTranscript(string partial)
    {
        // May be raised off the main thread; published from Update().
        pendingPartial = partial;
    }

    private void PublishPartialTranscript()
    {
        string partial = pendingPartial;
        if (partial == null || partial == publishedPartial)
        {
            return;
        }

        publishedPartial = partial;
        PartialTranscriptChanged?.Invoke(partial);
    }

    private void ClearPartial()
    {
        pendingPartial = string.Empty;
        if (publishedPartial.Length > 0)
        {
            publishedPartial = string.Empty;
            PartialTranscriptChanged?.Invoke(string.Empty);
        }
    }

    private void SendChunk(byte[] bytes, int byteCount)
    {
        session?.SendAudio(bytes, byteCount);
    }

    private void StopMicrophone(bool flush)
    {
        if (capture.IsRecording)
        {
            capture.Stop(flush ? SendChunk : (Action<byte[], int>)null);
        }
    }

    private void DropSession()
    {
        if (session == null)
        {
            return;
        }

        session.PartialTranscriptReceived -= HandlePartialTranscript;
        session.Cancel();
        session = null;
        trace = null;
    }

    private void ReportFailureAndReset(string message)
    {
        Debug.LogWarning($"[VoiceInput] {message}", this);
        CancelListening();
        StatusMessage?.Invoke("Voice input failed. Please try again.");
    }

    private bool IsListeningAllowed()
    {
        return dialogueManager != null &&
               dialogueManager.CurrentState == ConversationState.WaitingForUser &&
               !IsRemyAudible;
    }

    private bool IsCurrentTurn(int version)
    {
        return this != null &&
               version == turnVersion &&
               Phase == VoiceInputPhase.Finalizing;
    }

    private static VoiceInputPhase PhaseForState(ConversationState state)
    {
        return state == ConversationState.WaitingForUser
            ? VoiceInputPhase.Ready
            : VoiceInputPhase.Unavailable;
    }

    private void SetPhase(VoiceInputPhase phase)
    {
        if (Phase == phase)
        {
            return;
        }

        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }
}
