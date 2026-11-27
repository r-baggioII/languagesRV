using System;
using System.Threading;
using System.Threading.Tasks;

public interface ISttService
{
    ISttSession StartSession(
        SpeechTimingLogger.SttTrace timing = null,
        CancellationToken cancellationToken = default);
}

// One push-to-talk speech turn. Audio may be queued before the connection is
// ready; it is delivered in order once the service accepts it.
public interface ISttSession : IDisposable
{
    // Latest low-latency hypothesis. Never the submitted text: only the value
    // returned by CompleteAsync is authoritative.
    event Action<string> PartialTranscriptReceived;

    string LatestPartialTranscript { get; }
    bool IsFailed { get; }

    void SendAudio(byte[] pcm16LittleEndian, int byteCount);

    // Ends the speech turn and waits for the final transcript.
    Task<SttResult> CompleteAsync();

    // Drops the turn without producing a result.
    void Cancel();
}

public sealed class SttResult
{
    public SttResult(
        bool hasFinalTranscript,
        string finalTranscript,
        string lastPartialTranscript,
        string error)
    {
        HasFinalTranscript = hasFinalTranscript;
        FinalTranscript = finalTranscript ?? string.Empty;
        LastPartialTranscript = lastPartialTranscript ?? string.Empty;
        Error = error ?? string.Empty;
    }

    public bool HasFinalTranscript { get; }
    public string FinalTranscript { get; }
    public string LastPartialTranscript { get; }
    public string Error { get; }
}

public sealed class SttException : Exception
{
    public SttException(string message) : base(message)
    {
    }

    public SttException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
