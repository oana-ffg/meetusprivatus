using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using NAudio.CoreAudioApi;

namespace Meetu;

public sealed record SessionStatus(string State, string? SessionDirectory = null, long PendingBytes = 0,
    double OldestAudioSeconds = 0, string? Error = null);

public sealed class CaptureSession
{
    private readonly AppConfig config;
    private readonly StatePaths paths;
    private readonly Action<SessionStatus>? statusChanged;
    private readonly Func<AppConfig, ISessionAudioCapture> openCapture;
    private readonly Func<AppConfig, IClipRecognizer> openRecognizer;
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PendingBudget budget = new();
    private SessionStatus status = new("Starting");
    public SessionStatus Status
    {
        get
        {
            var pending = budget.Snapshot(Stopwatch.GetTimestamp());
            return Volatile.Read(ref status) with { PendingBytes = pending.Bytes, OldestAudioSeconds = pending.AgeSeconds };
        }
    }
    public void Stop() => stop.TrySetResult();

    public CaptureSession(AppConfig config, StatePaths paths, Action<SessionStatus>? statusChanged = null)
        : this(config, paths, c => new SessionAudioCapture(c), c => new Recognizer(c), statusChanged) { }

    internal CaptureSession(AppConfig config, StatePaths paths, Func<AppConfig, ISessionAudioCapture> openCapture,
        Func<AppConfig, IClipRecognizer> openRecognizer, Action<SessionStatus>? statusChanged = null)
    {
        this.config = config; this.paths = paths; this.statusChanged = statusChanged;
        this.openCapture = openCapture; this.openRecognizer = openRecognizer;
    }

    public async Task RunAsync()
    {
        TranscriptStore? transcripts = null;
        var sessionId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTimeOffset.UtcNow;
        var packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var clips = Channel.CreateBounded<AudioClip>(new BoundedChannelOptions(1500)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var cancelInference = new CancellationTokenSource();
        using var cancelMonitor = new CancellationTokenSource();
        var sessionClock = Stopwatch.StartNew();
        double captureSeconds = 0;
        Task? assembleTask = null;
        Task? recognitionTask = null;
        Task? monitorTask = null;
        ClipWorkspace? workspace = null;
        IClipRecognizer? recognizer = null;
        ISessionAudioCapture? capture = null;
        CaptureDiagnostics? diagnostics = null;
        try
        {
            if (stop.Task.IsCompleted) { Update(new SessionStatus("Complete")); return; }
            config.Validate();
            PrivateDirectory.Create(config.ResolvedOutputDirectory);
            workspace = new ClipWorkspace(paths.AudioRoot, sessionId);
            recognizer = openRecognizer(config);
            if (stop.Task.IsCompleted) { Update(new SessionStatus("Complete")); return; }
            capture = openCapture(config);
            var directory = Path.Combine(config.ResolvedOutputDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + sessionId);
            transcripts = new TranscriptStore(directory);
            startedUtc = DateTimeOffset.UtcNow;
            JsonFiles.Write(Path.Combine(directory, "session.json"), new
            { StartedUtc = startedUtc, State = "Capturing", capture.MicrophoneId, capture.PlaybackId });
            var startedTimestamp = Stopwatch.GetTimestamp();
            diagnostics = new CaptureDiagnostics(startedTimestamp);
            var localClock = new CaptureTimeline(startedTimestamp, 100);
            var remoteClock = new CaptureTimeline(startedTimestamp, 100);
            sessionClock.Restart();

            void Receive(AudioSource source, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
            {
                // The library lends the native buffer only until this callback returns.
                // No disk, inference, blocking queue writes, or logging occur here.
                if (buffer.Length > 32000 || buffer.Length % 2 != 0)
                { fault.TrySetResult("Capture returned an invalid or oversized audio packet."); return; }
                var arrival = Stopwatch.GetTimestamp();
                diagnostics.Received(source, buffer.Length, flags, position, qpc, arrival, packets.Reader.Count);
                // WASAPI can report no available frames. This is not an audio packet and its
                // unset timing fields must not replace the previous real packet's timeline.
                if (buffer.IsEmpty) { diagnostics.CallbackCompleted(source, arrival); return; }
                var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                var clock = source == AudioSource.Local ? localClock : remoteClock;
                var timing = clock.Stamp(buffer.Length / 2, qpc,
                    (flags & AudioClientBufferFlags.TimestampError) == 0, discontinuity, arrival);
                if (!packets.Writer.TryWrite(new AudioPacket(source, buffer.ToArray(), timing.StartSeconds,
                    discontinuity, timing.CapturedTimestamp)))
                    fault.TrySetResult("Audio capture queue filled. Capture stopped to protect meeting responsiveness.");
                diagnostics.CallbackCompleted(source, arrival);
            }
            assembleTask = AssembleAsync(packets.Reader, clips.Writer, workspace.DirectoryPath, transcripts, diagnostics);
            recognitionTask = RecognizeAsync(clips.Reader, recognizer, transcripts, diagnostics, cancelInference.Token);
            monitorTask = MonitorAsync(capture, workspace, cancelMonitor.Token);
            using var captureDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(config.MaxSessionMinutes));
            using var deadlineRegistration = captureDeadline.Token.Register(Stop);
            capture.Start(Receive, error => fault.TrySetResult(error));
            Update(new SessionStatus("Capturing", directory));
            await Task.WhenAny(stop.Task, fault.Task);
            Stop();
            captureSeconds = sessionClock.Elapsed.TotalSeconds;
            Update(Status with { State = "Finishing", Error = fault.Task.IsCompleted ? await fault.Task : null });
            await capture.StopAsync();
            packets.Writer.TryComplete();
            await assembleTask;
            await recognitionTask;
            cancelMonitor.Cancel();
            await monitorTask;
            var error = fault.Task.IsCompleted ? await fault.Task : null;
            if (error is not null)
            {
                foreach (var source in Enum.GetValues<AudioSource>())
                    transcripts.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), source, 0,
                        captureSeconds, [], "Session incomplete: capture stopped after a failure. Some speech may be missing."));
            }
            Update(Status with { State = error is null ? "Complete" : "Failed", Error = error });
            JsonFiles.Write(Path.Combine(directory, "session.json"), new
            { StartedUtc = startedUtc, capture.MicrophoneId, capture.PlaybackId,
                EstimatedMicrophoneTiming = localClock.UsedEstimatedTiming, EstimatedPlaybackTiming = remoteClock.UsedEstimatedTiming,
                State = status.State, EndedUtc = DateTimeOffset.UtcNow, CaptureSeconds = captureSeconds, Error = error,
                CaptureDiagnostics = diagnostics.Snapshot() });
        }
        catch (Exception exception)
        {
            if (captureSeconds == 0) captureSeconds = sessionClock.Elapsed.TotalSeconds;
            cancelInference.Cancel(); cancelMonitor.Cancel();
            if (capture is not null) await capture.StopAsync();
            packets.Writer.TryComplete(); clips.Writer.TryComplete();
            if (assembleTask is not null) { try { await assembleTask; } catch { /* failure recorded below */ } }
            if (recognitionTask is not null) { try { await recognitionTask; } catch { /* failure recorded below */ } }
            if (monitorTask is not null) { try { await monitorTask; } catch { /* failure recorded below */ } }
            var error = fault.Task.IsCompleted ? await fault.Task
                : $"Session failed ({exception.GetType().Name}). Check models, audio sources, and writable storage.";
            foreach (var source in Enum.GetValues<AudioSource>())
            {
                try { transcripts?.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), source, 0,
                    captureSeconds, [], "Session incomplete; some audio may not have been transcribed.")); }
                catch { /* A storage failure must not conceal the original failure. */ }
            }
            Update(Status with { State = "Failed", Error = error });
            if (transcripts is not null)
            {
                try { JsonFiles.Write(Path.Combine(transcripts.DirectoryPath, "session.json"), new
                { StartedUtc = startedUtc, State = "Failed", EndedUtc = DateTimeOffset.UtcNow, CaptureSeconds = captureSeconds, Error = error,
                    CaptureDiagnostics = diagnostics?.Snapshot() }); }
                catch { /* An unavailable destination is already reported in status. */ }
            }
        }
        finally
        {
            try { if (capture is not null) await capture.DisposeAsync(); }
            catch { Volatile.Write(ref status, Status with { State = "Failed", Error = "Audio device cleanup failed; committed text is preserved." }); }
            try { if (recognizer is not null) await recognizer.DisposeAsync(); }
            catch { Volatile.Write(ref status, Status with { State = "Failed", Error = "Model cleanup failed; committed text is preserved." }); }
            try { workspace?.Dispose(); budget.Clear(); }
            catch { Volatile.Write(ref status, Status with { State = "Failed", Error = "Temporary audio cleanup failed. Run cleanup after this worker exits; owned audio may remain." }); }
            Update(Status);
        }
    }

    private async Task AssembleAsync(ChannelReader<AudioPacket> packets, ChannelWriter<AudioClip> clips,
        string workspace, TranscriptStore transcripts, CaptureDiagnostics diagnostics)
    {
        void Emit(AudioClip clip)
        {
            if (!clips.TryWrite(clip)) throw new IOException("Pending clip queue filled.");
        }
        // Only this task writes capture-gap records; recognition also commits under a common lock.
        void Gap(TranscriptRecord record) => CommitTranscript(transcripts, record, diagnostics);
        using var local = new ClipAssembler(AudioSource.Local, workspace, budget, config.ClipSeconds, Emit, Gap);
        using var remote = new ClipAssembler(AudioSource.Remote, workspace, budget, config.ClipSeconds, Emit, Gap);
        try
        {
            await foreach (var packet in packets.ReadAllAsync())
            {
                var started = Stopwatch.GetTimestamp();
                (packet.Source == AudioSource.Local ? local : remote).Append(packet);
                diagnostics.AssemblyCompleted(started);
            }
            local.Finish(); remote.Finish();
        }
        catch
        {
            // Overload policy: stop capture but let already queued clips finish within their own deadlines.
            // Unfinished partial clips are deleted with the workspace; the session is marked incomplete.
            fault.TrySetResult("Audio storage or segmentation failed. Capture stopped; transcript may be incomplete.");
        }
        finally { clips.TryComplete(); }
    }

    private async Task RecognizeAsync(ChannelReader<AudioClip> clips, IClipRecognizer recognizer,
        TranscriptStore transcripts, CaptureDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        await foreach (var clip in clips.ReadAllAsync())
        {
            try
            {
                TranscriptRecord record;
                var remaining = PendingBudget.MaximumAge - PendingBudget.ExpiryMargin - Stopwatch.GetElapsedTime(clip.CreatedTimestamp);
                if (cancellationToken.IsCancellationRequested || remaining <= TimeSpan.Zero)
                    record = Missing(clip, cancellationToken.IsCancellationRequested);
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(remaining);
                    try
                    {
                        diagnostics.RecognitionStarted();
                        record = await recognizer.TranscribeAsync(clip, deadline.Token);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    { record = Missing(clip, cancellationToken.IsCancellationRequested); }
                    catch (FileNotFoundException) when (Stopwatch.GetElapsedTime(clip.CreatedTimestamp) >= PendingBudget.MaximumAge - PendingBudget.ExpiryMargin)
                    { record = Missing(clip, false); }
                    finally { diagnostics.RecognitionEnded(); }
                }
                CommitTranscript(transcripts, record, diagnostics);
            }
            catch
            {
                fault.TrySetResult("Local recognition or transcript storage failed. Capture stopped; transcript may be incomplete.");
                throw;
            }
            finally
            {
                File.Delete(clip.Path);
                budget.Remove(clip.Id);
            }
        }
    }

    private static void CommitTranscript(TranscriptStore transcripts, TranscriptRecord record, CaptureDiagnostics diagnostics)
    {
        var started = Stopwatch.GetTimestamp();
        lock (transcripts) { transcripts.Commit(record); diagnostics.CommitCompleted(started); }
    }

    private static TranscriptRecord Missing(AudioClip clip, bool sessionFailure) => new(clip.Id, clip.Source,
        clip.StartSeconds, clip.EndSeconds, [], sessionFailure ? "Processing cancelled after a session failure." : "Pending audio reached its retention limit.");

    private async Task MonitorAsync(ISessionAudioCapture capture, ClipWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // After a requested stop, device changes cannot lose speech and must not mark the session failed.
                if (!stop.Task.IsCompleted && !capture.DevicesActive)
                    fault.TrySetResult("Selected audio device is unavailable. Capture stopped without switching devices.");
                if (budget.Snapshot(Stopwatch.GetTimestamp()).AgeSeconds >= (PendingBudget.MaximumAge - PendingBudget.ExpiryMargin).TotalSeconds)
                {
                    fault.TrySetResult("Pending audio reached the one-hour limit. Capture stopped; gaps are marked in the transcripts.");
                    // Delete before the deadline rather than waiting for a native inference cancellation.
                    workspace.DeleteAudio(budget.Expire(Stopwatch.GetTimestamp()));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { fault.TrySetResult("Audio device or retention monitoring failed. Capture stopped."); }
    }

    private void Update(SessionStatus value)
    {
        Volatile.Write(ref status, value);
        JsonFiles.Write(paths.StatusFile, value);
        statusChanged?.Invoke(value);
    }
}
