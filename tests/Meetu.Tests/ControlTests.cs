using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class ControlTests
{
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
