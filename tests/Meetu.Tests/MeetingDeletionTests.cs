using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class MeetingDeletionTests
{
    private static string CreateMeeting(string root)
    {
        var directory = Path.Combine(root, $"20260105-090000-{Guid.NewGuid():N}");
        var store = new TranscriptStore(directory);
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 0, 5, [new TranscriptSegment(0, 5, "words")]));
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Remote, 0, 5, [new TranscriptSegment(0, 5, "more words")]));
        SessionMetadata.Write(directory, new SessionMetadata(DateTimeOffset.UtcNow, "Complete"));
        SummaryFiles.Write(directory, new MeetingSummary(SummaryState.Complete, DateTimeOffset.UtcNow, "model", 1, new MeetingNotes("Title", "Overview", [], [], [])));
        return directory;
    }

    [Fact]
    public void AMeetingIsRemovedCompletely()
    {
        using var root = new TestDirectory();
        var directory = CreateMeeting(root.Path);
        var other = CreateMeeting(root.Path);
        File.WriteAllText(Path.Combine(directory, $"summary.json.{Guid.NewGuid():N}.tmp"), "interrupted write");
        File.WriteAllText(Path.Combine(directory, "segments", $"{Guid.NewGuid():N}.json.{Guid.NewGuid():N}.tmp"), "interrupted write");
        Assert.True(MeetingDeletion.Delete(directory));
        Assert.False(Directory.Exists(directory));
        Assert.Equal(other, Assert.Single(MeetingLibrary.List(root.Path, null)).Directory);
    }

    [Fact]
    public void FilesTheAppDidNotWriteAreKeptWithTheirFolder()
    {
        using var root = new TestDirectory();
        var directory = CreateMeeting(root.Path);
        var notes = Path.Combine(directory, "my-notes.docx");
        File.WriteAllText(notes, "mine");
        var inRecords = Path.Combine(directory, "segments", "not-a-record.json");
        File.WriteAllText(inRecords, "mine");
        Assert.False(MeetingDeletion.Delete(directory));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(inRecords));
        Assert.Empty(Directory.GetFiles(directory, "*.txt"));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "segments")));
        // No longer a meeting, so it leaves the list.
        Assert.Empty(MeetingLibrary.List(root.Path, null));
    }

    [Fact]
    public void AnythingThatIsNotAMeetingFolderIsRefused()
    {
        using var root = new TestDirectory();
        var wronglyNamed = Path.Combine(root.Path, "holiday-photos");
        _ = new TranscriptStore(wronglyNamed);
        SessionMetadata.Write(wronglyNamed, new SessionMetadata(DateTimeOffset.UtcNow, "Complete"));
        Assert.Throws<ArgumentException>(() => MeetingDeletion.Delete(wronglyNamed));
        Assert.True(File.Exists(Path.Combine(wronglyNamed, SessionMetadata.FileName)));

        var rightNameOnly = Path.Combine(root.Path, $"20260105-090000-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rightNameOnly);
        var photo = Path.Combine(rightNameOnly, "photo.jpg");
        File.WriteAllBytes(photo, [1]);
        Assert.Throws<ArgumentException>(() => MeetingDeletion.Delete(rightNameOnly));
        Assert.True(File.Exists(photo));
    }

    // The metadata goes last, so a failure part way leaves the meeting listed instead of hiding what remains.
    [Fact]
    public void AFailedDeletionStaysListedAndCanBeRetried()
    {
        using var root = new TestDirectory();
        var directory = CreateMeeting(root.Path);
        var held = new FileStream(Path.Combine(directory, "local.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.ThrowsAny<IOException>(() => MeetingDeletion.Delete(directory));
            Assert.Single(MeetingLibrary.List(root.Path, null));
        }
        finally { held.Dispose(); }
        Assert.True(MeetingDeletion.Delete(directory));
        Assert.False(Directory.Exists(directory));
    }
}
