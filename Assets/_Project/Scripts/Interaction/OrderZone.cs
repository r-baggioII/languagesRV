using UnityEngine;

public class OrderZone : MonoBehaviour
{
    [SerializeField] private DialogueManager dialogueManager;

    private bool hasTriggered;

    public void TryStartOrder()
    {
        if (hasTriggered)
        {
            return;
        }

        if (dialogueManager == null)
        {
            Debug.LogError("[OrderZone] DialogueManager reference is not assigned.", this);
            return;
        }

        hasTriggered = true;
        Debug.Log("[OrderZone] Requesting conversation start.", this);
        dialogueManager.StartConversation();
    }

    public void ResetZone()
    {
        hasTriggered = false;
    }
}
