using System.Diagnostics;

namespace Meetu;

public sealed class CaptureTimeline(long startedTimestamp, int bufferMilliseconds)
{
    private readonly double bufferTicks = bufferMilliseconds * (Stopwatch.Frequency / 1000.0);
    private double? nextTimestamp;
    private long lastReceived;
    public bool UsedEstimatedTiming { get; private set; }

    public (double StartSeconds, long CapturedTimestamp) Stamp(int sampleCount, long qpc100ns,
        bool timestampValid, bool discontinuity, long receivedTimestamp)
    {
        var durationTicks = sampleCount * (Stopwatch.Frequency / 16000.0);
        double captured;
        if (timestampValid && qpc100ns > 0)
            captured = qpc100ns * (Stopwatch.Frequency / 10_000_000.0);
        else
        {
            UsedEstimatedTiming = true;
            // Some endpoint/format combinations return zero QPC. Anchor at callback time minus
            // the capture buffer, then advance by samples across batched callbacks. Re-anchor
            // after a long absence or discontinuity so playback silence is not compressed.
            captured = nextTimestamp is not null && !discontinuity && receivedTimestamp - lastReceived <= bufferTicks * 3
                ? nextTimestamp.Value : receivedTimestamp - bufferTicks;
            captured = Math.Clamp(captured, startedTimestamp, receivedTimestamp);
        }
        lastReceived = receivedTimestamp;
        nextTimestamp = captured + durationTicks;
        // A conservative arrival estimate also bounds retention if the native clock is absent.
        var retentionTimestamp = (long)Math.Min(captured, receivedTimestamp - bufferTicks);
        return (Math.Max(0, (captured - startedTimestamp) / Stopwatch.Frequency), retentionTimestamp);
    }
}
