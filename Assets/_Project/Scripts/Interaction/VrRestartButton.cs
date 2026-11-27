using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace CafeGame.Interaction
{
    // A physical XR button shown after Remy's final line. It restarts the visit when
    // it is selected through XRI (the same select event that starts the conversation
    // on Remy, which is also what a mouse click raises in the XR Interaction
    // Simulator while developing on PC), or when the right trigger is pressed while
    // the right controller's ray points at it (headset).
    [RequireComponent(typeof(BoxCollider), typeof(XRSimpleInteractable), typeof(MeshRenderer))]
    public sealed class VrRestartButton : MonoBehaviour
    {
        [SerializeField] private DialogueManager dialogueManager;
        [SerializeField] private OrderZone orderZone;
        [SerializeField] private TeleportationProvider teleportationProvider;
        [SerializeField] private Transform xrOrigin;
        [SerializeField] private Transform view;
        [SerializeField] private XRRayInteractor rightSelectionRay;
        [SerializeField] private TMP_Text label;

        private static readonly Color NormalColor = new Color(0.04f, 0.22f, 0.25f);
        private static readonly Color HoverColor = new Color(0.08f, 0.44f, 0.49f);
        private static readonly Vector3 ViewOffset = new Vector3(0.27f, -0.24f, 1.05f);

        private BoxCollider hitBox;
        private XRSimpleInteractable interactable;
        private MeshRenderer buttonRenderer;
        private MaterialPropertyBlock properties;
        private InputAction rightTrigger;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool restarting;

        private void Awake()
        {
            hitBox = GetComponent<BoxCollider>();
            interactable = GetComponent<XRSimpleInteractable>();
            buttonRenderer = GetComponent<MeshRenderer>();
            properties = new MaterialPropertyBlock();
            rightTrigger = new InputAction(
                "Restart with right trigger",
                InputActionType.Button,
                "<XRController>{RightHand}/{TriggerButton}");

            if (dialogueManager == null || orderZone == null ||
                teleportationProvider == null || xrOrigin == null ||
                view == null || rightSelectionRay == null || label == null)
            {
                Debug.LogError("[VRRestart] Scene references are incomplete.", this);
                enabled = false;
                return;
            }

            spawnPosition = xrOrigin.position;
            spawnRotation = xrOrigin.rotation;
            SetColor(NormalColor);
        }

        private void OnEnable()
        {
            if (dialogueManager == null || interactable == null)
            {
                return;
            }

            dialogueManager.StateChanged += OnStateChanged;
            interactable.hoverEntered.AddListener(OnHoverEntered);
            interactable.hoverExited.AddListener(OnHoverExited);
            interactable.selectEntered.AddListener(OnSelectEntered);
            rightTrigger.Enable();
            ShowIfFinished(dialogueManager.CurrentState);
        }

        private void OnDisable()
        {
            if (dialogueManager != null)
            {
                dialogueManager.StateChanged -= OnStateChanged;
            }

            if (interactable != null)
            {
                interactable.hoverEntered.RemoveListener(OnHoverEntered);
                interactable.hoverExited.RemoveListener(OnHoverExited);
                interactable.selectEntered.RemoveListener(OnSelectEntered);
            }

            rightTrigger?.Disable();
        }

        private void OnDestroy()
        {
            rightTrigger?.Dispose();
        }

        private void Update()
        {
            if (dialogueManager.CurrentState != ConversationState.ConversationCompleted ||
                !rightTrigger.WasPressedThisFrame())
            {
                return;
            }

            foreach (var hoveringInteractor in interactable.interactorsHovering)
            {
                if (hoveringInteractor == (IXRHoverInteractor)rightSelectionRay)
                {
                    Debug.Log("[VRRestart] Restart requested with the right trigger.", this);
                    Restart();
                    return;
                }
            }
        }

        private void OnStateChanged(ConversationState previous, ConversationState current)
        {
            ShowIfFinished(current);
        }

        private void ShowIfFinished(ConversationState state)
        {
            bool visible = state == ConversationState.ConversationCompleted;
            if (visible)
            {
                transform.SetPositionAndRotation(view.TransformPoint(ViewOffset), view.rotation);
                SetColor(NormalColor);
            }
            // Keep this component active so it can observe the next state change.
            if (!visible)
            {
                interactable.enabled = false;
            }

            hitBox.enabled = visible;
            buttonRenderer.enabled = visible;
            label.enabled = visible;

            if (visible)
            {
                interactable.enabled = true;
            }
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            Debug.Log($"[VRRestart] Selected by {args.interactorObject.transform.name}.", this);
            Restart();
        }

        private void OnHoverEntered(HoverEnterEventArgs args)
        {
            SetColor(HoverColor);
        }

        private void OnHoverExited(HoverExitEventArgs args)
        {
            SetColor(NormalColor);
        }

        public void Restart()
        {
            if (restarting || dialogueManager.CurrentState != ConversationState.ConversationCompleted)
            {
                return;
            }

            restarting = true;
            ShowIfFinished(ConversationState.WaitingForPlayer);

            // The dialogue reset also closes subtitles/mic UI and clears the
            // Gemini interaction history. OrderZone's guard must be cleared so
            // Remy can be selected again after returning to the entrance.
            orderZone.ResetZone();
            dialogueManager.ResetConversation();

            var request = new TeleportRequest
            {
                destinationPosition = spawnPosition,
                destinationRotation = spawnRotation,
                matchOrientation = MatchOrientation.TargetUpAndForward,
                requestTime = Time.time
            };

            if (!teleportationProvider.QueueTeleportRequest(request))
            {
                Debug.LogWarning("[VRRestart] Teleport was busy; restoring the spawn pose directly.", this);
                xrOrigin.SetPositionAndRotation(spawnPosition, spawnRotation);
            }

            restarting = false;
        }

        private void SetColor(Color color)
        {
            if (buttonRenderer == null)
            {
                return;
            }

            properties.SetColor("_BaseColor", color);
            buttonRenderer.SetPropertyBlock(properties);
        }
    }
}
