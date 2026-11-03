using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Meetu.App.ViewModels;

public enum RecordingPhase { Idle, Starting, Recording, Finishing }

public sealed class MainViewModel : Observable
{
    private const int IdlePollEverySeconds = 5;
    private const int PcmBytesPerSecond = 32000;
    private const int UpdateHandoffAttempts = 10;
    private static readonly TimeSpan UpdateHandoffDelay = TimeSpan.FromMilliseconds(200);

    private readonly StatePaths paths;
    private readonly SummaryRunner summaries;
    private readonly HelperProcess network = new();
    private readonly AppUpdates updates;
    private readonly string? updateFeed;
    private readonly IReadOnlyList<string> launchArguments;
    private readonly DispatcherTimer timer;
    private readonly List<string> requestedSummaries = [];
    private RecordingPhase phase;
    private string? activeDirectory;
    private DateTimeOffset activeStartedUtc;
    private int? activeMaxMinutes;
    private string? generatingDirectory;
    private bool polling;
    private bool startInProgress;
    private int secondsSinceIdlePoll = IdlePollEverySeconds;
    private bool modelAvailable;
    private double? modelDownloadPercent;
    private string updateText = "";
    private bool updateIsReady;
    private MeetingItem? selectedMeeting;
    private MeetingDetailViewModel? detail;
    private string? banner;
    private string? microphoneName;
    private string? playbackName;

    public MainViewModel(StatePaths paths, string? updateFeed = null, IReadOnlyList<string>? launchArguments = null)
    {
        this.paths = paths;
        this.updateFeed = updateFeed;
        this.launchArguments = launchArguments ?? [];
        summaries = new SummaryRunner(paths);
        updates = new AppUpdates(updateFeed);
        if (updates.PendingVersion is { } pending) SetUpdateReady(pending);
        MeetingsView = new ListCollectionView(Meetings);
        MeetingsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MeetingItem.DateGroup)));
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await TickAsync();
        ToggleRecordingCommand = new RelayCommand(() => _ = ToggleRecordingAsync(), () => phase is RecordingPhase.Idle or RecordingPhase.Recording);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        UpdateCommand = new RelayCommand(() => RunSafely(OnUpdatePressedAsync()), () => !network.IsRunning);
    }

    public ObservableCollection<MeetingItem> Meetings { get; } = [];
    public ICollectionView MeetingsView { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand UpdateCommand { get; }

    public bool ShowUpdates => updates.IsInstalled;
    public string VersionText => $"Version {updates.CurrentVersion}";
    public string UpdateText { get => updateText; private set => Set(ref updateText, value); }
    public string UpdateButtonLabel => updateIsReady ? "Restart to update" : "Check for updates";

    public StatePaths Paths => paths;
    public bool IsRecording => phase == RecordingPhase.Recording;
    public bool IsStarting => phase == RecordingPhase.Starting;
    public string RecordLabel => phase switch
    {
        RecordingPhase.Starting => "Starting…",
        RecordingPhase.Recording => "Stop recording",
        RecordingPhase.Finishing => "Finishing…",
        _ => "Start recording"
    };
    public string RecordGlyph => phase == RecordingPhase.Recording ? "" : "";

    private string statusLine = "";
    public string StatusLine { get => statusLine; private set => Set(ref statusLine, value); }

    public string? Banner { get => banner; private set => Set(ref banner, value); }
    public string? MicrophoneName { get => microphoneName; private set => Set(ref microphoneName, value); }
    public string? PlaybackName { get => playbackName; private set => Set(ref playbackName, value); }
    public bool HasMeetings => Meetings.Count > 0;

    public MeetingItem? SelectedMeeting
    {
        get => selectedMeeting;
        set
        {
            if (!Set(ref selectedMeeting, value)) return;
            Detail = value is null ? null : CreateDetail(value.Info);
            if (Detail is not null) RunSafely(Detail.RefreshTranscriptAsync());
        }
    }

    public MeetingDetailViewModel? Detail
    {
        get => detail;
        private set
        {
            if (Set(ref detail, value)) Raise(nameof(ShowLanding));
        }
    }

    public bool ShowLanding => detail is null;

    private MeetingDetailViewModel CreateDetail(MeetingInfo info)
    {
        var created = new MeetingDetailViewModel(info, IsActive(info.Directory), modelAvailable, generatingDirectory == info.Directory, ShowError, RequestSummary, DownloadModel);
        created.ReportModelDownload(modelDownloadPercent);
        return created;
    }

    private bool IsActive(string directory) => string.Equals(directory, activeDirectory, StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        RunSafely(InitializeAsync());
        timer.Start();
    }

    private async Task InitializeAsync()
    {
        // Audio a crash left behind is removed as soon as possible, not only when the next recording starts.
        await Task.Run(CleanupAbandonedAudioIfIdle);
        RefreshDevices();
        await RefreshMeetingsAsync();
        // A recording that outlived a previous window is found here, and its meeting selected.
        await TickAsync();
    }

    private void CleanupAbandonedAudioIfIdle()
    {
        if (RecordingControl.IsWorkerRunning(paths)) return;
        // A recording may start in this very moment. Its next start cleans up instead.
        try { RecordingControl.CleanupAbandonedAudio(paths); }
        catch (InvalidOperationException) { }
    }

    public void ShowError(string message) => Banner = message;

    public void Shutdown()
    {
        timer.Stop();
        // Any unfinished summary restarts next time the app opens; the transcript is what it needs, and that is saved.
        summaries.Cancel();
        // A partial model download resumes next time. An update already downloaded stays ready.
        network.Cancel();
    }

    public void RefreshAfterSettingsChange()
    {
        RefreshDevices();
        RunSafely(RefreshMeetingsAsync());
    }

    private void RefreshDevices()
    {
        var config = paths.LoadConfig();
        RunSafely(LoadDeviceNamesAsync(config));
    }

    private async Task LoadDeviceNamesAsync(AppConfig config)
    {
        (MicrophoneName, PlaybackName) = await Task.Run(() =>
            (AudioDevices.DescribeMicrophone(config.MicrophoneId) ?? "Unavailable", AudioDevices.DescribePlayback(config.PlaybackId) ?? "Unavailable"));
    }

    private async void RunSafely(Task task)
    {
        try { await task; }
        catch (Exception exception) { ShowError($"Something went wrong ({exception.GetType().Name}). Saved text is not affected."); }
    }

    private async Task ToggleRecordingAsync()
    {
        try
        {
            if (phase == RecordingPhase.Recording) await StopAsync();
            else if (phase == RecordingPhase.Idle) await StartRecordingAsync();
        }
        catch (Exception exception) { ShowError($"Something went wrong ({exception.GetType().Name}). Saved text is not affected."); }
    }

    private async Task StartRecordingAsync()
    {
        Banner = null;
        startInProgress = true;
        SetPhase(RecordingPhase.Starting);
        StatusLine = "Getting ready…";
        // A meeting's responsiveness comes first: an unfinished summary gives way and resumes afterwards.
        summaries.Cancel();
        try
        {
            var status = await Task.Run(async () =>
            {
                await RecordingControl.PrepareStartAsync(paths);
                return await RecordingControl.StartBackgroundAsync(paths, Path.Combine(AppContext.BaseDirectory, "meetus.exe"));
            });
            await ApplyLiveStatusAsync(status);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or TimeoutException or UnauthorizedAccessException or Win32Exception)
        {
            SetPhase(RecordingPhase.Idle);
            StatusLine = "";
            ShowError($"Recording did not start. {exception.Message}");
            await RefreshMeetingsAsync();
        }
        finally { startInProgress = false; }
    }

    private async Task StopAsync()
    {
        var status = await Task.Run(() => ControlServer.RequestAsync(paths, stop: true));
        if (status is null) { ShowError("The recording did not respond to Stop. It may have ended already; its saved text is kept."); return; }
        SetPhase(RecordingPhase.Finishing);
        StatusLine = "Finishing the transcript…";
    }

    // True only when the recording acknowledged the request or is verifiably gone: never close over a running microphone.
    public async Task<bool> StopForExitAsync()
    {
        var acknowledged = await Task.Run(async () => await ControlServer.RequestAsync(paths, stop: true) is not null || !RecordingControl.IsWorkerRunning(paths));
        if (!acknowledged) ShowError("The recording did not respond to Stop, so the window stays open. Try again in a moment.");
        return acknowledged;
    }

    private async Task TickAsync()
    {
        if (polling || startInProgress) return;
        polling = true;
        try
        {
            if (phase == RecordingPhase.Idle && ++secondsSinceIdlePoll < IdlePollEverySeconds) return;
            secondsSinceIdlePoll = 0;
            var live = await Task.Run(() => ControlServer.RequestAsync(paths));
            if (live is not null) await ApplyLiveStatusAsync(live);
            else if (phase == RecordingPhase.Idle) OnIdleTick();
            else if (await Task.Run(() => RecordingControl.IsWorkerRunning(paths))) StatusLine = "Waiting for the recording to respond…";
            else await OnRecordingEndedAsync();
            if (Detail is { IsLive: true } shown) await shown.RefreshTranscriptAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not check on the recording ({exception.GetType().Name}). It keeps running if it was already started.");
        }
        finally { polling = false; }
    }

    private async Task ApplyLiveStatusAsync(SessionStatus status)
    {
        var directory = status.SessionDirectory;
        if (directory is not null && !IsActive(directory))
        {
            activeDirectory = directory;
            var metadata = await Task.Run(() => SessionMetadata.Read(directory));
            activeStartedUtc = metadata.StartedUtc;
            activeMaxMinutes = metadata.MaxSessionMinutes;
            await RefreshMeetingsAsync();
            SelectedMeeting = Meetings.FirstOrDefault(meeting => meeting.Directory == directory);
        }
        switch (status.State)
        {
            case "Starting":
                SetPhase(RecordingPhase.Starting);
                break;
            case "Capturing":
                SetPhase(RecordingPhase.Recording);
                StatusLine = RecordingStatus(status);
                break;
            default: // Finishing, or a final state while the worker cleans up and exits.
                SetPhase(RecordingPhase.Finishing);
                StatusLine = FinishingStatus(status);
                break;
        }
    }

    private string RecordingStatus(SessionStatus status)
    {
        var elapsed = DateTimeOffset.UtcNow - activeStartedUtc;
        var clock = $"{(int)elapsed.TotalHours:0}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var stopsAt = activeMaxMinutes is { } minutes
            ? " · stops at " + activeStartedUtc.AddMinutes(minutes).ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)
            : "";
        // A clip normally waits up to its own length, so only a larger delay is worth mentioning.
        var behind = status.OldestAudioSeconds >= 60 ? $" · text is {DisplayText.Duration(status.OldestAudioSeconds)} behind" : "";
        return clock + stopsAt + behind;
    }

    private static string FinishingStatus(SessionStatus status)
    {
        var remaining = status.PendingBytes / (double)PcmBytesPerSecond;
        return remaining >= 1 ? $"Finishing the transcript: {DisplayText.Duration(remaining)} of audio left" : "Finishing the transcript…";
    }

    private async Task OnRecordingEndedAsync()
    {
        var final = RecordingControl.ReadStatus(null, paths);
        activeDirectory = null;
        SetPhase(RecordingPhase.Idle);
        StatusLine = "";
        if (final.State is "Failed" or "Interrupted") ShowError($"The recording stopped early. {final.Error}");
        await RefreshMeetingsAsync();
        // The worker's last records land just before it exits, after the final live poll.
        if (Detail is { } shown) await shown.RefreshTranscriptAsync();
        PumpSummaries();
    }

    private void OnIdleTick()
    {
        var available = File.Exists(paths.LoadConfig().ResolvedSummaryModelPath);
        if (available != modelAvailable)
        {
            modelAvailable = available;
            RefreshDetail();
        }
        PumpSummaries();
    }

    private void SetPhase(RecordingPhase value)
    {
        if (phase == value) return;
        phase = value;
        Raise(nameof(IsRecording));
        Raise(nameof(IsStarting));
        Raise(nameof(RecordLabel));
        Raise(nameof(RecordGlyph));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RefreshMeetingsAsync()
    {
        var config = paths.LoadConfig();
        modelAvailable = File.Exists(config.ResolvedSummaryModelPath);
        var active = activeDirectory;
        var latest = await Task.Run(() => MeetingLibrary.List(config.ResolvedOutputDirectory, active));
        var today = DateTime.Today;
        var existing = Meetings.ToDictionary(meeting => meeting.Directory, StringComparer.OrdinalIgnoreCase);
        var wanted = latest.Select(info => info.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Meetings.Where(meeting => !wanted.Contains(meeting.Directory)).ToList()) Meetings.Remove(gone);
        for (var index = 0; index < latest.Count; index++)
        {
            var info = latest[index];
            if (existing.TryGetValue(info.Directory, out var item)) item.Info = info;
            else Meetings.Insert(index, new MeetingItem(info, today));
        }
        Raise(nameof(HasMeetings));
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        if (Detail is null || SelectedMeeting is null) return;
        Detail.Update(SelectedMeeting.Info, IsActive(Detail.Directory), modelAvailable, generatingDirectory == Detail.Directory);
    }

    private void RequestSummary(string directory)
    {
        if (!requestedSummaries.Contains(directory)) requestedSummaries.Add(directory);
        PumpSummaries();
    }

    // One at a time and only with no recording running. Manual requests go first, then the newest pending meeting.
    private async void PumpSummaries()
    {
        if (generatingDirectory is not null || phase != RecordingPhase.Idle || !modelAvailable) return;
        var next = requestedSummaries.FirstOrDefault()
            ?? MeetingLibrary.PendingAutomaticSummaries(Meetings.Select(meeting => meeting.Info)).FirstOrDefault()?.Directory;
        if (next is null) return;
        generatingDirectory = next;
        RefreshDetail();
        try
        {
            void Report(SummaryProgress update) =>
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { if (Detail?.Directory == next) Detail.ReportProgress(update); });
            var outcome = await summaries.RunAsync(next, false, Report);
            // A GPU driver can fail cleanly or take the whole process down, so either way the CPU gets its turn.
            if (outcome == SummaryRunOutcome.GpuFailed || (outcome == SummaryRunOutcome.Crashed && paths.LoadConfig().SummaryProcessor == SummaryProcessor.Gpu))
                outcome = await summaries.RunAsync(next, true, Report);
            if (outcome != SummaryRunOutcome.Postponed) requestedSummaries.Remove(next);
            if (outcome == SummaryRunOutcome.Crashed)
                // Recorded so the meeting is not retried forever; Regenerate remains available.
                SummaryFiles.WriteFailure(next, null, summaries.LastError is { } reason
                    ? $"The summary could not be generated. {reason} The transcript is unaffected."
                    : "The summary process stopped unexpectedly, possibly from running out of memory. The transcript is unaffected.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            requestedSummaries.Remove(next);
            ShowError($"The summary could not be started ({exception.GetType().Name}).");
        }
        finally { generatingDirectory = null; }
        RunSafely(RefreshMeetingsAsync());
    }

    private void DownloadModel() => RunSafely(DownloadModelAsync());

    private async Task DownloadModelAsync()
    {
        if (network.IsRunning) return;
        ReportModelDownload(0);
        try
        {
            var result = await network.RunAsync(["download-model", "--state-dir", paths.Root], line =>
            {
                if (HelperProcess.TryParseProgress(line, out var received, out var total) && total > 0)
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ReportModelDownload(100.0 * received / total));
            });
            if (result.ExitCode != 0 && !result.Cancelled)
                ShowError($"The summary model did not download. {result.Error ?? "It stopped unexpectedly."} Anything already fetched is kept, so trying again continues from there.");
        }
        finally { ReportModelDownload(null); }
        OnIdleTick();
    }

    private void ReportModelDownload(double? percent)
    {
        modelDownloadPercent = percent;
        Detail?.ReportModelDownload(percent);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task OnUpdatePressedAsync()
    {
        if (updateIsReady) await ApplyUpdateAsync();
        else await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateText = "Checking…";
        var arguments = new List<string> { "check-update", "--state-dir", paths.Root };
        if (updateFeed is not null) arguments.AddRange(["--update-feed", updateFeed]);
        string? ready = null;
        var upToDate = false;
        var result = await network.RunAsync(arguments, line =>
        {
            if (line.StartsWith("ready ", StringComparison.Ordinal)) ready = line["ready ".Length..];
            else if (line == "current") upToDate = true;
            else if (HelperProcess.TryParseProgress(line, out var percent, out _))
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => UpdateText = $"Downloading the update: {percent}%");
        });
        CommandManager.InvalidateRequerySuggested();
        if (ready is not null) SetUpdateReady(ready);
        else if (upToDate) UpdateText = "You have the latest version.";
        else if (!result.Cancelled)
        {
            UpdateText = "";
            // Exception text from the network layer can contain URLs, but never meeting content.
            ShowError($"Could not check for updates. Check your connection and try again. {result.Error}");
        }
    }

    private void SetUpdateReady(string version)
    {
        updateIsReady = true;
        UpdateText = $"Version {version} is ready.";
        Raise(nameof(UpdateButtonLabel));
    }

    // Update.exe replaces the folder this runs from and force-closes anything still running from it.
    private async Task ApplyUpdateAsync()
    {
        if (paths.LoadConfig().HasStorageReplacedByUpdates)
        {
            ShowError("Your saved meetings or summary model are in the app's \"current\" folder, which updating would delete. Choose another location in Settings and move the files first.");
            return;
        }
        if (phase != RecordingPhase.Idle)
        {
            ShowError("A recording is running or finishing. Update when it has finished.");
            return;
        }
        summaries.Cancel();
        network.Cancel();
        // The summary was just asked to stop, so give it a moment; a recording will still be there afterwards.
        for (var attempt = 0; attempt < UpdateHandoffAttempts && await Task.Run(RecordingControl.IsHelperRunningFromThisInstallation); attempt++)
            await Task.Delay(UpdateHandoffDelay);
        if (await Task.Run(RecordingControl.IsHelperRunningFromThisInstallation))
        {
            ShowError("A recording or another task from this installation is still running. Update when it has finished.");
            return;
        }
        updates.ApplyAfterExit(launchArguments);
        System.Windows.Application.Current.Shutdown();
    }
}
