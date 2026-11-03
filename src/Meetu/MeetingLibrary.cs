using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meetu;

public sealed record TranscriptLine(double StartSeconds, double EndSeconds, AudioSource Source, string Text, bool IsGap);

public sealed record MeetingInfo(string Directory, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, double? CaptureSeconds,
    string State, string? Error, bool AutoSummarize, MeetingSummary? Summary);

// Read-only view over session directories. Nothing here touches audio or changes what a worker wrote.
public static partial class MeetingLibrary
{
    public const string InterruptedState = "Interrupted";
    public const string UnreadableState = "Unreadable";

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{32}$")]
    private static partial Regex SessionDirectoryName();

    public static bool IsSessionDirectory(string directory) =>
        File.Exists(Path.Combine(directory, SessionMetadata.FileName)) && Directory.Exists(Path.Combine(directory, "segments"));

    // Newest first. activeDirectory is the session a live worker reports; any other session that
    // never reached a final state was interrupted, so its saved "Capturing" must not be repeated.
    public static IReadOnlyList<MeetingInfo> List(string outputDirectory, string? activeDirectory)
    {
        LocalPath.Require(outputDirectory);
        if (!Directory.Exists(outputDirectory)) return [];
        var meetings = new List<MeetingInfo>();
        foreach (var directory in new DirectoryInfo(outputDirectory).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || !SessionDirectoryName().IsMatch(directory.Name)
                || !IsSessionDirectory(directory.FullName)) continue;
            var active = string.Equals(directory.FullName, activeDirectory, StringComparison.OrdinalIgnoreCase);
            meetings.Add(Describe(directory, active));
        }
        // A destination changed during a recording applies to the next one, so the live meeting stays listed.
        if (activeDirectory is not null && !meetings.Any(meeting => string.Equals(meeting.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase))
            && IsSessionDirectory(activeDirectory))
            meetings.Add(Describe(new DirectoryInfo(activeDirectory), true));
        return meetings.OrderByDescending(meeting => meeting.StartedUtc).ToList();
    }

    private static MeetingInfo Describe(DirectoryInfo directory, bool active)
    {
        try
        {
            var metadata = SessionMetadata.Read(directory.FullName);
            var final = metadata.State is "Complete" or "Failed";
            return new MeetingInfo(directory.FullName, metadata.StartedUtc, metadata.EndedUtc, metadata.CaptureSeconds,
                final || active ? metadata.State : InterruptedState, metadata.Error, metadata.AutoSummarize, SummaryFiles.Read(directory.FullName));
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            // Listed rather than hidden: its text may still exist, and the user should know it needs attention.
            return new MeetingInfo(directory.FullName, directory.CreationTimeUtc, null, null, UnreadableState,
                $"Meeting details could not be read ({exception.GetType().Name}).", false, null);
        }
    }

    // Requested at capture time, so sessions predating this feature are never summarised without being asked.
    public static IReadOnlyList<MeetingInfo> PendingAutomaticSummaries(IEnumerable<MeetingInfo> meetings) =>
        meetings.Where(meeting => meeting.AutoSummarize && meeting.Summary is null && meeting.State is "Complete" or "Failed").ToList();
}

// Incrementally reads committed records while a worker adds them. Records are never replaced once
// committed, so each file is read exactly once and the reader cannot disturb the writer.
public sealed partial class TranscriptFeed(string sessionDirectory)
{
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);

    public int UnreadableRecords => unreadable.Count;

    // Whisper's whole-segment annotations such as [BLANK_AUDIO] or (keyboard clicking) are not speech.
    [GeneratedRegex(@"^(\[[^\]]*\]|\([^)]*\)|[♪♫\s]+)$")]
    private static partial Regex NonSpeechAnnotation();

    public IReadOnlyList<TranscriptLine> ReadNew()
    {
        var lines = new List<TranscriptLine>();
        var directory = Path.Combine(sessionDirectory, "segments");
        try { LocalPath.Require(directory); }
        catch (ArgumentException)
        {
            unreadable.Add(directory);
            return lines;
        }
        if (!Directory.Exists(directory)) return lines;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (seen.Contains(path)) continue;
            try
            {
                // A link could lead anywhere, including a network share, so it is refused before being opened.
                LocalPath.Require(path);
                lines.AddRange(ToLines(TranscriptStore.ReadRecord(path)));
                seen.Add(path);
                unreadable.Remove(path);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Retried on the next read in case it was transient; the count stays visible until then.
                unreadable.Add(path);
            }
        }
        lines.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
        return lines;
    }

    private static IEnumerable<TranscriptLine> ToLines(TranscriptRecord record)
    {
        if (record.Gap is not null)
            yield return new TranscriptLine(record.StartSeconds, record.EndSeconds, record.Source, record.Gap, true);
        foreach (var segment in record.Segments)
        {
            var text = TranscriptText.Normalize(segment.Text);
            if (text.Length > 0 && !NonSpeechAnnotation().IsMatch(text))
                yield return new TranscriptLine(segment.StartSeconds, segment.EndSeconds, record.Source, text, false);
        }
    }
}
