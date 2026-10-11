using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace Meetu;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("Meetus Privatus requires Windows 11.");
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            var options = ParseOptions(args[1..]);
            var paths = new StatePaths(options.GetValueOrDefault("state-dir"));
            switch (args[0])
            {
                case "config":
                    RequireOptions(options, "state-dir", "output-dir", "model", "vad", "mic", "playback", "threads", "clip-seconds", "max-minutes");
                    var config = paths.LoadConfig().WithMissingDefaultModelsUnpinned();
                    config = WithDefaultOutput(paths, config with
                    {
                        OutputDirectory = AbsoluteOption(options, "output-dir", config.OutputDirectory),
                        ModelPath = options.TryGetValue("model", out var model) ? DefaultableAbsolute(model) : config.ModelPath,
                        VadPath = options.TryGetValue("vad", out var vad) ? DefaultableAbsolute(vad) : config.VadPath,
                        MicrophoneId = options.TryGetValue("mic", out var mic) ? (mic == "default" ? null : mic) : config.MicrophoneId,
                        PlaybackId = options.TryGetValue("playback", out var playback) ? (playback == "default" ? null : playback) : config.PlaybackId,
                        Threads = IntOption(options, "threads", config.Threads),
                        ClipSeconds = IntOption(options, "clip-seconds", config.ClipSeconds),
                        MaxSessionMinutes = IntOption(options, "max-minutes", config.MaxSessionMinutes)
                    });
                    if (options.Keys.Any(key => key != "state-dir")) { config.Validate(); JsonFiles.Write(paths.ConfigFile, config); }
                    Console.WriteLine(JsonSerializer.Serialize(config, JsonFiles.Options)); return 0;
                case "devices":
                    RequireOptions(options, "state-dir");
                    using (var enumerator = new MMDeviceEnumerator())
                        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                        { using (device) Console.WriteLine($"{(device.DataFlow == DataFlow.Capture ? "input" : "output")}\t{device.ID}\t{device.FriendlyName}"); }
                    return 0;
                case "start":
                case "run":
                {
                    RequireOptions(options, "state-dir");
                    if (await ControlServer.RequestAsync(paths) is not null) throw new InvalidOperationException("A recording is already running. Stop it in its existing window before starting another.");
                    Cleanup(paths);
                    PrepareConfig(paths);
                    return args[0] == "run" ? await RunWorkerAsync(paths, interactive: true) : await StartBackgroundAsync(paths);
                }
                case "cleanup":
                    RequireOptions(options, "state-dir");
                    Cleanup(paths);
                    Console.WriteLine("Abandoned app-owned audio cleaned; committed transcripts preserved."); return 0;
                case "worker":
                    RequireOptions(options, "state-dir");
                    return await RunWorkerAsync(paths);
                case "stop":
                case "status":
                    RequireOptions(options, "state-dir");
                    var response = await ControlServer.RequestAsync(paths, args[0] == "stop");
                    if (response is null && File.Exists(paths.StatusFile))
                    {
                        response = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(paths.StatusFile), JsonFiles.Options);
                        if (response?.State is "Starting" or "Capturing" or "Finishing")
                            response = response with { State = "Interrupted", Error = "Worker unavailable. Committed text is preserved; abandoned audio is removed on next start." };
                    }
                    Console.WriteLine(JsonSerializer.Serialize(response ?? new SessionStatus("Idle"), JsonFiles.Options));
                    return response?.State is "Failed" or "Interrupted" ? 1 : 0;
                case "repair":
                    RequireOptions(options, "state-dir", "session-dir");
                    // Text-only repair; never replay audio after a crash.
                    var sessionDirectory = Path.GetFullPath(options.GetValueOrDefault("session-dir") ?? throw new ArgumentException("repair requires --session-dir."));
                    LocalPath.Require(sessionDirectory);
                    if (!File.Exists(Path.Combine(sessionDirectory, "session.json")) || !Directory.Exists(Path.Combine(sessionDirectory, "segments")))
                        throw new ArgumentException("This is not a recognizable meeting transcript directory.");
                    // A live worker may still commit to this session; never overwrite its text views.
                    using (AcquireWorkerLock(paths)) new TranscriptStore(sessionDirectory).Render();
                    Console.WriteLine("Text files rebuilt from committed segment records."); return 0;
                default: throw new ArgumentException("Unknown command. Use meetus help.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            if (args.FirstOrDefault() == "run" && !Console.IsInputRedirected)
            { Console.WriteLine("Press any key to close."); Console.ReadKey(intercept: true); }
            return 1;
        }
    }

    private static void PrepareConfig(StatePaths paths)
    {
        var saved = paths.LoadConfig();
        var config = WithDefaultOutput(paths, saved.WithMissingDefaultModelsUnpinned());
        config.Validate();
        if (config != saved) JsonFiles.Write(paths.ConfigFile, config);
    }

    // First use saves the private transcripts folder, whether it starts with run, start, or config.
    private static AppConfig WithDefaultOutput(StatePaths paths, AppConfig config) =>
        config.OutputDirectory is null ? config with { OutputDirectory = Path.Combine(paths.Root, "transcripts") } : config;

    // The worker holds this exclusive handle for its lifetime; cleanup and repair take it briefly.
    private static FileStream AcquireWorkerLock(StatePaths paths)
    {
        try { return new FileStream(Path.Combine(paths.Root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        { throw new InvalidOperationException("A recording is still running or finishing. Wait for it to complete, then retry."); }
    }

    private static void Cleanup(StatePaths paths)
    {
        using var workerLock = AcquireWorkerLock(paths);
        ClipWorkspace.CleanupAbandoned(paths.AudioRoot);
    }

    private static async Task<int> RunWorkerAsync(StatePaths paths, bool interactive = false)
    {
        // The handle is held for the worker lifetime, including cleanup and model disposal.
        using var workerLock = AcquireWorkerLock(paths);
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        void ShowStatus(SessionStatus status)
        {
            if (!interactive) return;
            if (status.State == "Capturing") Console.WriteLine("Recording microphone and output. Press Ctrl+C to stop.");
            else if (status.State == "Finishing") Console.WriteLine("Capture stopped. Finishing pending text and deleting temporary audio...");
        }
        var config = paths.LoadConfig();
        var session = new CaptureSession(config, paths, ShowStatus);
        ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; session.Stop(); };
        if (interactive)
        {
            Console.CancelKeyPress += onCancel;
            Console.WriteLine($"Starting local transcription. Auto-stop after {config.MaxSessionMinutes} minutes; Ctrl+C stops earlier.");
        }
        using var cancellation = new CancellationTokenSource();
        var controls = ControlServer.RunAsync(paths, session, cancellation.Token);
        try { await session.RunAsync(); }
        finally
        {
            cancellation.Cancel(); await controls;
            if (interactive) Console.CancelKeyPress -= onCancel;
        }
        if (interactive)
        {
            Console.WriteLine(session.Status.State == "Complete"
                ? $"Finished. Transcripts: {session.Status.SessionDirectory ?? config.OutputDirectory}"
                : session.Status.Error);
            if (session.Status.State != "Complete" && !Console.IsInputRedirected)
            { Console.WriteLine("Press any key to close."); Console.ReadKey(intercept: true); }
        }
        return session.Status.State == "Complete" ? 0 : 1;
    }

    private static async Task<int> StartBackgroundAsync(StatePaths paths)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[] { "worker", "--state-dir", paths.Root }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the background worker.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var current = await ControlServer.RequestAsync(paths);
            if (current?.State == "Capturing") { Console.WriteLine($"Capturing. Text destination: {current.SessionDirectory}"); return 0; }
            if (child.HasExited)
            {
                var status = File.Exists(paths.StatusFile) ? File.ReadAllText(paths.StatusFile) : "Worker exited before capture started.";
                Console.Error.WriteLine(status); return 1;
            }
            await Task.Delay(200);
        }
        Console.Error.WriteLine("Worker is still starting. Use status to check it, or stop to cancel."); return 1;
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
    private static void RequireOptions(Dictionary<string, string> options, params string[] allowed)
    {
        if (options.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("Unknown option. Use meetus help.");
    }
    private static int IntOption(Dictionary<string, string> options, string key, int fallback) => options.TryGetValue(key, out var value)
        ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static string? AbsoluteOption(Dictionary<string, string> options, string key, string? fallback) => options.TryGetValue(key, out var value) ? Path.GetFullPath(value) : fallback;
    private static string? DefaultableAbsolute(string value) => value == "default" ? null : Path.GetFullPath(value);

    private static void PrintHelp() => Console.WriteLine("""
        Meetus Privatus — private, CPU-only meeting transcripts

        config [--output-dir PATH] [--model FILE|default] [--vad FILE|default] [--mic ID|default] [--playback ID|default]
               [--threads 1|2] [--clip-seconds 5..30] [--max-minutes 1..1440]
        config                     Show saved configuration
        devices                    List input and output device IDs
        run                        Record in this console; Ctrl+C stops capture (Start.cmd uses this)
        start                      Start hidden capture of mic + selected output device
        status                     Inspect state, backlog, errors, and text destination
        stop                       Stop capture and finish pending text in the background
        cleanup                    Remove abandoned owned audio after a crash (refuses a live worker)
        repair --session-dir PATH  Rebuild text from committed segment records after interruption

        All commands accept --state-dir PATH (default: %LOCALAPPDATA%\MeetusPrivatus).
        Capture includes mic speech while muted in Zoom/Teams. Use stop to stop the assistant.
        Playback includes every app on that output device. No Zoom/Teams selection is needed.
        Capture auto-stops after 60 minutes by default. Pending text finishes afterward.
        Models must already be local. No runtime downloads, network access, GUI, or live text display.
        """);
}
