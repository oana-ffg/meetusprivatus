using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meetu;

[JsonConverter(typeof(JsonStringEnumConverter<SummaryProcessor>))]
public enum SummaryProcessor { Cpu, Gpu }

// An update replaces the "current" folder the program runs from, along with everything in it. Text and the
// large summary model therefore live one level up, beside the launcher, which updates leave alone.
public static class AppPaths
{
    public static string DurableRoot { get; } = ResolveDurableRoot(AppContext.BaseDirectory);

    public static bool IsReplacedByUpdates(string path) => IsInside(path, AppContext.BaseDirectory, DurableRoot);

    // Only an installed layout has a folder that updates replace, which is when the program folder and durable root differ.
    internal static bool IsInside(string path, string programDirectory, string durableRoot)
    {
        var program = Path.GetFullPath(programDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (program.Equals(Path.GetFullPath(durableRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(program, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveDurableRoot(string programDirectory)
    {
        var directory = programDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(directory);
        return parent is not null && Path.GetFileName(directory).Equals("current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent, "Update.exe")) ? parent : directory;
    }
}

public sealed record AppConfig
{
    public string? OutputDirectory { get; init; }
    // Null selects the models folder beside the executable, so saved settings survive moving or upgrading the app.
    public string? ModelPath { get; init; }
    public string? VadPath { get; init; }
    public string? MicrophoneId { get; init; }
    public string? PlaybackId { get; init; }
    public int Threads { get; init; } = 1;
    public int ClipSeconds { get; init; } = 20;
    public int MaxSessionMinutes { get; init; } = 60;
    // Null selects the default summary model beside the executable. Any local GGUF file can be chosen instead.
    public string? SummaryModelPath { get; init; }
    public SummaryProcessor SummaryProcessor { get; init; } = SummaryProcessor.Cpu;
    public bool SummarizeAutomatically { get; init; } = true;
    public bool HideWindowFromScreenSharing { get; init; } = true;

    private const string DefaultModelFile = "ggml-base.en-q5_1.bin";
    private const string DefaultVadFile = "ggml-silero-v5.1.2.bin";
    public const string DefaultSummaryModelFile = "Qwen3.5-4B-Q4_K_M.gguf";

    [JsonIgnore] public string ResolvedModelPath => ModelPath ?? Path.Combine(AppContext.BaseDirectory, "models", DefaultModelFile);
    [JsonIgnore] public string ResolvedVadPath => VadPath ?? Path.Combine(AppContext.BaseDirectory, "models", DefaultVadFile);
    [JsonIgnore] public string ResolvedSummaryModelPath => SummaryModelPath ?? Path.Combine(AppPaths.DurableRoot, "models", DefaultSummaryModelFile);
    [JsonIgnore] public string ResolvedOutputDirectory => OutputDirectory ?? Path.Combine(AppPaths.DurableRoot, "transcripts");

    // Earlier versions saved the install folder's default models. When that copy is gone, follow the
    // app's own models folder instead of requiring a manual reset. Other model choices are kept as-is.
    public AppConfig WithMissingDefaultModelsUnpinned() => this with
    {
        ModelPath = Unpinned(ModelPath, DefaultModelFile),
        VadPath = Unpinned(VadPath, DefaultVadFile)
    };

    private static string? Unpinned(string? path, string defaultFile)
    {
        if (path is null || !Path.GetFileName(path).Equals(defaultFile, StringComparison.OrdinalIgnoreCase)) return path;
        // An old local install can be on a drive that is no longer mounted. Inspect only
        // its drive type before resetting it; never follow a network or linked path.
        if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Unknown or DriveType.NoRootDirectory)
            return null;
        LocalPath.Require(path);
        return File.Exists(path) ? path : null;
    }

    [JsonIgnore] public bool HasStorageReplacedByUpdates =>
        AppPaths.IsReplacedByUpdates(ResolvedOutputDirectory) || AppPaths.IsReplacedByUpdates(ResolvedSummaryModelPath);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResolvedOutputDirectory) || !Path.IsPathFullyQualified(ResolvedOutputDirectory))
            throw new ArgumentException("Choose an absolute folder for saved meetings in Settings.");
        LocalPath.Require(ResolvedOutputDirectory);
        LocalPath.Require(ResolvedModelPath);
        LocalPath.Require(ResolvedVadPath);
        // Existence is not required: recording works without a summary model, which is a separate setup download.
        LocalPath.Require(ResolvedSummaryModelPath);
        if (HasStorageReplacedByUpdates)
            throw new ArgumentException("Saved meetings and the summary model cannot live in the app's \"current\" folder, because updates replace it. Choose another location.");
        if (!File.Exists(ResolvedModelPath))
            throw new ArgumentException("The speech model is missing from this installation. Extract the complete release again.");
        if (!File.Exists(ResolvedVadPath))
            throw new ArgumentException("The speech detector is missing from this installation. Extract the complete release again.");
        if (Threads is < 1 or > 2) throw new ArgumentException("Recognition threads must be 1 or 2.");
        if (ClipSeconds is < 5 or > 30) throw new ArgumentException("Clip length must be 5 to 30 seconds.");
        if (MaxSessionMinutes is < 1 or > 1440) throw new ArgumentException("Maximum recording duration must be 1 to 1440 minutes.");
    }
}

public static class LocalPath
{
    public static void Require(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute local drive path; network and extended-length paths are unsupported.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Local path has no drive root.");
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new ArgumentException("Network drives are unsupported. Choose storage on this machine.");
        // Inspect from the drive root downward, before following any user-supplied link to a share.
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Choose local paths without links, junctions, or cloud placeholder files.");
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }
}

public sealed class StatePaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string StatusFile => Path.Combine(Root, "status.json");
    public string AudioRoot => Path.Combine(Root, "audio");
    public string PipeName { get; }

    public StatePaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetusPrivatus"));
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user identity unavailable.");
        PipeName = "meetus-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + Root.ToUpperInvariant())))[..24];
        PrivateDirectory.Create(Root);
    }

    public AppConfig LoadConfig() => File.Exists(ConfigFile)
        ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile), JsonFiles.Options) ?? throw new InvalidDataException("Configuration is empty.")
        : new AppConfig();
}

public static class PrivateDirectory
{
    public static void Create(string path)
    {
        LocalPath.Require(path);
        // Existing user-selected parents are never re-permissioned.
        if (!Directory.Exists(path))
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user identity unavailable.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An application-owned directory must not be a link or junction.");
    }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // Held open only for the read: on Windows an open reader makes a replacing writer retry (see MoveReplacing).
    public static T Read<T>(string path)
    {
        LocalPath.Require(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException("JSON file is empty.");
    }

    public static void Write<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Options));

    public static void WriteText(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            MoveReplacing(temporary, path, Thread.Sleep);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // The desktop app reads these files while a worker replaces them. Readers open them only briefly, so a
    // short bounded retry absorbs the overlap; persistent contention still fails the write as before.
    // The delay seam lets contention tests synchronize with a real reader instead of racing a timer.
    internal static void MoveReplacing(string temporary, string path, Action<TimeSpan> delay)
    {
        const int maximumRetries = 5;
        for (var retry = 0; ; retry++)
        {
            try { File.Move(temporary, path, true); return; }
            catch (Exception exception) when (retry < maximumRetries && exception is IOException or UnauthorizedAccessException)
            { delay(TimeSpan.FromMilliseconds(50)); }
        }
    }
}
