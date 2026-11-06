namespace Meetu;

public enum SpeechEngine { Whisper, Parakeet }

public enum SpeechModelState { Missing, Partial, Installed }

// One transcription model the user can select. Location is a file for Whisper and a folder for Parakeet, whose model
// is several files. A model with no downloads ships inside the release and can be selected offline.
public sealed record SpeechModel(string Id, string Name, string Description, SpeechEngine Engine, string Location, IReadOnlyList<ModelAsset> Downloads)
{
    public bool IsIncluded => Downloads.Count == 0;
    public long DownloadBytes => Downloads.Sum(asset => asset.Size);
}

public static class SpeechModels
{
    private const string WhisperRevision = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string ParakeetRevision = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce/";

    public const string IncludedWhisperId = "whisper-base.en";

    public static IReadOnlyList<SpeechModel> All { get; } =
    [
        Whisper("whisper-tiny.en", "Whisper tiny.en", "The fastest and lightest. It makes the most mistakes.",
            new ModelAsset("ggml-tiny.en-q5_1.bin", 32_166_155, "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b", new Uri(WhisperRevision + "ggml-tiny.en-q5_1.bin"))),
        new(IncludedWhisperId, "Whisper base.en", "Included with the app. A balance of speed and accuracy.", SpeechEngine.Whisper, "ggml-base.en-q5_1.bin", []),
        Whisper("whisper-small.en", "Whisper small.en", "More accurate than base.en, but much slower. It can fall behind a meeting, especially on a laptop.",
            new ModelAsset("ggml-small.en-q5_1.bin", 190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", new Uri(WhisperRevision + "ggml-small.en-q5_1.bin"))),
        new("parakeet-tdt-0.6b-v3", "Parakeet TDT 0.6 v3", "NVIDIA's multilingual model. Fast and accurate in testing, but it uses about 1 GB of memory and detects the language on its own.",
            SpeechEngine.Parakeet, "parakeet-tdt-0.6b-v3-int8",
            [
                Parakeet("vocab.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
                Parakeet("nemo128.onnx", 139_764, "a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f"),
                Parakeet("decoder_joint-model.int8.onnx", 18_202_004, "eea7483ee3d1a30375daedc8ed83e3960c91b098812127a0d99d1c8977667a70"),
                Parakeet("encoder-model.int8.onnx", 652_183_999, "6139d2fa7e1b086097b277c7149725edbab89cc7c7ae64b23c741be4055aff09")
            ])
    ];

    public static SpeechModel? Find(string? id) => All.FirstOrDefault(model => model.Id == id);

    private static SpeechModel Whisper(string id, string name, string description, ModelAsset asset) =>
        new(id, name, description, SpeechEngine.Whisper, asset.FileName, [asset]);

    private static ModelAsset Parakeet(string file, long size, string sha256) => new(file, size, sha256, new Uri(ParakeetRevision + file));
}

// What is on disk for a model. Downloads live in the durable models folder, which updates leave alone.
public static class SpeechModelFiles
{
    public static string Locate(SpeechModel model, string downloadedModelsDirectory) =>
        Path.Combine(model.IsIncluded ? AppPaths.IncludedModelsDirectory : downloadedModelsDirectory, model.Location);

    public static string DownloadDirectory(SpeechModel model, string downloadedModelsDirectory) =>
        model.Engine == SpeechEngine.Parakeet ? Locate(model, downloadedModelsDirectory) : downloadedModelsDirectory;

    // Throws ArgumentException for a network path or a link, which is refused before it can be followed.
    public static SpeechModelState GetState(SpeechModel model, string downloadedModelsDirectory)
    {
        if (model.IsIncluded)
        {
            var included = Locate(model, downloadedModelsDirectory);
            LocalPath.Require(included);
            return File.Exists(included) ? SpeechModelState.Installed : SpeechModelState.Missing;
        }
        var directory = DownloadDirectory(model, downloadedModelsDirectory);
        foreach (var asset in model.Downloads)
        {
            LocalPath.Require(Path.Combine(directory, asset.FileName));
            LocalPath.Require(Path.Combine(directory, asset.FileName + ModelDownloader.PartialSuffix));
        }
        // The download verified the hash. Size catches a damaged file without reading hundreds of megabytes at each start.
        var complete = model.Downloads.All(asset => new FileInfo(Path.Combine(directory, asset.FileName)) is { Exists: true } file && file.Length == asset.Size);
        if (complete) return SpeechModelState.Installed;
        return model.Downloads.Any(asset => File.Exists(Path.Combine(directory, asset.FileName)) || File.Exists(Path.Combine(directory, asset.FileName + ModelDownloader.PartialSuffix)))
            ? SpeechModelState.Partial : SpeechModelState.Missing;
    }

    // Removes only the files this model's download created, including partial ones. Anything else in the folder is the user's.
    public static void Delete(SpeechModel model, string downloadedModelsDirectory)
    {
        if (model.IsIncluded) throw new InvalidOperationException("A model included with the app cannot be deleted.");
        var directory = DownloadDirectory(model, downloadedModelsDirectory);
        foreach (var asset in model.Downloads)
        {
            foreach (var path in new[] { Path.Combine(directory, asset.FileName), Path.Combine(directory, asset.FileName + ModelDownloader.PartialSuffix) })
            {
                // A link could lead anywhere, so it is refused rather than followed or removed.
                LocalPath.Require(path);
                File.Delete(path);
            }
        }
        if (model.Engine == SpeechEngine.Parakeet && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
