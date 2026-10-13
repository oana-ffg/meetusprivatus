using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Meetu;

public static class AudioDevices
{
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
