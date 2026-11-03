using System.Runtime.InteropServices;
using System.Windows;
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

        public Fixture(bool withSummaryModel = true, bool hideFromCapture = false)
        {
            Paths = new StatePaths(Path.Combine(root.Path, "state"));
            Output = Path.Combine(root.Path, "meetings");
            Directory.CreateDirectory(Output);
            Model = Path.Combine(root.Path, "model.bin");
            File.WriteAllBytes(Model, [0]);
            JsonFiles.Write(Paths.ConfigFile, new AppConfig
            {
                OutputDirectory = Output, ModelPath = Model, VadPath = Model,
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
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
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
        var viewModel = new MainViewModel(fixture.Paths);
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
        var viewModel = new MainViewModel(fixture.Paths);
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
        using var fixture = new Fixture();
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
        var viewModel = new MainViewModel(fixture.Paths);
        var window = Open(viewModel);
        try
        {
            await WaitUntil(() => viewModel.IsStarting);
            Volatile.Write(ref status, new SessionStatus("Capturing", directory));
            await WaitUntil(() => viewModel.IsRecording);
            Assert.Equal(directory, viewModel.SelectedMeeting?.Directory);

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
        var settings = new SettingsWindow(new SettingsViewModel(fixture.Paths))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        settings.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        settings.Show();
        await Settle(settings);
        Snapshot(settings, "settings");
        settings.Close();
    });

    [Theory]
    [InlineData(true, 0x11u)]
    [InlineData(false, 0u)]
    public Task ScreenCapturePrivacyFollowsTheSetting(bool hide, uint expectedAffinity) => WpfHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(hideFromCapture: hide);
        var viewModel = new MainViewModel(fixture.Paths);
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
