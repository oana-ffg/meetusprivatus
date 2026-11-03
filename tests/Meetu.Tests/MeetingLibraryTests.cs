using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class MeetingLibraryTests
{
    private static string CreateSession(string root, DateTimeOffset started, string state, bool autoSummarize = true, string? name = null)
    {
        var directory = Path.Combine(root, name ?? $"{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        _ = new TranscriptStore(directory);
        SessionMetadata.Write(directory, new SessionMetadata(started, state) { AutoSummarize = autoSummarize, CaptureSeconds = 90 });
        return directory;
    }

    [Fact]
    public void ListsNewestFirstAndIgnoresUnrelatedFolders()
    {
        using var root = new TestDirectory();
        var older = CreateSession(root.Path, new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "Complete");
        var newer = CreateSession(root.Path, new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), "Complete");
        Directory.CreateDirectory(Path.Combine(root.Path, "holiday-photos"));
        CreateSession(root.Path, DateTimeOffset.UtcNow, "Complete", name: "not-a-session-name");
        var list = MeetingLibrary.List(root.Path, null);
        Assert.Equal([newer, older], list.Select(meeting => meeting.Directory));
    }

    [Fact]
    public void MissingFolderIsAnEmptyHistory()
    {
        using var root = new TestDirectory();
        Assert.Empty(MeetingLibrary.List(Path.Combine(root.Path, "never-created"), null));
    }

    [Fact]
    public void OnlyTheLiveSessionKeepsItsCapturingState()
    {
        using var root = new TestDirectory();
        var started = DateTimeOffset.UtcNow;
        var crashed = CreateSession(root.Path, started.AddDays(-1), "Capturing");
        var live = CreateSession(root.Path, started, "Capturing");
        var byDirectory = MeetingLibrary.List(root.Path, live).ToDictionary(meeting => meeting.Directory);
        Assert.Equal("Capturing", byDirectory[live].State);
        Assert.Equal(MeetingLibrary.InterruptedState, byDirectory[crashed].State);
    }

    [Fact]
    public void DamagedMetadataIsListedAsUnreadableRatherThanHidden()
    {
        using var root = new TestDirectory();
        var damaged = CreateSession(root.Path, DateTimeOffset.UtcNow, "Complete");
        File.WriteAllText(Path.Combine(damaged, SessionMetadata.FileName), "{ broken");
        var meeting = Assert.Single(MeetingLibrary.List(root.Path, null));
        Assert.Equal(MeetingLibrary.UnreadableState, meeting.State);
        Assert.NotNull(meeting.Error);
    }

    [Fact]
    public void AutomaticSummariesCoverOnlyFinishedRequestedMeetingsWithoutAnOutcome()
    {
        using var root = new TestDirectory();
        var started = DateTimeOffset.UtcNow;
        var pending = CreateSession(root.Path, started, "Complete");
        var failedCapture = CreateSession(root.Path, started.AddMinutes(-1), "Failed");
        CreateSession(root.Path, started.AddMinutes(-2), "Complete", autoSummarize: false);
        var live = CreateSession(root.Path, started.AddMinutes(-3), "Capturing");
        CreateSession(root.Path, started.AddMinutes(-4), "Capturing");
        var done = CreateSession(root.Path, started.AddMinutes(-5), "Complete");
        SummaryFiles.Write(done, new MeetingSummary(SummaryState.NoSpeech, started));
        var gaveUp = CreateSession(root.Path, started.AddMinutes(-6), "Complete");
        SummaryFiles.Write(gaveUp, new MeetingSummary(SummaryState.Failed, started, Error: "Reason."));
        var meetings = MeetingLibrary.List(root.Path, live);
        Assert.Equal([pending, failedCapture], MeetingLibrary.PendingAutomaticSummaries(meetings).Select(meeting => meeting.Directory));
    }

    [Fact]
    public void FeedDeliversEachRecordOnceInTimeOrderAndSkipsWhisperAnnotations()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        var feed = new TranscriptFeed(store.DirectoryPath);
        Assert.Empty(feed.ReadNew());
        store.Commit(Record(AudioSource.Remote, 20, " later   words "));
        store.Commit(Record(AudioSource.Local, 10, "earlier"));
        store.Commit(Record(AudioSource.Remote, 30, "[BLANK_AUDIO]"));
        store.Commit(Record(AudioSource.Remote, 31, "(keyboard clicking)"));
        store.Commit(Record(AudioSource.Remote, 32, "♪ ♪"));
        var first = feed.ReadNew();
        Assert.Equal(["earlier", "later words"], first.Select(line => line.Text));
        Assert.Equal(AudioSource.Local, first[0].Source);
        Assert.Empty(feed.ReadNew());
        store.Commit(Record(AudioSource.Local, 15, "arrived late"));
        Assert.Equal("arrived late", Assert.Single(feed.ReadNew()).Text);
        Assert.Equal(0, feed.UnreadableRecords);
    }

    [Fact]
    public void FeedIncludesGapsAndCountsDamagedRecordsUntilTheyBecomeReadable()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 5, 10, [], "Capture discontinuity."));
        var damagedPath = Path.Combine(store.DirectoryPath, "segments", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(damagedPath, "{ broken");
        var feed = new TranscriptFeed(store.DirectoryPath);
        var gap = Assert.Single(feed.ReadNew());
        Assert.True(gap.IsGap);
        Assert.Equal(1, feed.UnreadableRecords);
        File.Delete(damagedPath);
        File.WriteAllText(damagedPath, System.Text.Json.JsonSerializer.Serialize(Record(AudioSource.Local, 11, "recovered"), JsonFiles.Options));
        Assert.Equal("recovered", Assert.Single(feed.ReadNew()).Text);
        Assert.Equal(0, feed.UnreadableRecords);
    }

    [Fact]
    public void TheLiveMeetingStaysListedAfterTheDestinationChanges()
    {
        using var root = new TestDirectory();
        var live = CreateSession(Directory.CreateDirectory(Path.Combine(root.Path, "old")).FullName, DateTimeOffset.UtcNow, "Capturing");
        var other = Directory.CreateDirectory(Path.Combine(root.Path, "new")).FullName;
        var meeting = Assert.Single(MeetingLibrary.List(other, live));
        Assert.Equal(live, meeting.Directory);
        Assert.Equal("Capturing", meeting.State);
        Assert.Empty(MeetingLibrary.List(other, null));
    }

    [Fact]
    public void LinkedRecordsAndFoldersAreRefusedAndCountedInsteadOfFollowed()
    {
        using var root = new TestDirectory();
        var store = new TranscriptStore(Path.Combine(root.Path, "session"));
        store.Commit(Record(AudioSource.Local, 1, "safe text"));
        var outside = Path.Combine(root.Path, "outside.json");
        File.WriteAllText(outside, System.Text.Json.JsonSerializer.Serialize(Record(AudioSource.Local, 2, "text behind a link"), JsonFiles.Options));
        var linked = Path.Combine(store.DirectoryPath, "segments", Guid.NewGuid().ToString("N") + ".json");
        try { File.CreateSymbolicLink(linked, outside); }
        catch (IOException) { return; } // Creating links needs Developer Mode or elevation, so this cannot run everywhere.
        catch (UnauthorizedAccessException) { return; }
        var feed = new TranscriptFeed(store.DirectoryPath);
        Assert.Equal("safe text", Assert.Single(feed.ReadNew()).Text);
        Assert.Equal(1, feed.UnreadableRecords);
    }

    [Fact]
    public void AJunctionedSegmentsFolderIsRefusedInsteadOfFollowed()
    {
        using var root = new TestDirectory();
        var elsewhere = new TranscriptStore(Path.Combine(root.Path, "elsewhere"));
        elsewhere.Commit(Record(AudioSource.Local, 1, "text behind a junction"));
        var session = Directory.CreateDirectory(Path.Combine(root.Path, "session")).FullName;
        var segments = Path.Combine(session, "segments");
        // Unlike symbolic links, junctions need no special rights, so this check always runs.
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{segments}\" \"{Path.Combine(elsewhere.DirectoryPath, "segments")}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        try
        {
            var feed = new TranscriptFeed(session);
            Assert.Empty(feed.ReadNew());
            Assert.Equal(1, feed.UnreadableRecords);
        }
        finally { Directory.Delete(segments); }
    }

    [Fact]
    public void JsonReadsRefuseALinkedFile()
    {
        using var root = new TestDirectory();
        var outside = Path.Combine(root.Path, "outside.json");
        JsonFiles.Write(outside, new SessionStatus("Complete"));
        var linked = Path.Combine(root.Path, "linked.json");
        try { File.CreateSymbolicLink(linked, outside); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        Assert.Throws<ArgumentException>(() => JsonFiles.Read<SessionStatus>(linked));
    }

    [Fact]
    public void AtomicReplacementOutlastsABriefReaderButNotAStuckOne()
    {
        using var root = new TestDirectory();
        var path = Path.Combine(root.Path, "value.json");
        JsonFiles.Write(path, new SessionStatus("Capturing"));
        var replacement = Path.Combine(root.Path, "replacement");
        File.WriteAllText(replacement, "replaced");
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var retries = 0;
        JsonFiles.MoveReplacing(replacement, path, _ => { retries++; reader.Dispose(); });
        Assert.Equal(1, retries);
        Assert.Equal("replaced", File.ReadAllText(path));

        File.WriteAllText(replacement, "never");
        using var stuck = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        retries = 0;
        Assert.Throws<UnauthorizedAccessException>(() => JsonFiles.MoveReplacing(replacement, path, _ => retries++));
        Assert.Equal(5, retries);
    }

    [Fact]
    public void SessionMetadataRecordsTheAutomaticSummaryChoiceWithoutSpokenContent()
    {
        using var root = new TestDirectory();
        var directory = Directory.CreateDirectory(Path.Combine(root.Path, "session")).FullName;
        SessionMetadata.Write(directory, new SessionMetadata(DateTimeOffset.UtcNow, "Capturing") { AutoSummarize = true, MaxSessionMinutes = 45 });
        var loaded = SessionMetadata.Read(directory);
        Assert.True(loaded.AutoSummarize);
        Assert.Equal(45, loaded.MaxSessionMinutes);
    }

    private static TranscriptRecord Record(AudioSource source, double start, string text) =>
        new(Guid.NewGuid().ToString("N"), source, start, start + 2, [new TranscriptSegment(start, start + 2, text)]);
}
