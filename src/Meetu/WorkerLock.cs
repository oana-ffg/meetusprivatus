namespace Meetu;

// The worker owns this handle from before model/capture setup until cleanup finishes.
// Controllers may probe it, but only acquiring it grants permission to start or clean up.
internal static class WorkerLock
{
    public static FileStream Acquire(StatePaths paths)
    {
        try { return Open(paths, FileMode.OpenOrCreate); }
        catch (IOException exception) when (IsSharingViolation(exception))
        { throw new InvalidOperationException("A recording is still running or finishing. Wait for it to complete, then retry."); }
    }

    public static bool IsHeld(StatePaths paths)
    {
        try
        {
            // Open, rather than OpenOrCreate: status/stop must not create a lock file.
            using var probe = Open(paths, FileMode.Open);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException exception) when (IsSharingViolation(exception)) { return true; }
    }

    private static FileStream Open(StatePaths paths, FileMode mode) =>
        new(Path.Combine(paths.Root, "worker.lock"), mode, FileAccess.ReadWrite, FileShare.None);

    private static bool IsSharingViolation(IOException exception) => exception.HResult == unchecked((int)0x80070020);
}
