using System;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace CafeGame.Interaction
{
    // VR equivalent of OrderInteractionController's desktop teleport-to-OrderPosition
    // step. Unlike the Phase 3 design (which rejected a far-away selection with a
    // "move closer" hint), the player is now relocated to the same OrderPosition
    // desktop uses, behind a short fade-to-black, and ONLY THEN is
    // OrderZone.TryStartOrder() called - so the existing conversation flow is
    // reached no matter where in the cafe the player stood when they selected
    // Remy/the counter.
    //
    // Relocation reuses XRI's own TeleportationProvider (QueueTeleportRequest) rather
    // than setting the XR Origin or camera transform directly, so the camera-to-floor
    // offset and tracked head position are handled the same correct way a player-
    // initiated teleport would be.
    public sealed class VrOrderRelocator : MonoBehaviour
    {
        [SerializeField] private DialogueManager dialogueManager;
        [SerializeField] private OrderZone orderZone;
        [SerializeField] private TeleportationProvider teleportationProvider;
        [SerializeField] private Transform destination;
        [SerializeField] private Transform lookAtTarget;
        [SerializeField] private VrScreenFader screenFader;

        [Header("Eye height at the counter")]
        [Tooltip("Meters added to Remy's head height when placing the player's eyes. " +
                 "Negative values sit lower. Only used when lookAtTarget has a humanoid Animator.")]
        [SerializeField] private float eyeHeightOffsetFromHead = 0f;

        [Header("Fade timing (seconds) - short and deterministic")]
        [SerializeField, Min(0f)] private float fadeOutSeconds = 0.15f;
        [SerializeField, Min(0f)] private float holdBlackSeconds = 0.1f;
        [SerializeField, Min(0f)] private float fadeInSeconds = 0.2f;

        [Header("Locomotion providers to pause while a conversation is active (optional)")]
        [SerializeField] private GameObject[] locomotionProvidersToSuspend = Array.Empty<GameObject>();

        private bool requestInFlight;
        private DialogueManager subscribedDialogueManager;

        private void OnEnable()
        {
            if (dialogueManager != null)
            {
                subscribedDialogueManager = dialogueManager;
                subscribedDialogueManager.StateChanged += HandleStateChanged;
            }
        }

        private void OnDisable()
        {
            if (subscribedDialogueManager != null)
            {
                subscribedDialogueManager.StateChanged -= HandleStateChanged;
                subscribedDialogueManager = null;
            }
        }

        // Called by VrConversationInteractable when Remy or the counter is selected.
        public void RequestOrderStart()
        {
            if (requestInFlight)
            {
                return;
            }

            if (orderZone == null)
            {
                Debug.LogError("[VR] Cannot start the order: OrderZone reference is missing.", this);
                return;
            }

            requestInFlight = true;
            _ = RelocateAndStartAsync();
        }

        private async Task RelocateAndStartAsync()
        {
            try
            {
                Debug.Log("[VR] Relocating player to counter.", this);

                if (screenFader != null)
                {
                    await screenFader.FadeToBlackAsync(fadeOutSeconds);
                }

                if (holdBlackSeconds > 0f)
                {
                    await Task.Delay(Mathf.RoundToInt(holdBlackSeconds * 1000f));
                }

                Relocate();

                // Give the TeleportationProvider a frame to process the queued
                // request before revealing the new position.
                await Task.Yield();
                await Task.Yield();

                if (screenFader != null)
                {
                    await screenFader.FadeFromBlackAsync(fadeInSeconds);
                }

                Debug.Log("[VR] Conversation start requested.", this);
                orderZone.TryStartOrder();
            }
            finally
            {
                requestInFlight = false;
            }
        }

        private void Relocate()
        {
            if (destination == null)
            {
                Debug.LogError("[VR] No destination Transform assigned; skipping relocation.", this);
                return;
            }

            if (teleportationProvider == null)
            {
                Debug.LogError("[VR] No TeleportationProvider assigned; skipping relocation.", this);
                return;
            }

            Quaternion rotation = ComputeFacingRotation();
            Vector3 destinationPosition = ComputeDestinationPosition();

            var request = new TeleportRequest
            {
                destinationPosition = destinationPosition,
                destinationRotation = rotation,
                matchOrientation = MatchOrientation.TargetUpAndForward,
                requestTime = Time.time
            };

            bool queued = teleportationProvider.QueueTeleportRequest(request);
            Debug.Log($"[VR] Teleport request queued={queued} to {destinationPosition}.", this);
        }

        // XRI lands the body's ground point on destinationPosition and the head sits
        // CameraInOriginSpaceHeight above it (the simulator's fixed offset, or the
        // player's real tracked height on a headset). Solving for the floor height
        // here puts the player's eyes at Remy's head height for any player height,
        // instead of "destination height + however tall the player is".
        private Vector3 ComputeDestinationPosition()
        {
            Vector3 position = destination.position;

            if (lookAtTarget == null)
            {
                return position;
            }

            Animator remyAnimator = lookAtTarget.GetComponentInChildren<Animator>();
            Transform head = remyAnimator != null && remyAnimator.isHuman
                ? remyAnimator.GetBoneTransform(HumanBodyBones.Head)
                : null;
            XROrigin xrOrigin = teleportationProvider.GetComponentInParent<XROrigin>();

            if (head == null || xrOrigin == null)
            {
                return position;
            }

            position.y = head.position.y + eyeHeightOffsetFromHead - xrOrigin.CameraInOriginSpaceHeight;
            return position;
        }

        private Quaternion ComputeFacingRotation()
        {
            if (lookAtTarget == null)
            {
                return destination.rotation;
            }

            Vector3 flatDirection = lookAtTarget.position - destination.position;
            flatDirection.y = 0f;

            return flatDirection.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(flatDirection.normalized, Vector3.up)
                : destination.rotation;
        }

        private void HandleStateChanged(
            ConversationState previousState,
            ConversationState newState)
        {
            SetLocomotionSuspended(IsDialogueState(newState));
        }

        private void SetLocomotionSuspended(bool suspended)
        {
            for (int i = 0; i < locomotionProvidersToSuspend.Length; i++)
            {
                if (locomotionProvidersToSuspend[i] != null)
                {
                    locomotionProvidersToSuspend[i].SetActive(!suspended);
                }
            }
        }

        private static bool IsDialogueState(ConversationState state)
        {
            switch (state)
            {
                case ConversationState.StartingConversation:
                case ConversationState.RemySpeaking:
                case ConversationState.WaitingForUser:
                case ConversationState.ProcessingUserInput:
                case ConversationState.GeneratingResponse:
                    return true;

                default:
                    return false;
            }
        }
    }
}
