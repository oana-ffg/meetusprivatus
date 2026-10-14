using NAudio.CoreAudioApi;
using System.Diagnostics;
using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task EmptyCallbacksCannotCreateGapsOrReplaceNativeAudioTiming()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic-model.bin"); File.WriteAllBytes(model, [0]);
        var config = new AppConfig { OutputDirectory = Path.Combine(directory.Path, "transcripts"),
            ModelPath = model, VadPath = model, ClipSeconds = 5 };
        var capture = new SyntheticCapture();
        var recognizer = new PausedRecognizer(); recognizer.Resume.TrySetResult();
        var session = new CaptureSession(config, paths, _ => capture, _ => recognizer);
        using var workerLock = new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = session.RunAsync();
        try
        {
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var source in Enum.GetValues<AudioSource>())
                for (var packet = 0; packet < 5; packet++)
                {
                    capture.Emit(source);
                    // GetBuffer can return no frames with unset metadata. Even flags on that
                    // callback must not split audio, invent a gap, or change its timing source.
                    capture.EmitEmpty(source);
                }
        }
        finally { session.Stop(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal("Complete", session.Status.State);
        Assert.Equal(2, recognizer.Completed.Count);
        Assert.All(recognizer.Completed, clip => Assert.Equal(80000, clip.SampleCount));
        using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Status.SessionDirectory!, "session.json")));
        Assert.False(metadata.RootElement.GetProperty("EstimatedMicrophoneTiming").GetBoolean());
        Assert.False(metadata.RootElement.GetProperty("EstimatedPlaybackTiming").GetBoolean());
        foreach (var source in new[] { "local.txt", "remote.txt" })
            Assert.DoesNotContain("[GAP:", File.ReadAllText(Path.Combine(session.Status.SessionDirectory!, source)));
        Assert.Equal(0, session.Status.PendingBytes);
        Assert.Empty(Directory.GetDirectories(paths.AudioRoot));
    }

    [Fact]
    public async Task CaptureGapsWaitForRecognitionInsteadOfBlockingAssembly()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic-model.bin"); File.WriteAllBytes(model, [0]);
        var config = new AppConfig { OutputDirectory = Path.Combine(directory.Path, "transcripts"),
            ModelPath = model, VadPath = model, ClipSeconds = 5 };
        var capture = new SyntheticCapture();
        var recognizer = new PausedRecognizer();
        var session = new CaptureSession(config, paths, _ => capture, _ => recognizer);
        using var workerLock = new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = session.RunAsync();
        try
        {
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var packet = 0; packet < 5; packet++) capture.Emit(AudioSource.Local);
            await recognizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Recognition is now busy. A discontinuity must queue its gap record behind it
            // while assembly keeps draining capture into the next clip.
            capture.Emit(AudioSource.Local, AudioClientBufferFlags.DataDiscontinuity);
            for (var packet = 0; packet < 4; packet++) capture.Emit(AudioSource.Local);
            var workspace = Assert.Single(Directory.GetDirectories(paths.AudioRoot));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ClosedClips(workspace, 2)) await Task.Delay(10, timeout.Token);
            Assert.Equal("Capturing", session.Status.State);
            var sessionDirectory = session.Status.SessionDirectory!;
            Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory, "segments"), "*.json"));
            Assert.DoesNotContain("[GAP:", File.ReadAllText(Path.Combine(sessionDirectory, "local.txt")));
        }
        finally
        {
            session.Stop();
            await capture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            recognizer.Resume.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal("Complete", session.Status.State);
        var text = File.ReadAllText(Path.Combine(session.Status.SessionDirectory!, "local.txt"));
        Assert.Equal(1, text.Split("[GAP: Audio device reported a capture discontinuity.]").Length - 1);
        Assert.Equal(2, text.Split("Synthetic queued speech.").Length - 1);
        Assert.Empty(Directory.GetDirectories(paths.AudioRoot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppedSessionDrainsQueuedClipsAndDeletesAudio(bool storageFailure)
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic-model.bin"); File.WriteAllBytes(model, [0]);
        var config = new AppConfig { OutputDirectory = Path.Combine(directory.Path, "transcripts"),
            ModelPath = model, VadPath = model, ClipSeconds = 5 };
        var capture = new SyntheticCapture();
        var recognizer = new PausedRecognizer();
        var session = new CaptureSession(config, paths, _ => capture, _ => recognizer);
        // Match the real worker's ownership lock throughout processing and cleanup.
        using var workerLock = new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = session.RunAsync();
        string? movedWorkspace = null;
        string? workspace = null;
        try
        {
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var clip = 0; clip < 4; clip++)
                for (var packet = 0; packet < 5; packet++)
                    capture.Emit(clip % 2 == 0 ? AudioSource.Local : AudioSource.Remote);
            await recognizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workspace = Assert.Single(Directory.GetDirectories(paths.AudioRoot));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ClosedClips(workspace, 4)) await Task.Delay(10, timeout.Token);
            if (storageFailure)
            {
                // Make the owned audio destination unavailable after four clips are queued.
                // Restore it only after the session has stopped capture, before releasing inference.
                movedWorkspace = workspace + ".unavailable";
                Directory.Move(workspace, movedWorkspace);
                capture.Emit(AudioSource.Local);
            }
            else session.Stop();
            await capture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (movedWorkspace is not null) { Directory.Move(movedWorkspace, workspace); movedWorkspace = null; }
            Assert.Equal("Finishing", session.Status.State);
            // The real monitor must ignore inactive devices while queued recognition is draining.
            await Task.Delay(1100);
            recognizer.Resume.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(storageFailure ? "Failed" : "Complete", session.Status.State);
            Assert.Equal(4, recognizer.Completed.Count);
            Assert.All(recognizer.Completed, clip => Assert.False(File.Exists(clip.Path)));
            Assert.Equal(0, session.Status.PendingBytes);
            Assert.Empty(Directory.GetDirectories(paths.AudioRoot));
            var sessionDirectory = session.Status.SessionDirectory!;
            Assert.Equal(storageFailure ? 6 : 4, Directory.GetFiles(Path.Combine(sessionDirectory, "segments"), "*.json").Length);
            foreach (var source in new[] { "local.txt", "remote.txt" })
            {
                var text = File.ReadAllText(Path.Combine(sessionDirectory, source));
                Assert.Equal(2, text.Split("Synthetic queued speech.").Length - 1);
                Assert.DoesNotContain("Processing cancelled", text);
                if (storageFailure) Assert.Contains("Session incomplete", text);
                else Assert.DoesNotContain("[GAP:", text);
            }
            Assert.True(capture.Disposed); Assert.True(recognizer.Disposed);
        }
        finally
        {
            if (movedWorkspace is not null) Directory.Move(movedWorkspace, workspace!);
            session.Stop(); recognizer.Resume.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static bool ClosedClips(string workspace, int count)
    {
        var files = Directory.GetFiles(workspace, "*.pcm");
        if (files.Length != count) return false;
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                if (stream.Length != 160000) return false;
            }
            catch (IOException) { return false; }
        }
        return true;
    }

    private sealed class SyntheticCapture : ISessionAudioCapture
    {
        private CaptureBuffer? receive;
        private long startedTimestamp;
        private readonly int[] packets = new int[2];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string MicrophoneId => "synthetic-input";
        public string PlaybackId => "synthetic-output";
        public bool DevicesActive => !Stopped.Task.IsCompleted;
        public bool Disposed { get; private set; }
        public void Start(CaptureBuffer callback, Action<string> failed)
        { receive = callback; startedTimestamp = Stopwatch.GetTimestamp(); Started.TrySetResult(); }
        public void Emit(AudioSource source, AudioClientBufferFlags flags = 0)
        {
            var timestamp = startedTimestamp + packets[(int)source]++ * Stopwatch.Frequency;
            var qpc = (long)(timestamp * (10_000_000.0 / Stopwatch.Frequency));
            receive!(source, new byte[32000], flags, packets[(int)source] * 16000L, qpc);
        }
        public void EmitEmpty(AudioSource source) => receive!(source, [],
            AudioClientBufferFlags.DataDiscontinuity | AudioClientBufferFlags.TimestampError, 0, 0);
        public ValueTask StopAsync() { Stopped.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return StopAsync(); }
    }

    private sealed class PausedRecognizer : IClipRecognizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AudioClip> Completed { get; } = [];
        public bool Disposed { get; private set; }
        public async Task<TranscriptRecord> TranscribeAsync(AudioClip clip, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(clip.SampleCount * 2, File.ReadAllBytes(clip.Path).Length);
            Completed.Add(clip);
            return new TranscriptRecord(clip.Id, clip.Source, clip.StartSeconds, clip.EndSeconds,
                [new(clip.StartSeconds, clip.EndSeconds, "Synthetic queued speech.")]);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
