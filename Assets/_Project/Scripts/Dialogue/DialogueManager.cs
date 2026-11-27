using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class DialogueManager : MonoBehaviour
{
    private static readonly string[] OpeningLines =
    {
        "Hi! What can I get for you today?",
        "Hey there. What would you like?",
        "Hi, what can I get you?",
        "Hello. What can I get started for you?"
    };

    [SerializeField] private MenuManager menuManager;
    [SerializeField] private RemyVoiceController remyVoiceController;

    private GeminiDialogueService geminiDialogueService;

    private CancellationTokenSource geminiRequestCancellation;
    private CancellationTokenSource remySpeechCancellation;
    private int conversationVersion;
    private int lastOpeningIndex = -1;
    private bool completeAfterRemySpeaking;

    public ConversationState CurrentState { get; private set; } = ConversationState.WaitingForPlayer;
    public string CurrentRemyMessage { get; private set; } = string.Empty;

    public event Action<ConversationState, ConversationState> StateChanged;
    public event Action<string> RemyMessageGenerated;
    public event Action<PaymentMethod> PaymentMethodSelected;

    private void Awake()
    {
        if (menuManager == null)
        {
            Debug.LogError(
                "[Dialogue] MenuManager reference is not assigned.",
                this);
            return;
        }

        geminiDialogueService = new GeminiDialogueService(menuManager);
    }

    public void StartConversation()
    {
        bool restartingVisit = CurrentState == ConversationState.ConversationCompleted ||
                               CurrentState == ConversationState.Error;
        if (restartingVisit)
        {
            ResetConversation();
        }

        if (CurrentState != ConversationState.WaitingForPlayer)
        {
            Debug.LogWarning(
                $"[Dialogue] StartConversation ignored because the current state is {CurrentState}.",
                this);
            return;
        }

        // A new visit never reuses an earlier Gemini interaction chain.
        if (!restartingVisit)
        {
            geminiDialogueService?.ResetConversationContext();
        }

        if (!SetState(ConversationState.StartingConversation))
        {
            return;
        }

        PresentRemyMessage(ChooseOpeningLine());
    }

    private string ChooseOpeningLine()
    {
        int index = lastOpeningIndex < 0
            ? UnityEngine.Random.Range(0, OpeningLines.Length)
            : UnityEngine.Random.Range(0, OpeningLines.Length - 1);

        if (lastOpeningIndex >= 0 && index >= lastOpeningIndex)
        {
            index++;
        }

        lastOpeningIndex = index;
        return OpeningLines[index];
    }

    public void SubmitPlayerMessage(string message)
    {
        if (CurrentState != ConversationState.WaitingForUser)
        {
            Debug.LogWarning(
                $"[Dialogue] SubmitPlayerMessage ignored because the current state is {CurrentState}.",
                this);
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            Debug.LogWarning("[Dialogue] SubmitPlayerMessage ignored because the message is empty.", this);
            return;
        }

        string submittedMessage = message.Trim();

        if (!SetState(ConversationState.ProcessingUserInput))
        {
            return;
        }

        if (CurrentState != ConversationState.ProcessingUserInput ||
            !SetState(ConversationState.GeneratingResponse))
        {
            return;
        }

        CancelInFlightGeminiRequest();

        var requestCancellation = new CancellationTokenSource();
        geminiRequestCancellation = requestCancellation;
        int requestVersion = conversationVersion;

        _ = GenerateGeminiResponseAsync(
            submittedMessage,
            requestCancellation,
            requestVersion);
    }

    public void SetError(string errorMessage)
    {
        CancelInFlightGeminiRequest();
        CancelInFlightRemySpeech();
        completeAfterRemySpeaking = false;

        string message = string.IsNullOrWhiteSpace(errorMessage)
            ? "An unspecified dialogue error occurred."
            : errorMessage.Trim();

        Debug.LogError($"[Dialogue] {message}", this);
        SetState(ConversationState.Error);
    }

    public void ResetConversation()
    {
        conversationVersion++;
        CancelInFlightGeminiRequest();
        CancelInFlightRemySpeech();
        geminiDialogueService?.ResetConversationContext();

        completeAfterRemySpeaking = false;
        CurrentRemyMessage = string.Empty;

        if (CurrentState == ConversationState.WaitingForPlayer)
        {
            Debug.Log("[Dialogue] Conversation reset; already waiting for the player.", this);
            return;
        }

        SetState(ConversationState.WaitingForPlayer);
    }

    private void PresentRemyMessage(
        string message,
        bool shouldCompleteConversation = false)
    {
        if (!SetState(ConversationState.RemySpeaking))
        {
            return;
        }

        completeAfterRemySpeaking = shouldCompleteConversation;
        CurrentRemyMessage = message;
        Debug.Log($"[Dialogue] Remy: {message}", this);
        RemyMessageGenerated?.Invoke(message);

        if (CurrentState != ConversationState.RemySpeaking)
        {
            return;
        }

        CancelInFlightRemySpeech();

        var speechCancellation = new CancellationTokenSource();
        remySpeechCancellation = speechCancellation;
        int speechVersion = conversationVersion;

        _ = SpeakRemyMessageAsync(
            message,
            speechCancellation,
            speechVersion);
    }


    private void FinishRemySpeaking()
    {
        ConversationState nextState = completeAfterRemySpeaking
            ? ConversationState.ConversationCompleted
            : ConversationState.WaitingForUser;

        completeAfterRemySpeaking = false;
        SetState(nextState);
    }

    private bool SetState(ConversationState newState)
    {
        ConversationState previousState = CurrentState;

        if (previousState == newState)
        {
            return false;
        }

        if (!IsTransitionAllowed(previousState, newState))
        {
            Debug.LogWarning(
                $"[Dialogue] Rejected invalid state transition: {previousState} -> {newState}",
                this);
            return false;
        }

        CurrentState = newState;
        Debug.Log($"[Dialogue] {previousState} -> {newState}", this);
        StateChanged?.Invoke(previousState, newState);

        return CurrentState == newState;
    }

    private static bool IsTransitionAllowed(
        ConversationState previousState,
        ConversationState newState)
    {
        if (newState == ConversationState.Error ||
            newState == ConversationState.WaitingForPlayer)
        {
            return true;
        }

        switch (previousState)
        {
            case ConversationState.WaitingForPlayer:
                return newState == ConversationState.StartingConversation;

            case ConversationState.StartingConversation:
                return newState == ConversationState.RemySpeaking;

            case ConversationState.RemySpeaking:
                return newState == ConversationState.WaitingForUser ||
                       newState == ConversationState.ConversationCompleted;

            case ConversationState.WaitingForUser:
                return newState == ConversationState.ProcessingUserInput ||
                       newState == ConversationState.ConversationCompleted;

            case ConversationState.ProcessingUserInput:
                return newState == ConversationState.GeneratingResponse;

            case ConversationState.GeneratingResponse:
                return newState == ConversationState.RemySpeaking;

            default:
                return false;
        }
    }

    private void OnDestroy()
    {
        ResetConversation();
    }

    private void OnDisable()
    {
        // A disabled manager cannot carry this visit's memory into re-enable.
        ResetConversation();
    }

    private void CancelInFlightRemySpeech()
    {
        CancellationTokenSource speechCancellation =
            remySpeechCancellation;

        remySpeechCancellation = null;

        if (speechCancellation != null &&
            !speechCancellation.IsCancellationRequested)
        {
            speechCancellation.Cancel();
        }

        remyVoiceController?.StopSpeaking();
    }

    private bool IsCurrentRemySpeech(
        CancellationTokenSource speechCancellation,
        int speechVersion)
    {
        return ReferenceEquals(
                   remySpeechCancellation,
                   speechCancellation) &&
               !speechCancellation.IsCancellationRequested &&
               conversationVersion == speechVersion &&
               this != null &&
               isActiveAndEnabled &&
               CurrentState == ConversationState.RemySpeaking;
    }

    private async Task SpeakRemyMessageAsync(
        string message,
        CancellationTokenSource speechCancellation,
        int speechVersion)
    {
        try
        {
            if (remyVoiceController == null)
            {
                Debug.LogError(
                    "[Dialogue] RemyVoiceController is not assigned; " +
                    "continuing without speech.",
                    this);
            }
            else
            {
                await remyVoiceController.SpeakAsync(
                    message,
                    speechCancellation.Token);
            }

            if (IsCurrentRemySpeech(
                    speechCancellation,
                    speechVersion))
            {
                FinishRemySpeaking();
            }
        }
        catch (OperationCanceledException)
        {
            // Reset, disable, destroy, and replacement speech are expected.
        }
        catch (Exception exception)
        {
            if (IsCurrentRemySpeech(
                    speechCancellation,
                    speechVersion))
            {
                Debug.LogError(
                    $"[Dialogue] Remy speech failed; continuing without " +
                    $"audio: {exception.Message}",
                    this);
                FinishRemySpeaking();
            }
        }
        finally
        {
            if (ReferenceEquals(
                    remySpeechCancellation,
                    speechCancellation))
            {
                remySpeechCancellation = null;
            }

            speechCancellation.Dispose();
        }
    }

    private void CancelInFlightGeminiRequest()
    {
        CancellationTokenSource requestCancellation =
            geminiRequestCancellation;

        geminiRequestCancellation = null;

        if (requestCancellation != null &&
            !requestCancellation.IsCancellationRequested)
        {
            requestCancellation.Cancel();
        }
    }

    private bool IsCurrentGeminiRequest(
        CancellationTokenSource requestCancellation,
        int requestVersion)
    {
        return ReferenceEquals(
                   geminiRequestCancellation,
                   requestCancellation) &&
               !requestCancellation.IsCancellationRequested &&
               conversationVersion == requestVersion &&
               this != null &&
               isActiveAndEnabled &&
               CurrentState == ConversationState.GeneratingResponse;
    }

    private async Task GenerateGeminiResponseAsync(
        string playerMessage,
        CancellationTokenSource requestCancellation,
        int requestVersion)
    {
        try
        {
            if (geminiDialogueService == null)
            {
                throw new GeminiDialogueException(
                    "Gemini dialogue service is not configured with MenuManager.");
            }

            DialogueModelResponse response =
                await geminiDialogueService.GenerateResponseAsync(
                    playerMessage,
                    requestCancellation.Token);

            if (!IsCurrentGeminiRequest(
                    requestCancellation,
                    requestVersion))
            {
                return;
            }

            PresentRemyMessage(
                response.Reply,
                response.Status == ConversationStatus.Completed);

            if (response.Status == ConversationStatus.Completed &&
                response.PaymentMethod != PaymentMethod.None &&
                (CurrentState == ConversationState.RemySpeaking ||
                 CurrentState == ConversationState.ConversationCompleted))
            {
                PaymentMethodSelected?.Invoke(response.PaymentMethod);
            }
        }
        catch (OperationCanceledException)
        {
            // Reset, disable, and destroy cancellation are expected.
        }
        catch (GeminiDialogueException exception)
        {
            if (IsCurrentGeminiRequest(
                    requestCancellation,
                    requestVersion))
            {
                SetError($"Gemini dialogue request failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentGeminiRequest(
                    requestCancellation,
                    requestVersion))
            {
                SetError($"Unexpected Gemini dialogue failure: {exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(
                    geminiRequestCancellation,
                    requestCancellation))
            {
                geminiRequestCancellation = null;
            }

            requestCancellation.Dispose();
        }
    }
}
