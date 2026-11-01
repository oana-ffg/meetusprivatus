using System.Text;
using Meetu;
using Xunit;
using Xunit.Abstractions;

namespace Meetu.Tests;

public sealed class TranscriptStoreTests(ITestOutputHelper output)
{
    [Fact]
    public void OneHourSyntheticMeetingDoesNotRewriteViewsForSilentClips()
    {
        using var directory = new TestDirectory();
        var writes = new List<int>();
        var store = CreateStore(directory.Path, (path, text) =>
        {
            writes.Add(Encoding.UTF8.GetByteCount(text));
            JsonFiles.WriteText(path, text);
        });
        // First half-hour: a 20-second speech clip per source. Second half: an
        // eight-second silent clip per source, matching the assembler's quiet cutoff.
        var syntheticText = string.Join(' ', Enumerable.Repeat("synthetic", 32));
        foreach (var source in Enum.GetValues<AudioSource>())
            for (var clip = 0; clip < 90; clip++)
            {
                var start = clip * 20;
                store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), source, start, start + 20,
                    [new(start, start + 20, syntheticText)]));
            }
        output.WriteLine($"Speech half-hour: {writes.Count - 2} view writes/flushes/replacements, {writes.Skip(2).Sum(bytes => (long)bytes)} UTF-8 view bytes (excluding initial headers).");
        var before = new[] { "local.txt", "remote.txt" }.Select(name => File.ReadAllText(Path.Combine(store.DirectoryPath, name))).ToArray();
        writes.Clear();
        foreach (var source in Enum.GetValues<AudioSource>())
            for (var clip = 0; clip < 225; clip++)
            {
                var start = 1800 + clip * 8;
                store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), source, start, start + 8, []));
            }
        output.WriteLine($"Silent half-hour: {writes.Count} view writes/flushes/replacements, {writes.Sum(bytes => (long)bytes)} UTF-8 view bytes.");
        output.WriteLine($"Canonical records: {Directory.GetFiles(Path.Combine(store.DirectoryPath, "segments"), "*.json").Length}.");
        Assert.Empty(writes);
        Assert.Equal(630, Directory.GetFiles(Path.Combine(store.DirectoryPath, "segments"), "*.json").Length);
        Assert.Equal(before, new[] { "local.txt", "remote.txt" }.Select(name => File.ReadAllText(Path.Combine(store.DirectoryPath, name))).ToArray());
    }

    [Fact]
    public void WhitespaceIsInvisibleButGapsAndSpeechStillReplaceOnlyTheirOwnView()
    {
        using var directory = new TestDirectory();
        var writes = new List<string>();
        var store = CreateStore(directory.Path, (path, text) => { writes.Add(Path.GetFileName(path)); JsonFiles.WriteText(path, text); });
        writes.Clear();
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 0, 8, [new(0, 8, " \t\r\n ")]));
        Assert.Empty(writes);
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Remote, 8, 16, [], "Synthetic gap."));
        Assert.Equal(new[] { "remote.txt" }, writes);
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 16, 24, [new(16, 24, "Synthetic speech.")]));
        Assert.Equal(new[] { "remote.txt", "local.txt" }, writes);
        Assert.Contains("[GAP: Synthetic gap.]", File.ReadAllText(Path.Combine(store.DirectoryPath, "remote.txt")));
        Assert.Contains("Synthetic speech.", File.ReadAllText(Path.Combine(store.DirectoryPath, "local.txt")));
    }

    [Fact]
    public void FailedViewReplacementIsRetriedByNextSilentClip()
    {
        using var directory = new TestDirectory();
        var fail = false;
        var writes = 0;
        var store = CreateStore(directory.Path, (path, text) =>
        {
            writes++;
            if (fail) throw new IOException("Synthetic storage failure.");
            JsonFiles.WriteText(path, text);
        });
        var record = new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 0, 20, [new(0, 20, "Synthetic committed speech.")]);
        fail = true;
        Assert.Throws<IOException>(() => store.Commit(record));
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "segments", record.Id + ".json")));
        fail = false;
        writes = 0;
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 20, 28, []));
        Assert.Equal(1, writes);
        Assert.Contains("Synthetic committed speech.", File.ReadAllText(Path.Combine(store.DirectoryPath, "local.txt")));
        writes = 0;
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 28, 36, []));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void RetryAndExplicitRepairStillRebuildSortedViewsFromCanonicalRecords()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory.Path, JsonFiles.WriteText);
        var later = new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 20, 40, [new(20, 40, "Synthetic later.")]);
        store.Commit(later);
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Local, 0, 20, [new(0, 20, "Synthetic earlier.")]));
        var path = Path.Combine(store.DirectoryPath, "local.txt");
        var expected = File.ReadAllText(path);
        Assert.True(expected.IndexOf("Synthetic earlier.", StringComparison.Ordinal) < expected.IndexOf("Synthetic later.", StringComparison.Ordinal));
        File.WriteAllText(path, "Synthetic interrupted export.");
        store.Commit(later with { Segments = [] });
        Assert.Equal(expected, File.ReadAllText(path));
        File.Delete(path);
        store.Render();
        Assert.Equal(expected, File.ReadAllText(path));
        File.WriteAllText(path, "Synthetic interrupted export.");
        _ = new TranscriptStore(store.DirectoryPath);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(store.DirectoryPath, "segments"), "*.json").Length);
    }

    private static TranscriptStore CreateStore(string root, Action<string, string> writeView)
    {
        var session = Path.Combine(root, "session");
        // Pre-existing local directories also let these storage-only tests run on macOS;
        // Windows directory permission setup is exercised by the full behavioral suite.
        Directory.CreateDirectory(Path.Combine(session, "segments"));
        return new TranscriptStore(session, writeView);
    }
}
