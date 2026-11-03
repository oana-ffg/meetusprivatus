using Velopack;
using Velopack.Sources;

namespace Meetu;

// Velopack portable updates from this repository's public GitHub Releases. Nothing here contacts the network on its own:
// the app only reads local state and applies an update already downloaded. Checking and downloading run in the
// helper process, and only when the user presses the button, so the app and the recording worker stay offline.
public sealed class AppUpdates
{
    public const string RepositoryUrl = "https://github.com/oana-ffg/meetusprivatus";
    private const string Channel = "win-x64";
    private readonly UpdateManager manager;

    // Must run before anything else in a process that uses this class. During an update the installer starts the
    // main executable with hook arguments and expects it to exit here. Updates apply only when asked, never at startup.
    public static void InitializeProcess() => VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

    // A local feed is only for the update test, which must not depend on published releases.
    public AppUpdates(string? localFeed)
    {
        IUpdateSource source = localFeed is null
            ? new GithubSource(RepositoryUrl, null, prerelease: true)
            : new SimpleFileSource(new DirectoryInfo(localFeed));
        manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = Channel });
    }

    public bool IsInstalled => manager.IsInstalled;
    public string? CurrentVersion => manager.CurrentVersion?.ToString();
    public string? PendingVersion => manager.IsInstalled ? manager.UpdatePendingRestart?.Version.ToString() : null;

    // Returns the version now ready to apply, or null when already up to date.
    public async Task<string?> CheckAndDownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        if (!manager.IsInstalled) throw new InvalidOperationException("Updates are only available in an installed copy.");
        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return null;
        await manager.DownloadUpdatesAsync(update, progress, cancellationToken);
        return update.TargetFullRelease.Version.ToString();
    }

    // The updater waits for this process to exit. Everything else running from the application folder must already have stopped.
    public void ApplyAfterExit(IReadOnlyList<string> restartArguments)
    {
        var pending = manager.UpdatePendingRestart ?? throw new InvalidOperationException("The downloaded update is no longer available. Check for updates again.");
        manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true, restartArgs: restartArguments.ToArray());
    }
}
