using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Meetu;
using Meetu.App;
using Meetu.App.ViewModels;
using Xunit;

namespace Meetu.Tests;

// WPF allows one Application per process, bound to the thread that created it.
internal static class WpfHost
{
    private static readonly Lazy<Dispatcher> Host = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                AppUpdates.InitializeProcess();
                // Not the real App: its startup would open a window over the user's real settings.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Light };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MeetusPrivatus;component/Styles.xaml") });
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    public static Task RunAsync(Func<Task> work) => Host.Value.InvokeAsync(work).Task.Unwrap();
}

public sealed class AppWindowTests
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory root = new();
        public StatePaths Paths { get; }
        public string Output { get; }
        public string Model { get; }
        public string Models { get; }

        public Fixture(bool withSummaryModel = true, bool hideFromCapture = false, bool withSpeechModelChoice = true)
        {
            Paths = new StatePaths(Path.Combine(root.Path, "state"));
            Output = Path.Combine(root.Path, "meetings");
            Models = Path.Combine(root.Path, "models");
            Directory.CreateDirectory(Output);
            Model = Path.Combine(root.Path, "model.bin");
            File.WriteAllBytes(Model, [0]);
            JsonFiles.Write(Paths.ConfigFile, new AppConfig
            {
                OutputDirectory = Output, ModelPath = Model, VadPath = Model,
                SpeechModelId = withSpeechModelChoice ? SpeechModels.IncludedWhisperId : null,
                SummaryModelPath = withSummaryModel ? Model : Path.Combine(root.Path, "missing.gguf"),
                HideWindowFromScreenSharing = hideFromCapture
            });
        }

        public string AddMeeting(DateTimeOffset started, string state = "Complete", MeetingSummary? summary = null, string? error = null)
        {
            var directory = Path.Combine(Output, $"{started.ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            var store = new TranscriptStore(directory);
            var lines = new (AudioSource Source, string Text)[]
            {
                (AudioSource.Remote, "Okay, let's get started. This is the weekly sync for the Harbor launch."),
                (AudioSource.Remote, "The iOS build went through review on Tuesday. Android is still blocked on the payment SDK upgrade."),
                (AudioSource.Local, "Three days feels tight. What happens if the SDK slips?"),
                (AudioSource.Remote, "Then Android misses the first wave and we ship iOS only on November 18th."),
                (AudioSource.Local, "I can talk to Rafael in legal. I'll get that sorted by Wednesday.")
            };
            for (var index = 0; index < lines.Length; index++)
                store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), lines[index].Source, index * 6.0 + 3, index * 6.0 + 8,
                    [new TranscriptSegment(index * 6.0 + 3, index * 6.0 + 8, lines[index].Text)]));
            store.Commit(new TranscriptRecord(Guid.NewGuid().ToString("N"), AudioSource.Remote, 40, 45, [], "Capture discontinuity. Some audio may be missing."));
            SessionMetadata.Write(directory, new SessionMetadata(started, state) { CaptureSeconds = 2640, Error = error });
            if (summary is not null) SummaryFiles.Write(directory, summary);
            return directory;
        }

        public void Dispose() => root.Dispose();
    }

    private static MeetingSummary ReadySummary() => new(SummaryState.Complete, DateTimeOffset.UtcNow, "Qwen3.5-4B-Q4_K_M", 80,
        new MeetingNotes("Harbor launch weekly sync",
            "The team reviewed the Harbor launch and confirmed iOS ships first on November 18th while Android waits for the payment SDK.",
            ["The load test reached eleven hundred requests a second against a target of two thousand."],
            ["iOS ships on November 18th regardless of Android status."],
            [new MeetingActionItem("Talk to Rafael in legal about the pricing table", "Me", "by Wednesday", "00:00:27"),
             new MeetingActionItem("Update the release plan and send it out", "Priya", "by Friday", "00:00:09"),
             new MeetingActionItem("Rerun the load test after the limit is raised", null, null, null)]));

    // Polls until the condition holds, letting the dispatcher run the view model's background work.
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The window did not reach the expected state.");
            await Task.Delay(25);
        }
    }

    private static MainWindow Open(MainViewModel viewModel)
    {
        // Off-screen and never activated: these checks must not disturb the desktop.
        var window = new MainWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        window.Show();
        viewModel.Start();
        return window;
    }

    // MEETUS_UI_SNAPSHOTS names a folder to receive what each state actually looks like. No folder, no files.
    private static void Snapshot(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var folder = Environment.GetEnvironmentVariable("MEETUS_UI_SNAPSHOTS");
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        Assert.True(bitmap.PixelWidth > 0);
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private static async Task Select(MainViewModel viewModel, Window window, int index)
    {
        viewModel.SelectedMeeting = viewModel.Meetings[index];
        await WaitUntil(() => viewModel.Detail!.Lines.Count > 0);
        await Settle(window);
    }

    private static Task Settle(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task EveryMeetingStateRendersWithTheRealTheme(string label) => WpfHost.RunAsync(async () =>
    {
        Application.Current.ThemeMode = label == "light" ? ThemeMode.Light : ThemeMode.Dark;
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        fixture.AddMeeting(now.AddHours(-1), summary: ReadySummary());
        fixture.AddMeeting(now.AddHours(-3), summary: new MeetingSummary(SummaryState.Failed, now, "model", Error: "The summary could not be generated (InvalidDataException). The transcript is unaffected."));
        fixture.AddMeeting(now.AddDays(-1), state: "Failed", error: "Selected audio device is unavailable. Capture stopped without switching devices.");
        fixture.AddMeeting(now.AddDays(-20), state: "Capturing");
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.Meetings.Count == 4);
            await Settle(window);
            Snapshot(window, $"{label}-landing");
            await Select(viewModel, window, 0);
            Assert.Equal(SummaryPanel.Ready, viewModel.Detail!.Panel);
            Snapshot(window, $"{label}-summary");
            viewModel.Detail.SelectedTab = 1;
            await Settle(window);
            Snapshot(window, $"{label}-transcript");
            await Select(viewModel, window, 1);
            Assert.Equal(SummaryPanel.Failed, viewModel.Detail!.Panel);
            Snapshot(window, $"{label}-summary-failed");
            await Select(viewModel, window, 2);
            Snapshot(window, $"{label}-incomplete");
            await Select(viewModel, window, 3);
            Assert.Equal("Interrupted", viewModel.Meetings[3].Badge);
            Snapshot(window, $"{label}-interrupted");
        }
        finally
        {
            viewModel.Shutdown();
            window.Close();
        }
    });

    [Fact]
    public Task MissingSummaryModelOffersTheDownloadInsteadOfFailing() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(withSummaryModel: false);
        fixture.AddMeeting(DateTimeOffset.UtcNow.AddHours(-1));
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.Meetings.Count == 1);
            await Select(viewModel, window, 0);
            Assert.Equal(SummaryPanel.ModelMissing, viewModel.Detail!.Panel);
            Assert.True(viewModel.Detail.ShowDownload);
            Snapshot(window, "model-missing");
        }
        finally
        {
            viewModel.Shutdown();
            window.Close();
        }
    });

    // A worker that loads its model while the app opens reports Starting. The app must keep watching it,
    // and must not treat a worker it cannot reach as finished.
    [Fact]
    public Task ObservedStartingWorkerIsFollowedAndAnUnresponsiveOneStaysActive() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(withSpeechModelChoice: false);
        var directory = fixture.AddMeeting(DateTimeOffset.UtcNow, state: "Capturing");
        using var workerLock = new FileStream(Path.Combine(fixture.Paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var status = new SessionStatus("Starting");
        using var stopServer = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stopServer.IsCancellationRequested)
            {
                await using var pipe = new System.IO.Pipes.NamedPipeServerStream(fixture.Paths.PipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopServer.Token);
                var command = new byte[1];
                if (await pipe.ReadAsync(command, stopServer.Token) == 1)
                    await pipe.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Volatile.Read(ref status), JsonFiles.Options), stopServer.Token);
            }
        }, stopServer.Token);
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.IsStarting);
            Volatile.Write(ref status, new SessionStatus("Capturing", directory));
            await WaitUntil(() => viewModel.IsRecording);
            Assert.True(viewModel.NeedsSpeechModelSelection);
            Assert.False(viewModel.ShowSpeechModelSetup);
            Assert.True(viewModel.ToggleRecordingCommand.CanExecute(null));
            Assert.Equal(directory, viewModel.SelectedMeeting?.Directory);
            Assert.False(viewModel.SelectedMeeting!.CanDelete);
            await viewModel.DeleteMeetingAsync(viewModel.SelectedMeeting);
            Assert.True(File.Exists(Path.Combine(directory, SessionMetadata.FileName)));
            Assert.Contains("still being recorded", viewModel.Banner);

            // The pipe goes quiet while the worker still owns its lock: not the end of the recording.
            await stopServer.CancelAsync();
            await WaitUntil(() => viewModel.StatusLine.StartsWith("Waiting for the recording", StringComparison.Ordinal));
            Assert.True(viewModel.IsRecording);
        }
        finally
        {
            viewModel.Shutdown();
            window.CloseWithoutPrompt();
            await stopServer.CancelAsync();
            try { await server; } catch (OperationCanceledException) { }
        }
    });

    [Fact]
    public Task SettingsWindowRenders() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var settings = new SettingsWindow(new SettingsViewModel(fixture.Paths, viewModel.SpeechModels))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        settings.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        settings.Show();
        await Settle(settings);
        Snapshot(settings, "settings");
        settings.Close();
    });

    [Fact]
    public Task RightClickingAMeetingOffersToDeleteIt() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var deleted = fixture.AddMeeting(DateTimeOffset.UtcNow.AddHours(-1), summary: ReadySummary());
        var kept = fixture.AddMeeting(DateTimeOffset.UtcNow.AddHours(-2));
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.Meetings.Count == 2);
            await Select(viewModel, window, 0);
            var row = (ListBoxItem)window.MeetingList.ItemContainerGenerator.ContainerFromItem(viewModel.Meetings[0]);
            var delete = Assert.IsType<MenuItem>(Assert.Single(row.ContextMenu.Items.OfType<object>()));
            Assert.Equal("Delete meeting…", delete.Header);
            Assert.Equal(viewModel.Meetings[0].Title, System.Windows.Automation.AutomationProperties.GetName(row));
            // Shown in a window that screen sharing can see, so it must not repeat a title taken from the transcript.
            Assert.DoesNotContain("Harbor", viewModel.Meetings[0].DeleteConfirmation);
            Assert.Contains("cannot be undone", viewModel.Meetings[0].DeleteConfirmation);

            await viewModel.DeleteMeetingAsync(viewModel.Meetings[0]);
            Assert.False(Directory.Exists(deleted));
            Assert.Equal(kept, Assert.Single(viewModel.Meetings).Directory);
            Assert.Null(viewModel.Detail);
            Assert.Null(viewModel.Banner);
        }
        finally
        {
            viewModel.Shutdown();
            window.Close();
        }
    });

    [Fact]
    public Task AFailedMeetingDeletionIsReportedAndTheMeetingStays() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var directory = fixture.AddMeeting(DateTimeOffset.UtcNow.AddHours(-1), summary: ReadySummary());
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.Meetings.Count == 1);
            using (new FileStream(Path.Combine(directory, "remote.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                await viewModel.DeleteMeetingAsync(viewModel.Meetings[0]);
            Assert.Contains("could not be fully deleted", viewModel.Banner);
            Assert.Single(viewModel.Meetings);
        }
        finally
        {
            viewModel.Shutdown();
            window.Close();
        }
    });

    private static void InstallSparse(SpeechModel model, string models)
    {
        var directory = SpeechModelFiles.DownloadDirectory(model, models);
        Directory.CreateDirectory(directory);
        foreach (var asset in model.Downloads)
        {
            using var file = File.Create(Path.Combine(directory, asset.FileName));
            file.SetLength(asset.Size);
        }
    }

    [Fact]
    public Task FirstRunAndUpgradeGuideModelSelectionWithoutStartingCapture() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(withSpeechModelChoice: false);
        // Earlier previews saved a Whisper path but never asked the user to choose a model.
        var before = File.ReadAllText(fixture.Paths.ConfigFile);
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await Settle(window);
            Assert.True(viewModel.NeedsSpeechModelSelection);
            Assert.Equal(Visibility.Visible, window.SpeechModelSetup.Visibility);
            Assert.False(viewModel.ToggleRecordingCommand.CanExecute(null));
            viewModel.ToggleRecordingCommand.Execute(null);
            Assert.False(viewModel.IsStarting);
            // Startup cleanup briefly holds the same lock; no recording status may be created.
            Assert.False(File.Exists(fixture.Paths.StatusFile));
            Assert.Equal(before, File.ReadAllText(fixture.Paths.ConfigFile));

            var settings = new SettingsViewModel(fixture.Paths, viewModel.SpeechModels);
            Assert.True(settings.IsSpeechModelSetup);
            Assert.Null(settings.CustomSpeechModelNote);
            Assert.All(settings.SpeechModelChoices, choice => Assert.False(choice.IsChosen));
            Assert.False(settings.TrySave());
            Assert.Contains("Choose a transcription model", settings.Error);
            Assert.Equal(before, File.ReadAllText(fixture.Paths.ConfigFile));

            // A confirmed selection on disk removes the guide, including after a restart.
            JsonFiles.Write(fixture.Paths.ConfigFile, fixture.Paths.LoadConfig() with { SpeechModelId = SpeechModels.IncludedWhisperId });
            viewModel.RefreshAfterSettingsChange();
            Assert.False(viewModel.NeedsSpeechModelSelection);
            Assert.Equal(Visibility.Collapsed, window.SpeechModelSetup.Visibility);
            Assert.True(viewModel.ToggleRecordingCommand.CanExecute(null));
            Assert.False(new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models).NeedsSpeechModelSelection);
        }
        finally { viewModel.Shutdown(); window.Close(); }
    });

    [Fact]
    public Task ModelSetupOpensAtTheModelChoicesAndKeepsSelectionExplicit() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(withSpeechModelChoice: false);
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var settings = new SettingsWindow(new SettingsViewModel(fixture.Paths, viewModel.SpeechModels))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            settings.Show();
            await Settle(settings);
            Assert.Equal("Choose a transcription model", settings.Title);
            var position = settings.TranscriptionHeading.TranslatePoint(new Point(), settings.SettingsScroller);
            Assert.InRange(position.Y, 0, settings.SettingsScroller.ViewportHeight);
            Snapshot(settings, "model-setup");
        }
        finally { settings.Close(); }
    });

    [Fact]
    public Task ChoosingADownloadedModelReplacesAPinnedFile() => WpfHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        var whisper = SpeechModels.Find("whisper-small.en")!;
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var settings = new SettingsViewModel(fixture.Paths, viewModel.SpeechModels);
        // The fixture pins a specific file, which stands for any configuration from before this choice existed.
        Assert.Null(settings.ChosenSpeechModel);
        Assert.Contains("specific Whisper file", settings.CustomSpeechModelNote);
        Assert.False(viewModel.SpeechModels.Rows.Single(row => row.Model == whisper).IsInstalled);

        InstallSparse(whisper, fixture.Models);
        settings = new SettingsViewModel(fixture.Paths, viewModel.SpeechModels);
        var choice = settings.SpeechModelChoices.Single(item => item.Row.Model == whisper);
        Assert.True(choice.Row.IsInstalled);
        choice.IsChosen = true;
        Assert.True(choice.IsChosen);
        Assert.Null(settings.CustomSpeechModelNote);
        Assert.Equal(whisper.Id, settings.Updated().SpeechModelId);
        Assert.Null(settings.Updated().ModelPath);

        // Validation reads the app's real models folder, which holds no such model during a test: refused, and it says why.
        Assert.False(settings.TrySave());
        Assert.Contains(whisper.Name, settings.Error);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AModelInUseCannotBeDeletedAndAnUnusedOneCan() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var whisper = SpeechModels.Find("whisper-small.en")!;
        InstallSparse(whisper, fixture.Models);
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var settings = new SettingsViewModel(fixture.Paths, viewModel.SpeechModels);
        var row = viewModel.SpeechModels.Rows.Single(item => item.Model == whisper);
        var included = viewModel.SpeechModels.Rows.Single(item => item.Model.IsIncluded);
        Assert.False(included.ShowDelete);
        Assert.False(included.ShowDownload);

        settings.SpeechModelChoices.Single(item => item.Row == row).IsChosen = true;
        await settings.DeleteSpeechModelAsync(row);
        Assert.True(row.IsInstalled);
        Assert.Contains("is selected", settings.Error);

        settings.SpeechModelChoices.Single(item => item.Row == included).IsChosen = true;
        Assert.Contains(whisper.Name, settings.DeleteConfirmation(row));
        await settings.DeleteSpeechModelAsync(row);
        Assert.Null(settings.Error);
        Assert.False(row.IsInstalled);
        Assert.True(row.ShowDownload);
    });

    [Fact]
    public Task ADownloadReportsProgressAndItsOutcome() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var whisper = SpeechModels.Find("whisper-tiny.en")!;
        var attempt = new TaskCompletionSource<HelperResult>();
        Action<double>? report = null;
        var stops = 0;
        var errors = new List<string>();
        var models = new SpeechModelsViewModel(fixture.Paths, fixture.Models, (_, percent) => { report = percent; return attempt.Task; }, () => stops++, () => false, errors.Add);
        var row = models.Rows.Single(item => item.Model == whisper);
        Assert.Equal("Download", row.DownloadLabel);

        row.DownloadCommand.Execute(null);
        await WaitUntil(() => report is not null);
        Assert.True(row.IsDownloading);
        Assert.False(row.DownloadCommand.CanExecute(null));
        report!(42);
        Assert.Contains("42%", row.Status);
        Assert.Contains(whisper.Name, models.ActiveDownloadText);

        attempt.SetResult(new HelperResult(6, false, "No route to host."));
        await WaitUntil(() => !row.IsDownloading);
        Assert.Contains("No route to host", row.Message);
        Assert.Contains("No route to host", Assert.Single(errors));
        Assert.Null(models.ActiveDownloadText);

        // Starting a recording gives the meeting its bandwidth back, and says how to continue.
        attempt = new TaskCompletionSource<HelperResult>();
        report = null;
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(fixture.Models).FullName, whisper.Location + ModelDownloader.PartialSuffix), [1]);
        row.DownloadCommand.Execute(null);
        await WaitUntil(() => report is not null);
        models.PauseForRecording();
        Assert.Equal(1, stops);
        attempt.SetResult(new HelperResult(-1, true, null));
        await WaitUntil(() => !row.IsDownloading);
        Assert.Contains("Paused for the recording", row.Message);
        Assert.Equal("Resume", row.DownloadLabel);
        Assert.Single(errors);

        InstallSparse(whisper, fixture.Models);
        models.Refresh();
        Assert.True(row.IsInstalled);
    });

    // The download starts only after a check that the recording is not running. Stop, or a recording starting,
    // during that check must still prevent the helper from being launched.
    [Fact]
    public Task StoppingBeforeTheHelperStartsPreventsTheDownload() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var launched = 0;
        var models = new SpeechModelsViewModel(fixture.Paths, fixture.Models, (_, _) => { launched++; return Task.FromResult(new HelperResult(0, false, null)); }, () => { }, () => false, _ => { });
        var row = models.Rows.Single(item => item.Model == SpeechModels.Find("whisper-tiny.en"));

        row.DownloadCommand.Execute(null);
        models.StopDownload();
        await WaitUntil(() => !row.IsDownloading);
        Assert.Null(row.Message);

        row.DownloadCommand.Execute(null);
        models.PauseForRecording();
        await WaitUntil(() => !row.IsDownloading);
        Assert.Contains("Paused for the recording", row.Message);
        Assert.Equal(0, launched);
    });

    [Fact]
    public Task AModelBeingDeletedCannotStartDownloading() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var whisper = SpeechModels.Find("whisper-small.en")!;
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(fixture.Models).FullName, whisper.Location + ModelDownloader.PartialSuffix), [1]);
        var launched = 0;
        var models = new SpeechModelsViewModel(fixture.Paths, fixture.Models, (_, _) => { launched++; return Task.FromResult(new HelperResult(0, false, null)); }, () => { }, () => false, _ => { });
        var row = models.Rows.Single(item => item.Model == whisper);
        Assert.Equal("Resume", row.DownloadLabel);

        var deletion = models.DeleteAsync(row);
        Assert.False(row.DownloadCommand.CanExecute(null));
        row.DownloadCommand.Execute(null);
        Assert.Null(await deletion);
        Assert.Equal(0, launched);
        Assert.False(row.IsDownloading);
        Assert.Equal("Download", row.DownloadLabel);
    });

    [Fact]
    public Task ModelsCannotChangeWhileARecordingHoldsTheWorkerLock() => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var whisper = SpeechModels.Find("whisper-tiny.en")!;
        InstallSparse(whisper, fixture.Models);
        var started = false;
        var models = new SpeechModelsViewModel(fixture.Paths, fixture.Models, (_, _) => { started = true; return Task.FromResult(new HelperResult(0, false, null)); }, () => { }, () => false, _ => { });
        var installed = models.Rows.Single(item => item.Model == whisper);
        var missing = models.Rows.Single(item => item.Model == SpeechModels.Find("whisper-small.en"));
        using var workerLock = new FileStream(Path.Combine(fixture.Paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Contains("recording is running", await models.DeleteAsync(installed));
        Assert.True(installed.IsInstalled);

        missing.DownloadCommand.Execute(null);
        await WaitUntil(() => !missing.IsDownloading && missing.Message is not null);
        Assert.False(started);
        Assert.Contains("recording is running", missing.Message);
    });

    [Theory]
    [InlineData(true, 0x11u)]
    [InlineData(false, 0u)]
    public Task ScreenCapturePrivacyFollowsTheSetting(bool hide, uint expectedAffinity) => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(hideFromCapture: hide);
        var viewModel = new MainViewModel(fixture.Paths, modelsDirectory: fixture.Models);
        var window = Open(viewModel);
        try
        {
            await Settle(window);
            Assert.True(GetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(window).Handle, out var affinity));
            Assert.Equal(expectedAffinity, affinity);
        }
        finally
        {
            viewModel.Shutdown();
            window.Close();
        }
    });
}
