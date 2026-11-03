# Meetus Privatus

A private meeting transcriber for Windows 11. Press Start, watch the text appear as the meeting goes, and get a summary with action items when you stop. Prior meetings stay in the app.

It captures the mic and everything played through the selected output device, using Whisper base.en and a local AI model. It works independently of Zoom/Teams processes, so there is no app to select. No accounts, server, or cloud, and your meetings never leave your PC.

**Prototype:** public-audio recognition, output-device pipeline, and synthetic summary tests pass. Real Zoom/Teams calls, accent accuracy, summary quality on real meetings, and smooth operation on a low-powered laptop still need validation. Automatic meeting detection and individual-speaker diarization are deferred.

## Install

1. Use Windows 11 x64 and install the [Microsoft Visual C++ x64 redistributable](https://aka.ms/vc14/vc_redist.x64.exe).
2. Download `MeetusPrivatus-win-x64-Portable.zip` from [Releases](https://github.com/oana-ffg/meetusprivatus/releases) and use **Extract All** into a local folder you can write to, outside network drives, links/junctions, and cloud-placeholder folders. No installer or administrator rights are needed.
3. Allow desktop apps to access the microphone in Windows **Settings → Privacy & security → Microphone**. Use headphones, select the intended Windows default output and default communications microphone, and use those same devices in Zoom/Teams.
4. Open `Meetus Privatus.exe`. Press **Start recording**, and **Stop recording** when the meeting ends. Otherwise capture stops after 60 minutes.
5. For summaries, press **Download the summary model** the first time the app offers it (about 2.7 GB, one time). Transcripts work without it.

The ZIP includes the .NET runtime and verified Whisper/Silero models. No account, SDK, or GPU is required. Releases are not code-signed, so Windows or a managed machine's policy may warn about or block them. To remove the app, delete its folder and `%LOCALAPPDATA%\MeetusPrivatus`; check first that you have kept any meetings you want from its `transcripts` folder.

## Use it

**Open the app, press Start recording, press Stop recording. No commands, configuration, or app selection are needed.**

- **While recording:** text appears on the Transcript tab, merged from both sources in time order and labelled **Mic** or **Playback**. Expect it 20 to 40 seconds behind the conversation. Scrolling up to reread stops it following; **Jump to latest** resumes.
- **After you stop:** the rest of the audio is transcribed, then the summary appears: overview, action items with owners and deadlines, decisions, and key points. Each action item's timestamp links to that moment in the transcript, so you can check it. Copy the summary or transcript with one click.
- **Prior meetings:** they are listed on the left. Summaries give them readable titles.
- **Settings** (gear icon): microphone, output device, auto-stop duration, save location, summaries, GPU use, and screen-share privacy. Changes apply to the next recording.

**The window is hidden from screen sharing and screenshots by default.** Live text can include notes you spoke while muted, so sharing your whole screen shows nothing where this window is. You can turn this off in Settings.

Closing the window while recording asks whether to stop, so the mic never keeps running with nothing on screen. What was captured still finishes transcribing in the background, and its summary is written the next time you open the app. If the app itself crashes, the recording continues; reopen the app to get back to it.

**Playback capture includes every app on the selected output device**, including notification sounds, music, and browser audio. Speech from those sources can appear in the transcript and summary. Closing Zoom/Teams does not stop capture.

The default mic is the Windows **communications input**; playback uses the Windows **default output** at session start. If the meeting uses different devices, choose them in Settings. Headphones are the baseline. The app keeps the chosen devices throughout a session; it does not follow default-device changes or record other outputs.

**Mic capture continues while muted in Zoom/Teams**, including spoken notes to self, and those notes can appear in the summary. App mute only controls what meeting participants hear.

### Summaries

Summaries run after the recording, never during one, so they cannot slow down a meeting. Starting a new recording pauses any unfinished summary, which restarts afterwards. The default model uses about 3.2 GB of memory while it runs, released when it finishes.

**CPU or GPU:** summaries use the CPU by default. **Use the GPU for summaries** in Settings runs them through Vulkan, which covers integrated and discrete GPUs without vendor-specific installs. If that fails or no usable GPU exists, the summary runs on the CPU instead, and its footer says which was used. Transcription always stays on the CPU, so a GPU shared with your webcam and screen sharing stays free during a meeting.

As a guide, a 40-minute transcript took about 16 minutes on a mid-range desktop CPU and under a minute on a mid-range discrete GPU. A low-powered laptop will take longer, and an integrated GPU sits somewhere between, so it is worth trying both.

To use a different local model, choose any GGUF chat model file in Settings. llama.cpp must recognize the model's chat format, and the app says so if it does not.

Summaries are machine-generated from a machine transcript and can get things wrong, so check anything important against the transcript. Owners, deadlines, and timestamps are checked against the transcript and dropped if they do not appear there; that catches invented details, not every misunderstanding.

## Update

**Press Check for updates at the bottom of the sidebar, then Restart to update when it is ready.** Saved meetings, settings, and the summary model are kept. Updating is refused while a recording is running or finishing.

The app never checks by itself. Pressing the button runs a separate helper process that contacts this repository's public GitHub Releases and downloads the verified package, so neither the app nor the recording process ever connects to anything, and the update step reads no meeting content. Package hashes detect corrupted downloads, but releases are not publisher-signed, so authenticity relies on HTTPS and control of the GitHub repository.

**Without internet:** extract the new ZIP into a new folder, then move the `transcripts` and `models` folders across from the old one. Settings live in `%LOCALAPPDATA%\MeetusPrivatus` and carry over. For the summary model, place a verified `Qwen3.5-4B-Q4_K_M.gguf` in `models` beside `Meetus Privatus.exe`, or choose a local GGUF file in Settings.

Versions before 0.2 used a different updater and cannot update to this one. Install the new ZIP once as above, moving your old `transcripts` folder in if you want those meetings listed.

## Files and limits

Meetings are saved in the `transcripts` folder beside `Meetus Privatus.exe`. Each session contains timestamped UTF-8 `local.txt` and `remote.txt`, canonical transcript records under `segments`, lifecycle metadata in `session.json`, and `summary.json`/`summary.md` once summarized. Remote participants are grouped together; `remote.txt` is honestly labelled as all playback on the selected output, which may include other apps. No invented identities. Text stays until you delete the session folder, which the folder button in the app opens; `segments` and the summary files contain private spoken content too.

Settings and status live in `%LOCALAPPDATA%\MeetusPrivatus`. Set another destination in Settings; it affects future sessions, and the app lists meetings from the current destination only. Choose local storage if you do not want another program synchronizing the text.

State, output, and model paths must use local drives without links/junctions or cloud placeholders. UNC and mapped network drives are rejected before opening them. Ordinary local backup/synchronization software is still outside the application's control.

Defaults: one CPU inference thread for transcription, BelowNormal priority, and a 20-second clip maximum. `Threads` (1 or 2) and `ClipSeconds` (5 to 30) in `config.json` adjust these; more threads may increase contention, and shorter clips can increase recognition overhead. No automatic escalation occurs. A baseline CPU runtime is included for processors without the optimized runtime's instruction support.

Recording auto-stops after 60 minutes by default. This is separate from the shared pending-audio limit: both streams share an hour of pending session time and about 220 MiB of PCM. Processed clips are deleted promptly. At age/storage limits capture stops, completed text survives, and gaps are marked. Only expiring clips are discarded; newer pending speech can still finish within its own deadline. Finishing after Stop or auto-stop remains bounded by the same retention policy.

Whisper can mishear accents, names, numbers, silence, and words split at clip boundaries. Timestamps are approximate: when an endpoint supplies no usable native clock, capture uses a common monotonic arrival estimate and sample progression. This is a memory aid. Development-machine performance does not establish performance during screen sharing on a low-powered laptop.

## Interruption and privacy

After a crash, abandoned owned audio is removed the next time the app opens or a recording starts. Cleanup refuses an active recording and preserves unrelated files and finalized text. Crashes or suspension can leave clips until then.

The app and the recording process have no outbound workflow, telemetry, cloud service, automatic download, or network listener, and neither does the summarizer. Summaries run in-process with llama.cpp, with no local server. Only two actions reach the network, each when you press its button: downloading the summary model and checking for updates. Both run in a separate helper process, which reads no meeting content. The app talks to the recording process over a current-user-only named pipe. Newly created private directories restrict access to the current Windows user. OS memory/paging/dumps/backups, a compromised OS, and user-configured synchronization remain outside the app's control. Ordinary deletion is not forensic erasure. Zoom/Teams use their own network connections.

Failures are shown in the app; there are no notification pop-ups. Audio-device failures stop or fail a session instead of silently rerouting capture.

## Development

Prerequisites: Windows 11 x64, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and the [Microsoft Visual C++ x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). From the repository in PowerShell:

```powershell
.\scripts\download-models.ps1            # speech models, into .\models
.\scripts\build.ps1                      # warnings-as-errors build and behavioral tests
.\scripts\build.ps1 -Publish             # also publishes to .artifacts\publish
.\scripts\test-native.ps1                # real speech, playback capture, CPU baseline, summary model
.\scripts\test-updates.ps1               # a real portable update from one version to the next
```

Copy `models` into `.artifacts\publish\models` to run the published app. `MeetusPrivatus.exe --state-dir PATH` isolates development state.

`download-models.ps1` fetches revision-pinned Whisper base.en Q5_1 (~60 MB) plus Silero VAD (<1 MB) with size and SHA-256 verification, reusing valid files and refusing to overwrite a corrupt one. `-Model base.en` fetches unquantized English base for accuracy comparison. The app's button fetches the summary model.

The normal suite skips native checks and uses a scripted model for summaries, so it needs neither audio devices nor the summary model. Its window tests render the real windows off-screen. Set `MEETUS_UI_SNAPSHOTS` to a folder to save what each state looks like.

`test-native.ps1` downloads a hash-pinned **public JFK speech fixture** if absent, tests speech/silence and output-device playback-to-text, validates baseline CPU in a fresh process, and, if the summary model is in `models`, runs it on the CPU and GPU. The playback check briefly plays quiet public speech through the current output device and never opens the mic. Close meetings/media first: it refuses unrelated active playback before/during capture and before writing/transcribing captured data. This polling guard cannot guarantee detection of very brief sounds.

`test-updates.ps1` packages two versions, extracts the first, presses the real buttons through UI Automation against a local feed, and checks that the new version runs and that synthetic meetings and the model survive. It opens no mic and does not contact GitHub.

Keep generated models/audio/transcripts/test outputs/publication ignored. See [REQUIREMENTS.md](REQUIREMENTS.md), [SPEC.md](SPEC.md), and [AGENTS.md](AGENTS.md) for scope and outstanding acceptance checks.

## Releasing

Set `Version` in `Directory.Build.props`, write notes under `releases/`, and get the commit onto `main`. Then run `.\scripts\release.ps1 -ReleaseNotes releases\<version>.md`. It requires a clean checkout matching GitHub's `main`, runs the build and tests, packages into a fresh folder, uploads a draft with all assets, and publishes it. `.\scripts\package.ps1` builds the same files locally without uploading anything. A version must never be reused for different binaries. Preview versions are marked as pre-releases, and the updater includes them.

## License

[MIT](LICENSE), attributed to Meetus Privatus contributors. Dependencies/models retain their own terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`.
