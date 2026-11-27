using UnityEngine;
using UnityEngine.InputSystem;

// VR push-to-talk adapter. Reads the left controller trigger through the Input
// System and drives VoiceInputController.BeginListening/EndListening.
//
// Grip selects interactables with either ray. The left trigger is reserved for
// talking while the player is in a conversational turn.
public sealed class VrPushToTalkController : MonoBehaviour
{
    public const string ControlLabel = "left Trigger";

    [SerializeField] private VoiceInputController voiceInputController;

    [Header("Input (defaults to XRI Left Interaction/Activate = left-hand Trigger)")]
    [SerializeField] private InputActionAsset actionsAsset;
    [SerializeField] private string actionMapName = "XRI Left Interaction";
    [SerializeField] private string actionName = "Activate";

    private InputAction talkAction;
    private bool isHeld;

    private void OnEnable()
    {
        if (voiceInputController == null)
        {
            Debug.LogError("[VrPushToTalk] VoiceInputController reference is not assigned.", this);
            return;
        }

        talkAction = ResolveAction();

        if (talkAction == null)
        {
            Debug.LogError(
                $"[VrPushToTalk] Could not find action '{actionMapName}/{actionName}' " +
                "on the assigned Input Action Asset.",
                this);
            return;
        }

        talkAction.performed += HandlePerformed;
        talkAction.canceled += HandleCanceled;
        talkAction.Enable();
    }

    private void OnDisable()
    {
        if (talkAction != null)
        {
            talkAction.performed -= HandlePerformed;
            talkAction.canceled -= HandleCanceled;
        }

        // Covers interruption mid-hold: this object disabled, the scene tearing
        // down, or a conversation reset. VoiceInputController.CancelListening()
        // is a no-op if nothing is in flight.
        if (isHeld)
        {
            isHeld = false;
            voiceInputController?.CancelListening();
        }
    }

    private InputAction ResolveAction()
    {
        return actionsAsset != null
            ? actionsAsset.FindActionMap(actionMapName)?.FindAction(actionName)
            : null;
    }

    private void HandlePerformed(InputAction.CallbackContext context)
    {
        if (isHeld || voiceInputController == null)
        {
            return;
        }

        // BeginListening() itself re-checks dialogue state / Remy's audio / mic
        // availability (VoiceInputController.CanBeginListening); a false return
        // (e.g. Remy currently speaking) correctly leaves isHeld false so the
        // matching release is a no-op instead of ending a turn that never began.
        isHeld = voiceInputController.BeginListening();
    }

    private void HandleCanceled(InputAction.CallbackContext context)
    {
        if (!isHeld)
        {
            return;
        }

        isHeld = false;
        voiceInputController.EndListening();
    }
}
