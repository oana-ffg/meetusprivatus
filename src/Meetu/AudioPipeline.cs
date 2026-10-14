using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace Meetu;

public sealed record AudioPacket(AudioSource Source, byte[] Pcm, double StartSeconds, bool Discontinuity,
    long CapturedTimestamp = 0);
public sealed record AudioClip(string Id, AudioSource Source, string Path, double StartSeconds, int SampleCount, long CreatedTimestamp)
{
    public double EndSeconds => StartSeconds + SampleCount / 16000.0;
}

// Capture-to-assembly handoff. It is bounded by queued audio so short disk or scheduling stalls
// in assembly do not stop capture, and by packet count so tiny packets cannot grow it without limit.
public sealed class PacketQueue
{
    public const int MaximumSeconds = 30;
    public const long MaximumBytes = 16000L * 2 * 2 * MaximumSeconds;
    public const int MaximumPackets = 12_000;
    private readonly Channel<AudioPacket> channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(MaximumPackets)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private long bytes;
    public int Count => channel.Reader.Count;
    public long Bytes => Interlocked.Read(ref bytes);

    // Nonblocking; safe to call from both capture callbacks.
    public bool TryWrite(AudioPacket packet)
    {
        if (Interlocked.Add(ref bytes, packet.Pcm.Length) <= MaximumBytes && channel.Writer.TryWrite(packet)) return true;
        Interlocked.Add(ref bytes, -packet.Pcm.Length);
        return false;
    }

    public void Complete() => channel.Writer.TryComplete();

    public async IAsyncEnumerable<AudioPacket> ReadAllAsync()
    {
        await foreach (var packet in channel.Reader.ReadAllAsync())
        {
            Interlocked.Add(ref bytes, -packet.Pcm.Length);
            yield return packet;
        }
    }
}

public sealed class PendingBudget
{
    public const long MaximumBytes = 16000L * 2 * 2 * 3600;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly Dictionary<string, (long Timestamp, long Bytes)> clips = [];
    private long totalBytes;
    public void Add(string id, long timestamp)
    {
        lock (gate) clips.Add(id, (timestamp, 0));
    }
    public void Reserve(string id, int bytes)
    {
        lock (gate)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            if (!clips.TryGetValue(id, out var clip)) throw new IOException("This audio clip reached its retention limit.");
            if (totalBytes + bytes > MaximumBytes)
                throw new IOException("Pending audio reached its storage limit.");
            clips[id] = (clip.Timestamp, clip.Bytes + bytes);
            totalBytes += bytes;
        }
    }
    public void Remove(string id) { lock (gate) { if (clips.Remove(id, out var clip)) totalBytes -= clip.Bytes; } }
    public void Clear() { lock (gate) { clips.Clear(); totalBytes = 0; } }
    public IReadOnlyList<string> Expire(long now)
    {
        lock (gate)
        {
            var expired = clips.Where(clip => Stopwatch.GetElapsedTime(clip.Value.Timestamp, now) >= MaximumAge - ExpiryMargin)
                .Select(clip => clip.Key).ToArray();
            foreach (var id in expired) { totalBytes -= clips[id].Bytes; clips.Remove(id); }
            return expired;
        }
    }
    public (long Bytes, double AgeSeconds) Snapshot(long now)
    {
        lock (gate) return (totalBytes, clips.Count == 0 ? 0
            : Stopwatch.GetElapsedTime(clips.Values.Min(value => value.Timestamp), now).TotalSeconds);
    }
}

public sealed class ClipWorkspace : IDisposable
{
    public string DirectoryPath { get; }
    private const string Marker = "meetus-owned-audio-v1";

    public ClipWorkspace(string root, string id)
    {
        CleanupAbandoned(root);
        DirectoryPath = Path.Combine(root, id);
        PrivateDirectory.Create(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, Marker), id);
    }

    public static void CleanupAbandoned(string root)
    {
        PrivateDirectory.Create(root);
        // Caller must hold the exclusive worker lock. Only recognizable, marked workspaces are touched.
        foreach (var directory in Directory.EnumerateDirectories(root))
            DeleteOwned(directory);
    }

    private static void DeleteOwned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!Guid.TryParseExact(name, "N", out _) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        var marker = Path.Combine(directory, Marker);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != name) return;
        foreach (var file in Directory.GetFiles(directory, "*.pcm"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) &&
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
        // Unknown files and subdirectories survive; they do not prevent owned audio cleanup.
        if (Directory.EnumerateDirectories(directory).Any() || Directory.GetFiles(directory).Any(path => path != marker)) return;
        File.Delete(marker); Directory.Delete(directory);
    }

    public void Dispose() => DeleteOwned(DirectoryPath);

    public void DeleteAudio(IEnumerable<string> ids)
    {
        // Delete only expiring clips; newer clips may still produce useful text after capture stops.
        foreach (var id in ids)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid clip identity.");
            var path = Path.Combine(DirectoryPath, id + ".pcm");
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
            }
            catch (FileNotFoundException) { /* Recognition already deleted this clip. */ }
        }
    }
}

public sealed class ClipAssembler : IDisposable
{
    private readonly AudioSource source;
    private readonly string workspace;
    private readonly PendingBudget budget;
    private readonly int maximumSamples;
    private readonly Action<AudioClip> emit;
    private readonly Action<TranscriptRecord> gap;
    private FileStream? file;
    private string? id;
    private double start;
    private long created;
    private int samples;
    private int quietSamples;
    private bool receivedPacket;
    private double previousEnd;

    public ClipAssembler(AudioSource source, string workspace, PendingBudget budget, int clipSeconds,
        Action<AudioClip> emit, Action<TranscriptRecord> gap)
    {
        this.source = source; this.workspace = workspace; this.budget = budget;
        maximumSamples = clipSeconds * 16000; this.emit = emit; this.gap = gap;
    }

    public void Append(AudioPacket packet)
    {
        if (packet.Source != source || packet.Pcm.Length % 2 != 0) throw new InvalidDataException("Invalid PCM packet.");
        if (receivedPacket && packet.Discontinuity)
        {
            Finish();
            gap(new TranscriptRecord(Guid.NewGuid().ToString("N"), source, previousEnd,
                Math.Max(previousEnd, packet.StartSeconds), [], "Audio device reported a capture discontinuity."));
        }
        // Loopback can stop delivering packets during silence. Do not compress that time.
        if (file is not null && Math.Abs(packet.StartSeconds - previousEnd) > 0.25) Finish();
        var consumed = 0;
        while (consumed < packet.Pcm.Length)
        {
            if (file is null)
            {
                id = Guid.NewGuid().ToString("N");
                created = packet.CapturedTimestamp > 0
                    ? Math.Min(Stopwatch.GetTimestamp(), packet.CapturedTimestamp + (long)(consumed / 32000.0 * Stopwatch.Frequency))
                    : Stopwatch.GetTimestamp();
                start = Math.Max(0, packet.StartSeconds + consumed / 32000.0);
                budget.Add(id, created);
                file = new FileStream(Path.Combine(workspace, id + ".pcm"), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
            }
            var count = Math.Min(packet.Pcm.Length - consumed, (maximumSamples - samples) * 2);
            budget.Reserve(id!, count);
            var slice = packet.Pcm.AsSpan(consumed, count);
            file.Write(slice);
            for (var index = 0; index < slice.Length; index += 2)
                quietSamples = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(slice[index..])) < 180 ? quietSamples + 1 : 0;
            samples += count / 2; consumed += count;
            if (samples == maximumSamples || (samples >= 8 * 16000 && quietSamples >= 16000 / 2)) Finish();
        }
        previousEnd = packet.StartSeconds + packet.Pcm.Length / 32000.0;
        receivedPacket = true;
    }

    public void Finish()
    {
        if (file is null) return;
        var clip = new AudioClip(id!, source, file.Name, start, samples, created);
        file.Dispose(); file = null; samples = 0; quietSamples = 0;
        emit(clip);
    }

    public void Dispose() { file?.Dispose(); file = null; }
}
