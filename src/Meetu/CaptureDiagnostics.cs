using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Meetu;

// Bounded, content-free measurements. Each endpoint's callback owns its source counters;
// snapshots are taken only after capture and processing have joined.
internal sealed class CaptureDiagnostics(long startedTimestamp)
{
    private const int MaximumEventsPerSource = 64;
    private sealed class SourceCounters
    {
        public long Packets, Samples, EmptyPackets, EstimatedPackets, Discontinuities;
        public int MinimumBytes = int.MaxValue, MaximumBytes;
        public int? CaptureThreadPriorityLevel;
        public long LastArrival, LastAudioArrival, LastPosition, LastSamples, LastGcPauseTicks = GC.GetTotalPauseDuration().Ticks;
        public double MaximumArrivalMilliseconds, MaximumAudioArrivalMilliseconds, MaximumCallbackMilliseconds;
        public readonly List<object> Events = new(MaximumEventsPerSource);
    }
    private readonly SourceCounters[] sources = [new(), new()];
    private int maximumQueueDepth;
    private int recognitionActive;
    private double maximumAssemblyMilliseconds, maximumCommitMilliseconds;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    private static extern int GetThreadPriority(IntPtr thread);

    public void Received(AudioSource source, int bytes, AudioClientBufferFlags flags, long position,
        long qpc, long arrival, int queueDepth)
    {
        var counters = sources[(int)source];
        if (counters.Packets == 0)
        {
            var priority = GetThreadPriority(GetCurrentThread());
            if (priority != int.MaxValue) counters.CaptureThreadPriorityLevel = priority;
        }
        var interval = counters.Packets == 0 ? 0 : Stopwatch.GetElapsedTime(counters.LastArrival, arrival).TotalMilliseconds;
        var audioInterval = counters.LastAudioArrival == 0 ? 0 : Stopwatch.GetElapsedTime(counters.LastAudioArrival, arrival).TotalMilliseconds;
        var gcPause = GC.GetTotalPauseDuration().Ticks;
        if (bytes > 0 && counters.LastAudioArrival != 0 && (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
        {
            counters.Discontinuities++;
            if (counters.Events.Count < MaximumEventsPerSource)
                counters.Events.Add(new
                {
                    Seconds = Stopwatch.GetElapsedTime(startedTimestamp, arrival).TotalSeconds,
                    ArrivalIntervalMilliseconds = interval,
                    AudioArrivalIntervalMilliseconds = audioInterval,
                    GcPauseMilliseconds = TimeSpan.FromTicks(gcPause - counters.LastGcPauseTicks).TotalMilliseconds,
                    PacketBytes = bytes, DevicePosition = position, PreviousDevicePosition = counters.LastPosition,
                    PreviousSamples = counters.LastSamples, NativeQpc = qpc, Flags = (int)flags,
                    PacketQueueDepth = queueDepth, RecognitionActive = Volatile.Read(ref recognitionActive) != 0
                });
        }
        counters.Packets++; counters.Samples += bytes / 2;
        if (bytes == 0) counters.EmptyPackets++;
        if (bytes > 0 && (qpc <= 0 || (flags & AudioClientBufferFlags.TimestampError) != 0)) counters.EstimatedPackets++;
        counters.MaximumArrivalMilliseconds = Math.Max(counters.MaximumArrivalMilliseconds, interval);
        counters.LastArrival = arrival;
        if (bytes > 0)
        {
            counters.MinimumBytes = Math.Min(counters.MinimumBytes, bytes);
            counters.MaximumBytes = Math.Max(counters.MaximumBytes, bytes);
            counters.MaximumAudioArrivalMilliseconds = Math.Max(counters.MaximumAudioArrivalMilliseconds, audioInterval);
            counters.LastAudioArrival = arrival; counters.LastPosition = position; counters.LastSamples = bytes / 2;
            counters.LastGcPauseTicks = gcPause;
        }
        // Both capture threads can update this shared high-water mark without blocking.
        var previous = Volatile.Read(ref maximumQueueDepth);
        while (queueDepth > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximumQueueDepth, queueDepth, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    public void CallbackCompleted(AudioSource source, long arrival) =>
        sources[(int)source].MaximumCallbackMilliseconds = Math.Max(sources[(int)source].MaximumCallbackMilliseconds,
            Stopwatch.GetElapsedTime(arrival).TotalMilliseconds);

    public void AssemblyCompleted(long started) => maximumAssemblyMilliseconds =
        Math.Max(maximumAssemblyMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    // Called under the transcript-store lock, including any time spent waiting for that lock.
    public void CommitCompleted(long started) => maximumCommitMilliseconds =
        Math.Max(maximumCommitMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    public void RecognitionStarted() => Volatile.Write(ref recognitionActive, 1);
    public void RecognitionEnded() => Volatile.Write(ref recognitionActive, 0);

    public object Snapshot() => new
    {
        MaximumPacketQueueDepth = maximumQueueDepth,
        MaximumAssemblyMilliseconds = maximumAssemblyMilliseconds,
        MaximumTranscriptCommitMilliseconds = maximumCommitMilliseconds,
        Sources = sources.Select((counters, index) => new
        {
            Source = ((AudioSource)index).ToString(), Callbacks = counters.Packets,
            AudioPackets = counters.Packets - counters.EmptyPackets, counters.Samples,
            EmptyCallbacks = counters.EmptyPackets, EstimatedAudioPackets = counters.EstimatedPackets, counters.Discontinuities,
            counters.CaptureThreadPriorityLevel,
            MinimumPacketBytes = counters.MaximumBytes == 0 ? 0 : counters.MinimumBytes,
            MaximumPacketBytes = counters.MaximumBytes,
            counters.MaximumArrivalMilliseconds, counters.MaximumCallbackMilliseconds,
            counters.MaximumAudioArrivalMilliseconds,
            DiscontinuityEvents = counters.Events.ToArray(),
            OmittedEvents = counters.Discontinuities - counters.Events.Count
        }).ToArray()
    };
}
