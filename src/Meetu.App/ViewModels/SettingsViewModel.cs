using System.IO;

namespace Meetu.App.ViewModels;

public sealed record DeviceOption(string? Id, string Name);

// A row in Settings: the model itself, and whether this unsaved form has it selected.
public sealed class SpeechModelChoice(SpeechModelRow row, SettingsViewModel settings) : Observable
{
    public SpeechModelRow Row { get; } = row;

    public bool IsChosen
    {
        get => settings.ChosenSpeechModel == Row.Model;
        set { if (value) settings.ChosenSpeechModel = Row.Model; }
    }

    internal void Refresh() => Raise(nameof(IsChosen));
}

public sealed class SettingsViewModel : Observable
{
    private readonly StatePaths paths;
    private readonly AppConfig original;
    private readonly SpeechModelsViewModel speechModels;
    private SpeechModel? chosenSpeechModel;
    private DeviceOption? microphone;
    private DeviceOption? playback;
    private int maxMinutes;
    private string? outputDirectory;
    private string? summaryModelPath;
    private bool summarizeAutomatically;
    private bool summarizeOnGpu;
    private bool hideFromScreenSharing;
    private string? error;

    public SettingsViewModel(StatePaths paths, SpeechModelsViewModel speechModels)
    {
        this.paths = paths;
        this.speechModels = speechModels;
        original = paths.LoadConfig();
        chosenSpeechModel = original.SelectedSpeechModel;
        speechModels.Refresh();
        SpeechModelChoices = speechModels.Rows.Select(row => new SpeechModelChoice(row, this)).ToList();
        maxMinutes = original.MaxSessionMinutes;
        outputDirectory = original.OutputDirectory;
        summaryModelPath = original.SummaryModelPath;
        summarizeAutomatically = original.SummarizeAutomatically;
        summarizeOnGpu = original.SummaryProcessor == SummaryProcessor.Gpu;
        hideFromScreenSharing = original.HideWindowFromScreenSharing;
        var endpoints = ListEndpoints();
        Microphones = Options(endpoints.Where(endpoint => endpoint.IsInput), original.MicrophoneId);
        Playbacks = Options(endpoints.Where(endpoint => !endpoint.IsInput), original.PlaybackId);
        microphone = Microphones.First(option => option.Id == original.MicrophoneId);
        playback = Playbacks.First(option => option.Id == original.PlaybackId);
    }

    private IReadOnlyList<AudioEndpoint> ListEndpoints()
    {
        try { return AudioDevices.ListActive(); }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            error = $"Windows did not provide the list of audio devices (0x{exception.HResult:X8}). Only the defaults and saved choices are shown.";
            return [];
        }
    }

    // A saved device that is currently unplugged stays listed, so opening Settings cannot silently drop the choice.
    private static List<DeviceOption> Options(IEnumerable<AudioEndpoint> endpoints, string? savedId)
    {
        var options = new List<DeviceOption> { new(null, "Windows default") };
        options.AddRange(endpoints.Select(endpoint => new DeviceOption(endpoint.Id, endpoint.Name)));
        if (savedId is not null && options.All(option => option.Id != savedId))
            options.Add(new DeviceOption(savedId, "Saved device (not connected)"));
        return options;
    }

    public IReadOnlyList<SpeechModelChoice> SpeechModelChoices { get; }
    public bool IsSpeechModelSetup => original.NeedsSpeechModelSelection;
    public string? CustomSpeechModelNote => !IsSpeechModelSetup && original.ModelPath is { } path && chosenSpeechModel is null
        ? $"Using a specific Whisper file set in the configuration: {path}. Choosing a model here replaces it." : null;

    public SpeechModel? ChosenSpeechModel
    {
        get => chosenSpeechModel;
        internal set
        {
            chosenSpeechModel = value;
            foreach (var choice in SpeechModelChoices) choice.Refresh();
            Raise(nameof(CustomSpeechModelNote));
        }
    }

    public IReadOnlyList<DeviceOption> Microphones { get; }
    public IReadOnlyList<DeviceOption> Playbacks { get; }
    public DeviceOption? Microphone { get => microphone; set => Set(ref microphone, value); }
    public DeviceOption? Playback { get => playback; set => Set(ref playback, value); }
    public int MaxMinutes { get => maxMinutes; set => Set(ref maxMinutes, value); }
    public bool SummarizeAutomatically { get => summarizeAutomatically; set => Set(ref summarizeAutomatically, value); }
    public bool SummarizeOnGpu { get => summarizeOnGpu; set => Set(ref summarizeOnGpu, value); }
    public bool HideFromScreenSharing { get => hideFromScreenSharing; set => Set(ref hideFromScreenSharing, value); }
    public string? Error { get => error; private set => Set(ref error, value); }

    public string OutputDirectoryText => outputDirectory ?? new AppConfig().ResolvedOutputDirectory;
    public string SummaryModelText => summaryModelPath ?? "Default: " + AppConfig.DefaultSummaryModelFile;

    public void ChooseOutputDirectory(string directory)
    {
        outputDirectory = directory;
        Raise(nameof(OutputDirectoryText));
    }

    public void ChooseSummaryModel(string? path)
    {
        summaryModelPath = path;
        Raise(nameof(SummaryModelText));
    }

    public string DeleteConfirmation(SpeechModelRow row) =>
        $"Delete {row.Name}? It can be downloaded again later. Meetings it already transcribed are not affected.";

    // The next recording needs its model, so one in use has to be replaced first.
    public async Task DeleteSpeechModelAsync(SpeechModelRow row)
    {
        if (row.Model == chosenSpeechModel || row.Model == paths.LoadConfig().SelectedSpeechModel)
        {
            Error = $"{row.Name} is selected. Choose another model and press Save first.";
            return;
        }
        Error = await speechModels.DeleteAsync(row);
    }

    internal AppConfig Updated() => original with
    {
        SpeechModelId = chosenSpeechModel?.Id ?? original.SpeechModelId,
        ModelPath = chosenSpeechModel is null ? original.ModelPath : null,
        MicrophoneId = microphone?.Id,
        PlaybackId = playback?.Id,
        MaxSessionMinutes = maxMinutes,
        OutputDirectory = outputDirectory,
        SummaryModelPath = summaryModelPath,
        SummarizeAutomatically = summarizeAutomatically,
        SummaryProcessor = summarizeOnGpu ? SummaryProcessor.Gpu : SummaryProcessor.Cpu,
        HideWindowFromScreenSharing = hideFromScreenSharing
    };

    public bool TrySave()
    {
        if (chosenSpeechModel is null && original.NeedsSpeechModelSelection)
        {
            Error = "Choose a transcription model before saving. Download one first, or select the included Whisper base.en for offline use.";
            return false;
        }
        var updated = Updated();
        try
        {
            updated.Validate();
            JsonFiles.Write(paths.ConfigFile, updated);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Error = exception.Message;
            return false;
        }
    }
}
