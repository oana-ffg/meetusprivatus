using Meetu;
using Meetu.App;
using Meetu.App.ViewModels;
using Xunit;

namespace Meetu.Tests;

public sealed class AppTests
{
    private static MeetingInfo Info(MeetingSummary? summary = null, string state = "Complete", bool autoSummarize = true) =>
        new(@"C:\meetings\session", new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero), null, 3000, state, null, autoSummarize, summary);

    private static MeetingDetailViewModel Detail(MeetingInfo info, bool live = false, bool model = true, bool generating = false) =>
        new(info, live, model, generating, _ => { }, _ => { }, () => { });

    private static MeetingSummary Ready() => new(SummaryState.Complete, DateTimeOffset.UtcNow, "model", 65,
        new MeetingNotes("Launch review", "Overview.", [], [], [new MeetingActionItem("Task", "Me", null, "00:00:05")]));

    public static TheoryData<MeetingInfo, bool, bool, bool, SummaryPanel> PanelCases => new()
    {
        { Info(state: "Capturing"), true, true, false, SummaryPanel.DuringRecording },
        { Info(), false, true, true, SummaryPanel.Generating },
        { Info(Ready()), false, true, false, SummaryPanel.Ready },
        { Info(new MeetingSummary(SummaryState.NoSpeech, DateTimeOffset.UtcNow)), false, true, false, SummaryPanel.NoSpeech },
        { Info(new MeetingSummary(SummaryState.Failed, DateTimeOffset.UtcNow, Error: "x")), false, true, false, SummaryPanel.Failed },
        { Info(), false, false, false, SummaryPanel.ModelMissing },
        { Info(), false, true, false, SummaryPanel.Waiting },
        { Info(autoSummarize: false), false, true, false, SummaryPanel.NotGenerated },
        { Info(state: MeetingLibrary.InterruptedState), false, true, false, SummaryPanel.NotGenerated },
        { Info(state: MeetingLibrary.UnreadableState), false, true, false, SummaryPanel.Unavailable },
    };

    [Theory]
    [MemberData(nameof(PanelCases))]
    public void SummaryPanelFollowsTheMeetingsState(MeetingInfo info, bool live, bool model, bool generating, SummaryPanel expected) =>
        Assert.Equal(expected, Detail(info, live, model, generating).Panel);

    [Fact]
    public void LiveMeetingsOpenOnTheTranscriptAndFinishedOnesOnTheSummary()
    {
        Assert.Equal(1, Detail(Info(state: "Capturing"), live: true).SelectedTab);
        Assert.Equal(0, Detail(Info(Ready())).SelectedTab);
    }

    [Fact]
    public void FinishingMovesToTheSummaryUnlessTheReaderChoseATab()
    {
        var untouched = Detail(Info(state: "Capturing"), live: true);
        untouched.Update(Info(Ready()), false, true, false);
        Assert.Equal(0, untouched.SelectedTab);

        var reading = Detail(Info(state: "Capturing"), live: true);
        reading.SelectedTab = 1;
        reading.SelectedTab = 0;
        reading.SelectedTab = 1;
        reading.Update(Info(Ready()), false, true, false);
        Assert.Equal(1, reading.SelectedTab);
    }

    [Fact]
    public async Task TranscriptMergesBothSourcesInTimeOrderEvenWhenClipsFinishOutOfOrder()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        var detail = new MeetingDetailViewModel(Info() with { Directory = store.DirectoryPath }, true, true, false, _ => { }, _ => { }, () => { });
        Assert.False(detail.ShowTranscriptPlaceholder);
        await detail.RefreshTranscriptAsync();
        Assert.True(detail.ShowTranscriptPlaceholder);
        store.Commit(Record(AudioSource.Remote, 20, "second"));
        await detail.RefreshTranscriptAsync();
        store.Commit(Record(AudioSource.Local, 10, "first"));
        store.Commit(Record(AudioSource.Remote, 30, "third"));
        await detail.RefreshTranscriptAsync();
        Assert.Equal(["first", "second", "third"], detail.Lines.Select(row => row.Text));
        Assert.False(detail.ShowTranscriptPlaceholder);
        Assert.Equal("[00:00:10] Mic: first" + Environment.NewLine + "[00:00:20] Playback: second" + Environment.NewLine,
            MeetingDetailViewModel.RenderLines(detail.Lines.Take(2)));
    }

    [Fact]
    public async Task OverlappingRefreshesShowEachRecordOnce()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        for (var index = 0; index < 200; index++) store.Commit(Record(AudioSource.Local, index, $"line {index}"));
        var detail = new MeetingDetailViewModel(Info() with { Directory = store.DirectoryPath }, true, true, false, _ => { }, _ => { }, () => { });
        await Task.WhenAll(detail.RefreshTranscriptAsync(), detail.RefreshTranscriptAsync(), detail.RefreshTranscriptAsync());
        Assert.Equal(200, detail.Lines.Count);
        Assert.Equal(200, detail.Lines.Select(row => row.Text).Distinct().Count());
    }

    [Fact]
    public void ModelDownloadReplacesTheButtonWithProgress()
    {
        var detail = Detail(Info(), model: false);
        Assert.True(detail.ShowDownloadButton);
        detail.ReportModelDownload(42);
        Assert.False(detail.ShowDownloadButton);
        Assert.True(detail.IsDownloadingModel);
        Assert.Contains("42%", detail.ModelDownloadText);
        detail.ReportModelDownload(null);
        Assert.True(detail.ShowDownloadButton);
    }

    [Fact]
    public void TheSummaryNoteSaysWhichProcessorDidTheWork()
    {
        Assert.Contains("using the GPU", Detail(Info(Ready() with { Device = "GPU" })).ModelNote);
        Assert.Contains("using the CPU, because the GPU attempt failed", Detail(Info(Ready() with { Device = "CPU, because the GPU attempt failed" })).ModelNote);
    }

    [Fact]
    public void ARegenerationProblemIsShownBesideTheEarlierSummary()
    {
        var detail = Detail(Info(Ready() with { Error = "Reason." }));
        Assert.Equal(SummaryPanel.Ready, detail.Panel);
        Assert.Contains("Reason.", detail.RegenerationProblem);
        Assert.Null(Detail(Info(Ready())).RegenerationProblem);
    }

    [Fact]
    public async Task DamagedRecordsAreReportedToTheReader()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "segments", Guid.NewGuid().ToString("N") + ".json"), "{ broken");
        var errors = new List<string>();
        var detail = new MeetingDetailViewModel(Info() with { Directory = store.DirectoryPath }, false, true, false, errors.Add, _ => { }, () => { });
        await detail.RefreshTranscriptAsync();
        Assert.Contains("could not be read", Assert.Single(errors));
    }

    [Theory]
    [InlineData("progress 2 5", true, 2, 5)]
    [InlineData("progress x 5", false, 0, 0)]
    [InlineData("something else entirely", false, 0, 0)]
    public void ProgressLinesAreParsedStrictly(string line, bool valid, int completed, int total)
    {
        Assert.Equal(valid, HelperProcess.TryParseProgress(line, out var parsedCompleted, out var parsedTotal));
        if (!valid) return;
        Assert.Equal(completed, parsedCompleted);
        Assert.Equal(total, parsedTotal);
    }

    [Theory]
    [InlineData(45, "45 sec")]
    [InlineData(3000, "50 min")]
    [InlineData(5400, "1 hr 30 min")]
    public void DurationsReadNaturally(double seconds, string expected) => Assert.Equal(expected, DisplayText.Duration(seconds));

    [Fact]
    public void GroupsMeetingsByFriendlyDate()
    {
        var today = new DateTime(2026, 11, 3);
        Assert.Equal("Today", DisplayText.DateGroup(today, today));
        Assert.Equal("Yesterday", DisplayText.DateGroup(today.AddDays(-1), today));
        Assert.Contains("2025", DisplayText.DateGroup(new DateTime(2025, 6, 1), today));
    }

    [Fact]
    public void SettingsRejectInvalidValuesAndPersistValidOnes()
    {
        using var root = new TestDirectory();
        var paths = new StatePaths(Path.Combine(root.Path, "state"));
        var model = Path.Combine(root.Path, "model.bin"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { ModelPath = model, VadPath = model, MicrophoneId = "unplugged-microphone" });
        var settings = new SettingsViewModel(paths);
        Assert.Equal("unplugged-microphone", settings.Microphone!.Id);
        settings.MaxMinutes = 0;
        Assert.False(settings.TrySave());
        Assert.NotNull(settings.Error);
        Assert.Equal(60, paths.LoadConfig().MaxSessionMinutes);
        settings.MaxMinutes = 45;
        settings.HideFromScreenSharing = false;
        settings.SummarizeAutomatically = false;
        Assert.False(settings.SummarizeOnGpu);
        settings.SummarizeOnGpu = true;
        Assert.True(settings.TrySave());
        Assert.Equal(SummaryProcessor.Gpu, paths.LoadConfig().SummaryProcessor);
        Assert.Contains("\"Gpu\"", File.ReadAllText(paths.ConfigFile));
        var saved = paths.LoadConfig();
        Assert.Equal(45, saved.MaxSessionMinutes);
        Assert.False(saved.HideWindowFromScreenSharing);
        Assert.False(saved.SummarizeAutomatically);
        Assert.Equal("unplugged-microphone", saved.MicrophoneId);
    }

    private static TranscriptRecord Record(AudioSource source, double start, string text) =>
        new(Guid.NewGuid().ToString("N"), source, start, start + 2, [new TranscriptSegment(start, start + 2, text)]);
}
