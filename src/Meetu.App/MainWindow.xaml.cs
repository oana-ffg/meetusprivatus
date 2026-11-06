using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Meetu.App.ViewModels;

namespace Meetu.App;

public partial class MainWindow : Window
{
    private const double BottomTolerance = 8;
    private readonly MainViewModel viewModel;
    private ScrollViewer? transcriptScroller;
    private bool followLatest;
    private bool closingConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) => ApplyCapturePrivacy();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRecording))
        {
            // Recording stays visible even when the window is minimized.
            TaskbarInfo.Overlay = viewModel.IsRecording ? (ImageSource)FindResource("RecordingOverlay") : null;
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
        }
        else if (e.PropertyName == nameof(MainViewModel.Detail) && viewModel.Detail is { } detail)
        {
            detail.ScrollRequested += ScrollTo;
            followLatest = detail.IsLive;
            JumpToLatest.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyCapturePrivacy()
    {
        var hide = viewModel.Paths.LoadConfig().HideWindowFromScreenSharing;
        if (!WindowPrivacy.Apply(this, hide))
            viewModel.ShowError("Windows would not change whether this window appears in screen shares. Be careful what you share.");
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(new SettingsViewModel(viewModel.Paths, viewModel.SpeechModels)) { Owner = this };
        if (settings.ShowDialog() != true) return;
        ApplyCapturePrivacy();
        viewModel.RefreshAfterSettingsChange();
    }

    private async void DeleteMeetingClicked(object sender, RoutedEventArgs e)
    {
        var meeting = (MeetingItem)((FrameworkElement)sender).DataContext;
        var answer = MessageBox.Show(this, meeting.DeleteConfirmation, "Delete meeting", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await viewModel.DeleteMeetingAsync(meeting);
    }

    private void TranscriptLoaded(object sender, RoutedEventArgs e)
    {
        if (transcriptScroller is not null) return;
        transcriptScroller = FindScroller(TranscriptList);
        if (transcriptScroller is not null) transcriptScroller.ScrollChanged += OnTranscriptScrolled;
    }

    // Follows new text only while the reader is at the bottom; scrolling up to reread stops it from jumping away.
    private void OnTranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            followLatest = scroller.VerticalOffset >= scroller.ScrollableHeight - BottomTolerance;
        else if (followLatest)
            scroller.ScrollToEnd();
        var showJump = !followLatest && viewModel.Detail is { IsLive: true } && scroller.ScrollableHeight > 0;
        JumpToLatest.Visibility = showJump ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JumpToLatestClicked(object sender, RoutedEventArgs e)
    {
        followLatest = true;
        transcriptScroller?.ScrollToEnd();
    }

    private void ScrollTo(TranscriptRow row)
    {
        followLatest = false;
        TranscriptList.SelectedItem = row;
        // The transcript tab may have only just been selected, so wait until its list exists.
        Dispatcher.BeginInvoke(() => TranscriptList.ScrollIntoView(row), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CopySelectedLines(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (viewModel.Detail is not { } detail) return;
        var selected = TranscriptList.SelectedItems.Cast<TranscriptRow>().OrderBy(row => row.Line.StartSeconds);
        detail.Copy(MeetingDetailViewModel.RenderLines(selected));
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    internal void CloseWithoutPrompt()
    {
        closingConfirmed = true;
        Close();
    }

    // Recording runs in its own process, so closing this window must not leave the microphone
    // on with nothing visible. The choice is explicit, and "stop" still finishes the transcript.
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closingConfirmed) return;
        if (viewModel.IsStarting)
        {
            // The worker may be about to open the microphone. Closing now could leave it recording with no window.
            e.Cancel = true;
            viewModel.ShowError("A recording is starting. Wait a moment, then stop it or close the window.");
            return;
        }
        if (!viewModel.IsRecording) return;
        e.Cancel = true;
        var answer = MessageBox.Show(this,
            "A recording is running. Stop it and close?\n\nWhat was captured still gets transcribed in the background. Its summary is written next time you open the app.",
            "Meetus Privatus", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (!await viewModel.StopForExitAsync()) return;
        CloseWithoutPrompt();
    }
}
