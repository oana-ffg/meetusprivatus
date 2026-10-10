using Meetu;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Xunit;

namespace Meetu.Tests;

public sealed class BaselineFactAttribute : FactAttribute
{
    public BaselineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEETUS_BASELINE_TESTS") != "1")
            Skip = "Run scripts/test-native.ps1 to validate the baseline CPU runtime in a fresh process.";
    }
}

public sealed class BaselineRuntimeTests
{
    [BaselineFact]
    public async Task BaselineCpuLibrarySupportsWhisperAndSpeechDetection()
    {
        using var directory = new TestDirectory();
        var config = NativeRecognitionTests.Configuration(directory.Path);
        // The native loader is process-global. Initialize baseline explicitly in this separate test run.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.CpuNoAvx];
        using var initialize = WhisperFactory.FromPath(config.ModelPath, new WhisperFactoryOptions { UseGpu = false });
        Assert.Equal(RuntimeLibrary.CpuNoAvx, RuntimeOptions.LoadedLibrary);
        await using var recognizer = new Recognizer(config);
        using var wave = new NAudio.Wave.WaveFileReader(Environment.GetEnvironmentVariable("MEETUS_FIXTURE")!);
        var bytes = new byte[checked((int)wave.Length)]; wave.ReadExactly(bytes);
        var path = Path.Combine(directory.Path, "baseline.pcm"); await File.WriteAllBytesAsync(path, bytes);
        var result = await recognizer.TranscribeAsync(new AudioClip(Guid.NewGuid().ToString("N"), AudioSource.Local,
            path, 0, bytes.Length / 2, System.Diagnostics.Stopwatch.GetTimestamp()), CancellationToken.None);
        Assert.Contains("country", string.Join(' ', result.Segments.Select(segment => segment.Text)), StringComparison.OrdinalIgnoreCase);
    }
}
