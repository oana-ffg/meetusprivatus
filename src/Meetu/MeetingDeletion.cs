using System.Text.RegularExpressions;

namespace Meetu;

// Permanently removes a saved meeting when the user asks. Only files the app wrote are removed: anything else in the
// folder belongs to the user and stays, along with the folder. The metadata goes last, so an interrupted deletion
// leaves a meeting that is still listed and can be deleted again, rather than hidden private text.
public static partial class MeetingDeletion
{
    private const int MaximumRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    // The app's atomic writes can leave a uniquely named temporary file behind if they are interrupted.
    [GeneratedRegex(@"^(local\.txt|remote\.txt|summary\.json|summary\.md|session\.json)(\.[0-9a-f]{32}\.tmp)?$")]
    private static partial Regex MeetingFile();

    [GeneratedRegex(@"^[0-9a-f]{32}\.json(\.[0-9a-f]{32}\.tmp)?$")]
    private static partial Regex RecordFile();

    // True when the whole folder is gone, false when other files keep it there.
    public static bool Delete(string directory)
    {
        LocalPath.Require(directory);
        if (!MeetingLibrary.HasSessionDirectoryName(directory) || !MeetingLibrary.IsSessionDirectory(directory))
            throw new ArgumentException("This is not a recognizable meeting folder.");
        var records = Path.Combine(directory, "segments");
        LocalPath.Require(records);
        DeleteMatching(records, RecordFile());
        DeleteMatching(directory, MeetingFile(), SessionMetadata.FileName);
        Retried(() => File.Delete(Path.Combine(directory, SessionMetadata.FileName)));
        return RemoveIfEmpty(records) && RemoveIfEmpty(directory);
    }

    private static void DeleteMatching(string directory, Regex pattern, string? except = null)
    {
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (file.Name == except || !pattern.IsMatch(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            Retried(file.Delete);
        }
    }

    private static bool RemoveIfEmpty(string directory)
    {
        if (Directory.EnumerateFileSystemEntries(directory).Any()) return false;
        Retried(() => Directory.Delete(directory));
        return true;
    }

    // Antivirus and search indexing can hold a file for a moment after it was last used.
    private static void Retried(Action delete)
    {
        for (var retry = 0; ; retry++)
        {
            try { delete(); return; }
            catch (Exception exception) when (retry < MaximumRetries && exception is IOException or UnauthorizedAccessException) { Thread.Sleep(RetryDelay); }
        }
    }
}
