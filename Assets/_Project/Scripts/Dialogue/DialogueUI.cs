using TMPro;
using UnityEngine;

// Presentation only. Shows Remy's current line as a subtitle and reveals the
// push-to-talk control while a conversation is active. The line comes from
// DialogueManager.RemyMessageGenerated; the same Gemini reply is sent to speech.
public class DialogueUI : MonoBehaviour
{
    [SerializeField] private DialogueManager dialogueManager;
    [SerializeField] private GameObject subtitleRoot;
    [SerializeField] private TMP_Text subtitleText;
    [SerializeField] private GameObject voiceInputRoot;

    private DialogueManager subscribedDialogueManager;

    private void OnEnable()
    {
        ValidateReferences();

        if (dialogueManager == null)
        {
            SetVisible(false, false);
            return;
        }

        subscribedDialogueManager = dialogueManager;
        subscribedDialogueManager.StateChanged += HandleStateChanged;
        subscribedDialogueManager.RemyMessageGenerated += HandleRemyMessageGenerated;

        SynchronizeUI(dialogueManager.CurrentState);
    }

    private void OnDisable()
    {
        if (subscribedDialogueManager == null)
        {
            return;
        }

        subscribedDialogueManager.StateChanged -= HandleStateChanged;
        subscribedDialogueManager.RemyMessageGenerated -= HandleRemyMessageGenerated;
        subscribedDialogueManager = null;
    }

    private void HandleStateChanged(
        ConversationState previousState,
        ConversationState newState)
    {
        SynchronizeUI(newState);
    }

    private void HandleRemyMessageGenerated(string message)
    {
        // DialogueManager.CurrentRemyMessage already holds this same line.
        SynchronizeUI(dialogueManager.CurrentState);
    }

    private void SynchronizeUI(ConversationState state)
    {
        bool isWaitingForPlayer = state == ConversationState.WaitingForPlayer;
        bool isFinished = state == ConversationState.ConversationCompleted ||
                          state == ConversationState.Error;

        if (subtitleText != null)
        {
            subtitleText.text = isWaitingForPlayer
                ? string.Empty
                : dialogueManager.CurrentRemyMessage ?? string.Empty;
        }

        bool hasSubtitle = subtitleText != null &&
                           !string.IsNullOrEmpty(subtitleText.text);

        // Keep the final line visible, but hide the microphone after the visit.
        SetVisible(!isWaitingForPlayer && hasSubtitle, !isWaitingForPlayer && !isFinished);
    }

    private void SetVisible(bool subtitleVisible, bool voiceInputVisible)
    {
        if (subtitleRoot != null)
        {
            subtitleRoot.SetActive(subtitleVisible);
        }

        if (voiceInputRoot != null)
        {
            voiceInputRoot.SetActive(voiceInputVisible);
        }
    }

    private void ValidateReferences()
    {
        if (dialogueManager == null)
        {
            Debug.LogError("[DialogueUI] DialogueManager reference is not assigned.", this);
        }

        if (subtitleRoot == null)
        {
            Debug.LogError("[DialogueUI] Subtitle root reference is not assigned.", this);
        }

        if (subtitleText == null)
        {
            Debug.LogError("[DialogueUI] Subtitle text reference is not assigned.", this);
        }

        if (voiceInputRoot == null)
        {
            Debug.LogError("[DialogueUI] Voice input root reference is not assigned.", this);
        }
    }
}
