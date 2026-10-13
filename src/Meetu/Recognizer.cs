using System.Buffers.Binary;
using Whisper.net;
using Whisper.net.LibraryLoader;
using System.Runtime.Intrinsics.X86;

namespace Meetu;

internal interface IClipRecognizer : IAsyncDisposable
{
    Task<TranscriptRecord> TranscribeAsync(AudioClip clip, CancellationToken cancellationToken);
}

public sealed class Recognizer : IClipRecognizer
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;
    private readonly WhisperVadFactory vadFactory;
    private readonly WhisperVadProcessor vad;

    public Recognizer(AppConfig config)
    {
        var optimizedCpu = Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported &&
            (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0; // F16C
        RuntimeOptions.RuntimeLibraryOrder = optimizedCpu ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx] : [RuntimeLibrary.CpuNoAvx];
        factory = WhisperFactory.FromPath(config.ResolvedModelPath, new WhisperFactoryOptions { UseGpu = false });
        try
        {
            processor = factory.CreateBuilder().WithLanguage("en").WithThreads(config.Threads)
                .WithNoContext().WithTemperature(0).WithTemperatureInc(0).WithGreedySamplingStrategy().Build();
            vadFactory = WhisperVadFactory.FromPath(config.ResolvedVadPath, new WhisperFactoryOptions { UseGpu = false });
            vad = vadFactory.CreateBuilder().WithUseGpu(false).WithThreads(1)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100)).Build();
        }
        catch { vad?.Dispose(); vadFactory?.Dispose(); processor?.Dispose(); factory.Dispose(); throw; }
    }

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
        var segments = new List<TranscriptSegment>();
        if (vad.DetectSpeech(audio).Count > 0)
        {
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
            {
                segments.Add(new TranscriptSegment(clip.StartSeconds + segment.Start.TotalSeconds,
                    Math.Min(clip.EndSeconds, clip.StartSeconds + segment.End.TotalSeconds), segment.Text));
            }
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
        await processor.DisposeAsync(); vad.Dispose(); vadFactory.Dispose(); factory.Dispose();
    }
}
