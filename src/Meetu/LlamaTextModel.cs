using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Meetu;

// In-process llama.cpp: no server, no network, no separate runtime to install. Vulkan covers integrated
// and discrete GPUs without vendor-specific libraries; the CPU remains the default and the fallback.
internal sealed class LlamaTextModel : ITextModel
{
    private const int OffloadAllLayers = 999;
    // Reasoning models emit a long private monologue first. On a CPU that took minutes and ran out of tokens
    // before answering, so an already-closed block is prefilled, which is how their own templates turn it off.
    private const string ThinkingOpenToken = "<think>";
    private const string ClosedThinkingBlock = "<think>\n\n</think>\n\n";

    private readonly ModelParams parameters;
    private readonly LLamaWeights weights;
    private readonly string responsePrefill;

    public bool UsesGpu { get; }

    public LlamaTextModel(string modelPath, bool useGpu)
    {
        // Skipping GPU probing in CPU mode also avoids launching vulkaninfo.
        // Native logs are discarded so nothing derived from a prompt can reach a console or file.
        NativeLibraryConfig.All.WithCuda(false).WithVulkan(useGpu).WithLogCallback((_, _) => { });
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        parameters = new ModelParams(modelPath)
        {
            ContextSize = Summarizer.ContextTokens,
            GpuLayerCount = useGpu ? OffloadAllLayers : 0,
            Threads = threads,
            BatchThreads = threads
        };
        weights = LLamaWeights.LoadFromFile(parameters);
        UsesGpu = useGpu && NativeApi.llama_supports_gpu_offload();
        // Other models tokenize the marker as ordinary text, and must not see a block they do not use.
        responsePrefill = weights.Tokenize(ThinkingOpenToken, false, true, Encoding.UTF8).Length == 1 ? ClosedThinkingBlock : "";
    }

    public int CountTokens(string text) => weights.Tokenize(text, false, false, Encoding.UTF8).Length;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var template = new LLamaTemplate(weights.NativeHandle) { AddAssistant = true };
        template.Add("system", systemPrompt);
        template.Add("user", userPrompt);
        var prompt = Encoding.UTF8.GetString(template.Apply()) + responsePrefill;
        var executor = new StatelessExecutor(weights, parameters);
        var inference = new InferenceParams
        {
            MaxTokens = maxOutputTokens,
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, RepeatPenalty = 1.05f }
        };
        var output = new StringBuilder();
        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken)) output.Append(piece);
        return output.ToString();
    }

    public void Dispose() => weights.Dispose();
}
