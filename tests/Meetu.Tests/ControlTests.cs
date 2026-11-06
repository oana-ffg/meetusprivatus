using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Meetu;
using Xunit;
using Xunit.Abstractions;

namespace Meetu.Tests;

public sealed class ControlTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoWorkerRequestReturnsPromptly(bool stop)
    {
        using var directory = new TestDirectory();
        // Each test directory has a GUID, so its derived pipe name has no possible worker.
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var elapsed = Stopwatch.StartNew();
        var response = await ControlServer.RequestAsync(paths, stop).WaitAsync(TimeSpan.FromSeconds(5));
        elapsed.Stop();
        output.WriteLine($"No worker, stop={stop}: {elapsed.Elapsed.TotalMilliseconds:F3} ms.");
        Assert.Null(response);
        Assert.False(File.Exists(Path.Combine(paths.Root, "worker.lock")));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"No-server request took {elapsed.Elapsed.TotalMilliseconds:F3} ms; expected less than 1000 ms.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingWorkerCanOpenItsPipeAfterTheRequestBegins(bool stop)
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using var workerLock = WorkerLock.Acquire(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = ControlServer.RequestAsync(paths, stop);
        // A worker owns the lock before its pipe exists. A short pipe timeout or
        // a pipe-presence-only check must not declare that worker absent.
        await Task.Delay(300, timeout.Token);
        Assert.False(request.IsCompleted);
        await using (var pipe = new NamedPipeServerStream(paths.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var command = new byte[1];
            Assert.Equal(1, await pipe.ReadAsync(command, timeout.Token));
            Assert.Equal(stop ? (byte)1 : (byte)0, command[0]);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"state\":\"Starting\"}"), timeout.Token);
        }
        Assert.Equal("Starting", (await request.WaitAsync(timeout.Token))?.State);
        Assert.True(WorkerLock.IsHeld(paths));
    }

    [Fact]
    public async Task CrashedWorkerReportsInterruptedWithoutWaiting()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using (WorkerLock.Acquire(paths)) { } // A crash leaves the file, not the OS-held lock.
        JsonFiles.Write(paths.StatusFile, new SessionStatus("Capturing", directory.Path, 32000, 1));
        var elapsed = Stopwatch.StartNew();
        var status = RecordingControl.ReadStatus(await ControlServer.RequestAsync(paths), paths);
        elapsed.Stop();
        Assert.Equal("Interrupted", status.State);
        Assert.Equal(directory.Path, status.SessionDirectory);
        Assert.Equal(32000, status.PendingBytes);
        Assert.Contains("Worker unavailable", status.Error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Crashed-worker status took {elapsed.Elapsed.TotalMilliseconds:F3} ms.");
    }

    [Fact]
    public async Task StartingWorkerWithoutAPipeStillExcludesASecondRecording()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using (WorkerLock.Acquire(paths))
        {
            Assert.True(WorkerLock.IsHeld(paths));
            Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(paths));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RecordingControl.PrepareStartAsync(paths));
            Assert.False(File.Exists(paths.ConfigFile));
            Assert.False(Directory.Exists(paths.AudioRoot));
            Assert.True(WorkerLock.IsHeld(paths));
            Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(paths));
        }
        Assert.False(WorkerLock.IsHeld(paths));
        using var nextWorker = WorkerLock.Acquire(paths);
        Assert.True(WorkerLock.IsHeld(paths));
    }

    [Fact]
    public async Task NoWorkerStartReachesConfigurationValidationWithoutPipeDelay()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        // Force validation to fail before any models, capture or worker process can open.
        var missingModel = Path.Combine(directory.Path, "missing-synthetic-model.bin");
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, ModelPath = missingModel, VadPath = missingModel });
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ArgumentException>(() => RecordingControl.PrepareStartAsync(paths));
        elapsed.Stop();
        Assert.False(File.Exists(paths.StatusFile));
        Assert.Empty(Directory.GetDirectories(paths.AudioRoot));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"No-worker startup validation took {elapsed.Elapsed.TotalMilliseconds:F3} ms.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"state\":")]
    public async Task WorkerDisconnectDuringStatusReplyReturnsUnavailable(string reply)
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using var workerLock = WorkerLock.Acquire(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task ReplyAndDisconnectAsync()
        {
            await using var pipe = new NamedPipeServerStream(paths.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(timeout.Token);
            var command = new byte[1];
            Assert.Equal(1, await pipe.ReadAsync(command, timeout.Token));
            Assert.Equal(0, command[0]);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        var server = ReplyAndDisconnectAsync();
        try { Assert.Null(await ControlServer.RequestAsync(paths)); }
        finally { await server; }
        Assert.False(Directory.Exists(paths.AudioRoot));
    }

    [Fact]
    public async Task CurrentUserPipeReturnsStatusAndAcknowledgesStopWithoutAudioCapture()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using var workerLock = WorkerLock.Acquire(paths);
        var session = new CaptureSession(new AppConfig(), paths);
        using var cancellation = new CancellationTokenSource();
        var server = ControlServer.RunAsync(paths, session, cancellation.Token);
        try
        {
            Assert.Equal("Starting", (await ControlServer.RequestAsync(paths))?.State);
            Assert.Equal("Starting", (await ControlServer.RequestAsync(paths, stop: true))?.State);
            await session.RunAsync();
            Assert.Equal("Complete", session.Status.State);
            Assert.False(Directory.Exists(paths.AudioRoot));
        }
        finally { cancellation.Cancel(); await server; }
    }

    [Fact]
    public async Task StartingSavesDefaultsWithoutPinningTheInstallLocation()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "invalid-model-fixture.bin"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, ModelPath = model, VadPath = model, MicrophoneId = "configured-input" });
        await RecordingControl.PrepareStartAsync(paths);
        var saved = paths.LoadConfig();
        Assert.Null(saved.OutputDirectory);
        Assert.Equal(Path.Combine(AppPaths.DurableRoot, "transcripts"), saved.ResolvedOutputDirectory);
        Assert.Equal("configured-input", saved.MicrophoneId);
        // Unset model paths stay unset in saved settings, so a moved or upgraded app finds its own models.
        var defaults = new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId };
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "ggml-base.en-q5_1.bin"), defaults.ResolvedModelPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "ggml-silero-v5.1.2.bin"), defaults.ResolvedVadPath);
        var json = JsonSerializer.Serialize(defaults, JsonFiles.Options);
        Assert.DoesNotContain("ggml", json);
        Assert.Null(JsonSerializer.Deserialize<AppConfig>(json, JsonFiles.Options)!.ModelPath);
    }

    [Fact]
    public void LegacyDefaultOutputMigratesButCustomDestinationSurvives()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var legacy = Path.Combine(paths.Root, "transcripts");
        Assert.Null(RecordingControl.MigrateDefaultOutput(paths, new AppConfig { OutputDirectory = legacy }).OutputDirectory);
        Assert.Equal(directory.Path, RecordingControl.MigrateDefaultOutput(paths, new AppConfig { OutputDirectory = directory.Path }).OutputDirectory);
    }

    [Theory]
    [InlineData(@"C:\apps\Meetus\current", @"C:\apps\Meetus", true)]
    [InlineData(@"C:\apps\Meetus\current\", @"C:\apps\Meetus", true)]
    [InlineData(@"C:\apps\Meetus\current", @"C:\apps\Meetus\current", false)]
    [InlineData(@"C:\dev\publish", @"C:\dev\publish", false)]
    public void TextAndLargeModelsLiveOutsideTheFolderUpdatesReplace(string program, string expected, bool hasUpdater)
    {
        // Run against a real temporary tree so the check for the updater is the real one.
        using var directory = new TestDirectory();
        var relative = program[3..];
        var programDirectory = Path.Combine(directory.Path, relative);
        Directory.CreateDirectory(programDirectory);
        var parent = Path.GetDirectoryName(programDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        if (hasUpdater) File.WriteAllBytes(Path.Combine(parent, "Update.exe"), [0]);
        Assert.Equal(Path.Combine(directory.Path, expected[3..]), AppPaths.ResolveDurableRoot(programDirectory));
    }

    [Theory]
    [InlineData(@"C:\app\current\saved", true)]
    [InlineData(@"C:\app\current", true)]
    [InlineData(@"C:\APP\CURRENT\saved", true)]
    [InlineData(@"C:\app\transcripts", false)]
    [InlineData(@"C:\app\current-old\saved", false)]
    [InlineData(@"D:\meetings", false)]
    public void StorageInsideTheFolderUpdatesReplaceIsRecognized(string path, bool expected)
    {
        Assert.Equal(expected, AppPaths.IsInside(path, @"C:\app\current", @"C:\app"));
        // Outside an installed layout, nothing gets replaced.
        Assert.False(AppPaths.IsInside(path, @"C:\app\current", @"C:\app\current"));
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("config")]
    public async Task TheFormerCommandLineIsGone(params string[] arguments) =>
        Assert.Equal(Program.UnknownCommandExitCode, await Program.Main(arguments));

    [Fact]
    public void MissingPinnedDefaultModelsFollowTheAppButOtherChoicesAreKept()
    {
        using var directory = new TestDirectory();
        var present = Path.Combine(directory.Path, "kept", "ggml-base.en-q5_1.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(present)!); File.WriteAllBytes(present, [0]);
        var movedInstall = Path.Combine(directory.Path, "old-install", "models");
        var custom = Path.Combine(directory.Path, "missing", "ggml-base.en.bin");
        var unpinned = new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, ModelPath = Path.Combine(movedInstall, "ggml-base.en-q5_1.bin"),
            VadPath = Path.Combine(movedInstall, "ggml-silero-v5.1.2.bin") }.WithMissingDefaultModelsUnpinned();
        Assert.Null(unpinned.ModelPath); Assert.Null(unpinned.VadPath);
        Assert.Equal(present, new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, ModelPath = present }.WithMissingDefaultModelsUnpinned().ModelPath);
        // A different model the user chose is never silently replaced; validation reports it instead.
        Assert.Equal(custom, new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, ModelPath = custom }.WithMissingDefaultModelsUnpinned().ModelPath);
    }

    [Fact]
    public void UnavailableLegacyDriveIsResetWithoutAccessingItsFiles()
    {
        using var directory = new TestDirectory();
        var root = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\")
            .First(path => new DriveInfo(path).DriveType is DriveType.Unknown or DriveType.NoRootDirectory);
        var saved = new AppConfig
        {
            ModelPath = Path.Combine(root, "old-install", "models", "ggml-base.en-q5_1.bin"),
            VadPath = Path.Combine(root, "old-install", "models", "ggml-silero-v5.1.2.bin")
        };
        var migrated = saved.WithMissingDefaultModelsUnpinned();
        Assert.Null(migrated.ModelPath); Assert.Null(migrated.VadPath);
        var custom = Path.Combine(root, "chosen", "ggml-base.en.bin");
        Assert.Equal(custom, (saved with { ModelPath = custom }).WithMissingDefaultModelsUnpinned().ModelPath);
        Assert.Throws<ArgumentException>(() => (saved with
        { ModelPath = @"\\network.invalid\models\ggml-base.en-q5_1.bin" }).WithMissingDefaultModelsUnpinned());
    }
}
