using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace Meetu;

// NVIDIA Parakeet TDT, exported to ONNX: a feature extractor, an encoder, and a decoder that also predicts how far to
// advance after each step. ONNX Runtime is used directly rather than through a wrapper because the application needs
// two things wrappers hide: turning off the runtime's telemetry, and stopping a run when a clip's deadline passes.
internal sealed partial class ParakeetSpeechEngine : ISpeechEngine
{
    private const string PreprocessorFile = "nemo128.onnx";
    private const string EncoderFile = "encoder-model.int8.onnx";
    private const string DecoderFile = "decoder_joint-model.int8.onnx";
    private const string VocabularyFile = "vocab.txt";
    private const string BlankToken = "<blk>";
    private const char WordStart = '▁';
    private const int MaximumTokensPerStep = 10;
    private const double SecondsPerEncoderFrame = 0.08;
    private const int MinimumSamples = 1600;

    private readonly InferenceSession preprocessor;
    private readonly InferenceSession encoder;
    private readonly InferenceSession decoder;
    private readonly string[] vocabulary;
    private readonly int blank;
    private readonly long[] stateShape1;
    private readonly long[] stateShape2;

    public ParakeetSpeechEngine(string directory, int threads)
    {
        vocabulary = ReadVocabulary(Path.Combine(directory, VocabularyFile));
        blank = Array.IndexOf(vocabulary, BlankToken);
        if (blank < 0) throw new InvalidDataException("The Parakeet vocabulary has no blank token.");
        // Official Windows builds emit Microsoft telemetry unless told not to, so this comes before any session exists.
        OrtEnv.Instance().DisableTelemetryEvents();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        // Threads waiting for work would otherwise spin, taking CPU from the meeting app for nothing.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        preprocessor = new InferenceSession(Path.Combine(directory, PreprocessorFile), options);
        try
        {
            encoder = new InferenceSession(Path.Combine(directory, EncoderFile), options);
            decoder = new InferenceSession(Path.Combine(directory, DecoderFile), options);
        }
        catch { encoder?.Dispose(); preprocessor.Dispose(); throw; }
        stateShape1 = StateShape("input_states_1");
        stateShape2 = StateShape("input_states_2");
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] audio, CancellationToken cancellationToken) =>
        Task.Run(() => Transcribe(audio, cancellationToken), cancellationToken);

    private IReadOnlyList<TranscriptSegment> Transcribe(float[] audio, CancellationToken cancellationToken)
    {
        // The feature extractor fails below 257 samples, and a failure would end the recording. A stop can leave such a
        // fragment, and speech detection ignores anything under this length anyway, so it holds no speech worth keeping.
        if (audio.Length < MinimumSamples) return [];
        using var run = new RunOptions();
        using var stopOnCancel = cancellationToken.Register(() => run.Terminate = true);
        try
        {
            using var waveform = OrtValue.CreateTensorValueFromMemory(audio, [1, audio.Length]);
            using var waveformLength = OrtValue.CreateTensorValueFromMemory(new long[] { audio.Length }, [1]);
            using var features = preprocessor.Run(run, ["waveforms", "waveforms_lens"], [waveform, waveformLength], ["features", "features_lens"]);
            using var encoded = encoder.Run(run, ["audio_signal", "length"], [features[0], features[1]], ["outputs", "encoded_lengths"]);
            var featureSize = (int)encoded[0].GetTensorTypeAndShape().Shape[1];
            var frameCount = (int)encoded[1].GetTensorDataAsSpan<long>()[0];
            var tokens = Decode(encoded[0].GetTensorDataAsSpan<float>().ToArray(), featureSize, frameCount, run, cancellationToken);
            return ToSegments(tokens, SecondsPerEncoderFrame);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Greedy TDT decoding. The encoder output is laid out per feature, then per frame, so one frame is gathered at a time.
    private List<(string Token, int Frame)> Decode(float[] encoded, int featureSize, int frameCount, RunOptions run, CancellationToken cancellationToken)
    {
        var stride = encoded.Length / featureSize;
        var frame = new float[featureSize];
        var target = new[] { blank };
        var state1 = new float[stateShape1.Aggregate(1L, (total, size) => total * size)];
        var state2 = new float[stateShape2.Aggregate(1L, (total, size) => total * size)];
        using var frameValue = OrtValue.CreateTensorValueFromMemory(frame, [1, featureSize, 1]);
        using var targetValue = OrtValue.CreateTensorValueFromMemory(target, [1, 1]);
        using var targetLength = OrtValue.CreateTensorValueFromMemory(new[] { 1 }, [1]);
        using var state1Value = OrtValue.CreateTensorValueFromMemory(state1, stateShape1);
        using var state2Value = OrtValue.CreateTensorValueFromMemory(state2, stateShape2);
        var tokens = new List<(string Token, int Frame)>();
        var position = 0;
        var emitted = 0;
        while (position < frameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var feature = 0; feature < featureSize; feature++) frame[feature] = encoded[feature * stride + position];
            using var output = decoder.Run(run,
                ["encoder_outputs", "targets", "target_length", "input_states_1", "input_states_2"],
                [frameValue, targetValue, targetLength, state1Value, state2Value],
                ["outputs", "output_states_1", "output_states_2"]);
            var scores = output[0].GetTensorDataAsSpan<float>();
            var token = IndexOfMaximum(scores[..vocabulary.Length]);
            var advance = IndexOfMaximum(scores[vocabulary.Length..]);
            if (token != blank)
            {
                tokens.Add((vocabulary[token], position));
                target[0] = token;
                output[1].GetTensorDataAsSpan<float>().CopyTo(state1);
                output[2].GetTensorDataAsSpan<float>().CopyTo(state2);
                emitted++;
            }
            if (advance > 0) { position += advance; emitted = 0; }
            else if (token == blank || emitted == MaximumTokensPerStep) { position++; emitted = 0; }
        }
        return tokens;
    }

    // The model writes one run of text per clip, so it is split at sentence ends to read like Whisper's segments.
    // A full stop inside a number such as "3.5" has no word start after it and does not split.
    internal static IReadOnlyList<TranscriptSegment> ToSegments(IReadOnlyList<(string Token, int Frame)> tokens, double secondsPerFrame)
    {
        var words = tokens.Where(token => !IsControlToken(token.Token)).ToList();
        var segments = new List<TranscriptSegment>();
        var first = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var atEnd = index == words.Count - 1;
            if (!atEnd && !(EndsSentence(words[index].Token) && words[index + 1].Token[0] == WordStart)) continue;
            var text = string.Concat(words.Skip(first).Take(index - first + 1).Select(word => word.Token)).Replace(WordStart, ' ');
            var endFrame = atEnd ? words[index].Frame + 1 : words[index + 1].Frame;
            segments.Add(new TranscriptSegment(words[first].Frame * secondsPerFrame, endFrame * secondsPerFrame,
                SpaceBeforePunctuation().Replace(text, "").Trim()));
            first = index + 1;
        }
        return segments;
    }

    private static bool IsControlToken(string token) => token.Length > 2 && token[0] == '<' && token[^1] == '>';

    private static bool EndsSentence(string token) => token[^1] is '.' or '?' or '!';

    [GeneratedRegex(@"\s+(?=[.,?!;:])")]
    private static partial Regex SpaceBeforePunctuation();

    private static int IndexOfMaximum(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var index = 1; index < values.Length; index++)
            if (values[index] > values[best]) best = index;
        return best;
    }

    // One clip at a time, so the batch dimension the export leaves open is always 1.
    private long[] StateShape(string input) => decoder.InputMetadata[input].Dimensions.Select(size => size < 0 ? 1L : size).ToArray();

    private static string[] ReadVocabulary(string path) => File.ReadLines(path)
        .Select(line => line.Split(' '))
        .OrderBy(parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
        .Select(parts => parts[0])
        .ToArray();

    public ValueTask DisposeAsync()
    {
        decoder.Dispose(); encoder.Dispose(); preprocessor.Dispose();
        return ValueTask.CompletedTask;
    }
}
