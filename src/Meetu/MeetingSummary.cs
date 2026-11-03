using System.Text;
using System.Text.Json.Serialization;

namespace Meetu;

public sealed record MeetingActionItem(string Task, string? Owner, string? Due, string? Time);

public sealed record MeetingNotes(string Title, string Overview, IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> Decisions, IReadOnlyList<MeetingActionItem> ActionItems);

[JsonConverter(typeof(JsonStringEnumConverter<SummaryState>))]
public enum SummaryState { Complete, NoSpeech, Failed }

// Canonical per-meeting summary. Absence means "not generated yet"; a file is always a final outcome.
public sealed record MeetingSummary(SummaryState State, DateTimeOffset GeneratedUtc, string? ModelName = null,
    double? ElapsedSeconds = null, MeetingNotes? Notes = null, string? Error = null, string? Device = null);

public static class SummaryFiles
{
    public const string SummaryFile = "summary.json";
    public const string MarkdownFile = "summary.md";

    public static MeetingSummary? Read(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, SummaryFile);
        return File.Exists(path) ? JsonFiles.Read<MeetingSummary>(path) : null;
    }

    public static void Write(string sessionDirectory, MeetingSummary summary)
    {
        JsonFiles.Write(Path.Combine(sessionDirectory, SummaryFile), summary);
        if (summary.Notes is not null)
            JsonFiles.WriteText(Path.Combine(sessionDirectory, MarkdownFile), RenderMarkdown(summary.Notes, summary.ModelName));
    }

    // An earlier good summary is worth more than a record of this attempt failing: it stays, with the reason attached.
    public static void WriteFailure(string sessionDirectory, string? modelName, string error)
    {
        MeetingSummary? existing;
        try { existing = Read(sessionDirectory); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or IOException or ArgumentException) { existing = null; }
        Write(sessionDirectory, existing is { State: SummaryState.Complete }
            ? existing with { Error = error }
            : new MeetingSummary(SummaryState.Failed, DateTimeOffset.UtcNow, modelName, Error: error));
    }

    public static string RenderMarkdown(MeetingNotes notes, string? modelName)
    {
        var text = new StringBuilder();
        text.Append("# ").AppendLine(notes.Title.Length > 0 ? notes.Title : "Meeting notes");
        if (notes.Overview.Length > 0) text.AppendLine().AppendLine(notes.Overview);
        AppendSection(text, "Action items", notes.ActionItems.Select(DescribeActionItem));
        AppendSection(text, "Key points", notes.KeyPoints);
        AppendSection(text, "Decisions", notes.Decisions);
        if (modelName is not null)
            text.AppendLine().Append("_Generated locally by ").Append(modelName).AppendLine(" from a machine transcript. Check important details against the transcript._");
        return text.ToString();
    }

    private static void AppendSection(StringBuilder text, string heading, IEnumerable<string> items)
    {
        var bullets = items.ToList();
        if (bullets.Count == 0) return;
        text.AppendLine().Append("## ").AppendLine(heading).AppendLine();
        foreach (var bullet in bullets) text.Append("- ").AppendLine(bullet);
    }

    private static string DescribeActionItem(MeetingActionItem item)
    {
        var details = new[] { item.Owner, item.Due is null ? null : "due " + item.Due, item.Time }.OfType<string>().ToList();
        return details.Count == 0 ? item.Task : $"{item.Task} ({string.Join(", ", details)})";
    }
}
