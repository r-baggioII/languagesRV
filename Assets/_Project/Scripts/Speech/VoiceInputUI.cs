using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Status display for the VR controller push-to-talk flow. The left controller
// trigger owns capture; this component only presents state and partial text.
public sealed class VoiceInputUI : MonoBehaviour
{
    private const float StatusMessageSeconds = 3.5f;

    private static readonly Color IdleColor = new Color(0.12f, 0.48f, 0.68f, 1f);
    private static readonly Color ListeningColor = new Color(0.85f, 0.17f, 0.17f, 1f);
    private static readonly Color BusyColor = new Color(0.45f, 0.5f, 0.55f, 1f);

    [SerializeField] private VoiceInputController voiceInput;
    [SerializeField] private Image micBackground;
    [SerializeField] private TMP_Text micLabel;
    [SerializeField] private TMP_Text statusText;

    private string partialTranscript = string.Empty;
    private string transientMessage = string.Empty;
    private float transientMessageUntil;

    private void OnEnable()
    {
        if (voiceInput == null)
        {
            Debug.LogError("[VoiceInputUI] VoiceInputController reference is not assigned.", this);
            return;
        }

        voiceInput.PartialTranscriptChanged += HandlePartialChanged;
        voiceInput.StatusMessage += HandleStatusMessage;
        voiceInput.PhaseChanged += HandlePhaseChanged;
        Refresh();
    }

    private void OnDisable()
    {
        if (voiceInput != null)
        {
            voiceInput.PartialTranscriptChanged -= HandlePartialChanged;
            voiceInput.StatusMessage -= HandleStatusMessage;
            voiceInput.PhaseChanged -= HandlePhaseChanged;
        }
    }

    private void Update()
    {
        // Availability also depends on Remy's audio, which has no event.
        Refresh();
    }

    private void HandlePartialChanged(string partial)
    {
        partialTranscript = partial ?? string.Empty;
        Refresh();
    }

    private void HandleStatusMessage(string message)
    {
        transientMessage = message ?? string.Empty;
        transientMessageUntil = Time.unscaledTime + StatusMessageSeconds;
        Refresh();
    }

    private void HandlePhaseChanged(VoiceInputPhase phase)
    {
        if (phase != VoiceInputPhase.Listening &&
            phase != VoiceInputPhase.Finalizing)
        {
            partialTranscript = string.Empty;
        }

        Refresh();
    }

    private void Refresh()
    {
        if (voiceInput == null)
        {
            return;
        }

        VoiceInputPhase phase = voiceInput.Phase;
        bool listening = phase == VoiceInputPhase.Listening;

        if (micBackground != null)
        {
            Color target = listening
                ? Color.Lerp(
                    ListeningColor,
                    Color.white,
                    0.25f * Mathf.PingPong(Time.unscaledTime * 2f, 1f))
                : phase == VoiceInputPhase.Ready ? IdleColor : BusyColor;
            micBackground.color = target;
        }

        if (micLabel != null)
        {
            micLabel.text = listening
                ? "Listening..."
                : phase == VoiceInputPhase.Finalizing
                    ? "Processing..."
                    : "Hold " + voiceInput.PushToTalkKeyLabel + " to talk";
        }

        if (statusText != null)
        {
            statusText.text = BuildStatusText(phase);
        }
    }

    private string BuildStatusText(VoiceInputPhase phase)
    {
        switch (phase)
        {
            case VoiceInputPhase.Listening:
            case VoiceInputPhase.Finalizing:
                return FormatPartial();

            case VoiceInputPhase.Ready:
                return !string.IsNullOrEmpty(transientMessage) &&
                       Time.unscaledTime < transientMessageUntil
                    ? "<color=#FFD166>" + transientMessage + "</color>"
                    : string.Empty;

            default:
                return string.Empty;
        }
    }

    private string FormatPartial()
    {
        if (string.IsNullOrWhiteSpace(partialTranscript))
        {
            return string.Empty;
        }

        // Transcript text is untrusted for TMP rich-text purposes.
        string safe = partialTranscript.Trim().Replace("<", string.Empty);
        return "<i><color=#C9D3DD>" + safe + "</color></i>";
    }
}
