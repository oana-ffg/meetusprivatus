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

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public async Task CrashedWorkerStillReportsInterruptedWithoutWaiting(string command)
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using (WorkerLock.Acquire(paths)) { } // A crash leaves the file, not the OS-held lock.
        JsonFiles.Write(paths.StatusFile, new SessionStatus("Capturing", directory.Path, 32000, 1));
        using var reply = new StringWriter();
        var previousOutput = Console.Out;
        var elapsed = Stopwatch.StartNew();
        try
        {
            Console.SetOut(reply);
            Assert.Equal(1, await Program.Main([command, "--state-dir", paths.Root]));
        }
        finally { elapsed.Stop(); Console.SetOut(previousOutput); }
        var status = JsonSerializer.Deserialize<SessionStatus>(reply.ToString(), JsonFiles.Options)!;
        Assert.Equal("Interrupted", status.State);
        Assert.Equal(directory.Path, status.SessionDirectory);
        Assert.Equal(32000, status.PendingBytes);
        Assert.Contains("Worker unavailable", status.Error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"Crashed-worker {command} took {elapsed.Elapsed.TotalMilliseconds:F3} ms.");
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
            Assert.Equal(1, await Program.Main(["start", "--state-dir", paths.Root]));
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
        JsonFiles.Write(paths.ConfigFile, new AppConfig { ModelPath = missingModel, VadPath = missingModel });
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(1, await Program.Main(["start", "--state-dir", paths.Root]));
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
    public async Task SavedConfigurationSurvivesCliRestartAndRepairRejectsAnUnrelatedDirectory()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "invalid-model-fixture.bin"); File.WriteAllBytes(model, [0]);
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--output-dir", directory.Path, "--model", model, "--vad", model, "--playback", "configured-output", "--max-minutes", "45"]));
        Assert.Equal(directory.Path, new StatePaths(paths.Root).LoadConfig().OutputDirectory);
        Assert.Equal("configured-output", new StatePaths(paths.Root).LoadConfig().PlaybackId);
        Assert.Equal(45, paths.LoadConfig().MaxSessionMinutes);
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--playback", "default"]));
        Assert.Null(paths.LoadConfig().PlaybackId);
        var unrelated = Path.Combine(directory.Path, "unrelated"); Directory.CreateDirectory(unrelated);
        var text = Path.Combine(unrelated, "local.txt"); File.WriteAllText(text, "preserve existing user file");
        Assert.Equal(1, await Program.Main(["repair", "--state-dir", paths.Root, "--session-dir", unrelated]));
        Assert.Equal("preserve existing user file", File.ReadAllText(text));
    }

    [Fact]
    public async Task FirstConfigurationSavesDefaultOutputWithoutPinningTheInstallLocation()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "invalid-model-fixture.bin"); File.WriteAllBytes(model, [0]);
        // Device setup can precede the first recording; it must not require choosing an output directory.
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--model", model, "--vad", model, "--mic", "configured-input"]));
        var saved = paths.LoadConfig();
        Assert.Null(saved.OutputDirectory);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "transcripts"), saved.ResolvedOutputDirectory);
        Assert.Equal("configured-input", saved.MicrophoneId);
        // Unset model paths stay unset in saved settings, so a moved or upgraded app finds its own models.
        var defaults = new AppConfig { OutputDirectory = saved.OutputDirectory };
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "ggml-base.en-q5_1.bin"), defaults.ResolvedModelPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "ggml-silero-v5.1.2.bin"), defaults.ResolvedVadPath);
        var json = System.Text.Json.JsonSerializer.Serialize(defaults, JsonFiles.Options);
        Assert.DoesNotContain("ggml", json);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, JsonFiles.Options)!.ModelPath);
    }

    [Fact]
    public async Task LegacyDefaultOutputMigratesButCustomDestinationAndOldTextSurvive()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var legacy = Path.Combine(paths.Root, "transcripts");
        Directory.CreateDirectory(legacy);
        var text = Path.Combine(legacy, "existing.txt"); File.WriteAllText(text, "keep this text");
        var model = Path.Combine(directory.Path, "synthetic-model.bin"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { OutputDirectory = legacy, ModelPath = model, VadPath = model });
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--max-minutes", "45"]));
        Assert.Null(paths.LoadConfig().OutputDirectory);
        Assert.Equal("keep this text", File.ReadAllText(text));
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--output-dir", directory.Path]));
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--max-minutes", "60"]));
        Assert.Equal(directory.Path, paths.LoadConfig().OutputDirectory);
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--output-dir", "default"]));
        Assert.Null(paths.LoadConfig().OutputDirectory);
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--output-dir", legacy]));
        Assert.Equal(legacy, paths.LoadConfig().OutputDirectory);
    }

    [Fact]
    public async Task RecordingRefusesAnInstallationUnderUpdateBeforeOpeningAudio()
    {
        using var updateLock = new FileStream(Path.Combine(AppContext.BaseDirectory, ".update.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var directory = new TestDirectory();
        Assert.Equal(1, await Program.Main(["start", "--state-dir", directory.Path]));
        Assert.False(File.Exists(Path.Combine(directory.Path, "config.json")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "audio")));
    }

    [Fact]
    public async Task RepairRefusesASessionWhileAWorkerHoldsTheLock()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var store = new TranscriptStore(Path.Combine(directory.Path, "session"));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "session.json"), "{}");
        var local = Path.Combine(store.DirectoryPath, "local.txt");
        File.WriteAllText(local, "text a live worker is still writing");
        using (new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(1, await Program.Main(["repair", "--state-dir", paths.Root, "--session-dir", store.DirectoryPath]));
        Assert.Equal("text a live worker is still writing", File.ReadAllText(local));
        Assert.Equal(0, await Program.Main(["repair", "--state-dir", paths.Root, "--session-dir", store.DirectoryPath]));
        Assert.Contains("Microphone speech", File.ReadAllText(local));
    }

    [Fact]
    public void MissingPinnedDefaultModelsFollowTheAppButOtherChoicesAreKept()
    {
        using var directory = new TestDirectory();
        var present = Path.Combine(directory.Path, "kept", "ggml-base.en-q5_1.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(present)!); File.WriteAllBytes(present, [0]);
        var movedInstall = Path.Combine(directory.Path, "old-install", "models");
        var custom = Path.Combine(directory.Path, "missing", "ggml-base.en.bin");
        var unpinned = new AppConfig { ModelPath = Path.Combine(movedInstall, "ggml-base.en-q5_1.bin"),
            VadPath = Path.Combine(movedInstall, "ggml-silero-v5.1.2.bin") }.WithMissingDefaultModelsUnpinned();
        Assert.Null(unpinned.ModelPath); Assert.Null(unpinned.VadPath);
        Assert.Equal(present, new AppConfig { ModelPath = present }.WithMissingDefaultModelsUnpinned().ModelPath);
        // A different model the user chose is never silently replaced; validation reports it instead.
        Assert.Equal(custom, new AppConfig { ModelPath = custom }.WithMissingDefaultModelsUnpinned().ModelPath);
    }

    [Fact]
    public async Task UnavailableLegacyDriveIsResetWithoutAccessingItsFiles()
    {
        using var directory = new TestDirectory();
        var root = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\")
            .First(path => new DriveInfo(path).DriveType is DriveType.Unknown or DriveType.NoRootDirectory);
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var saved = new AppConfig
        {
            ModelPath = Path.Combine(root, "old-install", "models", "ggml-base.en-q5_1.bin"),
            VadPath = Path.Combine(root, "old-install", "models", "ggml-silero-v5.1.2.bin")
        };
        var migrated = saved.WithMissingDefaultModelsUnpinned();
        Assert.Null(migrated.ModelPath); Assert.Null(migrated.VadPath);
        JsonFiles.Write(paths.ConfigFile, saved);
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root]));
        var custom = Path.Combine(root, "chosen", "ggml-base.en.bin");
        Assert.Equal(custom, (saved with { ModelPath = custom }).WithMissingDefaultModelsUnpinned().ModelPath);
        Assert.Throws<ArgumentException>(() => (saved with
        { ModelPath = @"\\network.invalid\models\ggml-base.en-q5_1.bin" }).WithMissingDefaultModelsUnpinned());
    }

    [Fact]
    public async Task ExplicitModelOverridesAreAppliedBeforeLegacyPathValidation()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic-model.bin"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig
        {
            ModelPath = @"\\network.invalid\models\ggml-base.en-q5_1.bin",
            VadPath = @"\\network.invalid\models\ggml-silero-v5.1.2.bin"
        });
        Assert.Equal(0, await Program.Main(["config", "--state-dir", paths.Root, "--model", model, "--vad", model]));
        Assert.Equal(model, paths.LoadConfig().ModelPath); Assert.Equal(model, paths.LoadConfig().VadPath);
        // A default override must reach bundled-model validation, even with an invalid saved path.
        var config = paths.LoadConfig() with { ModelPath = @"\\network.invalid\models\ggml-base.en-q5_1.bin" };
        JsonFiles.Write(paths.ConfigFile, config);
        using var errors = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(errors);
            var result = await Program.Main(["config", "--state-dir", paths.Root, "--model", "default"]);
            Assert.DoesNotContain("network", errors.ToString(), StringComparison.OrdinalIgnoreCase);
            if (File.Exists(new AppConfig().ResolvedModelPath)) Assert.Equal(0, result);
            else Assert.Contains("Whisper model missing", errors.ToString());
        }
        finally { Console.SetError(previous); }
    }

    [Fact]
    public async Task RemovedProcessOptionsFailBeforeStartingCapture()
    {
        using var directory = new TestDirectory();
        foreach (var arguments in new[]
        {
            new[] { "start", "--app", "teams" }, new[] { "start", "--pid", "1" },
            new[] { "worker", "--pid", "1" }, new[] { "processes" }
        })
            Assert.Equal(1, await Program.Main([.. arguments, "--state-dir", directory.Path]));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "audio")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "status.json")));
    }
}
