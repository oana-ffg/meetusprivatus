using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Meetu;

public sealed record SessionStatus(string State, string? SessionDirectory = null, long PendingBytes = 0,
    double OldestAudioSeconds = 0, string? Error = null);

public sealed class CaptureSession
{
    private readonly AppConfig config;
    private readonly StatePaths paths;
    private readonly Action<SessionStatus>? statusChanged;
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
    {
        this.config = config; this.paths = paths; this.statusChanged = statusChanged;
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
        Recognizer? recognizer = null;
        WasapiRecorder? local = null;
        WasapiRecorder? remote = null;
        try
        {
            if (stop.Task.IsCompleted) { Update(new SessionStatus("Complete")); return; }
            config.Validate();
            workspace = new ClipWorkspace(paths.AudioRoot, sessionId);
            recognizer = new Recognizer(config);
            if (stop.Task.IsCompleted) { Update(new SessionStatus("Complete")); return; }
            using var enumerator = new MMDeviceEnumerator();
            using var microphone = AudioDevices.OpenMicrophone(enumerator, config.MicrophoneId);
            using var playback = AudioDevices.OpenPlayback(enumerator, config.PlaybackId);
            local = AudioDevices.CreateMicrophoneRecorder(microphone);
            remote = AudioDevices.CreatePlaybackRecorder(playback);
            var directory = Path.Combine(config.OutputDirectory!, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + sessionId);
            transcripts = new TranscriptStore(directory);
            startedUtc = DateTimeOffset.UtcNow;
            JsonFiles.Write(Path.Combine(directory, "session.json"), new
            { StartedUtc = startedUtc, State = "Capturing", MicrophoneId = microphone.ID, PlaybackId = playback.ID });
            var startedTimestamp = Stopwatch.GetTimestamp();
            var localClock = new CaptureTimeline(startedTimestamp, 100);
            var remoteClock = new CaptureTimeline(startedTimestamp, 100);
            sessionClock.Restart();

            void Receive(AudioSource source, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpc)
            {
                // The library lends the native buffer only until this callback returns.
                // No disk, inference, blocking queue writes, or logging occur here.
                if (buffer.Length > 32000 || buffer.Length % 2 != 0)
                { fault.TrySetResult("Capture returned an invalid or oversized audio packet."); return; }
                var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                var clock = source == AudioSource.Local ? localClock : remoteClock;
                var timing = clock.Stamp(buffer.Length / 2, qpc,
                    (flags & AudioClientBufferFlags.TimestampError) == 0, discontinuity, Stopwatch.GetTimestamp());
                if (!packets.Writer.TryWrite(new AudioPacket(source, buffer.ToArray(), timing.StartSeconds,
                    discontinuity, timing.CapturedTimestamp)))
                    fault.TrySetResult("Audio capture queue filled. Capture stopped to protect meeting responsiveness.");
            }
            local.DataAvailable += (buffer, flags, _, qpc) => Receive(AudioSource.Local, buffer, flags, qpc);
            remote.DataAvailable += (buffer, flags, _, qpc) => Receive(AudioSource.Remote, buffer, flags, qpc);
            local.RecordingStopped += (_, args) => { if (args.Exception is not null) fault.TrySetResult("Microphone capture failed. Check the selected device."); };
            remote.RecordingStopped += (_, args) => { if (args.Exception is not null) fault.TrySetResult("Output capture failed. Check the selected playback device."); };

            assembleTask = AssembleAsync(packets.Reader, clips.Writer, workspace.DirectoryPath, transcripts);
            recognitionTask = RecognizeAsync(clips.Reader, recognizer, transcripts, cancelInference.Token);
            monitorTask = MonitorAsync(microphone, playback, workspace, cancelMonitor.Token);
            using var captureDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(config.MaxSessionMinutes));
            using var deadlineRegistration = captureDeadline.Token.Register(Stop);
            local.StartRecording(); remote.StartRecording();
            Update(new SessionStatus("Capturing", directory));
            await Task.WhenAny(stop.Task, fault.Task);
            captureSeconds = sessionClock.Elapsed.TotalSeconds;
            Update(Status with { State = "Finishing", Error = fault.Task.IsCompleted ? await fault.Task : null });
            local.StopRecording(); remote.StopRecording();
            // DisposeAsync joins the capture threads before completing the channel.
            await local.DisposeAsync(); local = null;
            await remote.DisposeAsync(); remote = null;
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
            { StartedUtc = startedUtc, MicrophoneId = microphone.ID, PlaybackId = playback.ID,
                EstimatedMicrophoneTiming = localClock.UsedEstimatedTiming, EstimatedPlaybackTiming = remoteClock.UsedEstimatedTiming,
                State = status.State, EndedUtc = DateTimeOffset.UtcNow, CaptureSeconds = captureSeconds, Error = error });
        }
        catch (Exception exception)
        {
            if (captureSeconds == 0) captureSeconds = sessionClock.Elapsed.TotalSeconds;
            cancelInference.Cancel(); cancelMonitor.Cancel();
            if (local is not null) { await local.DisposeAsync(); local = null; }
            if (remote is not null) { await remote.DisposeAsync(); remote = null; }
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
                { StartedUtc = startedUtc, State = "Failed", EndedUtc = DateTimeOffset.UtcNow, CaptureSeconds = captureSeconds, Error = error }); }
                catch { /* An unavailable destination is already reported in status. */ }
            }
        }
        finally
        {
            try { if (recognizer is not null) await recognizer.DisposeAsync(); }
            catch { Volatile.Write(ref status, Status with { State = "Failed", Error = "Model cleanup failed; committed text is preserved." }); }
            try { workspace?.Dispose(); budget.Clear(); }
            catch { Volatile.Write(ref status, Status with { State = "Failed", Error = "Temporary audio cleanup failed. Run cleanup after this worker exits; owned audio may remain." }); }
            Update(Status);
        }
    }

    private async Task AssembleAsync(ChannelReader<AudioPacket> packets, ChannelWriter<AudioClip> clips,
        string workspace, TranscriptStore transcripts)
    {
        void Emit(AudioClip clip)
        {
            if (!clips.TryWrite(clip)) throw new IOException("Pending clip queue filled.");
        }
        // Only this task writes capture-gap records; recognition also commits under a common lock.
        void Gap(TranscriptRecord record) { lock (transcripts) transcripts.Commit(record); }
        using var local = new ClipAssembler(AudioSource.Local, workspace, budget, config.ClipSeconds, Emit, Gap);
        using var remote = new ClipAssembler(AudioSource.Remote, workspace, budget, config.ClipSeconds, Emit, Gap);
        try
        {
            await foreach (var packet in packets.ReadAllAsync())
                (packet.Source == AudioSource.Local ? local : remote).Append(packet);
            local.Finish(); remote.Finish();
        }
        catch
        {
            fault.TrySetResult("Audio storage or segmentation failed. Capture stopped; transcript may be incomplete.");
            throw;
        }
        finally { clips.TryComplete(); }
    }

    private async Task RecognizeAsync(ChannelReader<AudioClip> clips, Recognizer recognizer,
        TranscriptStore transcripts, CancellationToken cancellationToken)
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
                    try { record = await recognizer.TranscribeAsync(clip, deadline.Token); }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    { record = Missing(clip, cancellationToken.IsCancellationRequested); }
                    catch (FileNotFoundException) when (Stopwatch.GetElapsedTime(clip.CreatedTimestamp) >= PendingBudget.MaximumAge - PendingBudget.ExpiryMargin)
                    { record = Missing(clip, false); }
                }
                lock (transcripts) transcripts.Commit(record);
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

    private static TranscriptRecord Missing(AudioClip clip, bool sessionFailure) => new(clip.Id, clip.Source,
        clip.StartSeconds, clip.EndSeconds, [], sessionFailure ? "Processing cancelled after a session failure." : "Pending audio reached its retention limit.");

    private async Task MonitorAsync(MMDevice microphone, MMDevice playback, ClipWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (microphone.State != DeviceState.Active || playback.State != DeviceState.Active)
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
