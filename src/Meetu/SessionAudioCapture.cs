using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Meetu;

internal delegate void CaptureBuffer(AudioSource source, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpc);

internal interface ISessionAudioCapture : IAsyncDisposable
{
    string MicrophoneId { get; }
    string PlaybackId { get; }
    bool DevicesActive { get; }
    void Start(CaptureBuffer receive, Action<string> failed);
    ValueTask StopAsync();
}

// Own both endpoints for the entire session; stopping joins callbacks before the packet queue closes.
internal sealed class SessionAudioCapture : ISessionAudioCapture
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice microphone;
    private readonly MMDevice playback;
    private WasapiRecorder? local;
    private WasapiRecorder? remote;

    public string MicrophoneId => microphone.ID;
    public string PlaybackId => playback.ID;
    public bool DevicesActive => microphone.State == DeviceState.Active && playback.State == DeviceState.Active;

    public SessionAudioCapture(AppConfig config)
    {
        try
        {
            microphone = AudioDevices.OpenMicrophone(enumerator, config.MicrophoneId);
            playback = AudioDevices.OpenPlayback(enumerator, config.PlaybackId);
        }
        catch { microphone?.Dispose(); enumerator.Dispose(); throw; }
    }

    public void Start(CaptureBuffer receive, Action<string> failed)
    {
        local = AudioDevices.CreateMicrophoneRecorder(microphone);
        remote = AudioDevices.CreatePlaybackRecorder(playback);
        local.DataAvailable += (buffer, flags, _, qpc) => receive(AudioSource.Local, buffer, flags, qpc);
        remote.DataAvailable += (buffer, flags, _, qpc) => receive(AudioSource.Remote, buffer, flags, qpc);
        local.RecordingStopped += (_, args) => { if (args.Exception is not null) failed("Microphone capture failed. Check the selected device."); };
        remote.RecordingStopped += (_, args) => { if (args.Exception is not null) failed("Output capture failed. Check the selected playback device."); };
        local.StartRecording(); remote.StartRecording();
    }

    public async ValueTask StopAsync()
    {
        try { local?.StopRecording(); remote?.StopRecording(); }
        finally
        {
            try
            {
                if (local is not null)
                {
                    var recorder = local; local = null;
                    await recorder.DisposeAsync();
                }
            }
            finally
            {
                if (remote is not null)
                {
                    var recorder = remote; remote = null;
                    await recorder.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { microphone.Dispose(); playback.Dispose(); enumerator.Dispose(); }
    }
}
