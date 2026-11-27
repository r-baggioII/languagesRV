using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CafeGame.Interaction
{
    // VR equivalent of OrderInteractionController's desktop raycast-and-click flow:
    // selecting Remy or the counter with an XR ray requests the same conversation
    // start desktop uses (OrderZone.TryStartOrder()), through VrOrderRelocator so the
    // player is first relocated to the counter exactly like desktop's teleport-to-
    // OrderPosition step (see VrOrderRelocator for the relocation/fade sequence).
    //
    // Phase 3.1 change: the Phase 3 proximity gate (reject selection unless already
    // within ~2.5m) was removed. The user now explicitly wants selection to succeed
    // from the cafe's initial spawn, with relocation handling the rest - mirroring
    // desktop, which never required the player to already be standing at the counter.
    [RequireComponent(typeof(XRSimpleInteractable))]
    public sealed class VrConversationInteractable : MonoBehaviour
    {
        [SerializeField] private DialogueManager dialogueManager;
        [SerializeField] private OrderZone orderZone;
        [SerializeField] private VrOrderRelocator relocator;
        [SerializeField] private GameObject hoverHighlight;

        private XRSimpleInteractable interactable;
        private DialogueManager subscribedDialogueManager;

        private void Awake()
        {
            interactable = GetComponent<XRSimpleInteractable>();
        }

        private void OnEnable()
        {
            interactable.hoverEntered.AddListener(HandleHoverEntered);
            interactable.hoverExited.AddListener(HandleHoverExited);
            interactable.selectEntered.AddListener(HandleSelectEntered);

            if (dialogueManager == null)
            {
                Debug.LogError("[VrConversation] DialogueManager reference is not assigned.", this);
            }
            else
            {
                subscribedDialogueManager = dialogueManager;
                subscribedDialogueManager.StateChanged += HandleDialogueStateChanged;
                SynchronizeInteractable(dialogueManager.CurrentState);
            }

            if (orderZone == null)
            {
                Debug.LogError("[VrConversation] OrderZone reference is not assigned.", this);
            }

            if (relocator == null)
            {
                Debug.LogWarning(
                    "[VrConversation] VrOrderRelocator reference is not assigned; " +
                    "falling back to starting the order in place (no relocation/fade).",
                    this);
            }

            SetHighlight(false);
        }

        private void OnDisable()
        {
            interactable.hoverEntered.RemoveListener(HandleHoverEntered);
            interactable.hoverExited.RemoveListener(HandleHoverExited);
            interactable.selectEntered.RemoveListener(HandleSelectEntered);

            if (subscribedDialogueManager != null)
            {
                subscribedDialogueManager.StateChanged -= HandleDialogueStateChanged;
                subscribedDialogueManager = null;
            }

            SetHighlight(false);
        }

        private void HandleDialogueStateChanged(
            ConversationState previousState,
            ConversationState newState)
        {
            SynchronizeInteractable(newState);
        }

        // Only selectable before a visit has started. This mirrors OrderZone's own
        // one-shot `hasTriggered` guard, but also stops hover/select (and the
        // highlight) from firing once a visit is under way, instead of silently
        // no-opping on select every time the player re-points at Remy mid-conversation.
        private void SynchronizeInteractable(ConversationState state)
        {
            bool canStart = state == ConversationState.WaitingForPlayer;
            interactable.enabled = canStart;

            if (!canStart)
            {
                SetHighlight(false);
            }
        }

        private void HandleHoverEntered(HoverEnterEventArgs args)
        {
            Debug.Log($"[VR] {name} hovered by {args.interactorObject.transform.name}.", this);
            SetHighlight(true);
        }

        private void HandleHoverExited(HoverExitEventArgs args)
        {
            SetHighlight(false);
        }

        private void HandleSelectEntered(SelectEnterEventArgs args)
        {
            Debug.Log($"[VR] {name} selected by {args.interactorObject.transform.name}.", this);

            if (orderZone == null)
            {
                Debug.LogError("[VR] Cannot start the order: OrderZone reference is missing.", this);
                return;
            }

            Debug.Log("[VR] Conversation start requested.", this);

            if (relocator != null)
            {
                relocator.RequestOrderStart();
            }
            else
            {
                orderZone.TryStartOrder();
            }
        }

        private void SetHighlight(bool visible)
        {
            if (hoverHighlight != null)
            {
                hoverHighlight.SetActive(visible);
            }
        }
    }
}
