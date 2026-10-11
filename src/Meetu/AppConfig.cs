using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meetu;

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

    private const string DefaultModelFile = "ggml-base.en-q5_1.bin";
    private const string DefaultVadFile = "ggml-silero-v5.1.2.bin";

    [JsonIgnore] public string ResolvedModelPath => ModelPath ?? Path.Combine(AppContext.BaseDirectory, "models", DefaultModelFile);
    [JsonIgnore] public string ResolvedVadPath => VadPath ?? Path.Combine(AppContext.BaseDirectory, "models", DefaultVadFile);

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
        LocalPath.Require(path);
        return File.Exists(path) ? path : null;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
            throw new ArgumentException("Configure an absolute output directory with config --output-dir.");
        LocalPath.Require(OutputDirectory);
        LocalPath.Require(ResolvedModelPath);
        LocalPath.Require(ResolvedVadPath);
        if (!File.Exists(ResolvedModelPath))
            throw new ArgumentException("Whisper model missing. Restore the models folder beside meetus.exe (config --model default) or configure --model FILE.");
        if (!File.Exists(ResolvedVadPath))
            throw new ArgumentException("Speech detector missing. Restore the models folder beside meetus.exe (config --vad default) or configure --vad FILE.");
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
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
