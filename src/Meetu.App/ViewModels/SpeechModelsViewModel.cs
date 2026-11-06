using System.Globalization;
using System.IO;
using System.Windows.Input;

namespace Meetu.App.ViewModels;

// One transcription model in Settings: what is on disk, and the download running for it, if any.
public sealed class SpeechModelRow : Observable
{
    private SpeechModelState state;
    private double? downloadPercent;
    private string? message;

    internal SpeechModelRow(SpeechModel model, SpeechModelsViewModel owner)
    {
        Model = model;
        DownloadCommand = new RelayCommand(() => owner.Download(this), () => owner.CanStartDownload);
        StopCommand = new RelayCommand(owner.StopDownload);
    }

    public SpeechModel Model { get; }
    public ICommand DownloadCommand { get; }
    public ICommand StopCommand { get; }

    public string Name => Model.Name;
    public string Description => Model.Description;
    public bool IsInstalled => state == SpeechModelState.Installed;
    public bool IsDownloading => downloadPercent is not null;
    public double DownloadPercent => downloadPercent ?? 0;
    public bool ShowDownload => !Model.IsIncluded && !IsInstalled && !IsDownloading;
    public bool ShowDelete => !Model.IsIncluded && state != SpeechModelState.Missing && !IsDownloading;
    public string DownloadLabel => state == SpeechModelState.Partial ? "Resume" : "Download";
    public string? Message { get => message; internal set => Set(ref message, value); }

    public string Status
    {
        get
        {
            if (IsDownloading) return $"Downloading: {downloadPercent:0}% of {Size}";
            if (Model.IsIncluded) return IsInstalled ? "Included" : "Missing from this installation";
            return state switch
            {
                SpeechModelState.Installed => $"Downloaded · {Size}",
                SpeechModelState.Partial => $"Partly downloaded · {Size}",
                _ => $"Not downloaded · {Size}"
            };
        }
    }

    private string Size => Model.DownloadBytes >= 1_000_000_000
        ? (Model.DownloadBytes / 1_000_000_000.0).ToString("0.0 GB", CultureInfo.CurrentCulture)
        : (Model.DownloadBytes / 1_000_000.0).ToString("0 MB", CultureInfo.CurrentCulture);

    internal void Show(SpeechModelState updatedState, double? updatedPercent)
    {
        state = updatedState;
        downloadPercent = updatedPercent;
        foreach (var name in new[] { nameof(IsInstalled), nameof(IsDownloading), nameof(DownloadPercent), nameof(ShowDownload), nameof(ShowDelete), nameof(DownloadLabel), nameof(Status) }) Raise(name);
    }
}

// Lives as long as the app, so a download keeps going, and keeps reporting, after Settings closes. Files change at once:
// only which model the next recording uses waits for Save. Nothing here runs or stays running during a recording.
public sealed class SpeechModelsViewModel : Observable
{
    internal delegate Task<HelperResult> DownloadRunner(SpeechModel model, Action<double> reportPercent);

    private const string PausedForRecording = "Paused for the recording. Press Resume to continue.";
    private readonly StatePaths paths;
    private readonly string modelsDirectory;
    private readonly DownloadRunner runDownload;
    private readonly Action stopDownload;
    private readonly Func<bool> networkBusy;
    private readonly Action<string> reportError;
    private SpeechModelRow? active;
    private SpeechModelRow? deleting;
    private bool stopRequested;
    private bool pausing;

    internal SpeechModelsViewModel(StatePaths paths, string modelsDirectory, DownloadRunner runDownload, Action stopDownload, Func<bool> networkBusy, Action<string> reportError)
    {
        this.paths = paths;
        this.modelsDirectory = modelsDirectory;
        this.runDownload = runDownload;
        this.stopDownload = stopDownload;
        this.networkBusy = networkBusy;
        this.reportError = reportError;
        Rows = SpeechModels.All.Select(model => new SpeechModelRow(model, this)).ToList();
        Refresh();
    }

    public IReadOnlyList<SpeechModelRow> Rows { get; }
    public string? ActiveDownloadText => active?.IsDownloading == true ? $"{active.Name}: {active.Status}" : null;
    internal bool CanStartDownload => active is null && deleting is null && !networkBusy();

    // Also picks up files placed in the folder by hand, which is how offline import works.
    public void Refresh()
    {
        foreach (var row in Rows)
            if (row != active) row.Show(Inspect(row), null);
    }

    // A models folder that is a link or on a network share is refused before anything inside it is looked at.
    private SpeechModelState Inspect(SpeechModelRow row)
    {
        try { return SpeechModelFiles.GetState(row.Model, modelsDirectory); }
        catch (ArgumentException exception)
        {
            row.Message = exception.Message;
            return SpeechModelState.Missing;
        }
    }

    internal async void Download(SpeechModelRow row)
    {
        if (!CanStartDownload) return;
        active = row;
        stopRequested = false;
        row.Message = null;
        row.Show(Inspect(row), 0);
        Raise(nameof(ActiveDownloadText));
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await Task.Run(() => RecordingControl.IsWorkerRunning(paths)))
            {
                row.Message = "A recording is running. Download when it has finished, so the meeting keeps its bandwidth.";
                return;
            }
            // Stop, or a recording starting, during that wait must still prevent the download.
            if (stopRequested)
            {
                if (pausing) row.Message = PausedForRecording;
                return;
            }
            // An update check could have started during that wait, and only one helper runs at a time.
            if (networkBusy())
            {
                row.Message = "Another download or update check is running. Try again when it has finished.";
                return;
            }
            var result = await runDownload(row.Model, percent =>
            {
                // A progress report can arrive after the download ended.
                if (active != row) return;
                row.Show(Inspect(row), percent);
                Raise(nameof(ActiveDownloadText));
            });
            if (pausing) row.Message = PausedForRecording;
            else if (result.ExitCode != 0 && !result.Cancelled) Fail(row, $"{row.Name} did not download. {result.Error ?? "It stopped unexpectedly."} Anything already fetched is kept, so trying again continues from there.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Fail(row, $"{row.Name} did not download ({exception.GetType().Name}). Anything already fetched is kept, so trying again continues from there.");
        }
        finally
        {
            active = null;
            pausing = false;
            stopRequested = false;
            row.Show(Inspect(row), null);
            Raise(nameof(ActiveDownloadText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // Shown beside the model and in the main window, because Settings may have closed since the download started.
    private void Fail(SpeechModelRow row, string text)
    {
        row.Message = text;
        reportError(text);
    }

    internal void StopDownload()
    {
        if (active is null) return;
        stopRequested = true;
        stopDownload();
    }

    // A large download competes with the meeting for bandwidth. The partial file means nothing is lost.
    internal void PauseForRecording()
    {
        if (active is null) return;
        pausing = true;
        StopDownload();
    }

    // Returns a reason when the model stays. The chosen and saved models are checked by the caller's view of them.
    internal async Task<string?> DeleteAsync(SpeechModelRow row)
    {
        if (row.Model.IsIncluded) return "This model is included with the app.";
        if (row == active) return "This model is downloading. Stop the download first.";
        if (deleting is not null) return "Another model is being deleted. Try again in a moment.";
        // Held until the files are gone, so a download cannot start on them meanwhile.
        deleting = row;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await Task.Run(() => RecordingControl.IsWorkerRunning(paths))) return "A recording is running. Delete the model when it has finished.";
            await Task.Run(() => SpeechModelFiles.Delete(row.Model, modelsDirectory));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{row.Name} could not be fully deleted ({exception.GetType().Name}). Close anything using its files and try again.";
        }
        finally
        {
            deleting = null;
            CommandManager.InvalidateRequerySuggested();
            Refresh();
        }
    }
}
