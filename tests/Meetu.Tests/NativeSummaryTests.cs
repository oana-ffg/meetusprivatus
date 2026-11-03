using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class NativeSummaryFactAttribute : FactAttribute
{
    public NativeSummaryFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEETUS_SUMMARY_MODEL")))
            Skip = "Set MEETUS_SUMMARY_MODEL to a local GGUF file to run the real summary model.";
    }
}

// The real model and llama.cpp on an entirely synthetic meeting. It establishes that the runtime, the chat template,
// and the line format work together, and it guards library and model upgrades. It is not a quality measurement.
public sealed class NativeSummaryTests
{
    [NativeSummaryFact]
    public async Task RealModelProducesActionItemsGroundedInTheTranscript()
    {
        // The native library choice is per process, so scripts/test-native.ps1 runs the GPU case in its own test run.
        var useGpu = Environment.GetEnvironmentVariable("MEETUS_SUMMARY_GPU") == "1";
        var script = new (AudioSource Source, string Text)[]
        {
            (AudioSource.Remote, "Okay let's get started, this is the weekly sync for the Harbor launch."),
            (AudioSource.Remote, "The iOS build went through review on Tuesday, Android is still blocked on the payment SDK upgrade."),
            (AudioSource.Local, "So the decision is that iOS ships November 18th no matter what, and Android follows when it's ready."),
            (AudioSource.Remote, "Works for me. I'm Priya, I'll update the release plan and send it out by Friday."),
            (AudioSource.Local, "I can talk to Rafael in legal about the pricing table. I'll get that sorted by Wednesday."),
            (AudioSource.Remote, "Great, thanks everyone, talk next week.")
        };
        var transcript = script.Select((line, index) => new TranscriptLine(index * 40.0, index * 40.0 + 5, line.Source, line.Text, false)).ToList();
        using var model = new LlamaTextModel(Environment.GetEnvironmentVariable("MEETUS_SUMMARY_MODEL")!, useGpu);
        Assert.Equal(useGpu, model.UsesGpu);
        var summary = await SummaryJob.SummarizeAsync(model, transcript, "model", "test", _ => { });
        Assert.Equal(SummaryState.Complete, summary.State);
        var notes = summary.Notes!;
        Assert.NotEmpty(notes.Overview);
        Assert.NotEmpty(notes.Title);
        Assert.NotEmpty(notes.ActionItems);
        // Whatever the model chose to say, these fields can only hold what the transcript supports.
        var spoken = string.Join(' ', transcript.Select(line => line.Text));
        var times = transcript.Select(line => TranscriptText.Timestamp(line.StartSeconds)).ToHashSet();
        Assert.All(notes.ActionItems, item =>
        {
            Assert.True(item.Owner is null or "Me" || spoken.Contains(item.Owner, StringComparison.OrdinalIgnoreCase));
            Assert.True(item.Due is null || spoken.Contains(item.Due, StringComparison.OrdinalIgnoreCase));
            Assert.True(item.Time is null || times.Contains(item.Time));
        });
    }
}
