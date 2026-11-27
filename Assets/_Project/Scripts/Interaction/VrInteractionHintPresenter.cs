using TMPro;
using UnityEngine;

namespace CafeGame.Interaction
{
    // Presentation only, in the same spirit as DialogueUI / CafeMenuVisibilityController:
    // shows a short instruction while waiting for the player to start a visit, and
    // hides it once a conversation is under way. Holds no conversation logic of its own.
    //
    // Phase 3.1: no longer listens for a per-interactable "too far" hint - the Phase 3
    // proximity gate was removed (VrOrderRelocator now relocates the player instead of
    // rejecting a distant selection), so there is nothing left to raise that hint.
    public sealed class VrInteractionHintPresenter : MonoBehaviour
    {
        private const string DefaultHint =
            "Point at Remy or the counter, then squeeze Grip to order.";
        private const string RestartHint =
            "Visit complete. Point at RESTART and press the right trigger.";

        [SerializeField] private DialogueManager dialogueManager;
        [SerializeField] private TMP_Text hintText;

        private DialogueManager subscribedDialogueManager;

        private void OnEnable()
        {
            if (dialogueManager != null)
            {
                subscribedDialogueManager = dialogueManager;
                subscribedDialogueManager.StateChanged += HandleStateChanged;
            }

            Refresh(CurrentState());
        }

        private void OnDisable()
        {
            if (subscribedDialogueManager != null)
            {
                subscribedDialogueManager.StateChanged -= HandleStateChanged;
                subscribedDialogueManager = null;
            }
        }

        private void HandleStateChanged(
            ConversationState previousState,
            ConversationState newState)
        {
            Refresh(newState);
        }

        private ConversationState CurrentState()
        {
            return dialogueManager != null
                ? dialogueManager.CurrentState
                : ConversationState.WaitingForPlayer;
        }

        private void Refresh(ConversationState state)
        {
            if (hintText == null)
            {
                return;
            }

            hintText.text = state == ConversationState.WaitingForPlayer
                ? DefaultHint
                : state == ConversationState.ConversationCompleted
                    ? RestartHint
                    : string.Empty;
        }
    }
}
