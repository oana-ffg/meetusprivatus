# Meetus Privatus

A small Windows 11 meeting transcriber. It captures the mic and everything played through the selected output device into separate text files using Whisper base.en on the CPU. Works independently of Zoom/Teams processes: no app selection needed. No GUI, live transcript display, accounts, server, or runtime network calls.

**Prototype:** public-audio recognition and output-device pipeline tests pass. Real Zoom/Teams calls, accent accuracy, and smooth operation on a low-powered laptop still need validation. Automatic meeting detection and individual-speaker diarization are deferred.

## Download and run

1. Use Windows 11 x64 and install the [Microsoft Visual C++ x64 redistributable](https://aka.ms/vc14/vc_redist.x64.exe).
2. Download the Windows x64 ZIP asset from [Releases](https://github.com/oana-ffg/meetusprivatus/releases). Use **Extract All** into a local folder outside network drives, links/junctions, and cloud-placeholder folders. Keep all extracted files together; do not run from inside the ZIP.
3. Allow desktop apps to access the microphone in Windows **Settings → Privacy & security → Microphone**. Use headphones, select the intended Windows default output and default communications microphone, and use those same devices in Zoom/Teams.
4. Open the extracted application folder and double-click `Start.cmd`. Press **Ctrl+C** to stop early, then leave the window open while queued transcription finishes. Otherwise capture stops after 60 minutes.
5. Open the `transcripts` folder beside `Start.cmd` and `meetus.exe`. Each session contains `local.txt` for microphone speech and `remote.txt` for output playback.

The archive includes the .NET runtime and verified Whisper/Silero models; no SDK or model download is needed. No account or GPU is required. Mic capture includes speech while muted in Zoom/Teams, and playback includes other apps on the selected output. Preview releases retain the validation limits stated above.

## Update

**Stop recording and let queued transcription finish, then double-click `Update.cmd`.** It checks the most recently published release, including previews, downloads the complete package, verifies its SHA-256 checksum and every packaged file, and updates the current folder. Transcripts, saved settings, and unrelated user files are preserved. No GitHub account, browser download, or re-extraction is needed for subsequent updates. Install this release once to obtain the updater; earlier releases do not include it.

Updates are explicitly requested downloads from this public GitHub repository. The updater never reads or uploads meeting content. `Start.cmd` and the recording application remain offline and never invoke the updater. A recording and an update cannot run together. If replacement fails, the previous version is restored; if Windows interrupts recovery, run `Update.cmd` again before recording. It preserves the backup and blocks recording until recovery finishes. Modified packaged files or colliding user files cause an inspectable error instead of being silently overwritten.

For an offline update, bring the release ZIP and its `.zip.sha256` file to the machine. With both files in the application folder, this release can be installed using `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Update.ps1 -Archive .\meetus-privatus-0.1.0-preview.3-win-x64.zip -Checksum .\meetus-privatus-0.1.0-preview.3-win-x64.zip.sha256`. The same integrity and preservation checks apply. Use a writable local application folder without links or junctions.

## Build and setup

Build prerequisites: Windows 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Runtime prerequisite: the [Microsoft Visual C++ x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). Self-contained publication includes .NET; the destination machine needs neither the SDK nor a GPU.

From the repository in PowerShell:

```powershell
.\scripts\build.ps1 -Publish
.\scripts\download-models.ps1 -Destination .artifacts\publish\models
```

After setup, double-click `Start.cmd`. No configuration is required for normal use.

The model script is a **manual setup download**: revision-pinned Whisper base.en Q5_1 (~60 MB) plus Silero VAD (<1 MB), with size/SHA-256 verification. It reuses valid files and refuses to overwrite a corrupt existing file. The app does not invoke it. Build/package restore needs networking unless dependencies are cached.

For offline installation, copy the complete published directory, supply the two verified `.bin` files under its `models` folder, and install the redistributable separately. Unless configured otherwise, the app always looks in the `models` folder beside `meetus.exe`, so moving or upgrading the published directory keeps working. Settings saved by earlier previews that point at a removed folder's default models also switch to the models beside `meetus.exe` automatically. Alternatively configure explicit local paths:

```powershell
$meetus = (Resolve-Path .artifacts\publish\meetus.exe).Path
& $meetus config --model "$PWD\models\ggml-base.en-q5_1.bin" --vad "$PWD\models\ggml-silero-v5.1.2.bin"
```

`config --model default --vad default` returns to the models beside `meetus.exe`. `download-models.ps1 -Model base.en` fetches unquantized English base for accuracy comparison; configure its `ggml-base.en.bin` path explicitly. The recognizer stays Whisper base.en. Preserve third-party notices when redistributing models/binaries.

## Use it

**Double-click `Start.cmd`. Press Ctrl+C in its window to stop early. Otherwise it stops capturing after 60 minutes. No commands or app selection are needed.**

The launcher opens a small status console, without live transcript text. Ctrl+C and auto-stop end capture, then finish queued transcription and delete each clip after saving its text. Leave the window open while it finishes; it closes when done. Closing the window forcibly can interrupt cleanup. The pending-audio retention limit still applies during finishing.

The first launch automatically saves defaults. Transcripts go to `transcripts` beside `meetus.exe`. The previous standard AppData destination automatically returns to this default for future sessions; existing text stays in its old location. Explicit custom destinations are preserved. To change the recording duration or destination, open `%LOCALAPPDATA%\MeetusPrivatus\config.json` and edit `MaxSessionMinutes` (default 60; 1–1440) or `OutputDirectory`. A null output directory follows the application folder, including after moving it. `config --output-dir default` restores this default. JSON paths use doubled backslashes. Changes affect the next session. No configuration is needed for normal use.

For development or optional advanced control, the CLI remains available (`$meetus` is the published `meetus.exe` path, as above):

```powershell
& $meetus start
& $meetus status
& $meetus stop
```

`start` launches a hidden worker and returns once capture starts. `stop` ends capture; pending recognition finishes in the background. Use `status` until it reports `Complete` or `Failed` before starting another session. These commands are optional; `Start.cmd` uses foreground `run` so Ctrl+C reaches the recording process directly, without a batch-file confirmation prompt.

**Playback capture includes every app on the selected output device**, including notification sounds, music, and browser audio. Speech from those sources can appear in `remote.txt`. Closing Zoom/Teams does not stop capture; use `stop` after the meeting.

The default mic is the Windows **communications input**; playback uses the Windows **default output** at session start. If the meeting uses different devices, run `devices` to list input/output IDs, then set `config --mic` and/or `config --playback` to the matching IDs. `config --mic default` and `config --playback default` restore defaults for future sessions. Headphones are the baseline. The app keeps the chosen devices throughout a session; it does not follow default-device changes or record other outputs.

**Mic capture continues while muted in Zoom/Teams**, including spoken notes to self. Use `stop` to stop the assistant; app mute only controls what meeting participants hear.

## Files and limits

Each output session contains timestamped UTF-8 `local.txt` and `remote.txt`, canonical transcript records under `segments`, and lifecycle metadata in `session.json`. Remote participants are grouped together; `remote.txt` is honestly labelled as all playback on the selected output, which may include other apps. No summaries or invented identities. Text stays until you delete the session folder; `segments` contains private spoken content too.

Settings/status default to `%LOCALAPPDATA%\MeetusPrivatus`; `config` displays them. Set another destination with `config --output-dir`; it affects future sessions. Choose local storage if you do not want another program synchronizing the text.

State, output, and model paths must use local drives without links/junctions or cloud placeholders. UNC and mapped network drives are rejected before opening them. Ordinary local backup/synchronization software is still outside the application's control.

Defaults: one CPU inference thread, BelowNormal priority, and a 20-second clip maximum. `config --threads 2` raises the ceiling to two; `config --clip-seconds` accepts 5–30. More threads may increase contention; shorter clips can increase repeated recognition overhead. No automatic escalation occurs. A baseline CPU runtime is included for processors without the optimized runtime's instruction support.

Recording auto-stops after `MaxSessionMinutes` (60 by default). This is separate from the shared pending-audio limit: both streams share an hour of pending session time and about 220 MiB of PCM. Processed clips are deleted promptly. At age/storage limits capture stops, completed text survives, and gaps are marked. Only expiring clips are discarded; newer pending speech can still finish within its own deadline. Finishing after Ctrl+C, auto-stop, or normal stop remains bounded by the same retention policy.

Whisper can mishear accents, names, numbers, silence, and words split at clip boundaries. Timestamps are approximate: when an endpoint supplies no usable native clock, capture uses a common monotonic arrival estimate and sample progression. This is a memory aid. Development-machine performance does not establish performance during screen sharing on a low-powered laptop.

## Interruption and privacy

After a crash, use `cleanup` or start a new session to remove abandoned owned audio. Cleanup refuses an active worker and preserves unrelated files and finalized text. Crashes/suspension can leave clips until cleanup executes.

Use `repair --session-dir` with an affected session directory to rebuild interrupted text exports from canonical records, without audio replay. Unrelated folders are rejected, and repair refuses while a recording is running or finishing.

The running app has no outbound workflow, telemetry, cloud service, automatic download, or network listener. Controllers use a current-user-only named pipe. Newly created private directories restrict access to the current Windows user. OS memory/paging/dumps/backups, a compromised OS, and user-configured synchronization remain outside the app's control. Ordinary deletion is not forensic erasure. Zoom/Teams use their own network connections.

Failures are inspectable through `status` and nonzero exit codes; there are no notification pop-ups. Audio-device failures stop or fail a session instead of silently rerouting capture.

## Development checks

```powershell
.\scripts\build.ps1
.\scripts\test-native.ps1
.\scripts\test-update.ps1
```

The normal suite skips native checks. `test-native.ps1` and `package-release.ps1` read models from the repository's `models` folder by default; pass `-Models .artifacts\publish\models` to reuse the setup download above. The native script downloads a hash-pinned **public JFK speech fixture** if absent, tests speech/silence and output-device playback-to-text, publishes the app, then validates baseline CPU in a fresh process and real Windows Ctrl+C during startup in an isolated hidden console. The console check deliberately selects a nonexistent mic and checks graceful completion, owned-audio cleanup, and automatic settings. It can also be run independently with `scripts/test-console.ps1` after publication. The playback check briefly plays quiet public speech through the current output device and never opens the mic. Close meetings/media first: it refuses unrelated active playback before/during capture and before writing/transcribing captured data. This polling guard cannot guarantee detection of very brief sounds. Cached fixtures/models permit recognition tests without new downloads.

Use `--state-dir` to isolate development CLI state. Keep generated models/audio/transcripts/test outputs/publication ignored. See [REQUIREMENTS.md](REQUIREMENTS.md), [SPEC.md](SPEC.md), and [AGENTS.md](AGENTS.md) for scope and outstanding acceptance checks.

`test-update.ps1` builds isolated copies and exercises the actual offline updater with verified local models, synthetic user files, checksum/traversal/collision rejection, installation locking, failed file replacement, journal recovery, obsolete-file removal, and preservation of transcripts. It does not capture audio or contact GitHub.

## Release packaging

Maintainers can run `scripts/package-release.ps1` from a clean committed checkout with local verified models. It builds/tests into a fresh directory, includes models, launchers, updater, notices, and a source-commit manifest with per-file hashes, checks console cancellation, and writes a ZIP plus SHA-256 file under ignored `.artifacts/releases`. Existing release assets are never overwritten.

## License

[MIT](LICENSE), attributed to Meetus Privatus contributors. Dependencies/models retain their own terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`.
