using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Meetu.App.ViewModels;

namespace Meetu.App;

public partial class App : Application
{
    private Mutex? singleInstance;
    private EventWaitHandle? activationSignal;
    private RegisteredWaitHandle? activationWait;
    private MainViewModel? viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("Meetus Privatus requires Windows 11.");
            var options = Program.ParseOptions(e.Args);
            var paths = new StatePaths(options.GetValueOrDefault("state-dir"));
            if (!TryBecomeOnlyInstance(paths)) { Shutdown(0); return; }
            viewModel = new MainViewModel(paths, options.GetValueOrDefault("update-feed"), e.Args);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            viewModel.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "Meetus Privatus", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // A second launch brings the existing window forward instead of running two controllers over one recording.
    private bool TryBecomeOnlyInstance(StatePaths paths)
    {
        singleInstance = new Mutex(true, $@"Local\{paths.PipeName}-app", out var createdNew);
        activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{paths.PipeName}-activate");
        if (!createdNew)
        {
            activationSignal.Set();
            return false;
        }
        activationWait = ThreadPool.RegisterWaitForSingleObject(activationSignal, (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow), null, Timeout.Infinite, false);
        return true;
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A bug in the viewer must never end a recording, which runs in its own process. Say what happened
        // (type only, so no meeting text can appear) and keep the window usable.
        e.Handled = true;
        if (viewModel is not null) viewModel.ShowError($"Something went wrong in the app ({e.Exception.GetType().Name}). Recording and saved text are not affected.");
        else MessageBox.Show($"Meetus Privatus could not start ({e.Exception.GetType().Name}).", "Meetus Privatus", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        viewModel?.Shutdown();
        activationWait?.Unregister(null);
        activationSignal?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
