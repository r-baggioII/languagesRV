using System.Diagnostics;
using UnityEngine;

public static class SpeechTimingLogger
{
    // One text-to-speech turn (any provider): LLM reply ready -> request sent ->
    // HTTP response completed -> AudioClip ready -> playback.
    public sealed class Trace
    {
        internal int TurnId;
        internal string Provider = "Kokoro";
        internal double RequestSentMs = double.NaN;
        internal double ResponseReceivedMs = double.NaN;
        internal double ClipReadyMs = double.NaN;
        internal double PlaybackStartedMs = double.NaN;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static int turnId;
    private static double llmResponseMs = double.NaN;

    public static int LogLlmResponseReceived()
    {
        turnId++;
        llmResponseMs = NowMs;
        Log(turnId, "LLM response received", llmResponseMs, string.Empty);
        return turnId;
    }

    // Creates the trace for the current LLM turn; nothing is logged yet.
    public static Trace BeginTtsTrace(string provider = "Kokoro")
    {
        return new Trace { TurnId = turnId, Provider = provider };
    }

    public static void LogTtsRequestSent(Trace trace)
    {
        if (trace == null)
        {
            return;
        }

        trace.RequestSentMs = NowMs;
        Log(
            trace.TurnId,
            $"{trace.Provider} request sent",
            trace.RequestSentMs,
            FormatDelta("llmToRequest", llmResponseMs, trace.RequestSentMs));
    }

    public static void LogTtsResponseReceived(Trace trace, int byteCount)
    {
        if (trace == null)
        {
            return;
        }

        trace.ResponseReceivedMs = NowMs;
        Log(
            trace.TurnId,
            $"{trace.Provider} HTTP response completed",
            trace.ResponseReceivedMs,
            Join(
                FormatDelta(
                    "requestToResponse",
                    trace.RequestSentMs,
                    trace.ResponseReceivedMs),
                $"bytes={byteCount}"));
    }

    public static void LogTtsClipReady(Trace trace, float clipSeconds)
    {
        if (trace == null)
        {
            return;
        }

        trace.ClipReadyMs = NowMs;
        Log(
            trace.TurnId,
            "AudioClip ready",
            trace.ClipReadyMs,
            Join(
                FormatDelta(
                    "decode",
                    trace.ResponseReceivedMs,
                    trace.ClipReadyMs),
                $"audioDuration={clipSeconds:F2}s"));
    }

    public static void LogPlaybackStarted(Trace trace)
    {
        if (trace == null)
        {
            return;
        }

        trace.PlaybackStartedMs = NowMs;
        Log(
            trace.TurnId,
            "playback started",
            trace.PlaybackStartedMs,
            Join(
                FormatDelta(
                    "llmToPlayback",
                    llmResponseMs,
                    trace.PlaybackStartedMs),
                FormatDelta(
                    "requestToPlayback",
                    trace.RequestSentMs,
                    trace.PlaybackStartedMs)));
    }

    public static void LogPlaybackFinished(Trace trace)
    {
        if (trace == null)
        {
            return;
        }

        double nowMs = NowMs;
        Log(
            trace.TurnId,
            "playback finished",
            nowMs,
            Join(
                FormatDelta(
                    "playbackDuration",
                    trace.PlaybackStartedMs,
                    nowMs),
                FormatDelta(
                    "requestToFinished",
                    trace.RequestSentMs,
                    nowMs)));
    }

    public static void LogCachedPlaybackStarted()
    {
        Log(
            turnId,
            "playback started (cached greeting)",
            NowMs,
            "requestToPlayback=0.0ms");
    }

    public static void LogCachedPlaybackFinished()
    {
        Log(
            turnId,
            "playback finished (cached greeting)",
            NowMs,
            string.Empty);
    }

    // Speech-to-text turns are traced independently of TTS turns: they start
    // when the microphone opens, not when the LLM answers.
    public sealed class SttTrace
    {
        internal int SttTurnId;
        internal double MicrophoneStartedMs;
        internal double FirstChunkSentMs = double.NaN;
        internal double FirstTranscriptMs = double.NaN;
        internal double SpeechEndedMs = double.NaN;
        internal double FinalTranscriptMs = double.NaN;
    }

    private static int sttTurnId;

    public static SttTrace LogSttMicrophoneStarted(string deviceName)
    {
        var trace = new SttTrace
        {
            SttTurnId = ++sttTurnId,
            MicrophoneStartedMs = NowMs
        };

        LogStt(
            trace.SttTurnId,
            "microphone started",
            trace.MicrophoneStartedMs,
            $"device='{deviceName}'");

        return trace;
    }

    public static void LogSttFirstAudioChunkSent(SttTrace trace)
    {
        if (trace == null || !double.IsNaN(trace.FirstChunkSentMs))
        {
            return;
        }

        trace.FirstChunkSentMs = NowMs;
        LogStt(
            trace.SttTurnId,
            "first audio chunk sent",
            trace.FirstChunkSentMs,
            FormatDelta(
                "micToFirstChunk",
                trace.MicrophoneStartedMs,
                trace.FirstChunkSentMs));
    }

    public static void LogSttFirstTranscriptReceived(
        SttTrace trace,
        bool isFinal)
    {
        if (trace == null || !double.IsNaN(trace.FirstTranscriptMs))
        {
            return;
        }

        trace.FirstTranscriptMs = NowMs;
        LogStt(
            trace.SttTurnId,
            isFinal
                ? "first transcript received (final)"
                : "first transcript received (partial)",
            trace.FirstTranscriptMs,
            Join(
                FormatDelta(
                    "firstChunkToFirstTranscript",
                    trace.FirstChunkSentMs,
                    trace.FirstTranscriptMs),
                FormatDelta(
                    "micToFirstTranscript",
                    trace.MicrophoneStartedMs,
                    trace.FirstTranscriptMs)));
    }

    public static void LogSttSpeechEnded(SttTrace trace)
    {
        if (trace == null || !double.IsNaN(trace.SpeechEndedMs))
        {
            return;
        }

        trace.SpeechEndedMs = NowMs;
        LogStt(
            trace.SttTurnId,
            "speech turn ended (release, activityEnd queued)",
            trace.SpeechEndedMs,
            FormatDelta(
                "micOpen",
                trace.MicrophoneStartedMs,
                trace.SpeechEndedMs));
    }

    public static void LogSttFinalTranscriptReceived(SttTrace trace)
    {
        if (trace == null || !double.IsNaN(trace.FinalTranscriptMs))
        {
            return;
        }

        trace.FinalTranscriptMs = NowMs;
        LogStt(
            trace.SttTurnId,
            "final transcript received",
            trace.FinalTranscriptMs,
            Join(
                FormatDelta(
                    "speechEndToFinal",
                    trace.SpeechEndedMs,
                    trace.FinalTranscriptMs),
                FormatDelta(
                    "micToFinal",
                    trace.MicrophoneStartedMs,
                    trace.FinalTranscriptMs)));
    }

    public static void LogSttTranscriptSubmitted(SttTrace trace)
    {
        if (trace == null)
        {
            return;
        }

        double nowMs = NowMs;
        LogStt(
            trace.SttTurnId,
            "transcript submitted to DialogueManager",
            nowMs,
            Join(
                FormatDelta(
                    "finalToSubmit",
                    trace.FinalTranscriptMs,
                    nowMs),
                FormatDelta(
                    "speechEndToSubmit",
                    trace.SpeechEndedMs,
                    nowMs)));
    }

    // Milliseconds between two events of a trace; NaN when either is missing.
    public static double SttSpeechEndToFinalMs(SttTrace trace)
    {
        return trace == null
            ? double.NaN
            : trace.FinalTranscriptMs - trace.SpeechEndedMs;
    }

    public static double SttFirstChunkToFirstTranscriptMs(SttTrace trace)
    {
        return trace == null
            ? double.NaN
            : trace.FirstTranscriptMs - trace.FirstChunkSentMs;
    }

    public static double SttMicToFirstChunkMs(SttTrace trace)
    {
        return trace == null
            ? double.NaN
            : trace.FirstChunkSentMs - trace.MicrophoneStartedMs;
    }

    private static void LogStt(
        int traceTurnId,
        string eventName,
        double timestampMs,
        string details)
    {
        string suffix = string.IsNullOrEmpty(details)
            ? string.Empty
            : " | " + details;

        UnityEngine.Debug.Log(
            $"[SpeechTiming] stt={traceTurnId} | {eventName} | " +
            $"t={timestampMs:F1}ms{suffix}");
    }

    private static double NowMs => Clock.Elapsed.TotalMilliseconds;

    private static string FormatDelta(
        string label,
        double startMs,
        double endMs)
    {
        return double.IsNaN(startMs) || double.IsNaN(endMs)
            ? string.Empty
            : $"{label}={endMs - startMs:F1}ms";
    }

    private static string Join(string first, string second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return second;
        }

        return string.IsNullOrEmpty(second)
            ? first
            : first + " | " + second;
    }

    private static void Log(
        int traceTurnId,
        string eventName,
        double timestampMs,
        string details)
    {
        string suffix = string.IsNullOrEmpty(details)
            ? string.Empty
            : " | " + details;

        UnityEngine.Debug.Log(
            $"[SpeechTiming] turn={traceTurnId} | {eventName} | " +
            $"t={timestampMs:F1}ms{suffix}");
    }
}
