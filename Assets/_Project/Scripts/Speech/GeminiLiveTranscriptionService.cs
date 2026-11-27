using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

public enum SttTurnControl
{
    // Explicit activityStart ... activityEnd. No partial transcripts.
    ManualActivity,
    // The server segments speech; the client signals end of audio.
    // Streams incremental partial transcripts.
    AutomaticActivityDetection
}

// Real-time speech-to-text only. The conversational model and Remy's voice are
// untouched: this service turns one push-to-talk turn into a final transcript.
public sealed class GeminiLiveTranscriptionService : ISttService
{
    public const string ModelName = "gemini-3.5-transcribe-live";
    public const string LanguageCode = "en-US";
    // VERBATIM (not SMART): this is a language-learning app, so the learner's
    // actual wording (fillers, mistakes, repetitions) must be preserved.
    public const string TranscriptionMode = "VERBATIM";
    public const int SampleRate = 16000;

    // The notebook proxies Gemini Live frames. Its cloud key never reaches Unity.

    internal static string BuildSetupJson(SttTurnControl turnControl)
    {
        // Push-to-talk (manual): the client marks the speech turn explicitly,
        // so automatic voice-activity detection is disabled.
        string realtimeInputConfig =
            turnControl == SttTurnControl.ManualActivity
                ? "\"realtimeInputConfig\":{\"automaticActivityDetection\":" +
                  "{\"disabled\":true}},"
                : string.Empty;

        return "{\"setup\":{" +
               "\"model\":\"models/" + ModelName + "\"," +
               "\"generationConfig\":{\"responseModalities\":[\"TEXT\"]}," +
               realtimeInputConfig +
               "\"inputAudioTranscription\":{\"languageCodes\":[\"" +
               LanguageCode + "\"],\"mode\":\"" + TranscriptionMode + "\"}" +
               "}}";
    }

    // Logs which kinds of server messages arrive (never audio, never the key).
    public static bool VerboseDiagnostics { get; set; }

    // Automatic by default: with manual activityStart/activityEnd this model
    // emits only a final transcript (no partials), which was verified against
    // the live endpoint. The client still owns push-to-talk: audio is streamed
    // only while the button is held and release sends audioStreamEnd.
    public SttTurnControl TurnControl { get; set; } =
        SttTurnControl.AutomaticActivityDetection;

    public ISttSession StartSession(
        SpeechTimingLogger.SttTrace timing = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string endpoint = LanguageBackend.LiveWebSocketUrl();
        string token = LanguageBackend.Token;
        if (endpoint == null || token == null)
        {
            throw new SttException(LanguageBackend.MissingConfigurationMessage());
        }

        return new GeminiLiveTranscriptionSession(
            endpoint,
            token,
            TurnControl,
            timing,
            cancellationToken);
    }
}

internal sealed class GeminiLiveTranscriptionSession : ISttSession
{
    private const int ConnectTimeoutMs = 10000;
    private const int SetupTimeoutMs = 3000;
    private const int FinalTimeoutMs = 6000;
    private const int FinalQuietWindowMs = 250;
    private const int LateFinalGraceMs = 300;
    private const int NothingPendingGraceMs = 250;
    private const int NoFinalAfterEndWindowMs = 900;
    private const int NoSpeechGraceMs = 1200;
    private const int ReceiveBufferBytes = 16 * 1024;
    private const int MaximumMessageBytes = 1024 * 1024;

    private const string ActivityStartJson =
        "{\"realtimeInput\":{\"activityStart\":{}}}";
    private const string ActivityEndJson =
        "{\"realtimeInput\":{\"activityEnd\":{}}}";
    private const string AudioStreamEndJson =
        "{\"realtimeInput\":{\"audioStreamEnd\":true}}";
    private const string AudioJsonPrefix =
        "{\"realtimeInput\":{\"audio\":{\"data\":\"";
    private const string AudioJsonSuffix =
        "\",\"mimeType\":\"audio/pcm;rate=16000\"}}}";

    private readonly string endpoint;
    private readonly string token;
    private readonly SttTurnControl turnControl;
    private readonly SpeechTimingLogger.SttTrace timing;
    private readonly ClientWebSocket socket = new ClientWebSocket();
    private readonly CancellationTokenSource lifetime;
    private readonly ConcurrentQueue<OutboundMessage> outbox =
        new ConcurrentQueue<OutboundMessage>();
    private readonly SemaphoreSlim outboxSignal = new SemaphoreSlim(0);
    private readonly TaskCompletionSource<bool> readySource =
        new TaskCompletionSource<bool>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object sync = new object();
    private readonly StringBuilder finalText = new StringBuilder();

    private string latestPartial = string.Empty;
    private string failure;
    private int finalEventCount;
    private long lastFinalMs;
    private bool speechEndRequested;
    private long speechEndMs;
    private bool activityOpen;
    private bool activityOpenAtEnd;
    private bool endMarkerAfterEnd;
    private long endMarkerMs;
    private bool disposed;

    public GeminiLiveTranscriptionSession(
        string endpoint,
        string token,
        SttTurnControl turnControl,
        SpeechTimingLogger.SttTrace timing,
        CancellationToken cancellationToken)
    {
        this.endpoint = endpoint;
        this.token = token;
        this.turnControl = turnControl;
        this.timing = timing;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        // activityStart must precede any audio; the sender loop only starts
        // once the connection is ready, so audio recorded meanwhile is kept.
        if (turnControl == SttTurnControl.ManualActivity)
        {
            outbox.Enqueue(new OutboundMessage(
                ActivityStartJson,
                OutboundKind.ActivityStart));
        }

        _ = RunAsync();
    }

    public event Action<string> PartialTranscriptReceived;

    public string LatestPartialTranscript
    {
        get
        {
            lock (sync)
            {
                return latestPartial;
            }
        }
    }

    public bool IsFailed
    {
        get
        {
            lock (sync)
            {
                return failure != null;
            }
        }
    }

    public void SendAudio(byte[] pcm16LittleEndian, int byteCount)
    {
        if (pcm16LittleEndian == null || byteCount <= 0 ||
            disposed || IsFailed || Volatile.Read(ref speechEndRequested))
        {
            return;
        }

        string json = AudioJsonPrefix +
                      Convert.ToBase64String(pcm16LittleEndian, 0, byteCount) +
                      AudioJsonSuffix;
        outbox.Enqueue(new OutboundMessage(json, OutboundKind.Audio));
        SignalOutbox();
    }

    public async Task<SttResult> CompleteAsync()
    {
        if (disposed)
        {
            return new SttResult(false, null, null, "Session already closed.");
        }

        lock (sync)
        {
            speechEndRequested = true;
            speechEndMs = clock.ElapsedMilliseconds;
            activityOpenAtEnd = activityOpen;
        }

        SpeechTimingLogger.LogSttSpeechEnded(timing);
        outbox.Enqueue(new OutboundMessage(
            turnControl == SttTurnControl.ManualActivity
                ? ActivityEndJson
                : AudioStreamEndJson,
            OutboundKind.ActivityEnd));
        SignalOutbox();

        long deadlineMs = clock.ElapsedMilliseconds + FinalTimeoutMs;
        string timeoutError = null;

        try
        {
            while (true)
            {
                if (IsComplete(out bool timedOut, deadlineMs))
                {
                    if (timedOut)
                    {
                        timeoutError =
                            "Timed out waiting for the final transcript.";
                    }

                    break;
                }

                await Task.Delay(15, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel()/Dispose() while waiting: report whatever we have.
        }

        SttResult result;
        lock (sync)
        {
            string text = finalText.ToString().Trim();
            bool hasFinal = finalEventCount > 0 &&
                            !string.IsNullOrWhiteSpace(text);

            result = new SttResult(
                hasFinal,
                text,
                latestPartial,
                failure ?? (hasFinal ? null : timeoutError));
        }

        if (result.HasFinalTranscript)
        {
            // Normally already logged when the final arrived; this covers
            // finals that arrived before the speech turn was ended.
            SpeechTimingLogger.LogSttFinalTranscriptReceived(timing);
        }

        Dispose();
        return result;
    }

    public void Cancel()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            socket.Abort();
            socket.Dispose();
        }
        catch (Exception)
        {
            // Teardown must never throw into gameplay code.
        }

        readySource.TrySetResult(false);
    }

    private bool IsComplete(out bool timedOut, long deadlineMs)
    {
        timedOut = false;

        lock (sync)
        {
            if (failure != null)
            {
                return true;
            }

            long now = clock.ElapsedMilliseconds;

            if (finalEventCount > 0)
            {
                // Server-side speech segmentation can finalise an utterance
                // while the button is still held, so a final that predates the
                // release does not prove the last utterance has arrived.
                bool finalAfterEnd = lastFinalMs >= speechEndMs;

                if (finalAfterEnd &&
                    (endMarkerAfterEnd ||
                     now - lastFinalMs >= FinalQuietWindowMs))
                {
                    return true;
                }

                if (!finalAfterEnd)
                {
                    // Nothing was in flight at release: every utterance was
                    // already finalised while the button was held.
                    if (!activityOpenAtEnd &&
                        now - speechEndMs >= NothingPendingGraceMs)
                    {
                        return true;
                    }

                    if (endMarkerAfterEnd
                            ? now - endMarkerMs >= LateFinalGraceMs
                            : now - speechEndMs >= NoFinalAfterEndWindowMs)
                    {
                        return true;
                    }
                }
            }
            else if (endMarkerAfterEnd &&
                     now - endMarkerMs >= NoSpeechGraceMs)
            {
                return true;
            }

            if (now >= deadlineMs)
            {
                timedOut = true;
                return true;
            }

            return false;
        }
    }

    private void SignalOutbox()
    {
        try
        {
            outboxSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using (var connectCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       lifetime.Token))
            {
                connectCancellation.CancelAfter(ConnectTimeoutMs);

                socket.Options.SetRequestHeader(LanguageBackend.TokenHeader, token);
                await socket.ConnectAsync(new Uri(endpoint), connectCancellation.Token);
            }

            await SendTextAsync(
                GeminiLiveTranscriptionService.BuildSetupJson(turnControl),
                lifetime.Token);

            _ = ReceiveLoopAsync();

            Task finished = await Task.WhenAny(
                readySource.Task,
                Task.Delay(SetupTimeoutMs, lifetime.Token));

            if (!ReferenceEquals(finished, readySource.Task))
            {
                Debug.LogWarning(
                    "[GeminiLiveStt] No setupComplete within " +
                    $"{SetupTimeoutMs}ms; streaming anyway.");
            }

            if (IsFailed)
            {
                return;
            }

            await SendLoopAsync();
        }
        catch (OperationCanceledException)
        {
            if (!disposed && !lifetime.IsCancellationRequested)
            {
                Fail("Connecting to Gemini timed out.");
            }
        }
        catch (WebSocketException exception)
        {
            // ClientWebSocket surfaces a rejected HTTP upgrade as an exception.
            // On Unity/IL2CPP the status is commonly available only in its text.
            if (!disposed)
            {
                if (IsAuthFailure(exception.Message))
                {
                    LanguageBackend.RejectToken(token);
                    Fail("Backend session rejected during transcription. Pair again in VR Settings.");
                }
                else
                {
                    Fail("Gemini transcription connection failed: " + exception.Message);
                    _ = ProbeAuthAfterWebSocketFailureAsync();
                }
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                if (IsAuthFailure(exception.ToString()))
                {
                    LanguageBackend.RejectToken(token);
                    Fail("Backend session rejected during transcription. Pair again in VR Settings.");
                }
                else
                {
                    Fail("Gemini transcription connection failed: " +
                         exception.Message);
                    _ = ProbeAuthAfterWebSocketFailureAsync();
                }
            }
        }
    }

    private async Task SendLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            await outboxSignal.WaitAsync(lifetime.Token);

            while (outbox.TryDequeue(out OutboundMessage message))
            {
                await SendTextAsync(message.Json, lifetime.Token);

                if (message.Kind == OutboundKind.Audio)
                {
                    SpeechTimingLogger.LogSttFirstAudioChunkSent(timing);
                }
                else if (message.Kind == OutboundKind.ActivityEnd)
                {
                    // Nothing may follow activityEnd for this turn.
                    return;
                }
            }
        }
    }

    private Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[ReceiveBufferBytes];
        var message = new MemoryStream();

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        lifetime.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleClose(
                            result.CloseStatus,
                            result.CloseStatusDescription);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);

                    if (message.Length > MaximumMessageBytes)
                    {
                        throw new SttException(
                            "Gemini sent an unexpectedly large message.");
                    }
                }
                while (!result.EndOfMessage);

                // JSON may arrive in text or binary frames.
                HandleMessage(Encoding.UTF8.GetString(
                    message.GetBuffer(), 0, (int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // Local cancel/dispose.
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                Fail("Gemini transcription stream failed: " +
                     exception.Message);
            }
        }
    }

    private void HandleClose(
        WebSocketCloseStatus? status,
        string description)
    {
        if (disposed)
        {
            return;
        }

        bool alreadyHaveFinal;
        lock (sync)
        {
            alreadyHaveFinal = finalEventCount > 0;
        }

        if (status == WebSocketCloseStatus.NormalClosure && alreadyHaveFinal)
        {
            return;
        }

        string reason = string.IsNullOrWhiteSpace(description)
            ? "no reason given"
            : description.Trim();

        if (IsAuthFailure(reason))
            LanguageBackend.RejectToken(token);

        Fail($"Gemini closed the transcription connection " +
             $"({status?.ToString() ?? "unknown"}): {reason}");
    }

    private void HandleMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        if (GeminiLiveTranscriptionService.VerboseDiagnostics)
        {
            // Inbound messages carry text only (no audio); the key is never
            // echoed, but sanitise anyway.
            Debug.Log(
                $"[GeminiLiveStt] raw t={clock.ElapsedMilliseconds}ms: " +
                Sanitize(Truncate(json, 400)));
        }

        if (json.Contains("\"setupComplete\""))
        {
            readySource.TrySetResult(true);
        }

        ServerMessage parsed;
        try
        {
            parsed = JsonUtility.FromJson<ServerMessage>(json);
        }
        catch (ArgumentException)
        {
            if (json.Contains("\"error\""))
            {
                Fail("Gemini reported an error: " + Truncate(json, 300));
            }

            return;
        }

        if (parsed == null)
        {
            return;
        }

        if (parsed.error != null &&
            (parsed.error.code == 401 || parsed.error.code == 403 ||
             !string.IsNullOrWhiteSpace(parsed.error.message)))
        {
            if (parsed.error.code == 401 || parsed.error.code == 403)
                LanguageBackend.RejectToken(token);
            Fail(parsed.error.code == 401 || parsed.error.code == 403
                ? "Backend session rejected during transcription. Pair again in VR Settings."
                : "Gemini reported an error: " + (parsed.error.message ?? "Unknown error.").Trim());
            return;
        }

        if (parsed.voiceActivity != null)
        {
            if (parsed.voiceActivity.type == "ACTIVITY_START")
            {
                lock (sync)
                {
                    activityOpen = true;
                }
            }
            else if (parsed.voiceActivity.type == "ACTIVITY_END")
            {
                lock (sync)
                {
                    activityOpen = false;
                }

                MarkEndOfTurn();
            }
        }

        ServerContent content = parsed.serverContent;
        if (content == null)
        {
            return;
        }

        string interim = content.interimInputTranscription?.text;
        string final = content.inputTranscription?.text;
        bool hasInterim = !string.IsNullOrEmpty(interim);
        bool hasFinal = !string.IsNullOrWhiteSpace(final);

        if (GeminiLiveTranscriptionService.VerboseDiagnostics)
        {
            Debug.Log(
                "[GeminiLiveStt] message: " +
                (hasInterim ? $"interim(len={interim.Length}) " : string.Empty) +
                (hasFinal ? $"final(len={final.Length}) " : string.Empty) +
                (content.turnComplete ? "turnComplete " : string.Empty) +
                (content.generationComplete ? "generationComplete " : string.Empty) +
                $"t={clock.ElapsedMilliseconds}ms");
        }

        if (hasInterim)
        {
            string caption;
            lock (sync)
            {
                // Display only: utterances already finalised in this hold
                // plus the current hypothesis. The submitted transcript is
                // assembled separately from final events.
                string committed = finalText.ToString().Trim();
                caption = committed.Length == 0
                    ? interim
                    : committed + " " + interim.TrimStart();
                latestPartial = caption;
            }

            SpeechTimingLogger.LogSttFirstTranscriptReceived(timing, false);
            PartialTranscriptReceived?.Invoke(caption);
        }

        if (hasFinal)
        {
            bool afterSpeechEnd;
            lock (sync)
            {
                AppendFinal(final);
                latestPartial = string.Empty;
                finalEventCount++;
                lastFinalMs = clock.ElapsedMilliseconds;
                afterSpeechEnd = speechEndRequested;
            }

            SpeechTimingLogger.LogSttFirstTranscriptReceived(timing, true);

            if (afterSpeechEnd)
            {
                SpeechTimingLogger.LogSttFinalTranscriptReceived(timing);
            }
        }

        if (content.turnComplete || content.generationComplete)
        {
            MarkEndOfTurn();
        }
    }

    private void MarkEndOfTurn()
    {
        lock (sync)
        {
            if (speechEndRequested && !endMarkerAfterEnd)
            {
                endMarkerAfterEnd = true;
                endMarkerMs = clock.ElapsedMilliseconds;
            }
        }
    }

    // Caller holds `sync`. Accepts either whole-utterance finals or finals that
    // repeat everything said so far, without duplicating text.
    private void AppendFinal(string fragment)
    {
        string current = finalText.ToString();

        if (current.Length == 0)
        {
            finalText.Append(fragment);
            return;
        }

        if (fragment.TrimStart().StartsWith(
                current.Trim(),
                StringComparison.Ordinal))
        {
            finalText.Clear();
            finalText.Append(fragment);
            return;
        }

        bool needsSpace = !char.IsWhiteSpace(current[current.Length - 1]) &&
                          !char.IsWhiteSpace(fragment[0]);
        if (needsSpace)
        {
            finalText.Append(' ');
        }

        finalText.Append(fragment);
    }

    private void Fail(string message)
    {
        string safeMessage = Sanitize(message);

        lock (sync)
        {
            if (failure != null)
            {
                return;
            }

            failure = safeMessage;
        }

        readySource.TrySetResult(false);

        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Redact the session token if an error includes request details.
    private string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return text;
        }

        return text.Replace(token, "[redacted]");
    }

    private static bool IsAuthFailure(string message) =>
        !string.IsNullOrEmpty(message) &&
        Regex.IsMatch(message, @"(?<!\d)(401|403)(?!\d)");

    // Some Unity/IL2CPP ClientWebSocket implementations hide the rejected
    // upgrade's HTTP status. The same credential guards /api/health, so probe
    // it after an opaque handshake failure and invalidate only on 401/403.
    private static async Task ProbeAuthAfterWebSocketFailureAsync()
    {
        try { await LanguageBackend.TestConnectionAsync(); }
        catch (Exception) { /* Keep the original WebSocket error. */ }
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value.Substring(0, maximumLength) + "...";
    }

    private enum OutboundKind
    {
        ActivityStart,
        Audio,
        ActivityEnd
    }

    private readonly struct OutboundMessage
    {
        public OutboundMessage(string json, OutboundKind kind)
        {
            Json = json;
            Kind = kind;
        }

        public string Json { get; }
        public OutboundKind Kind { get; }
    }

    [Serializable]
    private sealed class ServerMessage
    {
        public ServerContent serverContent;
        public VoiceActivity voiceActivity;
        public GeminiError error;
    }

    [Serializable]
    private sealed class VoiceActivity
    {
        public string type;
    }

    [Serializable]
    private sealed class ServerContent
    {
        public Transcription interimInputTranscription;
        public Transcription inputTranscription;
        public bool turnComplete;
        // Observed on the wire for this model instead of turnComplete.
        public bool generationComplete;
    }

    [Serializable]
    private sealed class Transcription
    {
        public string text;
    }

    [Serializable]
    private sealed class GeminiError
    {
        public int code;
        public string message;
        public string status;
    }
}
