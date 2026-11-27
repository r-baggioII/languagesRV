using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Gemini owns the conversational order for one visit. Unity supplies the current
// menu on every turn and uses only the reply and technical completion status.
public sealed class GeminiDialogueService
{
    // Served by the LAN backend, which holds GEMINI_API_KEY and forwards the
    // body to Gemini's interactions API unchanged.
    public const string EndpointPath = "/api/gemini/interactions";
    public const string ModelName = "gemini-3.5-flash-lite";

    public const string SystemInstruction =
        "Who you are\n" +
        "You are Remy, a barista at a fast-casual American coffee shop. Friendly, relaxed, efficient — the kind of person who's good at their job and easy to talk to. You are not an AI assistant, a customer service system, or an English tutor. Stay in character as Remy at all times, no matter what the customer says or asks. Each visit is a fresh conversation — you don't know or reference any previous customer or order.\n" +
        "\n" +
        "Hard rules (never break these)\n" +
        "Stay in character, always speak as Remy, in English. Never mention that you're an AI, a model, or a system, and never break character regardless of what the customer says.\n" +
        "CURRENT MENU is the only source of truth. Never invent items, sizes, temperatures, milks, extras, or prices that aren't in it. If something isn't available, decline the way a real barista would and offer a real alternative from the menu when one naturally fits — don't just say \"that item isn't available.\"\n" +
        "Track the order through conversation, not a checklist. Use the full history to know what's been ordered, changed, added, or dropped. Never ask the customer to re-confirm something the context already makes obvious.\n" +
        "Never expose internal mechanics. No \"I've updated your order,\" no \"order confirmed,\" no references to stages like ordering/payment. The customer should only ever experience a normal conversation.\n" +
        "Don't teach English. Never correct grammar, praise the customer for speaking English, explain vocabulary unprompted, evaluate their level, or require full sentences. Just understand what they mean and respond like a barista would.\n" +
        "Don't invent facts about the physical world. No made-up tables, wait times, order numbers, or staff. If you don't know something outside the menu, handle it the way a person would (\"Not sure — go ahead and check\").\n" +
        "Default to short, natural responses — one or two sentences. Save longer replies for real questions, recommendations, or genuine clarifications.\n" +
        "Handle changes of mind smoothly, at any point — including after the total's been given or mid-payment. Adjust and recalculate without friction or commentary.\n" +
        "Move the conversation forward naturally — toward payment once the order seems done, toward a plausible close once payment is settled — without requiring a specific phrase from the customer to trigger it.\n" +
        "Never sound like a chatbot. Avoid formal or corporate phrasing (\"I'd be happy to assist you,\" \"Your order has been finalized\"). Talk the way someone behind the counter actually talks.\n" +
        "\n" +
        "Using the menu\n" +
        "CURRENT MENU lists everything actually available this session: items, categories, sizes, temperatures, milks, extras, and prices. Use it to calculate the total, accounting for quantities, swaps, and removals as the order evolves. It's the boundary of what you can sell — stay inside it without ever sounding like you're citing a database.\n" +
        "\n" +
        "Holding the order in your head\n" +
        "You don't need to recap the order after every turn — just track it as it evolves. \"Make that a chai instead\" replaces the previous item. \"Scratch that\" removes it. Short answers like \"medium,\" \"oat,\" \"iced,\" or \"two\" fill in whatever you just asked about. Ask for clarification only when a real barista genuinely would — not when the context already makes the answer obvious.\n" +
        "\n" +
        "Tone\n" +
        "Casual but competent — closer to \"Sure, what size?\" than \"Certainly, I'd be happy to take your order.\" Vary short acknowledgments naturally (sure / got it / no problem / sounds good / alright) instead of repeating the same one every turn, but don't force variety for its own sake — let it feel spontaneous, not rotated. Small talk and quick recommendation questions get a brief, human reaction, then the conversation moves on.\n" +
        "\n" +
        "Example\n" +
        "Customer: Hey, I'm kinda dragging today. What's good if I need some caffeine?\n" +
        "Remy: I'd go with the cold brew if you want something strong.\n" +
        "Customer: Alright, I'll do that.\n" +
        "Remy: Sure. What size?\n" +
        "Customer: Medium, black.\n" +
        "Remy: Got it. Anything else?\n" +
        "Customer: Maybe a croissant too.\n" +
        "Remy: Absolutely.\n" +
        "Customer: Actually, make that a chocolate chip cookie.\n" +
        "Remy: Sure, cookie instead. Anything else?\n" +
        "Customer: Nah, I'm good.\n" +
        "Remy: Alright, that'll be $7.75. Cash or card?\n" +
        "Customer: Card.\n" +
        "Remy: Perfect. You're all set — we'll call you when it's ready.\n" +
        "\n" +
        "Output format\n" +
        "Return only a JSON object with reply, conversationStatus, and paymentMethod. reply is exactly what Remy says. conversationStatus is active while the customer can continue, including when asking for payment, and completed only when the reply naturally ends the visit. paymentMethod is cash or card only when the customer's chosen method is accepted in a closing reply; otherwise it is none. Do not infer a method from Remy's question, a hypothetical, or a method the customer rejected.\n" +
        "\n" +
        "CURRENT MENU\n" +
        "{{MENU}}";

    private const int RequestTimeoutSeconds = 30;
    private const string MenuContextToken = "{{MENU}}";

    private readonly MenuManager menuManager;
    private string previousInteractionId;
    private int contextVersion;

    public GeminiDialogueService(MenuManager menuManager)
    {
        this.menuManager = menuManager != null
            ? menuManager
            : throw new ArgumentNullException(nameof(menuManager));
    }

    public async Task<DialogueModelResponse> GenerateResponseAsync(
        string playerMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerMessage))
        {
            throw new ArgumentException("The player message cannot be empty.", nameof(playerMessage));
        }

        cancellationToken.ThrowIfCancellationRequested();
        int requestContextVersion = contextVersion;
        string backendUrl = LanguageBackend.BaseUrl;
        string token = LanguageBackend.Token;

        if (backendUrl == null || token == null)
        {
            throw new GeminiDialogueException(
                LanguageBackend.MissingConfigurationMessage() + " No request was sent.");
        }

        byte[] requestBytes = Encoding.UTF8.GetBytes(CreateRequestJson(playerMessage.Trim()));

        using (var request = new UnityWebRequest(
                   LanguageBackend.BuildUrl(backendUrl, EndpointPath),
                   UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(requestBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = RequestTimeoutSeconds;
            request.SetRequestHeader("Content-Type", "application/json");
            LanguageBackend.ApplyAuth(request, token);

            Debug.Log("[GeminiDialogue] Sending dialogue turn.");
            await SendAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (requestContextVersion != contextVersion)
            {
                throw new OperationCanceledException("The conversation was reset while Gemini was responding.");
            }

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new GeminiDialogueException(
                    "Language backend is unreachable: " +
                    LanguageBackend.Redact(request.error, token));
            }

            if (request.responseCode < 200 || request.responseCode >= 300 ||
                request.result == UnityWebRequest.Result.ProtocolError)
            {
                string reason = request.responseCode == 401 || request.responseCode == 403
                    ? "the backend session expired or was rejected; pair again in VR Settings"
                    : LanguageBackend.Redact(request.error, token);
                if (request.responseCode == 401 || request.responseCode == 403)
                    LanguageBackend.RejectToken(token);
                throw new GeminiDialogueException(
                    $"Gemini (via backend) returned HTTP {request.responseCode}: {reason}",
                    request.responseCode);
            }

            if (request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new GeminiDialogueException($"Gemini response processing failed: {request.error}");
            }

            GeminiInteractionResponse interaction =
                ParseInteractionResponse(request.downloadHandler.text);
            DialogueModelResponse response = ParseModelOutput(interaction);

            // Advance only after both the interaction and its structured answer
            // are valid. A reset invalidates an in-flight turn before it can write.
            cancellationToken.ThrowIfCancellationRequested();
            if (requestContextVersion != contextVersion)
            {
                throw new OperationCanceledException("The conversation was reset while Gemini was responding.");
            }

            previousInteractionId = interaction.id.Trim();
            Debug.Log("[GeminiDialogue] Response received.");
            SpeechTimingLogger.LogLlmResponseReceived();
            return response;
        }
    }

    public void ResetConversationContext()
    {
        contextVersion++;
        previousInteractionId = null;
        Debug.Log("[GeminiDialogue] Conversation context reset.");
    }

    private string CreateRequestJson(string playerMessage)
    {
        GeminiInteractionRequest request = string.IsNullOrEmpty(previousInteractionId)
            ? new GeminiInteractionRequest()
            : new GeminiContinuedInteractionRequest
            {
                previous_interaction_id = previousInteractionId
            };

        request.model = ModelName;
        request.input = playerMessage;
        // Interactions instructions are scoped to each request. Include the
        // current menu even when previous_interaction_id links earlier turns.
        request.system_instruction = BuildSystemInstruction();
        request.store = true;
        request.response_format = new[]
        {
            new GeminiResponseFormat
            {
                type = "text",
                mime_type = "application/json",
                schema = new GeminiResponseSchema
                {
                    type = "object",
                    properties = new GeminiResponseProperties
                    {
                        reply = new GeminiSchemaProperty
                        {
                            type = "string",
                            description = "The exact, concise natural reply Remy says to the customer."
                        },
                        conversationStatus = new GeminiEnumSchemaProperty
                        {
                            type = "string",
                            description = "active until the visit naturally ends; completed only with a closing reply.",
                            @enum = new[] { "active", "completed" }
                        },
                        paymentMethod = new GeminiEnumSchemaProperty
                        {
                            type = "string",
                            description = "cash or card only when accepted in a closing reply; none otherwise.",
                            @enum = new[] { "none", "cash", "card" }
                        }
                    },
                    required = new[] { "reply", "conversationStatus", "paymentMethod" },
                    additionalProperties = false
                }
            }
        };

        return JsonUtility.ToJson(request);
    }

    private string BuildSystemInstruction()
    {
        string instruction = SystemInstruction.Replace(
            MenuContextToken,
            menuManager.BuildLlmMenuContext());

        if (instruction.Contains(MenuContextToken))
        {
            throw new InvalidOperationException("Gemini system instruction has an unresolved menu token.");
        }

        return instruction;
    }

    private static async Task SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
    {
        UnityWebRequestAsyncOperation operation = request.SendWebRequest();

        while (!operation.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                request.Abort();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Yield();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static GeminiInteractionResponse ParseInteractionResponse(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new GeminiDialogueException("Gemini returned an empty response body.");
        }

        GeminiInteractionResponse interaction;
        try
        {
            interaction = JsonUtility.FromJson<GeminiInteractionResponse>(responseJson);
        }
        catch (ArgumentException exception)
        {
            throw new GeminiDialogueException("Gemini returned malformed interaction JSON.", exception);
        }

        if (interaction == null || string.IsNullOrWhiteSpace(interaction.id))
        {
            throw new GeminiDialogueException("Gemini response did not include an interaction ID.");
        }

        if (!string.Equals(interaction.status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new GeminiDialogueException(
                $"Gemini interaction did not complete successfully (status: {interaction.status ?? "missing"}).");
        }

        return interaction;
    }

    private static DialogueModelResponse ParseModelOutput(GeminiInteractionResponse interaction)
    {
        DialogueResponsePayload payload;
        try
        {
            payload = JsonUtility.FromJson<DialogueResponsePayload>(ExtractStructuredOutput(interaction));
        }
        catch (ArgumentException exception)
        {
            throw new GeminiDialogueException("Gemini returned malformed structured model output.", exception);
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.reply))
        {
            throw new GeminiDialogueException("Gemini structured output is missing a non-empty reply.");
        }

        ConversationStatus status;
        switch (payload.conversationStatus?.Trim().ToLowerInvariant())
        {
            case "active":
                status = ConversationStatus.Active;
                break;
            case "completed":
                status = ConversationStatus.Completed;
                break;
            default:
                throw new GeminiDialogueException("Gemini structured output has an invalid conversationStatus.");
        }

        PaymentMethod paymentMethod;
        switch (payload.paymentMethod?.Trim().ToLowerInvariant())
        {
            case "none":
                paymentMethod = PaymentMethod.None;
                break;
            case "cash":
                paymentMethod = PaymentMethod.Cash;
                break;
            case "card":
                paymentMethod = PaymentMethod.Card;
                break;
            default:
                // A missing visual cue must not prevent the dialogue from ending.
                paymentMethod = PaymentMethod.None;
                break;
        }

        if (status != ConversationStatus.Completed)
        {
            paymentMethod = PaymentMethod.None;
        }

        return new DialogueModelResponse(payload.reply, status, paymentMethod);
    }

    private static string ExtractStructuredOutput(GeminiInteractionResponse interaction)
    {
        if (interaction.steps == null)
        {
            throw new GeminiDialogueException("Gemini response did not include model output.");
        }

        foreach (GeminiInteractionStep step in interaction.steps)
        {
            if (step == null ||
                !string.Equals(step.type, "model_output", StringComparison.OrdinalIgnoreCase) ||
                step.content == null)
            {
                continue;
            }

            var output = new StringBuilder();
            foreach (GeminiInteractionContent content in step.content)
            {
                if (content != null &&
                    string.Equals(content.type, "text", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(content.text))
                {
                    output.Append(content.text);
                }
            }

            if (output.Length > 0)
            {
                return output.ToString();
            }
        }

        throw new GeminiDialogueException("Gemini response did not include valid structured model output.");
    }

    [Serializable]
    private class GeminiInteractionRequest
    {
        public string model = string.Empty;
        public string input = string.Empty;
        public string system_instruction = string.Empty;
        public bool store;
        public GeminiResponseFormat[] response_format = Array.Empty<GeminiResponseFormat>();
    }

    [Serializable]
    private sealed class GeminiContinuedInteractionRequest : GeminiInteractionRequest
    {
        public string previous_interaction_id = string.Empty;
    }

    [Serializable]
    private sealed class GeminiResponseFormat
    {
        public string type = string.Empty;
        public string mime_type = string.Empty;
        public GeminiResponseSchema schema = new GeminiResponseSchema();
    }

    [Serializable]
    private sealed class GeminiResponseSchema
    {
        public string type = string.Empty;
        public GeminiResponseProperties properties = new GeminiResponseProperties();
        public string[] required = Array.Empty<string>();
        public bool additionalProperties;
    }

    [Serializable]
    private sealed class GeminiResponseProperties
    {
        public GeminiSchemaProperty reply = new GeminiSchemaProperty();
        public GeminiEnumSchemaProperty conversationStatus = new GeminiEnumSchemaProperty();
        public GeminiEnumSchemaProperty paymentMethod = new GeminiEnumSchemaProperty();
    }

    [Serializable]
    private sealed class GeminiSchemaProperty
    {
        public string type = string.Empty;
        public string description = string.Empty;
    }

    [Serializable]
    private sealed class GeminiEnumSchemaProperty
    {
        public string type = string.Empty;
        public string description = string.Empty;
        public string[] @enum = Array.Empty<string>();
    }

    [Serializable]
    private sealed class GeminiInteractionResponse
    {
        public string id = string.Empty;
        public string status = string.Empty;
        public GeminiInteractionStep[] steps = Array.Empty<GeminiInteractionStep>();
    }

    [Serializable]
    private sealed class GeminiInteractionStep
    {
        public string type = string.Empty;
        public GeminiInteractionContent[] content = Array.Empty<GeminiInteractionContent>();
    }

    [Serializable]
    private sealed class GeminiInteractionContent
    {
        public string type = string.Empty;
        public string text = string.Empty;
    }

    [Serializable]
    private sealed class DialogueResponsePayload
    {
        public string reply = string.Empty;
        public string conversationStatus = string.Empty;
        public string paymentMethod = string.Empty;
    }
}

public sealed class GeminiDialogueException : Exception
{
    public long? HttpStatusCode { get; }

    public GeminiDialogueException(string message) : base(message)
    {
    }

    public GeminiDialogueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GeminiDialogueException(string message, long httpStatusCode)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
    }
}
