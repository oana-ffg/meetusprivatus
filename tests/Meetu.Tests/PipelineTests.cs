using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using Meetu;
using Xunit;

[assembly: SupportedOSPlatform("windows10.0.22000")]

namespace Meetu.Tests;

public sealed class PipelineTests
{
    [Fact]
    public void SegmentationPreservesEverySampleAcrossArbitraryPackets()
    {
        using var workspace = new TestDirectory();
        var clips = new List<AudioClip>();
        var gaps = new List<TranscriptRecord>();
        var budget = new PendingBudget();
        using var assembler = new ClipAssembler(AudioSource.Local, workspace.Path, budget, 5, clips.Add, gaps.Add);
        var original = new byte[16000 * 2 * 13 + 26];
        var random = new Random(2026); random.NextBytes(original);
        var offset = 0;
        while (offset < original.Length)
        {
            var length = Math.Min((random.Next(1, 1000) * 2), original.Length - offset);
            assembler.Append(new AudioPacket(AudioSource.Local, original[offset..(offset + length)], 10 + offset / 32000.0, false));
            offset += length;
        }
        assembler.Finish();
        Assert.Empty(gaps);
        Assert.Equal(3, clips.Count);
        Assert.All(clips, clip => Assert.InRange(clip.SampleCount, 1, 80000));
        Assert.Equal(original, clips.SelectMany(clip => File.ReadAllBytes(clip.Path)).ToArray());
        Assert.Equal(10, clips[0].StartSeconds);
        Assert.Equal(15, clips[1].StartSeconds, 6);
        Assert.Equal(23 + 13 / 16000.0, clips[^1].EndSeconds, 6);
        Assert.Equal(original.Length, budget.Snapshot(Stopwatch.GetTimestamp()).Bytes);
    }

    [Fact]
    public void SilenceInPlaybackDoesNotCompressSessionTime()
    {
        using var workspace = new TestDirectory();
        var clips = new List<AudioClip>();
        using var assembler = new ClipAssembler(AudioSource.Remote, workspace.Path, new PendingBudget(), 20, clips.Add, _ => { });
        assembler.Append(new AudioPacket(AudioSource.Remote, new byte[32000], 0, false));
        assembler.Append(new AudioPacket(AudioSource.Remote, new byte[32000], 20, false));
        assembler.Finish();
        Assert.Equal(2, clips.Count);
        Assert.Equal(20, clips[1].StartSeconds);
    }

    [Fact]
    public void DiscontinuitySplitsClipAndMarksTheMissingInterval()
    {
        using var workspace = new TestDirectory();
        var clips = new List<AudioClip>(); var gaps = new List<TranscriptRecord>();
        using var assembler = new ClipAssembler(AudioSource.Local, workspace.Path, new PendingBudget(), 20, clips.Add, gaps.Add);
        assembler.Append(new AudioPacket(AudioSource.Local, new byte[32000], 0, false));
        assembler.Append(new AudioPacket(AudioSource.Local, new byte[32000], 3, true));
        assembler.Finish();
        Assert.Equal(2, clips.Count);
        var gap = Assert.Single(gaps);
        Assert.Equal(1, gap.StartSeconds); Assert.Equal(3, gap.EndSeconds); Assert.NotNull(gap.Gap);
    }

    [Fact]
    public async Task PacketQueueHoldsThirtySecondsOfAudioFromBothSourcesAndBoundsPacketCount()
    {
        var queue = new PacketQueue();
        // One-second packets, alternating sources: 30 seconds from each fills the queue exactly.
        for (var second = 0; second < PacketQueue.MaximumSeconds * 2; second++)
            Assert.True(queue.TryWrite(new AudioPacket(second % 2 == 0 ? AudioSource.Local : AudioSource.Remote, new byte[32000], second / 2, false)));
        Assert.Equal(PacketQueue.MaximumBytes, queue.Bytes);
        Assert.False(queue.TryWrite(new AudioPacket(AudioSource.Local, new byte[2], 30, false)));
        Assert.Equal(PacketQueue.MaximumBytes, queue.Bytes);
        await using (var reader = queue.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(PacketQueue.MaximumBytes - 32000, queue.Bytes);
            Assert.True(queue.TryWrite(new AudioPacket(AudioSource.Local, new byte[32000], 30, false)));
        }

        var tiny = new PacketQueue();
        for (var packet = 0; packet < PacketQueue.MaximumPackets; packet++)
            Assert.True(tiny.TryWrite(new AudioPacket(AudioSource.Local, new byte[2], packet / 16000.0, false)));
        Assert.False(tiny.TryWrite(new AudioPacket(AudioSource.Local, new byte[2], 1, false)));
        Assert.Equal(PacketQueue.MaximumPackets * 2L, tiny.Bytes);
    }

    [Fact]
    public void BothSourcesShareStorageAndAgeBudget()
    {
        var budget = new PendingBudget();
        var now = Stopwatch.GetTimestamp();
        budget.Add("local", now - (long)(3599 * (double)Stopwatch.Frequency));
        budget.Add("remote", now);
        budget.Reserve("local", (int)PendingBudget.MaximumBytes - 10);
        budget.Reserve("remote", 10);
        Assert.Throws<IOException>(() => budget.Reserve("remote", 2));
        Assert.InRange(budget.Snapshot(now).AgeSeconds, 3598.99, 3599.01);
        budget.Remove("local");
        Assert.Equal((10L, 0.0), budget.Snapshot(now));
    }

    [Fact]
    public void RetentionExpiryDiscardsOnlyExpiredAudioAndKeepsNewerWork()
    {
        var now = Stopwatch.GetTimestamp();
        var budget = new PendingBudget(); budget.Add("expired", now - (long)(3596 * (double)Stopwatch.Frequency)); budget.Reserve("expired", 32000);
        budget.Add("fresh", now); budget.Reserve("fresh", 16000);
        Assert.Equal("expired", Assert.Single(budget.Expire(now)));
        Assert.Equal((16000L, 0.0), budget.Snapshot(now));
        Assert.Throws<IOException>(() => budget.Reserve("expired", 320));
        budget.Remove("expired"); // Recognition finishing a discarded clip remains harmless.
        Assert.Equal(16000, budget.Snapshot(now).Bytes);
    }

    [Fact]
    public void RetryAndRepairPreserveOneCanonicalTranscriptRecord()
    {
        using var workspace = new TestDirectory();
        var store = new TranscriptStore(System.IO.Path.Combine(workspace.Path, "session"));
        var id = Guid.NewGuid().ToString("N");
        var first = new TranscriptRecord(id, AudioSource.Local, 65, 68, [new(65, 68, "Remember\nthe next step.")]);
        store.Commit(first);
        store.Commit(first with { Segments = [new(65, 68, "Duplicate retry must not replace committed text.")] });
        store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Remote, 66, 69, [new(66, 69, "Overlapping playback.")]));
        File.WriteAllText(System.IO.Path.Combine(store.DirectoryPath, "local.txt"), "interrupted export");
        new TranscriptStore(store.DirectoryPath).Render();
        var text = File.ReadAllText(System.IO.Path.Combine(store.DirectoryPath, "local.txt"));
        Assert.Contains("[00:01:06–00:01:09] Overlapping playback.", File.ReadAllText(System.IO.Path.Combine(store.DirectoryPath, "remote.txt")));
        Assert.Contains("[00:01:05–00:01:08] Remember the next step.", text);
        Assert.DoesNotContain("Duplicate", text);
        Assert.Equal(2, Directory.GetFiles(System.IO.Path.Combine(store.DirectoryPath, "segments"), "*.json").Length);
        Assert.Contains("notes spoken while muted", text);
        Assert.DoesNotContain("Remember", File.ReadAllText(System.IO.Path.Combine(store.DirectoryPath, "remote.txt")));
    }

    [Fact]
    public void CleanupRemovesOwnedAudioButPreservesUnrecognizedFiles()
    {
        using var workspace = new TestDirectory();
        var root = System.IO.Path.Combine(workspace.Path, "audio");
        var owned = new ClipWorkspace(root, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(System.IO.Path.Combine(owned.DirectoryPath, Guid.NewGuid().ToString("N") + ".pcm"), [1, 2]);
        var unrelated = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(System.IO.Path.Combine(unrelated, "important.txt"), "keep");
        ClipWorkspace.CleanupAbandoned(root);
        Assert.False(Directory.Exists(owned.DirectoryPath));
        Assert.Equal("keep", File.ReadAllText(System.IO.Path.Combine(unrelated, "important.txt")));
        var mixed = new ClipWorkspace(root, Guid.NewGuid().ToString("N"));
        var audio = System.IO.Path.Combine(mixed.DirectoryPath, Guid.NewGuid().ToString("N") + ".pcm");
        File.WriteAllBytes(audio, [1, 2]);
        var unknown = System.IO.Path.Combine(mixed.DirectoryPath, "user-added.txt"); File.WriteAllText(unknown, "preserve");
        mixed.Dispose();
        Assert.False(File.Exists(audio)); Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void PcmDecodingHandlesFullSignedRangeAndRejectsTruncation()
    {
        var data = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(data, short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4), short.MaxValue);
        Assert.Equal(new[] { -1f, 0f, 32767f / 32768 }, Recognizer.DecodePcm(data));
        Assert.Throws<InvalidDataException>(() => Recognizer.DecodePcm([1]));
    }

    [Fact]
    public void MalformedControlOptionsFailClearly()
    {
        foreach (var args in new[] { new[] { "--threads" }, new[] { "--threads", "1", "--threads", "2" }, new[] { "--model", "--vad" } })
            Assert.Throws<ArgumentException>(() => Program.ParseOptions(args));
    }

    [Fact]
    public void ConfigurationDoesNotAcceptMissingModelsOrUnboundedWorkers()
    {
        using var workspace = new TestDirectory();
        var model = System.IO.Path.Combine(workspace.Path, "model.bin"); File.WriteAllBytes(model, [0]);
        var config = new AppConfig { OutputDirectory = workspace.Path, ModelPath = model, VadPath = model };
        config.Validate();
        Assert.Throws<ArgumentException>(() => (config with { Threads = 3 }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { ClipSeconds = 31 }).Validate());
        Assert.Equal(60, config.MaxSessionMinutes);
        Assert.Throws<ArgumentException>(() => (config with { MaxSessionMinutes = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { MaxSessionMinutes = 1441 }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { OutputDirectory = "relative" }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { ModelPath = model + ".missing" }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { ModelPath = @"\\network.invalid\models\base.bin" }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { OutputDirectory = @"\\network.invalid\transcripts" }).Validate());
        Assert.Throws<ArgumentException>(() => new StatePaths(@"\\network.invalid\state"));
    }

    [Fact]
    public void MissingNativeClockPreservesBatchedSamplesAndPlaybackSilence()
    {
        var start = Stopwatch.GetTimestamp();
        long At(double seconds) => start + (long)(seconds * Stopwatch.Frequency);
        var clock = new CaptureTimeline(start, 100);
        var first = clock.Stamp(160, 0, true, false, At(1));
        var second = clock.Stamp(160, 0, false, false, At(1));
        var afterSilence = clock.Stamp(160, 0, true, false, At(20));
        Assert.Equal(0.9, first.StartSeconds, 5);
        Assert.Equal(0.91, second.StartSeconds, 5);
        Assert.Equal(19.9, afterSilence.StartSeconds, 5);
        Assert.True(clock.UsedEstimatedTiming);
        Assert.True(first.CapturedTimestamp <= At(0.9));
        Assert.True(second.CapturedTimestamp <= At(0.9));
        var native = new CaptureTimeline(start, 100);
        var timed = native.Stamp(160, (long)(At(2) * (10_000_000.0 / Stopwatch.Frequency)), true, false, At(2.1));
        Assert.Equal(2, timed.StartSeconds, 5);
        Assert.False(native.UsedEstimatedTiming);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
