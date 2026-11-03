using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class SummaryTests
{
    private static TranscriptLine Line(double start, AudioSource source, string text) => new(start, start + 3, source, text, false);

    private static List<TranscriptLine> Meeting(int lines = 12) => Enumerable.Range(0, lines)
        .Select(index => Line(index * 40, index % 2 == 0 ? AudioSource.Remote : AudioSource.Local,
            $"Priya will send the release plan by Friday and Marcus reruns the load test, point number {index}."))
        .ToList();

    private sealed class ScriptedModel(params string[] responses) : ITextModel
    {
        private int next;
        public List<string> Inputs { get; } = [];
        public int CountTokens(string text) => text.Length / 4;
        public void Dispose() { }
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken)
        {
            Inputs.Add(userPrompt);
            return Task.FromResult(responses[Math.Min(next++, responses.Length - 1)]);
        }
    }

    private const string GoodResponse = """
        ACTION: Priya | Send the release plan | by Friday | 00:00:00
        ACTION: Me | Open the infra ticket | - | -
        DECISION: iOS ships first.
        POINT: The load test missed its target.
        OVERVIEW: The team reviewed the launch.
        TITLE: Launch review
        """;

    [Fact]
    public async Task TooLittleSpeechIsNotSummarisedAtAll()
    {
        var model = new ScriptedModel(GoodResponse);
        var summary = await SummaryJob.SummarizeAsync(model, [Line(0, AudioSource.Remote, "Thank you.")], "model", "CPU", _ => { });
        Assert.Equal(SummaryState.NoSpeech, summary.State);
        Assert.Null(summary.Notes);
        Assert.Empty(model.Inputs);
    }

    [Fact]
    public async Task ParsesAllSectionsAndLabelsSourcesInThePrompt()
    {
        var model = new ScriptedModel(GoodResponse);
        var progress = new List<SummaryProgress>();
        var summary = await SummaryJob.SummarizeAsync(model, Meeting(), "model", "CPU", progress.Add);
        var notes = summary.Notes!;
        Assert.Equal(SummaryState.Complete, summary.State);
        Assert.Equal("Launch review", notes.Title);
        Assert.Equal("The team reviewed the launch.", notes.Overview);
        Assert.Equal(["iOS ships first."], notes.Decisions);
        Assert.Equal(["The load test missed its target."], notes.KeyPoints);
        Assert.Equal(2, notes.ActionItems.Count);
        Assert.Equal(new MeetingActionItem("Send the release plan", "Priya", "by Friday", "00:00:00"), notes.ActionItems[0]);
        Assert.Contains("] Playback: Priya", model.Inputs[0]);
        Assert.Contains("] Mic: Priya", model.Inputs[0]);
        Assert.Equal(new SummaryProgress(1, 1), progress[^1]);
    }

    [Fact]
    public async Task InventedOwnersDeadlinesAndTimestampsAreDropped()
    {
        var model = new ScriptedModel("""
            ACTION: Gandalf | Send the release plan | by next Tuesday | 09:09:09
            ACTION: ME | Open the ticket | - | -
            OVERVIEW: Something happened.
            """);
        var summary = await SummaryJob.SummarizeAsync(model, Meeting(), "model", "CPU", _ => { });
        Assert.Equal(new MeetingActionItem("Send the release plan", null, null, null), summary.Notes!.ActionItems[0]);
        Assert.Equal("Me", summary.Notes.ActionItems[1].Owner);
    }

    [Fact]
    public async Task ATaskForTheReaderMustRestOnWordsActuallySaid()
    {
        var transcript = new List<TranscriptLine>
        {
            Line(0, AudioSource.Remote, "The iOS build went through review on Tuesday and Android is still blocked on the upgrade."),
            Line(100, AudioSource.Local, "Three days feels tight. What happens if the SDK slips past the freeze date?"),
            Line(200, AudioSource.Remote, "Oana, can you send the pricing table to legal before the review?"),
            Line(300, AudioSource.Local, "Sounds fine. I'll open the ticket with infrastructure today."),
            Line(320, AudioSource.Remote, "Great, the connection pool limit is what needs raising there.")
        };
        var model = new ScriptedModel("""
            ACTION: Me | Prepare a contingency plan | - | 00:01:40
            ACTION: Me | Send the pricing table to legal | - | 00:03:20
            ACTION: Me | Open the ticket | today | 00:05:00
            ACTION: Me | Raise the connection pool limit | - | 00:05:20
            ACTION: Me | Do something without a citation | - | -
            ACTION: Priya | Check the freeze date | - | 00:01:40
            OVERVIEW: Summary.
            """);
        var notes = (await SummaryJob.SummarizeAsync(model, transcript, "model", "CPU", _ => { })).Notes!;
        Assert.Equal(["Send the pricing table to legal", "Open the ticket", "Raise the connection pool limit", "Do something without a citation", "Check the freeze date"],
            notes.ActionItems.Select(item => item.Task));
    }

    [Fact]
    public async Task UnusableOutputIsRetriedOnceThenReportedInsteadOfProducingAnEmptySummary()
    {
        var recovering = new ScriptedModel("Sorry, I cannot help with that.", GoodResponse);
        Assert.Equal("Launch review", (await SummaryJob.SummarizeAsync(recovering, Meeting(), "model", "CPU", _ => { })).Notes!.Title);
        Assert.Equal(2, recovering.Inputs.Count);

        var hopeless = new ScriptedModel("Sorry, I cannot help with that.");
        await Assert.ThrowsAsync<InvalidDataException>(() => SummaryJob.SummarizeAsync(hopeless, Meeting(), "model", "CPU", _ => { }));
        Assert.Equal(2, hopeless.Inputs.Count);
    }

    [Fact]
    public async Task LongMeetingsAreSummarisedInPartsThenMergedWithoutOverflowingTheContext()
    {
        var model = new ScriptedModel(GoodResponse);
        var progress = new List<SummaryProgress>();
        // Far more text than one context holds. CountTokens is roughly realistic at four characters per token.
        var longMeeting = Meeting(lines: 3000);
        var summary = await SummaryJob.SummarizeAsync(model, longMeeting, "model", "CPU", progress.Add);
        Assert.Equal(SummaryState.Complete, summary.State);
        Assert.True(model.Inputs.Count > 2);
        Assert.Contains("consecutive parts", model.Inputs[^1]);
        Assert.All(model.Inputs, input => Assert.True(model.CountTokens(input) <= Summarizer.ContextTokens));
        Assert.Equal(progress[^1].Total, progress[^1].Completed);
        Assert.Equal(progress.Select(update => update.Completed).Order(), progress.Select(update => update.Completed));
    }

    [Theory]
    [InlineData("- **ACTION:** Me | Do it | - | -")]
    [InlineData("* ACTION: Me | Do it | - | -")]
    [InlineData("<think>\nprivate reasoning, ACTION: Nobody | Wrong | - | -\n</think>\nACTION: Me | Do it | - | -")]
    public void ParserToleratesCommonFormattingAroundPrefixes(string response)
    {
        var item = Assert.Single(Summarizer.Parse(response)!.ActionItems);
        Assert.Equal("Do it", item.Task);
        Assert.Equal("Me", item.Owner);
    }

    [Fact]
    public void ParserKeepsWrappedOverviewsAndItemsWithoutColumnsButNotNonsense()
    {
        var notes = Summarizer.Parse("ACTION: Call the vendor\nOVERVIEW: First sentence.\nSecond sentence.\nTITLE: Topic")!;
        Assert.Equal("Call the vendor", Assert.Single(notes.ActionItems).Task);
        Assert.Equal("First sentence. Second sentence.", notes.Overview);
        Assert.Equal("Topic", notes.Title);
        Assert.Null(Summarizer.Parse("I am a helpful assistant."));
    }

    [Theory]
    [InlineData("Open infrastructure ticket to raise database connection pool limit", "Open a ticket with the infrastructure team to raise the database connection pool limit", true)]
    [InlineData("Update release plan and send it out", "Update the release plan and send it out", true)]
    [InlineData("Send the report to Alice", "Send the report to Bob", false)]
    [InlineData("Send the report", "Send the report to Alice", false)]
    [InlineData("Ship 3 units on the next order", "Ship 5 units on the next order", false)]
    [InlineData("Enable the feature flag", "Disable the feature flag", false)]
    [InlineData("Send the report", "Do not send the report", false)]
    [InlineData("Book the venue", "Write the incident report", false)]
    public void RewordedItemsCountAsRepeatsOnlyWhenNothingMeaningfulDiffers(string first, string second, bool expected)
    {
        Assert.Equal(expected, Summarizer.IsRepeat(first, second));
        Assert.Equal(expected, Summarizer.IsRepeat(second, first));
    }

    [Fact]
    public async Task RepeatsCollapseToTheFullerWordingButDifferentAssignmentsSurvive()
    {
        var model = new ScriptedModel("""
            ACTION: Me | Update release plan and send it out | - | -
            ACTION: Me | Update the release plan and then send it out | - | -
            ACTION: Priya | Send the release plan | by Friday | -
            ACTION: Marcus | Send the release plan | by Friday | -
            ACTION: Me | Review the budget | by Friday | -
            ACTION: Me | Review the budget | by Monday | -
            DECISION: iOS ships first regardless of Android.
            DECISION: iOS ships first regardless of the Android delay.
            OVERVIEW: Summary.
            """);
        var transcript = Meeting().Append(Line(600, AudioSource.Remote, "Budget review by Monday or by Friday.")).ToList();
        var notes = (await SummaryJob.SummarizeAsync(model, transcript, "model", "CPU", _ => { })).Notes!;
        Assert.Equal(
            [("Update the release plan and then send it out", "Me", null), ("Send the release plan", "Priya", "by Friday"), ("Send the release plan", "Marcus", "by Friday"),
             ("Review the budget", "Me", "by Friday"), ("Review the budget", "Me", "by Monday")],
            notes.ActionItems.Select(item => (item.Task, item.Owner, item.Due)));
        Assert.Equal("iOS ships first regardless of the Android delay.", Assert.Single(notes.Decisions));
    }

    [Fact]
    public void AFailedRegenerationKeepsTheEarlierSummaryAndSaysWhatHappened()
    {
        using var directory = new TestDirectory();
        var notes = new MeetingNotes("Title", "Overview.", [], [], []);
        SummaryFiles.Write(directory.Path, new MeetingSummary(SummaryState.Complete, DateTimeOffset.UtcNow, "good-model", 10, notes));
        SummaryFiles.WriteFailure(directory.Path, "bad-model", "Reason.");
        var kept = SummaryFiles.Read(directory.Path)!;
        Assert.Equal(SummaryState.Complete, kept.State);
        Assert.Equal("Title", kept.Notes!.Title);
        Assert.Equal("good-model", kept.ModelName);
        Assert.Equal("Reason.", kept.Error);
        SummaryFiles.Write(directory.Path, new MeetingSummary(SummaryState.Complete, DateTimeOffset.UtcNow, "good-model", 10, notes));
        Assert.Null(SummaryFiles.Read(directory.Path)!.Error);
    }

    [Fact]
    public void FirstFailureAndDamagedSummaryFilesRecordTheFailure()
    {
        using var directory = new TestDirectory();
        SummaryFiles.WriteFailure(directory.Path, "model", "First.");
        Assert.Equal(SummaryState.Failed, SummaryFiles.Read(directory.Path)!.State);
        File.WriteAllText(Path.Combine(directory.Path, SummaryFiles.SummaryFile), "{ broken");
        SummaryFiles.WriteFailure(directory.Path, "model", "Second.");
        Assert.Equal("Second.", SummaryFiles.Read(directory.Path)!.Error);
    }

    [Fact]
    public async Task MergingThatCannotShrinkFailsInsteadOfLoopingForever()
    {
        // Every response is too large for two to share a context, so a merge pass can never reduce the count.
        var oversized = "OVERVIEW: " + string.Join(' ', Enumerable.Repeat("word", 24000));
        var model = new ScriptedModel(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() => SummaryJob.SummarizeAsync(model, Meeting(lines: 3000), "model", "CPU", _ => { }));
        Assert.True(model.Inputs.Count < 100);
    }

    [Fact]
    public void SummaryFilesRoundTripAndRenderReadableMarkdown()
    {
        using var directory = new TestDirectory();
        var notes = new MeetingNotes("Launch review", "Overview text.", ["A point"], ["A decision"],
            [new MeetingActionItem("Send plan", "Priya", "by Friday", "00:01:00"), new MeetingActionItem("Book room", null, null, null)]);
        SummaryFiles.Write(directory.Path, new MeetingSummary(SummaryState.Complete, DateTimeOffset.UtcNow, "model", 12, notes));
        var loaded = SummaryFiles.Read(directory.Path)!;
        Assert.Equal(SummaryState.Complete, loaded.State);
        Assert.Equal("Send plan", loaded.Notes!.ActionItems[0].Task);
        Assert.Contains("\"State\": \"Complete\"", File.ReadAllText(Path.Combine(directory.Path, SummaryFiles.SummaryFile)));
        var markdown = File.ReadAllText(Path.Combine(directory.Path, SummaryFiles.MarkdownFile));
        Assert.Contains("# Launch review", markdown);
        Assert.Contains("- Send plan (Priya, due by Friday, 00:01:00)", markdown);
        Assert.Contains("- Book room", markdown);
        Assert.Contains("Generated locally by model", markdown);
    }

    [Fact]
    public void AFailedSummaryWritesNoMarkdownAndNeverOverwritesItWithPartialText()
    {
        using var directory = new TestDirectory();
        SummaryFiles.Write(directory.Path, new MeetingSummary(SummaryState.Failed, DateTimeOffset.UtcNow, Error: "Reason."));
        Assert.Equal("Reason.", SummaryFiles.Read(directory.Path)!.Error);
        Assert.False(File.Exists(Path.Combine(directory.Path, SummaryFiles.MarkdownFile)));
    }

    [Fact]
    public async Task SummaryJobWithoutAModelFailsBeforeTouchingTheSession()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SummaryModelPath = Path.Combine(directory.Path, "missing.gguf") });
        var session = Directory.CreateDirectory(Path.Combine(directory.Path, "session")).FullName;
        await Assert.ThrowsAsync<FileNotFoundException>(() => SummaryJob.RunAsync(paths, session, SummaryJobProcessor.Configured, _ => { }, () => { }));
        Assert.Null(SummaryFiles.Read(session));
    }

    [Fact]
    public async Task SummaryJobGivesWayToARunningRecordingWithoutWritingAnything()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic.gguf"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SummaryModelPath = model });
        var session = Directory.CreateDirectory(Path.Combine(directory.Path, "session")).FullName;
        using var workerLock = new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(SummaryJob.InterruptedExitCode, await SummaryJob.RunAsync(paths, session, SummaryJobProcessor.Configured, _ => { }, () => { }));
        Assert.Null(SummaryFiles.Read(session));
    }

    [Fact]
    public async Task SecondSummaryJobReportsBusyInsteadOfFailingTheMeeting()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic.gguf"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SummaryModelPath = model });
        var session = Directory.CreateDirectory(Path.Combine(directory.Path, "session")).FullName;
        using var running = new FileStream(Path.Combine(paths.Root, "summary.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(SummaryJob.BusyExitCode, await SummaryJob.RunAsync(paths, session, SummaryJobProcessor.Configured, _ => { }, () => { }));
        Assert.Null(SummaryFiles.Read(session));
    }

    [Fact]
    public async Task DamagedRecordsFailTheSummaryInsteadOfQuietlySummarisingAroundThem()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var model = Path.Combine(directory.Path, "synthetic.gguf"); File.WriteAllBytes(model, [0]);
        JsonFiles.Write(paths.ConfigFile, new AppConfig { SummaryModelPath = model });
        var store = new TranscriptStore(Path.Combine(directory.Path, "session"));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "segments", Guid.NewGuid().ToString("N") + ".json"), "{ not json");
        Assert.Equal(SummaryJob.FailedExitCode, await SummaryJob.RunAsync(paths, store.DirectoryPath, SummaryJobProcessor.Configured, _ => { }, () => { }));
        var summary = SummaryFiles.Read(store.DirectoryPath)!;
        Assert.Equal(SummaryState.Failed, summary.State);
        Assert.Contains(nameof(InvalidDataException), summary.Error);
    }
}
