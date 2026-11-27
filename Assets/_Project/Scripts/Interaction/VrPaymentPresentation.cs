using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Plays the final payment gesture in Cafe_VR. The XR controller remains
// tracked while its model is hidden.
public sealed class VrPaymentPresentation : MonoBehaviour
{
    [SerializeField] private DialogueManager dialogueManager;
    [SerializeField] private Transform paymentHand;
    [SerializeField] private GameObject cash;
    [SerializeField] private GameObject card;
    [SerializeField] private Transform rightController;

    private static readonly Vector3 Pocket = new Vector3(0.30f, -0.78f, 0.05f);
    private static readonly Vector3 Reveal = new Vector3(0.28f, -0.23f, 0.55f);
    private static readonly Vector3 Offer = new Vector3(0.19f, -0.34f, 0.72f);

    // The glove has its fingers along +Y and its palm facing -Z.
    // This base pose points the fingers forward with the back of the hand (nails)
    // up and the thumb on the left, i.e. a right hand held palm down.
    private static readonly Quaternion HandBase = Quaternion.LookRotation(Vector3.up, Vector3.forward);

    // Roll about the forward axis. Negative is clockwise from the player's view,
    // so the thumb sweeps left -> up -> right and ends palm up, thumb on the right.
    private const float NailsUpRoll = 0f;
    private const float PalmUpRoll = -180f;
    private const float RevealRoll = -75f;
    private const float FingerPitchUp = 10f;
    private const float FingerYawInward = -10f;

    private Renderer[] controllerVisuals;
    private bool[] controllerVisualStates;
    private Coroutine gesture;
    private bool hasPlayed;
    private bool controllerVisualsHidden;

    private void Awake()
    {
        if (dialogueManager == null || paymentHand == null || cash == null ||
            card == null || rightController == null)
        {
            Debug.LogError("[VrPayment] Payment scene references are incomplete.", this);
            enabled = false;
            return;
        }

        var visuals = new List<Renderer>();
        foreach (Renderer candidate in rightController.GetComponentsInChildren<Renderer>(true))
        {
            if (!(candidate is LineRenderer))
            {
                visuals.Add(candidate);
            }
        }

        controllerVisuals = visuals.ToArray();
        controllerVisualStates = new bool[controllerVisuals.Length];
        paymentHand.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PaymentMethodSelected += OnPaymentSelected;
        dialogueManager.StateChanged += OnDialogueStateChanged;
    }

    private void OnDisable()
    {
        if (dialogueManager != null)
        {
            dialogueManager.PaymentMethodSelected -= OnPaymentSelected;
            dialogueManager.StateChanged -= OnDialogueStateChanged;
        }

        HidePayment();
    }

    private void OnDialogueStateChanged(ConversationState previous, ConversationState current)
    {
        if (current == ConversationState.WaitingForPlayer || current == ConversationState.Error)
        {
            HidePayment();
            hasPlayed = false;
        }
    }

    private void OnPaymentSelected(PaymentMethod method)
    {
        if (hasPlayed || method == PaymentMethod.None)
        {
            return;
        }

        hasPlayed = true;
        cash.SetActive(method == PaymentMethod.Cash);
        card.SetActive(method == PaymentMethod.Card);
        paymentHand.localPosition = Pocket;
        paymentHand.localRotation = HandRotation(NailsUpRoll);
        HideControllerVisuals();
        paymentHand.gameObject.SetActive(true);
        gesture = StartCoroutine(PlayGesture());
    }

    private IEnumerator PlayGesture()
    {
        yield return Move(Pocket, Reveal, NailsUpRoll, RevealRoll, 0.72f);
        yield return new WaitForSecondsRealtime(0.28f);
        yield return Move(Reveal, Offer, RevealRoll, PalmUpRoll, 0.48f);
        yield return new WaitForSecondsRealtime(0.38f);
        // Withdraw palm up: unwinding the roll would turn the hand counter-clockwise.
        yield return Move(Offer, Pocket, PalmUpRoll, PalmUpRoll, 0.55f);

        paymentHand.gameObject.SetActive(false);
        RestoreControllerVisuals();
        gesture = null;
    }

    private IEnumerator Move(Vector3 from, Vector3 to, float fromRoll, float toRoll, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            paymentHand.localPosition = Vector3.LerpUnclamped(from, to, t);
            paymentHand.localRotation = HandRotation(Mathf.Lerp(fromRoll, toRoll, t));
            yield return null;
        }
    }

    private static Quaternion HandRotation(float roll)
    {
        return Quaternion.AngleAxis(FingerYawInward, Vector3.up) *
               Quaternion.AngleAxis(-FingerPitchUp, Vector3.right) *
               Quaternion.AngleAxis(roll, Vector3.forward) *
               HandBase;
    }

    private void HidePayment()
    {
        if (gesture != null)
        {
            StopCoroutine(gesture);
            gesture = null;
        }

        if (paymentHand != null)
        {
            paymentHand.gameObject.SetActive(false);
        }

        RestoreControllerVisuals();
    }

    private void HideControllerVisuals()
    {
        if (controllerVisualsHidden || controllerVisuals == null)
        {
            return;
        }

        for (int i = 0; i < controllerVisuals.Length; i++)
        {
            controllerVisualStates[i] = controllerVisuals[i].enabled;
            controllerVisuals[i].enabled = false;
        }

        controllerVisualsHidden = true;
    }

    private void RestoreControllerVisuals()
    {
        if (!controllerVisualsHidden)
        {
            return;
        }

        for (int i = 0; i < controllerVisuals.Length; i++)
        {
            if (controllerVisuals[i] != null)
            {
                controllerVisuals[i].enabled = controllerVisualStates[i];
            }
        }

        controllerVisualsHidden = false;
    }
}
