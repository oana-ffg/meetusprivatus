using System.Globalization;

namespace Meetu.App.ViewModels;

// One row in the sidebar.
public sealed class MeetingItem(MeetingInfo info, DateTime today) : Observable
{
    private MeetingInfo info = info;

    public MeetingInfo Info
    {
        get => info;
        set
        {
            info = value;
            Raise();
            Raise(nameof(Title));
            Raise(nameof(Subtitle));
            Raise(nameof(Badge));
            Raise(nameof(CanDelete));
        }
    }

    public string Directory => info.Directory;
    public string Title => DisplayText.Title(info);
    public string Subtitle => DisplayText.StartAndDuration(info);
    public string? Badge => info.State switch
    {
        "Capturing" or "Starting" => "Recording",
        "Finishing" => "Finishing",
        "Failed" => "Incomplete",
        MeetingLibrary.InterruptedState => "Interrupted",
        MeetingLibrary.UnreadableState => "Unreadable",
        _ => null
    };

    public bool CanDelete => info.State is not ("Capturing" or "Starting" or "Finishing");

    // Message boxes are separate windows, which the screen-sharing exclusion does not cover. So this names
    // the meeting by its time only: its title comes from the transcript and could give away what was said.
    public string DeleteConfirmation =>
        $"Permanently delete the meeting from {DisplayText.FullDate(info)} at {Subtitle}?\n\nIts transcript and summary are removed from this PC and do not go to the Recycle Bin. This cannot be undone.";

    public string DateGroup { get; } = DisplayText.DateGroup(info.StartedUtc.ToLocalTime().Date, today);
}

internal static class DisplayText
{
    public static string Title(MeetingInfo info) =>
        info.Summary?.Notes is { Title.Length: > 0 } notes ? notes.Title : "Meeting at " + info.StartedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public static string StartAndDuration(MeetingInfo info)
    {
        var start = info.StartedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        return info.CaptureSeconds is { } seconds ? $"{start} · {Duration(seconds)}" : start;
    }

    public static string FullDate(MeetingInfo info) =>
        info.StartedUtc.ToLocalTime().ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    public static string DateGroup(DateTime date, DateTime today)
    {
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        return date.ToString(date.Year == today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} sec";
        return span.TotalHours < 1 ? $"{Math.Round(span.TotalMinutes)} min" : $"{(int)span.TotalHours} hr {span.Minutes} min";
    }
}
