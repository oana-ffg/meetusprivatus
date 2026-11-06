using System.Diagnostics;
using Meetu;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace Meetu.Tests;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEETUS_NATIVE_TESTS") != "1")
            Skip = "Set MEETUS_NATIVE_TESTS=1 and supply MEETUS_MODELS / MEETUS_FIXTURE to run offline native checks.";
    }
}

public sealed class ParakeetFactAttribute : FactAttribute
{
    public ParakeetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEETUS_NATIVE_TESTS") != "1" || Environment.GetEnvironmentVariable("MEETUS_PARAKEET_MODEL") is null)
            Skip = "Download Parakeet into the models folder and run scripts/test-native.ps1 to run this check.";
    }
}

public sealed class NativeRecognitionTests(ITestOutputHelper output)
{
    [NativeFact]
    public async Task OutputLoopbackCapturesPublicPlaybackThroughTheProductionPipeline()
    {
        var fixture = Environment.GetEnvironmentVariable("MEETUS_FIXTURE") ?? throw new InvalidOperationException("MEETUS_FIXTURE is required.");
        using var enumerator = new MMDeviceEnumerator();
        using var playback = AudioDevices.OpenPlayback(enumerator, null);
        using var configured = AudioDevices.OpenPlayback(enumerator, playback.ID);
        Assert.Equal(playback.ID, configured.ID);
        Assert.Throws<InvalidOperationException>(() => AudioDevices.OpenMicrophone(enumerator, playback.ID));
        Assert.ThrowsAny<Exception>(() => AudioDevices.OpenPlayback(enumerator, "nonexistent-test-output"));
        // Refuse unrelated active playback before capture and before transcribing any captured data.
        // These tests never open the microphone or persist raw output before the safety check.
        void RequirePublicPlaybackOnly()
        {
            playback.AudioSessionManager.RefreshSessions();
            var sessions = playback.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                Assert.False(session.State == AudioSessionState.AudioSessionStateActive && session.GetProcessID != Environment.ProcessId,
                    "Other playback is active. Close meetings/media before running the public output-capture check.");
            }
        }
        RequirePublicPlaybackOnly();
        await using var recorder = AudioDevices.CreatePlaybackRecorder(playback);
        Assert.Equal(16000, recorder.WaveFormat.SampleRate);
        Assert.Equal(1, recorder.WaveFormat.Channels);
        Assert.Equal(16, recorder.WaveFormat.BitsPerSample);
        var packets = new ConcurrentQueue<AudioPacket>();
        var failures = new ConcurrentQueue<string>();
        var startedTimestamp = Stopwatch.GetTimestamp();
        var timeline = new CaptureTimeline(startedTimestamp, 100);
        recorder.DataAvailable += (buffer, flags, _, qpc) =>
        {
            if (buffer.IsEmpty) return;
            var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
            var timing = timeline.Stamp(buffer.Length / 2, qpc,
                (flags & AudioClientBufferFlags.TimestampError) == 0, discontinuity, Stopwatch.GetTimestamp());
            packets.Enqueue(new AudioPacket(AudioSource.Remote, buffer.ToArray(), timing.StartSeconds,
                discontinuity, timing.CapturedTimestamp));
        };
        recorder.RecordingStopped += (_, args) => { if (args.Exception is not null) failures.Enqueue(args.Exception.GetType().Name); };
        using var wave = new WaveFileReader(fixture);
        using var player = new WasapiPlayerBuilder().WithDevice(playback).WithSharedMode().Build();
        player.Init(new VolumeSampleProvider(wave.ToSampleProvider()) { Volume = 0.05f }.ToWaveProvider());
        recorder.StartRecording();
        try
        {
            player.Play();
            var duration = Stopwatch.StartNew();
            while (duration.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(50);
                RequirePublicPlaybackOnly();
            }
        }
        finally { player.Stop(); recorder.StopRecording(); await recorder.DisposeAsync(); }
        RequirePublicPlaybackOnly();
        Assert.Empty(failures);
        var audio = Recognizer.DecodePcm(packets.SelectMany(packet => packet.Pcm).ToArray());
        Assert.Contains(audio, sample => Math.Abs(sample) > 0.001);
        Assert.All(packets, packet => Assert.NotEmpty(packet.Pcm));
        Assert.All(packets, packet => Assert.InRange(packet.StartSeconds, 0, 4));
        using var directory = new TestDirectory();
        var clips = new List<AudioClip>(); var gaps = new List<TranscriptRecord>();
        using var assembler = new ClipAssembler(AudioSource.Remote, directory.Path, new PendingBudget(), 20, clips.Add, gaps.Add);
        foreach (var packet in packets) assembler.Append(packet);
        assembler.Finish();
        Assert.Empty(gaps);
        await using var recognizer = new Recognizer(Configuration(directory.Path));
        var store = new TranscriptStore(Path.Combine(directory.Path, "transcripts"));
        foreach (var clip in clips)
        {
            store.Commit(await recognizer.TranscribeAsync(clip, CancellationToken.None));
            File.Delete(clip.Path);
        }
        var text = File.ReadAllText(Path.Combine(store.DirectoryPath, "remote.txt"));
        Assert.Contains("fellow", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio from other apps", text);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.pcm"));
        output.WriteLine($"Output loopback: {audio.Length} samples passed through capture, transcription, and audio deletion; estimated timing: {timeline.UsedEstimatedTiming}.");
    }

    [NativeFact]
    public async Task PublicSpeechAndSilenceUseTheLocalCpuPipeline()
    {
        using var directory = new TestDirectory();
        var config = Configuration(directory.Path);
        config.Validate();
        await using var recognizer = new Recognizer(config);
        await ExerciseRecognizerAsync(recognizer, directory.Path);
    }

    [ParakeetFact]
    public async Task ParakeetTranscribesThroughTheSamePipeline()
    {
        using var directory = new TestDirectory();
        ParakeetSpeechEngine? engine = null;
        await using var recognizer = new Recognizer(Configuration(directory.Path), _ => engine = new ParakeetSpeechEngine(Environment.GetEnvironmentVariable("MEETUS_PARAKEET_MODEL")!, 1));
        Assert.Equal("1.28.0", Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString());
        await ExerciseRecognizerAsync(recognizer, directory.Path);

        // A stop can leave a fragment far shorter than a word. Speech detection usually catches these, but one that gets
        // through reaches the model directly, and a failure here would end the whole recording.
        using var wave = new WaveFileReader(Environment.GetEnvironmentVariable("MEETUS_FIXTURE")!);
        var bytes = new byte[checked((int)wave.Length)];
        wave.ReadExactly(bytes);
        var audio = Recognizer.DecodePcm(bytes);
        foreach (var milliseconds in new[] { 1, 10, 50, 100, 200, 400 })
        {
            var segments = await engine!.TranscribeAsync(audio[16000..(16000 + 16 * milliseconds)], CancellationToken.None);
            output.WriteLine($"{milliseconds} ms fragment: {string.Join(' ', segments.Select(segment => segment.Text))}");
        }
    }

    private async Task ExerciseRecognizerAsync(Recognizer recognizer, string workspace)
    {
        var fixture = Environment.GetEnvironmentVariable("MEETUS_FIXTURE") ?? throw new InvalidOperationException("MEETUS_FIXTURE is required.");
        using var wave = new WaveFileReader(fixture);
        Assert.Equal(16000, wave.WaveFormat.SampleRate); Assert.Equal(1, wave.WaveFormat.Channels); Assert.Equal(16, wave.WaveFormat.BitsPerSample);
        var bytes = new byte[checked((int)wave.Length)];
        wave.ReadExactly(bytes);
        var path = Path.Combine(workspace, "public.pcm"); await File.WriteAllBytesAsync(path, bytes);
        var clock = Stopwatch.StartNew();
        var clip = new AudioClip(Guid.NewGuid().ToString("N"), AudioSource.Remote, path, 45, bytes.Length / 2, Stopwatch.GetTimestamp());
        var record = await recognizer.TranscribeAsync(clip, CancellationToken.None);
        output.WriteLine($"Public speech: {bytes.Length / 32000.0:F2}s audio, {clock.Elapsed.TotalSeconds:F2}s CPU recognition (one thread).");
        var text = string.Join(' ', record.Segments.Select(segment => segment.Text)).ToLowerInvariant();
        Assert.Contains("ask not", text); Assert.Contains("country", text);
        Assert.All(record.Segments, segment => Assert.InRange(segment.StartSeconds, clip.StartSeconds, clip.EndSeconds));
        var shortSpeech = bytes[..24000]; // 750 ms of the same public speech, as at a manual-stop boundary.
        await File.WriteAllBytesAsync(path, shortSpeech);
        var shortRecord = await recognizer.TranscribeAsync(clip with { SampleCount = shortSpeech.Length / 2 }, CancellationToken.None);
        Assert.NotEmpty(shortRecord.Segments);
        await File.WriteAllBytesAsync(path, bytes);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.TranscribeAsync(clip, deadline.Token));
        var resumed = await recognizer.TranscribeAsync(clip, CancellationToken.None);
        Assert.Contains("country", string.Join(' ', resumed.Segments.Select(segment => segment.Text)), StringComparison.OrdinalIgnoreCase);
        await File.WriteAllBytesAsync(path, new byte[16000 * 2 * 5]);
        var silence = await recognizer.TranscribeAsync(clip with { SampleCount = 16000 * 5 }, CancellationToken.None);
        Assert.Empty(silence.Segments);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognizer.TranscribeAsync(clip, cancelled.Token));
    }

    internal static AppConfig Configuration(string outputDirectory)
    {
        var models = Environment.GetEnvironmentVariable("MEETUS_MODELS") ?? throw new InvalidOperationException("MEETUS_MODELS is required.");
        return new AppConfig { SpeechModelId = SpeechModels.IncludedWhisperId, OutputDirectory = outputDirectory, ModelPath = Path.Combine(models, "ggml-base.en-q5_1.bin"),
            VadPath = Path.Combine(models, "ggml-silero-v5.1.2.bin"), Threads = 1 };
    }
}
