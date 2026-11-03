using System.Diagnostics;

namespace Meetu;

// A helper the desktop app starts for work that belongs in its own process. It has no command-line interface:
// recording stays independent of the window, summaries release their memory, and only the two network commands
// here ever touch the network, each when the user asks. The recording worker and the app itself stay offline.
public static class Program
{
    public const int UnknownCommandExitCode = 2;
    public const int UnhandledFailureExitCode = 6;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("Meetus Privatus requires Windows 11.");
            if (args.Length == 0 || args[0] is not ("worker" or "summarize" or "check-update" or "download-model")) return UnknownCommandExitCode;
            var options = ParseOptions(args[1..]);
            var paths = new StatePaths(options.GetValueOrDefault("state-dir"));
            switch (args[0])
            {
                case "worker":
                    return await RunWorkerAsync(paths);
                case "summarize":
                    return await SummaryJob.RunAsync(paths, RequireSessionDirectory(options), ParseProcessor(options.GetValueOrDefault("processor")),
                        progress => Report($"progress {progress.Completed} {progress.Total}"),
                        () => Environment.Exit(SummaryJob.InterruptedExitCode));
                case "check-update":
                    return await CheckForUpdateAsync(options.GetValueOrDefault("update-feed"));
                default:
                    return await DownloadSummaryModelAsync();
            }
        }
        catch (Exception exception)
        {
            Report($"error {exception.Message}");
            return UnhandledFailureExitCode;
        }
    }

    private static string RequireSessionDirectory(Dictionary<string, string> options)
    {
        var directory = Path.GetFullPath(options.GetValueOrDefault("session-dir") ?? throw new ArgumentException("This command requires a session directory."));
        LocalPath.Require(directory);
        return MeetingLibrary.IsSessionDirectory(directory) ? directory : throw new ArgumentException("This is not a recognizable meeting transcript directory.");
    }

    private static SummaryJobProcessor ParseProcessor(string? value) => value switch
    {
        null => SummaryJobProcessor.Configured,
        "cpu-fallback" => SummaryJobProcessor.CpuAfterGpuFailure,
        _ => throw new ArgumentException("Unknown summary processor.")
    };

    // Progress is advisory and content-free. The app reading it may have closed; the work still finishes.
    private static void Report(string line)
    {
        try { Console.WriteLine(line); }
        catch (IOException) { }
    }

    private static async Task<int> RunWorkerAsync(StatePaths paths)
    {
        // The handle is held for the worker lifetime, including cleanup and model disposal.
        using var workerLock = WorkerLock.Acquire(paths);
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var session = new CaptureSession(paths.LoadConfig(), paths);
        using var cancellation = new CancellationTokenSource();
        var controls = ControlServer.RunAsync(paths, session, cancellation.Token);
        try { await session.RunAsync(); }
        finally
        {
            cancellation.Cancel();
            await controls;
        }
        return session.Status.State == "Complete" ? 0 : 1;
    }

    private static async Task<int> CheckForUpdateAsync(string? updateFeed)
    {
        AppUpdates.InitializeProcess();
        var updates = new AppUpdates(updateFeed);
        var available = await updates.CheckAndDownloadAsync(percent => Report($"progress {percent} 100"), CancellationToken.None);
        Report(available is null ? "current" : $"ready {available}");
        return 0;
    }

    private static async Task<int> DownloadSummaryModelAsync()
    {
        // Always the default location: a custom model the user chose is theirs to manage.
        var destination = Path.GetDirectoryName(new AppConfig().ResolvedSummaryModelPath)!;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await new ModelDownloader(http).DownloadAsync(ModelDownloader.SummaryModel, destination,
            (received, total) => Report($"progress {received} {total}"), CancellationToken.None);
        return 0;
    }

    public static Dictionary<string, string> ParseOptions(string[] arguments)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Options use --name value pairs. Quote paths containing spaces.");
            if (!options.TryAdd(arguments[index][2..], arguments[index + 1])) throw new ArgumentException("Duplicate option.");
        }
        return options;
    }
}
