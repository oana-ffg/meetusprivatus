using System.IO.Pipes;
using System.Text;
using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class ControlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{\"state\":")]
    public async Task WorkerDisconnectDuringStatusReplyReturnsUnavailable(string reply)
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
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
        Assert.Equal(Path.Combine(paths.Root, "transcripts"), saved.OutputDirectory);
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
