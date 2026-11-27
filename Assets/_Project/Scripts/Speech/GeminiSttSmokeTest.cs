using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Isolated speech-to-text smoke test. It never touches DialogueManager.
// Requires Play mode (async continuations run on Unity's main thread).
//
// Two sources of audio:
//   * Microphone: detects devices, records the test phrase spoken by a person.
//   * Synthetic:  streams a raw 16 kHz mono 16-bit little-endian PCM file at
//     real-time pace. This exercises the WebSocket/transcription path without
//     a person or a microphone, and is NOT a microphone test. (Deliberately
//     independent of the TTS service.)
public sealed class GeminiSttSmokeTest : MonoBehaviour
{
    public const string TestPhrase = "I'd like a medium cappuccino with oat milk.";

    [SerializeField] private string preferredMicrophone = string.Empty;
    [SerializeField, Min(0f)] private float countdownSeconds = 3f;
    [SerializeField, Min(1f)] private float recordSeconds = 6f;
    [SerializeField] private bool runMicrophoneOnStart;
    [SerializeField] private string syntheticPcmPath = string.Empty;
    [SerializeField] private SttTurnControl turnControl = SttTurnControl.AutomaticActivityDetection;

    private int consoleErrorCount;

    public bool IsRunning { get; private set; }
    public bool HasRun { get; private set; }
    public bool Passed { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public int DetectedMicrophoneCount { get; private set; }
    public string DeviceUsed { get; private set; } = string.Empty;
    public int SourceSampleRate { get; private set; }
    public float StreamedSeconds { get; private set; }
    public float PeakLevel { get; private set; }
    public int PartialCount { get; private set; }
    public string FirstPartial { get; private set; } = string.Empty;
    public string FinalTranscript { get; private set; } = string.Empty;
    public double WordErrorRate { get; private set; } = double.NaN;
    public double MicToFirstChunkMs { get; private set; } = double.NaN;
    public double FirstChunkToFirstTranscriptMs { get; private set; } = double.NaN;
    public double SpeechEndToFinalMs { get; private set; } = double.NaN;
    public int ConsoleErrorsDuringRun => consoleErrorCount;
    public string Failure { get; private set; } = string.Empty;
    public string Report { get; private set; } = string.Empty;

    private void Start()
    {
        if (runMicrophoneOnStart)
        {
            RunMicrophoneSmokeTest();
        }
    }

    [ContextMenu("Run STT Smoke Test (Microphone)")]
    public async void RunMicrophoneSmokeTest()
    {
        await RunAsync(useMicrophone: true);
    }

    [ContextMenu("Run STT Smoke Test (Synthetic PCM file)")]
    public async void RunSyntheticSmokeTest()
    {
        await RunAsync(useMicrophone: false, syntheticPcmPath);
    }

    public async void RunSyntheticSmokeTest(string rawPcmPath)
    {
        await RunAsync(useMicrophone: false, rawPcmPath);
    }

    public void SetTurnControl(SttTurnControl value)
    {
        turnControl = value;
    }

    public async Task RunAsync(bool useMicrophone, string rawPcmPath = null)
    {
        if (IsRunning)
        {
            return;
        }

        ResetResults();
        IsRunning = true;
        HasRun = true;
        Source = useMicrophone
            ? "microphone"
            : "synthetic PCM file " + Path.GetFileName(rawPcmPath ?? string.Empty);
        GeminiLiveTranscriptionService.VerboseDiagnostics = true;
        Application.logMessageReceived += HandleLogMessage;

        ISttSession session = null;

        try
        {
            Debug.Log($"[GeminiSttSmokeTest] Starting ({Source}). " +
                      $"Model={GeminiLiveTranscriptionService.ModelName}, " +
                      $"language={GeminiLiveTranscriptionService.LanguageCode}, " +
                      $"mode={GeminiLiveTranscriptionService.TranscriptionMode}, " +
                      $"turnControl={turnControl}.", this);

            byte[] syntheticPcm = null;
            if (!useMicrophone)
            {
                if (string.IsNullOrWhiteSpace(rawPcmPath) ||
                    !File.Exists(rawPcmPath))
                {
                    throw new FileNotFoundException(
                        "Synthetic PCM file not found (expected raw 16 kHz " +
                        "mono s16le).");
                }

                syntheticPcm = File.ReadAllBytes(rawPcmPath);
            }

            var capture = new MicrophonePcmCapture();
            string deviceLabel;

            if (useMicrophone)
            {
                DetectMicrophones();
                if (DetectedMicrophoneCount == 0)
                {
                    throw new InvalidOperationException("No microphone was detected.");
                }

                Debug.Log(
                    $"[GeminiSttSmokeTest] Say: \"{TestPhrase}\" -- recording " +
                    $"starts in {countdownSeconds:F0}s and lasts {recordSeconds:F0}s.",
                    this);

                await WaitSecondsAsync(countdownSeconds);

                if (!capture.TryStart(preferredMicrophone, out string startError))
                {
                    throw new InvalidOperationException(startError);
                }

                deviceLabel = capture.DeviceName;
            }
            else
            {
                deviceLabel = "synthetic:pcm-file";
            }

            DeviceUsed = deviceLabel;
            SpeechTimingLogger.SttTrace trace =
                SpeechTimingLogger.LogSttMicrophoneStarted(deviceLabel);

            session = new GeminiLiveTranscriptionService { TurnControl = turnControl }
                .StartSession(trace);
            var partials = new List<string>();
            session.PartialTranscriptReceived += partial =>
            {
                PartialCount++;
                partials.Add(partial);
                if (FirstPartial.Length == 0)
                {
                    FirstPartial = partial;
                }
            };

            long bytesStreamed;
            if (useMicrophone)
            {
                Debug.Log($"[GeminiSttSmokeTest] SPEAK NOW: \"{TestPhrase}\"", this);
                bytesStreamed = await StreamMicrophoneAsync(capture, session);
                SourceSampleRate = capture.SourceSampleRate;
                PeakLevel = capture.PeakLevel;
            }
            else
            {
                bytesStreamed = await StreamSyntheticAsync(syntheticPcm, session);
                SourceSampleRate = MicrophonePcmCapture.TargetSampleRate;
            }

            StreamedSeconds = bytesStreamed / 2f /
                              MicrophonePcmCapture.TargetSampleRate;

            SttResult result = await session.CompleteAsync();
            session = null;

            FinalTranscript = result.FinalTranscript;
            MicToFirstChunkMs = SpeechTimingLogger.SttMicToFirstChunkMs(trace);
            FirstChunkToFirstTranscriptMs =
                SpeechTimingLogger.SttFirstChunkToFirstTranscriptMs(trace);
            SpeechEndToFinalMs = SpeechTimingLogger.SttSpeechEndToFinalMs(trace);
            WordErrorRate = ComputeWordErrorRate(TestPhrase, FinalTranscript);

            Evaluate(result, double.IsNaN(MicToFirstChunkMs), partials.Count);
        }
        catch (Exception exception)
        {
            Failure = exception.Message;
            Passed = false;
        }
        finally
        {
            session?.Cancel();
            Application.logMessageReceived -= HandleLogMessage;
            GeminiLiveTranscriptionService.VerboseDiagnostics = false;
            IsRunning = false;
            Report = BuildReport();

            if (Passed)
            {
                Debug.Log(Report, this);
            }
            else
            {
                Debug.LogWarning(Report, this);
            }
        }
    }

    private void Evaluate(SttResult result, bool noChunkSent, int partialCount)
    {
        var problems = new List<string>();

        if (noChunkSent)
        {
            problems.Add("no audio chunk was sent");
        }

        if (partialCount == 0)
        {
            problems.Add("no incremental (partial) transcription was received");
        }

        if (!result.HasFinalTranscript)
        {
            problems.Add("no final transcription was received" +
                         (string.IsNullOrEmpty(result.Error)
                             ? string.Empty
                             : ": " + result.Error));
        }

        if (consoleErrorCount > 0)
        {
            problems.Add($"{consoleErrorCount} Unity Console error(s) during the run");
        }

        Passed = problems.Count == 0;
        Failure = string.Join("; ", problems);
    }

    private void DetectMicrophones()
    {
        string[] devices = MicrophonePcmCapture.GetDevices();
        DetectedMicrophoneCount = devices?.Length ?? 0;

        Debug.Log(
            $"[GeminiSttSmokeTest] Detected {DetectedMicrophoneCount} " +
            "microphone(s).", this);

        if (devices == null)
        {
            return;
        }

        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"[GeminiSttSmokeTest]   [{i}] {devices[i]}", this);
        }
    }

    private async Task<long> StreamMicrophoneAsync(
        MicrophonePcmCapture capture,
        ISttSession session)
    {
        long bytes = 0;
        void Send(byte[] buffer, int count)
        {
            session.SendAudio(buffer, count);
            bytes += count;
        }

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < recordSeconds)
        {
            capture.Pump(Send);
            await Task.Yield();
        }

        // Same release tail the game uses, so the last word is not clipped.
        await WaitSecondsAsync(0.15f);
        capture.Pump(Send);
        capture.Stop(Send);
        return bytes;
    }

    private static async Task<long> StreamSyntheticAsync(
        byte[] pcm,
        ISttSession session)
    {
        const int ChunkBytes =
            MicrophonePcmCapture.TargetSampleRate / 10 * 2; // 100 ms

        Stopwatch clock = Stopwatch.StartNew();
        int chunkIndex = 0;

        for (int offset = 0; offset < pcm.Length; offset += ChunkBytes)
        {
            // Pace like a live microphone: chunk k is due at k * 100 ms.
            while (clock.ElapsedMilliseconds < chunkIndex * 100L)
            {
                await Task.Yield();
            }

            int count = Math.Min(ChunkBytes, pcm.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(pcm, offset, chunk, 0, count);
            session.SendAudio(chunk, count);
            chunkIndex++;
        }

        return pcm.Length;
    }

    private static async Task WaitSecondsAsync(float seconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Yield();
        }
    }

    private void HandleLogMessage(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception ||
            type == LogType.Assert)
        {
            consoleErrorCount++;
        }
    }

    private void ResetResults()
    {
        consoleErrorCount = 0;
        Passed = false;
        Source = string.Empty;
        DetectedMicrophoneCount = 0;
        DeviceUsed = string.Empty;
        SourceSampleRate = 0;
        StreamedSeconds = 0f;
        PeakLevel = 0f;
        PartialCount = 0;
        FirstPartial = string.Empty;
        FinalTranscript = string.Empty;
        WordErrorRate = double.NaN;
        MicToFirstChunkMs = double.NaN;
        FirstChunkToFirstTranscriptMs = double.NaN;
        SpeechEndToFinalMs = double.NaN;
        Failure = string.Empty;
        Report = string.Empty;
    }

    private string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"[GeminiSttSmokeTest] RESULT: {(Passed ? "PASS" : "FAIL")} ({Source})");
        builder.AppendLine($"  device: {DeviceUsed} (source rate {SourceSampleRate} Hz)");
        builder.AppendLine($"  microphones detected: {DetectedMicrophoneCount}");
        builder.AppendLine($"  streamed: {StreamedSeconds:F2}s, peak level {PeakLevel:F3}");
        builder.AppendLine($"  partial transcripts: {PartialCount} (first: \"{FirstPartial}\")");
        builder.AppendLine($"  final transcript: \"{FinalTranscript}\"");
        builder.AppendLine($"  expected phrase:  \"{TestPhrase}\"");
        builder.AppendLine($"  word error rate vs phrase: {Format(WordErrorRate * 100.0)}%");
        builder.AppendLine(
            $"  latency: micToFirstChunk={Format(MicToFirstChunkMs)}ms, " +
            $"firstChunkToFirstTranscript={Format(FirstChunkToFirstTranscriptMs)}ms, " +
            $"speechEndToFinal={Format(SpeechEndToFinalMs)}ms");
        builder.AppendLine($"  console errors during run: {consoleErrorCount}");

        if (!string.IsNullOrEmpty(Failure))
        {
            builder.AppendLine($"  problems: {Failure}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Format(double value)
    {
        return double.IsNaN(value) ? "n/a" : value.ToString("F1");
    }

    // Word-level edit distance normalised by the reference length.
    public static double ComputeWordErrorRate(string reference, string hypothesis)
    {
        string[] r = Tokenize(reference);
        string[] h = Tokenize(hypothesis);

        if (r.Length == 0)
        {
            return h.Length == 0 ? 0.0 : 1.0;
        }

        var d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= h.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= r.Length; i++)
        {
            for (int j = 1; j <= h.Length; j++)
            {
                int cost = r[i - 1] == h[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return (double)d[r.Length, h.Length] / r.Length;
    }

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
        {
            // Keep apostrophes so "I'd" stays one word.
            builder.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        }

        return builder.ToString().Split(
            new[] { ' ' },
            StringSplitOptions.RemoveEmptyEntries);
    }
}
