namespace Meetu.App;

public enum SummaryRunOutcome { Finished, Postponed, Crashed, GpuFailed }

// Runs summaries as a separate low-priority process, so model memory is released afterwards
// and a native failure cannot affect the app. The meeting text is read and written by the job.
internal sealed class SummaryRunner(StatePaths paths)
{
    private readonly HelperProcess helper = new();

    public async Task<SummaryRunOutcome> RunAsync(string sessionDirectory, bool cpuAfterGpuFailure, Action<SummaryProgress> progress)
    {
        var arguments = new List<string> { "summarize", "--state-dir", paths.Root, "--session-dir", sessionDirectory };
        if (cpuAfterGpuFailure) arguments.AddRange(["--processor", "cpu-fallback"]);
        var result = await helper.RunAsync(arguments, line =>
        {
            if (HelperProcess.TryParseProgress(line, out var completed, out var total)) progress(new SummaryProgress((int)completed, (int)total));
        });
        if (result.Cancelled) return SummaryRunOutcome.Postponed;
        return result.ExitCode switch
        {
            SummaryJob.CompletedExitCode or SummaryJob.FailedExitCode when SummaryFiles.Read(sessionDirectory) is not null => SummaryRunOutcome.Finished,
            SummaryJob.InterruptedExitCode or SummaryJob.BusyExitCode => SummaryRunOutcome.Postponed,
            SummaryJob.GpuFailedExitCode => SummaryRunOutcome.GpuFailed,
            _ => SummaryRunOutcome.Crashed
        };
    }

    // A stopped job leaves nothing behind: its output is only ever written whole, at the end.
    public void Cancel() => helper.Cancel();
}
