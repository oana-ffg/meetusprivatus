using System.Text;
using System.Text.Json;
using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class SpeechModelTests
{
    private static SpeechModel Model(string id) => SpeechModels.Find(id)!;
    private static SpeechModel Parakeet => Model("parakeet-tdt-0.6b-v3");
    private static SpeechModel SmallWhisper => Model("whisper-small.en");

    // Real names and sizes are hundreds of megabytes, so a sparse file stands in for each download.
    private static void Install(SpeechModel model, string models)
    {
        var directory = SpeechModelFiles.DownloadDirectory(model, models);
        Directory.CreateDirectory(directory);
        foreach (var asset in model.Downloads)
        {
            using var file = File.Create(Path.Combine(directory, asset.FileName));
            file.SetLength(asset.Size);
        }
    }

    [Fact]
    public void EveryDownloadIsPinnedAndVerifiable()
    {
        Assert.Equal(SpeechModels.All.Count, SpeechModels.All.Select(model => model.Id).Distinct().Count());
        var downloads = SpeechModels.All.SelectMany(model => model.Downloads).ToList();
        Assert.NotEmpty(downloads);
        Assert.All(downloads, asset =>
        {
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
            Assert.Matches("^https://huggingface.co/.+/resolve/[0-9a-f]{40}/", asset.Url.AbsoluteUri);
            Assert.EndsWith(asset.FileName, asset.Url.AbsolutePath);
            Assert.True(asset.Size > 0);
        });
    }

    [Fact]
    public void AnIncludedModelIsAvailableWithoutAnAutomaticSelection()
    {
        var included = Assert.Single(SpeechModels.All, model => model.IsIncluded);
        Assert.Equal(SpeechModels.IncludedWhisperId, included.Id);
        Assert.Null(new AppConfig().SpeechModelId);
        Assert.True(new AppConfig().NeedsSpeechModelSelection);
        Assert.Throws<ArgumentException>(() => new AppConfig().ResolvedModelPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "ggml-base.en-q5_1.bin"),
            new AppConfig { SpeechModelId = included.Id }.ResolvedModelPath);
    }

    [Theory]
    [InlineData("{\"Threads\":2}")]
    [InlineData("{\"ModelPath\":\"C:\\\\old\\\\ggml-base.en-q5_1.bin\"}")]
    [InlineData("{\"SpeechModelId\":\"unknown\"}")]
    public void EarlierSettingsRequireAnExplicitChoice(string json) =>
        Assert.True(JsonSerializer.Deserialize<AppConfig>(json, JsonFiles.Options)!.NeedsSpeechModelSelection);

    [Fact]
    public void AnUnconfirmedLegacyFileCannotBypassTheModelChoice()
    {
        using var workspace = new TestDirectory();
        var file = Path.Combine(workspace.Path, "legacy.bin");
        File.WriteAllBytes(file, [0]);
        var config = new AppConfig { ModelPath = file, VadPath = file };
        Assert.Contains("Choose a transcription model", Assert.Throws<ArgumentException>(config.Validate).Message);
    }

    [Theory]
    [InlineData("whisper-base.en")]
    [InlineData("parakeet-tdt-0.6b-v3")]
    public void ASavedChoiceSurvivesSubsequentLaunches(string id)
    {
        var saved = JsonSerializer.Serialize(new AppConfig { SpeechModelId = id }, JsonFiles.Options);
        var config = JsonSerializer.Deserialize<AppConfig>(saved, JsonFiles.Options)!;
        Assert.False(config.NeedsSpeechModelSelection);
        Assert.Equal(id, config.SelectedSpeechModel!.Id);
    }

    [Fact]
    public void StateDistinguishesMissingPartialAndInstalled()
    {
        using var models = new TestDirectory();
        Assert.Equal(SpeechModelState.Missing, SpeechModelFiles.GetState(Parakeet, models.Path));
        Install(Parakeet, models.Path);
        Assert.Equal(SpeechModelState.Installed, SpeechModelFiles.GetState(Parakeet, models.Path));

        // A file of the wrong size is damaged, not installed.
        var directory = SpeechModelFiles.DownloadDirectory(Parakeet, models.Path);
        File.WriteAllBytes(Path.Combine(directory, "vocab.txt"), [1]);
        Assert.Equal(SpeechModelState.Partial, SpeechModelFiles.GetState(Parakeet, models.Path));

        File.WriteAllBytes(Path.Combine(models.Path, SmallWhisper.Location + ModelDownloader.PartialSuffix), [1]);
        Assert.Equal(SpeechModelState.Partial, SpeechModelFiles.GetState(SmallWhisper, models.Path));
    }

    [Fact]
    public void DeletingAModelRemovesItsFilesAndNothingElse()
    {
        using var models = new TestDirectory();
        Install(Parakeet, models.Path);
        Install(SmallWhisper, models.Path);
        var mine = Path.Combine(models.Path, "my-notes.txt");
        File.WriteAllText(mine, "keep");

        SpeechModelFiles.Delete(SmallWhisper, models.Path);
        Assert.Equal(SpeechModelState.Missing, SpeechModelFiles.GetState(SmallWhisper, models.Path));
        Assert.Equal(SpeechModelState.Installed, SpeechModelFiles.GetState(Parakeet, models.Path));
        Assert.True(File.Exists(mine));

        var directory = SpeechModelFiles.DownloadDirectory(Parakeet, models.Path);
        File.WriteAllBytes(Path.Combine(directory, "encoder-model.int8.onnx" + ModelDownloader.PartialSuffix), [1]);
        SpeechModelFiles.Delete(Parakeet, models.Path);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void AFolderHoldingSomethingUnfamiliarOutlivesTheModel()
    {
        using var models = new TestDirectory();
        Install(Parakeet, models.Path);
        var directory = SpeechModelFiles.DownloadDirectory(Parakeet, models.Path);
        var other = Path.Combine(directory, "readme-from-the-user.txt");
        File.WriteAllText(other, "keep");
        SpeechModelFiles.Delete(Parakeet, models.Path);
        Assert.Equal(SpeechModelState.Missing, SpeechModelFiles.GetState(Parakeet, models.Path));
        Assert.True(File.Exists(other));
    }

    // Junctions need no elevation, unlike symbolic links, so this always runs.
    [Fact]
    public void ALinkedModelsFolderIsRefusedBeforeAnythingInsideIsChecked()
    {
        using var workspace = new TestDirectory();
        var target = Path.Combine(workspace.Path, "elsewhere");
        Directory.CreateDirectory(target);
        Install(SmallWhisper, target);
        var linked = Path.Combine(workspace.Path, "models");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", linked, target])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        Assert.Throws<ArgumentException>(() => SpeechModelFiles.GetState(SmallWhisper, linked));
        Assert.Throws<ArgumentException>(() => SpeechModelFiles.GetState(Parakeet, linked));
        Directory.Delete(linked);
    }

    [Fact]
    public void AnIncludedModelCannotBeDeleted()
    {
        using var models = new TestDirectory();
        Assert.Throws<InvalidOperationException>(() => SpeechModelFiles.Delete(Model(SpeechModels.IncludedWhisperId), models.Path));
    }

    [Fact]
    public void ALinkedModelFileIsRefusedAndItsTargetKept()
    {
        using var models = new TestDirectory();
        var outside = Path.Combine(models.Path, "outside.bin");
        File.WriteAllBytes(outside, [9]);
        try { File.CreateSymbolicLink(Path.Combine(models.Path, SmallWhisper.Location), outside); }
        catch (IOException) { return; } // Creating links needs Developer Mode or elevation, so this cannot run everywhere.
        catch (UnauthorizedAccessException) { return; }
        Assert.Throws<ArgumentException>(() => SpeechModelFiles.Delete(SmallWhisper, models.Path));
        Assert.Equal([9], File.ReadAllBytes(outside));
    }

    [Fact]
    public void ValidationExplainsAModelThatIsNotReady()
    {
        using var workspace = new TestDirectory();
        var vad = Path.Combine(workspace.Path, "vad.bin");
        File.WriteAllBytes(vad, [0]);
        var config = new AppConfig { OutputDirectory = workspace.Path, VadPath = vad };
        var notDownloaded = Assert.Throws<ArgumentException>(() => (config with { SpeechModelId = Parakeet.Id }).Validate());
        Assert.Contains(Parakeet.Name, notDownloaded.Message);
        var unknown = Assert.Throws<ArgumentException>(() => (config with { SpeechModelId = "from-a-newer-version" }).Validate());
        Assert.Contains("Choose a transcription model in Settings", unknown.Message);
    }

    [Fact]
    public void ASpecificWhisperFileWinsUntilAModelIsChosen()
    {
        using var workspace = new TestDirectory();
        var file = Path.Combine(workspace.Path, "custom.bin");
        File.WriteAllBytes(file, [0]);
        var config = new AppConfig { OutputDirectory = workspace.Path, VadPath = file, ModelPath = file, SpeechModelId = Parakeet.Id };
        Assert.Null(config.SelectedSpeechModel);
        Assert.Equal(file, config.ResolvedModelPath);
        config.Validate();
    }

    [Fact]
    public void SegmentsSplitAtSentenceEndsAndCarryTheirTimes()
    {
        var segments = ParakeetSpeechEngine.ToSegments(
            [("▁Ask", 10), ("▁not", 20), (".", 25), ("▁What", 50), ("▁now", 60), ("?", 65), ("▁Go", 80)], 0.08);
        Assert.Equal(["Ask not.", "What now?", "Go"], segments.Select(segment => segment.Text));
        Assert.Equal(0.8, segments[0].StartSeconds, 3);
        Assert.Equal(4.0, segments[0].EndSeconds, 3);
        Assert.Equal(6.4, segments[2].StartSeconds, 3);
        Assert.Equal(6.48, segments[2].EndSeconds, 3);
    }

    [Fact]
    public void ADecimalPointDoesNotEndASentence()
    {
        var segment = Assert.Single(ParakeetSpeechEngine.ToSegments([("▁version", 1), ("▁3", 2), (".", 3), ("5", 4), ("▁ships", 5)], 0.08));
        Assert.Equal("version 3.5 ships", segment.Text);
    }

    [Fact]
    public void ControlTokensAreNotSpeech()
    {
        Assert.Empty(ParakeetSpeechEngine.ToSegments([("<|nospeech|>", 3), ("<unk>", 4)], 0.08));
        Assert.Empty(ParakeetSpeechEngine.ToSegments([], 0.08));
        Assert.Equal("hello there", Assert.Single(ParakeetSpeechEngine.ToSegments([("<|nospeech|>", 3), ("▁hello", 4), ("▁there", 5)], 0.08)).Text);
    }

    // A later official runtime adds an uploader. The recording process must not gain one through a version bump.
    [Fact]
    public void TheBundledSpeechRuntimeContainsNoNetworkCode()
    {
        var library = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "onnxruntime.dll");
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(library));
        foreach (var marker in new[] { "winhttp", "wininet", "ws2_32", "cpp_client_telemetry", "events.data.microsoft" })
            Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
    }
}
