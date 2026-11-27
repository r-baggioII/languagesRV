using System;

public enum ConversationStatus
{
    Active,
    Completed
}

public enum PaymentMethod
{
    None,
    Cash,
    Card
}

public sealed class DialogueModelResponse
{
    public string Reply { get; }
    public ConversationStatus Status { get; }
    public PaymentMethod PaymentMethod { get; }

    public DialogueModelResponse(
        string reply,
        ConversationStatus status,
        PaymentMethod paymentMethod = PaymentMethod.None)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new ArgumentException("Remy's reply cannot be empty.", nameof(reply));
        }

        Reply = reply.Trim();
        Status = status;
        PaymentMethod = paymentMethod;
    }
}
