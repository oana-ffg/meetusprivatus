using System.Text;
using System.Text.Json;

namespace Meetu;

public enum AudioSource { Local, Remote }
public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text);
public sealed record TranscriptRecord(string Id, AudioSource Source, double StartSeconds, double EndSeconds,
    IReadOnlyList<TranscriptSegment> Segments, string? Gap = null);

public sealed class TranscriptStore
{
    private readonly string recordsDirectory;
    // Canonical records are read once; each commit then rewrites only its own source's text view.
    private readonly Dictionary<string, TranscriptRecord> records;
    public string DirectoryPath { get; }

    public TranscriptStore(string directory)
    {
        DirectoryPath = directory;
        PrivateDirectory.Create(directory);
        recordsDirectory = Path.Combine(directory, "segments");
        PrivateDirectory.Create(recordsDirectory);
        records = System.IO.Directory.EnumerateFiles(recordsDirectory, "*.json")
            .Select(path => JsonSerializer.Deserialize<TranscriptRecord>(File.ReadAllText(path), JsonFiles.Options)
                ?? throw new InvalidDataException("Invalid transcript record."))
            .ToDictionary(record => record.Id, StringComparer.Ordinal);
        Render();
    }

    public void Commit(TranscriptRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _)) throw new ArgumentException("Invalid record identity.");
        // A committed clip is canonical. Replaying it only repairs the derived text view.
        if (!records.ContainsKey(record.Id))
        {
            JsonFiles.Write(Path.Combine(recordsDirectory, record.Id + ".json"), record);
            records.Add(record.Id, record);
        }
        Render(record.Source);
    }

    public void Render()
    {
        foreach (var source in Enum.GetValues<AudioSource>()) Render(source);
    }

    private void Render(AudioSource source)
    {
        var text = new StringBuilder(source == AudioSource.Local
            ? "Microphone speech (includes notes spoken while muted in the meeting app).\n\n"
            : "Output device playback (remote speakers grouped together; includes audio from other apps on this device).\n\n");
        foreach (var record in records.Values.Where(record => record.Source == source)
            .OrderBy(record => record.StartSeconds).ThenBy(record => record.Id, StringComparer.Ordinal))
        {
            if (record.Gap is not null)
                text.AppendLine($"[{Timestamp(record.StartSeconds)}–{Timestamp(record.EndSeconds)}] [GAP: {record.Gap}]");
            foreach (var segment in record.Segments)
            {
                var clean = string.Join(' ', segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (clean.Length > 0)
                    text.AppendLine($"[{Timestamp(segment.StartSeconds)}–{Timestamp(segment.EndSeconds)}] {clean}");
            }
        }
        JsonFiles.WriteText(Path.Combine(DirectoryPath, source == AudioSource.Local ? "local.txt" : "remote.txt"), text.ToString());
    }

    private static string Timestamp(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
