namespace Meetu;

// The worker owns this handle from before model/capture setup until cleanup finishes.
// Controllers may probe it, but only acquiring it grants permission to start or clean up.
internal static class WorkerLock
{
    private const int MaximumRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static FileStream Acquire(StatePaths paths) => Acquire(paths, Thread.Sleep);

    // The delay seam lets contention tests synchronize with a real probe instead of racing a timer.
    internal static FileStream Acquire(StatePaths paths, Action<TimeSpan> delay)
    {
        for (var retry = 0; ; retry++)
        {
            try { return Open(paths, FileMode.OpenOrCreate); }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (retry == MaximumRetries)
                    throw new InvalidOperationException("A recording is still running or finishing. Wait for it to complete, then retry.");
                // A controller probe can briefly own the handle without a recording being active.
                // Every attempt still requires the same exclusive open; waiting grants no ownership.
                delay(RetryDelay);
            }
        }
    }

    public static bool IsHeld(StatePaths paths) => IsHeld(paths, null);

    internal static bool IsHeld(StatePaths paths, Action? probeOpened)
    {
        try
        {
            // Open, rather than OpenOrCreate: status/stop must not create a lock file.
            using var probe = Open(paths, FileMode.Open);
            probeOpened?.Invoke();
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException exception) when (IsSharingViolation(exception)) { return true; }
    }

    private static FileStream Open(StatePaths paths, FileMode mode) =>
        new(Path.Combine(paths.Root, "worker.lock"), mode, FileAccess.ReadWrite, FileShare.None);

    private static bool IsSharingViolation(IOException exception) => exception.HResult == unchecked((int)0x80070020);
}
