using System.Diagnostics;
using LLama.Exceptions;

namespace Meetu;

// One summary in its own low-priority process, so model memory is fully released afterwards and a native
// failure cannot affect the app or a recording. The output is written only when complete: stopping
// early leaves no partial private text behind, and the meeting simply stays pending.
public enum SummaryJobProcessor { Configured, CpuAfterGpuFailure }

public static class SummaryJob
{
    public const int CompletedExitCode = 0;
    public const int FailedExitCode = 1;
    public const int InterruptedExitCode = 3;
    public const int BusyExitCode = 4;
    public const int GpuFailedExitCode = 5;
    private static readonly TimeSpan RecordingCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecordingConfirmationDelay = TimeSpan.FromMilliseconds(200);

    public static async Task<int> RunAsync(StatePaths paths, string sessionDirectory, SummaryJobProcessor processor, Action<SummaryProgress> progress, Action recordingStarted)
    {
        var config = paths.LoadConfig();
        var modelPath = config.ResolvedSummaryModelPath;
        var useGpu = config.SummaryProcessor == SummaryProcessor.Gpu && processor == SummaryJobProcessor.Configured;
        LocalPath.Require(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Summary model missing. Download it from the app, or choose a local GGUF file in Settings.");
        FileStream summaryLock;
        try { summaryLock = new FileStream(Path.Combine(paths.Root, "summary.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x80070020)) { return BusyExitCode; }
        using (summaryLock)
        {
            if (WorkerLock.IsHeld(paths)) return InterruptedExitCode;
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            var modelName = Path.GetFileNameWithoutExtension(modelPath);
            using var stopWatching = new CancellationTokenSource();
            var watcher = WatchForRecordingAsync(paths, recordingStarted, stopWatching.Token);
            try
            {
                var feed = new TranscriptFeed(sessionDirectory);
                var transcript = feed.ReadNew();
                // Summarising around damaged records would quietly misrepresent the meeting.
                if (feed.UnreadableRecords > 0) throw new InvalidDataException("Some transcript records could not be read.");
                using var model = new LlamaTextModel(modelPath, useGpu);
                var device = model.UsesGpu ? "GPU" : useGpu ? "CPU, because no usable GPU was found"
                    : processor == SummaryJobProcessor.CpuAfterGpuFailure ? "CPU, because the GPU attempt failed" : "CPU";
                SummaryFiles.Write(sessionDirectory, await SummarizeAsync(model, transcript, modelName, device, progress));
                return CompletedExitCode;
            }
            // Drivers vary a lot, especially on integrated GPUs. The app retries on the CPU in a fresh process,
            // because native libraries cannot be swapped within one, so nothing is recorded against the meeting yet.
            catch (Exception exception) when (useGpu && exception is not InvalidDataException) { return GpuFailedExitCode; }
            catch (Exception exception)
            {
                // Only the exception type: a native or parser message could quote the transcript.
                SummaryFiles.WriteFailure(sessionDirectory, modelName, Describe(exception));
                return FailedExitCode;
            }
            finally
            {
                stopWatching.Cancel();
                await watcher;
            }
        }
    }

    private static string Describe(Exception exception) => exception is MissingTemplateException
        ? "This model's chat format is not supported. Choose a different GGUF model in Settings. The transcript is unaffected."
        : $"The summary could not be generated ({exception.GetType().Name}). The transcript is unaffected.";

    internal static async Task<MeetingSummary> SummarizeAsync(ITextModel model, IReadOnlyList<TranscriptLine> transcript, string modelName, string device, Action<SummaryProgress> progress)
    {
        var elapsed = Stopwatch.StartNew();
        var notes = await new Summarizer(model).SummarizeAsync(transcript, progress, CancellationToken.None);
        return notes is null
            ? new MeetingSummary(SummaryState.NoSpeech, DateTimeOffset.UtcNow, modelName)
            : new MeetingSummary(SummaryState.Complete, DateTimeOffset.UtcNow, modelName, elapsed.Elapsed.TotalSeconds, notes, Device: device);
    }

    // Native prompt evaluation cannot be cancelled midway, so the caller ends the process: a meeting's
    // responsiveness outranks an unfinished summary, which restarts once the recording is done.
    private static async Task WatchForRecordingAsync(StatePaths paths, Action recordingStarted, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecordingCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!WorkerLock.IsHeld(paths)) continue;
                // The app probes this lock too. Colliding with that probe looks like a held lock for an instant,
                // and acting on it would throw away minutes of work, so a recording must still be there a moment later.
                await Task.Delay(RecordingConfirmationDelay, cancellationToken);
                if (WorkerLock.IsHeld(paths)) { recordingStarted(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
