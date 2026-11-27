using System;
using UnityEngine;

// Manual integration check for the minimal reply/status contract, linked turns,
// and a fresh interaction chain after resetting the visit.
public sealed class GeminiDialogueSmokeTest : MonoBehaviour
{
    [SerializeField] private MenuManager menuManager;

    public bool HasRun { get; private set; }
    public bool IsRunning { get; private set; }
    public bool ApiKeyDetected { get; private set; }
    public bool RequestSucceeded { get; private set; }
    public bool ContinuationSucceeded { get; private set; }
    public bool NewVisitSucceeded { get; private set; }
    public string Reply { get; private set; } = string.Empty;
    public string ContinuationReply { get; private set; } = string.Empty;
    public string NewVisitReply { get; private set; } = string.Empty;
    public ConversationStatus Status { get; private set; }
    public string ControlledError { get; private set; } = string.Empty;

    [ContextMenu("Run Gemini Dialogue Smoke Test")]
    public async void RunSmokeTest()
    {
        if (IsRunning)
        {
            return;
        }

        HasRun = true;
        IsRunning = true;
        RequestSucceeded = false;
        ContinuationSucceeded = false;
        NewVisitSucceeded = false;
        ControlledError = string.Empty;
        ApiKeyDetected = LanguageBackend.IsConfigured;

        try
        {
            if (!ApiKeyDetected)
            {
                throw new InvalidOperationException(LanguageBackend.MissingConfigurationMessage());
            }

            if (menuManager == null)
            {
                throw new InvalidOperationException("MenuManager is not assigned.");
            }

            var service = new GeminiDialogueService(menuManager);
            DialogueModelResponse first = await service.GenerateResponseAsync(
                "I'd like a medium cappuccino.");
            Reply = first.Reply;
            Status = first.Status;
            if (Status != ConversationStatus.Active)
            {
                throw new InvalidOperationException("The first ordering turn ended the visit unexpectedly.");
            }
            RequestSucceeded = true;

            // This turn must send the first interaction ID as previous_interaction_id.
            DialogueModelResponse followUp = await service.GenerateResponseAsync(
                "Actually, make that a medium chai latte with oat milk instead.");
            ContinuationReply = followUp.Reply;
            if (followUp.Status != ConversationStatus.Active)
            {
                throw new InvalidOperationException("The correction turn ended the visit unexpectedly.");
            }
            ContinuationSucceeded = true;

            // The next turn must have no previous_interaction_id.
            service.ResetConversationContext();
            DialogueModelResponse newVisit = await service.GenerateResponseAsync(
                "Can I get a butter croissant?");
            NewVisitReply = newVisit.Reply;
            if (newVisit.Status != ConversationStatus.Active)
            {
                throw new InvalidOperationException("The new visit ended on its first ordering turn.");
            }
            NewVisitSucceeded = true;

            Debug.Log($"[GeminiSmokeTest] First ({Status}): {Reply}", this);
            Debug.Log($"[GeminiSmokeTest] Follow-up: {ContinuationReply}", this);
            Debug.Log($"[GeminiSmokeTest] New visit: {NewVisitReply}", this);
            Debug.Log("[GeminiSmokeTest] Three requests succeeded across continuation and reset.", this);
        }
        catch (Exception exception)
        {
            ControlledError = exception.Message;
            Debug.LogError($"[GeminiSmokeTest] Request failed: {ControlledError}", this);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
