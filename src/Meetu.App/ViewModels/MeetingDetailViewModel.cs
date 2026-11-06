using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Meetu.App.ViewModels;

public enum SummaryPanel { DuringRecording, Waiting, Generating, Ready, NoSpeech, Failed, ModelMissing, NotGenerated, Unavailable }

public sealed record TranscriptRow(TranscriptLine Line)
{
    public string Time => TranscriptText.Timestamp(Line.StartSeconds);
    public string Text => Line.Text;
    public bool IsMic => Line.Source == AudioSource.Local;
    public bool IsGap => Line.IsGap;
    public string SourceLabel => IsMic ? "Mic" : "Playback";
    public string SourceHint => IsMic
        ? "Your microphone, including anything said while muted in the meeting app."
        : "Everything played on the selected output device, not only the meeting.";
    public string Copyable => $"[{Time}] {SourceLabel}: {(IsGap ? "[Gap] " : "")}{Text}";
}

public sealed record ActionItemRow(MeetingActionItem Item, ICommand Jump);

// Everything shown for the selected meeting. Recreated on selection, so state cannot leak between meetings.
public sealed class MeetingDetailViewModel : Observable
{
    private const string SummaryModelDownloadSize = "about 2.7 GB";
    private double? modelDownloadPercent;

    private readonly TranscriptFeed feed;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Action<string> reportError;
    private readonly Action<string> requestSummary;
    private MeetingInfo info;
    private bool isLive;
    private bool modelAvailable;
    private bool isGenerating;
    private SummaryProgress progress;
    private int selectedTab;
    private bool isLoading = true;
    private bool userChoseTab;

    public MeetingDetailViewModel(MeetingInfo info, bool isLive, bool modelAvailable, bool isGenerating, Action<string> reportError,
        Action<string> requestSummary, Action downloadModel)
    {
        this.info = info;
        this.isLive = isLive;
        this.modelAvailable = modelAvailable;
        this.isGenerating = isGenerating;
        this.reportError = reportError;
        this.requestSummary = requestSummary;
        feed = new TranscriptFeed(info.Directory);
        selectedTab = DefaultTab;
        CopySummaryCommand = new RelayCommand(CopySummary, () => Notes is not null);
        CopyTranscriptCommand = new RelayCommand(CopyTranscript, () => Lines.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        GenerateCommand = new RelayCommand(() => this.requestSummary(this.info.Directory), () => !this.isLive && !this.isGenerating && this.modelAvailable);
        DownloadModelCommand = new RelayCommand(downloadModel);
        JumpToTimeCommand = new RelayCommand<string>(JumpTo);
    }

    public ObservableCollection<TranscriptRow> Lines { get; } = [];
    public event Action<TranscriptRow>? ScrollRequested;

    public ICommand CopySummaryCommand { get; }
    public ICommand CopyTranscriptCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand DownloadModelCommand { get; }
    public ICommand JumpToTimeCommand { get; }

    public string Directory => info.Directory;
    public string Title => DisplayText.Title(info);
    public string Subtitle => $"{DisplayText.FullDate(info)} · {DisplayText.StartAndDuration(info)}{(info.SpeechModel is { } model ? $" · transcribed by {model}" : "")}";
    public bool IsLive => isLive;
    public string? Problem => isLive ? null : info.Error ?? (info.State == MeetingLibrary.InterruptedState
        ? "This recording ended unexpectedly. Text saved before that point is kept; some speech may be missing."
        : null);

    private int unreadableRecords;

    public bool ShowTranscriptPlaceholder => !isLoading && Lines.Count == 0;
    public string TranscriptPlaceholder => isLive
        ? "Listening. Text appears here after each short clip is transcribed, usually within 20 to 40 seconds."
        : "No speech was transcribed in this meeting.";

    public int SelectedTab
    {
        get => selectedTab;
        set { if (Set(ref selectedTab, value)) userChoseTab = true; }
    }

    private int DefaultTab => info.Summary?.State == SummaryState.Complete ? 0 : isLive ? 1 : 0;

    public MeetingNotes? Notes => info.Summary?.Notes;
    public IReadOnlyList<ActionItemRow> ActionItems => Notes?.ActionItems.Select(item => new ActionItemRow(item, JumpToTimeCommand)).ToList() ?? [];
    public string ActionItemsHeading => $"Action items ({ActionItems.Count})";
    public string ModelNote => info.Summary is { ModelName: { } model } summary
        ? $"Generated on this PC by {model}{(summary.ElapsedSeconds is { } seconds ? $" in {DisplayText.Duration(seconds)}" : "")}{(summary.Device is { } device ? $" using the {device}" : "")}. It can get things wrong, so check anything important against the transcript."
        : "";
    public string? SummaryError => info.Summary?.Error;
    public string? RegenerationProblem => info.Summary is { State: SummaryState.Complete, Error: { } error } ? $"The last attempt to regenerate failed, so this is the earlier summary. {error}" : null;

    public SummaryPanel Panel
    {
        get
        {
            if (isLive) return SummaryPanel.DuringRecording;
            if (isGenerating) return SummaryPanel.Generating;
            if (info.Summary is { } summary)
                return summary.State switch
                {
                    SummaryState.Complete => SummaryPanel.Ready,
                    SummaryState.NoSpeech => SummaryPanel.NoSpeech,
                    _ => SummaryPanel.Failed
                };
            if (info.State == MeetingLibrary.UnreadableState) return SummaryPanel.Unavailable;
            if (!modelAvailable) return SummaryPanel.ModelMissing;
            return info.AutoSummarize && info.State is "Complete" or "Failed" ? SummaryPanel.Waiting : SummaryPanel.NotGenerated;
        }
    }

    public bool ShowSummary => Panel == SummaryPanel.Ready;
    public bool ShowProgress => Panel == SummaryPanel.Generating;
    public bool ShowDownload => Panel == SummaryPanel.ModelMissing;
    public bool ShowGenerate => Panel is SummaryPanel.NoSpeech or SummaryPanel.Failed or SummaryPanel.NotGenerated;
    public string GenerateLabel => Panel == SummaryPanel.NotGenerated ? "Generate summary" : "Try again";
    public bool IsDownloadingModel => modelDownloadPercent is not null;
    public bool ShowDownloadButton => ShowDownload && !IsDownloadingModel;
    public double ModelDownloadPercent => modelDownloadPercent ?? 0;
    public string ModelDownloadText => $"Downloading the summary model: {modelDownloadPercent:0}%. You can keep using the app.";
    public string DownloadLabel => $"Download the summary model ({SummaryModelDownloadSize})";
    public string SummaryMessage => Panel switch
    {
        SummaryPanel.DuringRecording => "Your summary and action items will appear here after you stop recording. Nothing is summarized during a meeting, to keep your PC responsive.",
        SummaryPanel.Waiting => "This meeting is waiting for its summary. It starts as soon as nothing else is running.",
        SummaryPanel.Generating => ProgressText,
        SummaryPanel.NoSpeech => "There was too little speech in this meeting to summarize.",
        SummaryPanel.Failed => SummaryError ?? "The summary could not be generated.",
        SummaryPanel.ModelMissing => "Summaries use a local AI model, which is a one-time download. The model runs entirely on this PC, and your meetings never leave it. The transcript is available either way.",
        SummaryPanel.NotGenerated => "This meeting has no summary yet.",
        SummaryPanel.Unavailable => "This meeting's details could not be read, so it cannot be summarized.",
        _ => ""
    };

    public double ProgressPercent => progress.Total == 0 ? 0 : 100.0 * progress.Completed / progress.Total;
    public bool ProgressIsIndeterminate => progress.Total == 0;
    public string ProgressText => progress.Total <= 1
        ? "Reading the transcript and writing the summary. This runs on your PC and can take a while. You can keep using it."
        : $"Working through the meeting: step {Math.Min(progress.Completed + 1, progress.Total)} of {progress.Total}. A long meeting can take 15 minutes or more. You can keep using your PC.";

    public void Update(MeetingInfo updated, bool live, bool model, bool generating)
    {
        var wasLive = isLive;
        info = updated;
        isLive = live;
        modelAvailable = model;
        isGenerating = generating;
        if (!generating) progress = default;
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(IsLive), nameof(Problem), nameof(TranscriptPlaceholder), nameof(Notes), nameof(ActionItems),
            nameof(ActionItemsHeading), nameof(ModelNote), nameof(SummaryError), nameof(RegenerationProblem), nameof(Panel), nameof(ProgressPercent), nameof(ProgressIsIndeterminate), nameof(ProgressText), nameof(ShowSummary), nameof(ShowProgress),
            nameof(ShowDownload), nameof(ShowDownloadButton), nameof(ShowGenerate), nameof(GenerateLabel), nameof(SummaryMessage), nameof(ShowTranscriptPlaceholder) }) Raise(name);
        // The summary is what you came back for, unless you already went somewhere else.
        if (!userChoseTab && (wasLive != live || updated.Summary is not null) && DefaultTab != selectedTab)
        {
            selectedTab = DefaultTab;
            Raise(nameof(SelectedTab));
        }
    }

    public void ReportModelDownload(double? percent)
    {
        modelDownloadPercent = percent;
        Raise(nameof(IsDownloadingModel));
        Raise(nameof(ShowDownloadButton));
        Raise(nameof(ModelDownloadPercent));
        Raise(nameof(ModelDownloadText));
    }

    public void ReportProgress(SummaryProgress update)
    {
        progress = update;
        Raise(nameof(ProgressPercent));
        Raise(nameof(ProgressIsIndeterminate));
        Raise(nameof(ProgressText));
        Raise(nameof(SummaryMessage));
    }

    // Disk work runs off the UI thread; the sorted merge happens on it, in one small step per poll.
    public async Task RefreshTranscriptAsync()
    {
        // Selecting a live meeting and the next status tick can overlap; each record must still appear once.
        await refreshGate.WaitAsync();
        try
        {
            var added = await Task.Run(feed.ReadNew);
            foreach (var line in added) InsertInOrder(new TranscriptRow(line));
        }
        finally { refreshGate.Release(); }
        isLoading = false;
        Raise(nameof(ShowTranscriptPlaceholder));
        // Command availability depends on what just loaded, not on any user input.
        CommandManager.InvalidateRequerySuggested();
        if (feed.UnreadableRecords != unreadableRecords)
        {
            unreadableRecords = feed.UnreadableRecords;
            if (unreadableRecords > 0) reportError($"{unreadableRecords} saved transcript records could not be read, so this view may be missing text.");
        }
    }

    // Two sources finish clips at different times, so a new line is not always the latest one.
    private void InsertInOrder(TranscriptRow row)
    {
        var index = Lines.Count;
        while (index > 0 && Lines[index - 1].Line.StartSeconds > row.Line.StartSeconds) index--;
        Lines.Insert(index, row);
    }

    private void JumpTo(string? time)
    {
        var row = Lines.FirstOrDefault(line => line.Time == time);
        if (row is null) return;
        SelectedTab = 1;
        ScrollRequested?.Invoke(row);
    }

    private void CopySummary()
    {
        if (Notes is { } notes) Copy(SummaryFiles.RenderMarkdown(notes, info.Summary?.ModelName));
    }

    private void CopyTranscript() => Copy(RenderLines(Lines));

    public static string RenderLines(IEnumerable<TranscriptRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows) text.AppendLine(row.Copyable);
        return text.ToString();
    }

    public void Copy(string text)
    {
        // The clipboard can be briefly held by another application.
        try { Clipboard.SetDataObject(text, true); }
        catch (System.Runtime.InteropServices.ExternalException) { reportError("Windows would not let the app use the clipboard just now. Try copying again."); }
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(info.Directory) }, UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception exception) { reportError($"Could not open the folder ({exception.NativeErrorCode})."); }
    }
}
