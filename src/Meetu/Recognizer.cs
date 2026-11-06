using System.Buffers.Binary;
using Whisper.net;
using Whisper.net.LibraryLoader;
using System.Runtime.Intrinsics.X86;

namespace Meetu;

internal interface IClipRecognizer : IAsyncDisposable
{
    Task<TranscriptRecord> TranscribeAsync(AudioClip clip, CancellationToken cancellationToken);
}

// Transcribes the audio of one clip, with times relative to its start.
internal interface ISpeechEngine : IAsyncDisposable
{
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] audio, CancellationToken cancellationToken);
}

internal sealed class WhisperSpeechEngine : ISpeechEngine
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;

    public WhisperSpeechEngine(string modelPath, int threads)
    {
        factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        try
        {
            processor = factory.CreateBuilder().WithLanguage("en").WithThreads(threads)
                .WithNoContext().WithTemperature(0).WithTemperatureInc(0).WithGreedySamplingStrategy().Build();
        }
        catch { factory.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] audio, CancellationToken cancellationToken)
    {
        var segments = new List<TranscriptSegment>();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
            segments.Add(new TranscriptSegment(segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text));
        return segments;
    }

    public async ValueTask DisposeAsync()
    {
        await processor.DisposeAsync();
        factory.Dispose();
    }
}

public sealed class Recognizer : IClipRecognizer
{
    private readonly ISpeechEngine engine;
    private readonly WhisperVadFactory vadFactory;
    private readonly WhisperVadProcessor vad;

    public Recognizer(AppConfig config) : this(config, OpenEngine) { }

    internal Recognizer(AppConfig config, Func<AppConfig, ISpeechEngine> openEngine)
    {
        var optimizedCpu = Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported &&
            (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0; // F16C
        // Speech detection uses this library whichever engine transcribes.
        RuntimeOptions.RuntimeLibraryOrder = optimizedCpu ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx] : [RuntimeLibrary.CpuNoAvx];
        vadFactory = WhisperVadFactory.FromPath(config.ResolvedVadPath, new WhisperFactoryOptions { UseGpu = false });
        try
        {
            vad = vadFactory.CreateBuilder().WithUseGpu(false).WithThreads(1)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100)).Build();
            engine = openEngine(config);
        }
        catch { vad?.Dispose(); vadFactory.Dispose(); throw; }
    }

    private static ISpeechEngine OpenEngine(AppConfig config) => config.SelectedSpeechModel?.Engine == SpeechEngine.Parakeet
        ? new ParakeetSpeechEngine(config.ResolvedModelPath, config.Threads)
        : new WhisperSpeechEngine(config.ResolvedModelPath, config.Threads);

    public async Task<TranscriptRecord> TranscribeAsync(AudioClip clip, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[checked(clip.SampleCount * 2)];
        await using (var stream = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length != bytes.Length) throw new InvalidDataException("Temporary audio is incomplete.");
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }
        var audio = DecodePcm(bytes);
        IReadOnlyList<TranscriptSegment> segments = [];
        if (vad.DetectSpeech(audio).Count > 0)
        {
            segments = (await engine.TranscribeAsync(audio, cancellationToken)).Select(segment => new TranscriptSegment(clip.StartSeconds + segment.StartSeconds,
                Math.Min(clip.EndSeconds, clip.StartSeconds + segment.EndSeconds), segment.Text)).ToList();
        }
        return new TranscriptRecord(clip.Id, clip.Source, clip.StartSeconds, clip.EndSeconds, segments);
    }

    public static float[] DecodePcm(byte[] bytes)
    {
        if (bytes.Length % 2 != 0) throw new InvalidDataException("PCM must contain whole samples.");
        var samples = new float[bytes.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2)) / 32768f;
        return samples;
    }

    public async ValueTask DisposeAsync()
    {
        await engine.DisposeAsync(); vad.Dispose(); vadFactory.Dispose();
    }
}
