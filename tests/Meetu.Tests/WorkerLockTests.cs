using Meetu;
using Xunit;
using Xunit.Abstractions;

namespace Meetu.Tests;

public sealed class WorkerLockTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RepeatedAcquisitionSurvivesBackgroundProbesWithoutFalseRefusals()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        using (WorkerLock.Acquire(paths)) { }
        const int rounds = 128;
        // These deadlines only guard broken synchronization; no elapsed-time performance assertion.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cancellation.Token;
        using var nextProbe = new SemaphoreSlim(0, 1);
        using var releaseProbe = new SemaphoreSlim(0, 1);
        using var probeOpened = new Barrier(2);
        using var probeClosed = new Barrier(2);
        var probing = Task.Factory.StartNew(() =>
        {
            try
            {
                for (var round = 0; round < rounds; round++)
                {
                    nextProbe.Wait(token);
                    Assert.False(WorkerLock.IsHeld(paths, () =>
                    {
                        // Keep the actual IsHeld handle open until Acquire has collided with it.
                        probeOpened.SignalAndWait(token);
                        releaseProbe.Wait(token);
                    }));
                    // IsHeld has now disposed its handle. Let the retry open the file exclusively.
                    probeClosed.SignalAndWait(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var retries = 0;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                nextProbe.Release();
                probeOpened.SignalAndWait(token);
                var roundRetries = 0;
                using var worker = WorkerLock.Acquire(paths, delay =>
                {
                    Assert.Equal(TimeSpan.FromMilliseconds(50), delay);
                    Assert.Equal(1, ++roundRetries);
                    releaseProbe.Release();
                    probeClosed.SignalAndWait(token);
                });
                Assert.Equal(1, roundRetries);
                retries += roundRetries;
                Assert.True(WorkerLock.IsHeld(paths));
            }
        }
        finally { cancellation.Cancel(); await probing; }
        Assert.Equal(rounds, retries);
        output.WriteLine($"{rounds} forced probe collisions, {retries} retries, no false acquisition refusals.");
    }

    [Fact]
    public void RealHolderExhaustsTheBoundedRetryBudgetAndStillRefuses()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        var delays = new List<TimeSpan>();
        using (WorkerLock.Acquire(paths))
        {
            var error = Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(paths, delays.Add));
            Assert.Contains("A recording is still running or finishing", error.Message);
            Assert.Equal(5, delays.Count);
            Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(50), delay));
            Assert.Equal(TimeSpan.FromMilliseconds(250), TimeSpan.FromTicks(delays.Sum(delay => delay.Ticks)));
            Assert.True(WorkerLock.IsHeld(paths));
        }
        delays.Clear();
        using var nextWorker = WorkerLock.Acquire(paths, delays.Add);
        Assert.Empty(delays);
        Assert.True(WorkerLock.IsHeld(paths));
    }

    [Fact]
    public void OtherStorageFailuresPropagateWithoutRetries()
    {
        using var directory = new TestDirectory();
        var paths = new StatePaths(Path.Combine(directory.Path, "state"));
        Directory.Delete(paths.Root);
        var delays = new List<TimeSpan>();
        Assert.Throws<DirectoryNotFoundException>(() => WorkerLock.Acquire(paths, delays.Add));
        Assert.Empty(delays);
    }
}
