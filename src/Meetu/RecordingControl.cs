using System.Diagnostics;

namespace Meetu;

// Shared by the command line and the desktop app, so both start and stop recordings identically.
public static class RecordingControl
{
    // True while a worker owns its lock, including when it is too busy to answer the pipe. A null
    // status alone does not mean a recording ended: the request also returns null on a timeout.
    public static bool IsWorkerRunning(StatePaths paths) => WorkerLock.IsHeld(paths);

    public static SessionStatus ReadStatus(SessionStatus? live, StatePaths paths)
    {
        if (live is not null) return live;
        if (!File.Exists(paths.StatusFile)) return new SessionStatus("Idle");
        var saved = JsonFiles.Read<SessionStatus>(paths.StatusFile);
        return saved.State is "Starting" or "Capturing" or "Finishing"
            ? saved with { State = "Interrupted", Error = "Worker unavailable. Committed text is preserved; abandoned audio is removed on next start." }
            : saved;
    }

    public static void CleanupAbandonedAudio(StatePaths paths)
    {
        using var workerLock = WorkerLock.Acquire(paths);
        ClipWorkspace.CleanupAbandoned(paths.AudioRoot);
    }

    // Migrate the previous standard destination, without moving existing text or pinning this install.
    public static AppConfig MigrateDefaultOutput(StatePaths paths, AppConfig config) =>
        string.Equals(config.OutputDirectory, Path.Combine(paths.Root, "transcripts"), StringComparison.OrdinalIgnoreCase)
            ? config with { OutputDirectory = null } : config;

    public static async Task PrepareStartAsync(StatePaths paths)
    {
        if (await ControlServer.RequestAsync(paths) is not null)
            throw new InvalidOperationException("A recording is already running. Stop it before starting another.");
        CleanupAbandonedAudio(paths);
        var config = MigrateDefaultOutput(paths, paths.LoadConfig().WithMissingDefaultModelsUnpinned());
        config.Validate();
        // Also materialize implicit defaults from manually supplied/older minimal settings files.
        JsonFiles.Write(paths.ConfigFile, config);
    }

    // Returns once capture runs. Throws with a content-free reason if the worker never gets there.
    public static async Task<SessionStatus> StartBackgroundAsync(StatePaths paths, string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "worker", "--state-dir", paths.Root }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the background worker.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var current = await ControlServer.RequestAsync(paths);
            if (current?.State == "Capturing") return current;
            if (child.HasExited)
                throw new InvalidOperationException(File.Exists(paths.StatusFile)
                    ? JsonFiles.Read<SessionStatus>(paths.StatusFile).Error ?? "Worker exited before capture started."
                    : "Worker exited before capture started.");
            // Requests can now return immediately before the child owns its lock.
            // Keep startup polling paced even when no controller pipe exists yet.
            await Task.Delay(200);
        }
        throw new TimeoutException("The recording is taking too long to start. If it does start, the app will show it.");
    }
}
