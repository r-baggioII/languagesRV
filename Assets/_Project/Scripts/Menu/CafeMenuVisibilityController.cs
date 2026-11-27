using UnityEngine;

public class CafeMenuVisibilityController : MonoBehaviour
{
    [SerializeField] private DialogueManager dialogueManager;
    [SerializeField] private GameObject cafeMenuPanel;

    private void OnEnable()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.StateChanged += HandleStateChanged;
        SynchronizeVisibility(dialogueManager.CurrentState);
    }

    private void OnDisable()
    {
        if (dialogueManager != null)
        {
            dialogueManager.StateChanged -= HandleStateChanged;
        }
    }

    private void HandleStateChanged(
        ConversationState previousState,
        ConversationState currentState)
    {
        SynchronizeVisibility(currentState);
    }

    private void SynchronizeVisibility(ConversationState state)
    {
        if (cafeMenuPanel == null)
        {
            return;
        }

        bool shouldBeVisible = state == ConversationState.StartingConversation ||
                               state == ConversationState.RemySpeaking ||
                               state == ConversationState.WaitingForUser ||
                               state == ConversationState.ProcessingUserInput ||
                               state == ConversationState.GeneratingResponse;

        if (cafeMenuPanel.activeSelf != shouldBeVisible)
        {
            cafeMenuPanel.SetActive(shouldBeVisible);
        }
    }
}
