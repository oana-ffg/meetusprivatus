using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Meetu;

public sealed record AudioEndpoint(string Id, string Name, bool IsInput);

public static class AudioDevices
{
    public static IReadOnlyList<AudioEndpoint> ListActive()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device) endpoints.Add(new AudioEndpoint(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        return endpoints;
    }

    // The endpoints a session with these settings would use. Null when one is currently unavailable.
    public static string? DescribeMicrophone(string? id) => Describe(enumerator => OpenMicrophone(enumerator, id));
    public static string? DescribePlayback(string? id) => Describe(enumerator => OpenPlayback(enumerator, id));

    private static string? Describe(Func<MMDeviceEnumerator, MMDevice> open)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using var device = open(enumerator);
            return device.FriendlyName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }

    public static MMDevice OpenMicrophone(MMDeviceEnumerator enumerator, string? id) =>
        Open(enumerator, id, DataFlow.Capture, Role.Communications);

    public static MMDevice OpenPlayback(MMDeviceEnumerator enumerator, string? id) =>
        Open(enumerator, id, DataFlow.Render, Role.Console);

    private static MMDevice Open(MMDeviceEnumerator enumerator, string? id, DataFlow flow, Role role)
    {
        var device = id is null ? enumerator.GetDefaultAudioEndpoint(flow, role) : enumerator.GetDevice(id);
        if (device.DataFlow == flow && device.State == DeviceState.Active) return device;
        device.Dispose();
        throw new InvalidOperationException(flow == DataFlow.Capture
            ? "Configured microphone is not an active input device."
            : "Configured playback device is not an active output device.");
    }

    public static WasapiRecorder CreateMicrophoneRecorder(MMDevice device) => new WasapiRecorderBuilder()
        .WithDevice(device).WithSharedMode().WithMmcssThreadPriority("Audio")
        .WithFormat(new WaveFormat(16000, 16, 1)).Build();

    public static WasapiRecorder CreatePlaybackRecorder(MMDevice device) => new WasapiRecorderBuilder()
        .WithDevice(device).WithSharedMode().WithLoopbackCapture().WithMmcssThreadPriority("Audio")
        .WithFormat(new WaveFormat(16000, 16, 1)).Build();
}
