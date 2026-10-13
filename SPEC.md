# Meetus Privatus — Technical specification

Status: implemented manual-start prototype, updated 2026-10-10. Live Zoom/Teams acceptance, accent accuracy, and performance on a low-powered laptop remain release gates.

## Chosen stack and scope

Windows 11 x64, English, Zoom/Teams desktop apps, headphones, CPU-only recognition. No GUI, live transcript display, meeting bot, server, cloud inference, automatic updates, or application networking.

| Component | Implementation |
| --- | --- |
| Runtime | C# / .NET 10; self-contained Windows x64 publication |
| Capture | NAudio.Wasapi 3.1.0; shared mic capture and output-device loopback |
| Recognition | Whisper.net 1.9.1 / whisper.cpp CPU runtimes |
| Model | Whisper base.en Q5_1; optional unquantized base.en setup download |
| Silence detection | Local Silero VAD 5.1.2 |
| Default inference | One coordinated worker, one thread; configurable ceiling of two |
| Scheduling | Recording process at BelowNormal priority; Start.cmd opens its status console |
| PCM | 16 kHz, 16-bit, mono; independent local and remote sources |
| Clips | Default maximum 20 seconds; configurable 5–30 seconds |
| Pending audio | One-hour session timeline; shared 230,400,000-byte PCM cap |
| Recording duration | Default 60 minutes; configurable 1–1440 minutes, independent of pending retention |

Dependencies are pinned with committed package lock files. Runtime selection is restricted to CPU. Use the optimized library when AVX, AVX2, FMA, and F16C are present; include a baseline CPU fallback otherwise. No GPU engine is selected. Windows requires the x64 Visual C++ redistributable. The model decision is resolved: implement base.en without speculative alternative engines.

## Controls and state

| Command | Behavior |
| --- | --- |
| Start.cmd | Double-click to launch run directly in a console, without typing or batch cancellation prompts |
| run | Record in the current console; Ctrl+C ends capture and finishes queued work |
| config | Show configuration or set output/model/VAD/mic/playback/thread/clip/max-minutes settings |
| devices | List active input and output endpoint IDs |
| start | Start hidden capture of mic and the selected output device |
| status | Inspect state, destination, backlog, and content-free failures |
| stop | End capture, then finish bounded pending work in the background |
| cleanup | Remove abandoned owned audio; refuse a live worker |
| repair --session-dir PATH | Rebuild derived text from committed transcript records; refuse a live worker |

State defaults to %LOCALAPPDATA%\MeetusPrivatus; --state-dir isolates it for tests/portable use. The first run, start, or config change fills an unset output directory with the state directory's transcripts subfolder and persists it; existing choices are preserved. Unset model/VAD paths are not persisted and resolve to the models folder beside the executable, so moving or upgrading the published directory keeps working; config --model/--vad default restores that. A saved path to a default model file that no longer exists (as earlier previews persisted) is reset to the executable's models folder, including when its old local drive is unavailable; other model choices are never substituted. Explicit model overrides are applied before inspecting legacy paths. Network and linked legacy paths are still rejected unless explicitly replaced, without accessing their files. Models must already be local. Reject UNC/mapped network paths and reparse-point ancestors before filesystem access; local paths without links/junctions/cloud placeholders are required. The default mic is the Windows communications input; default playback is the Windows default output (Console role). devices/config --mic and --playback select the actual input/output endpoints. The value default clears the corresponding explicit selection. Resolve and validate active endpoints once at session start; keep them for the session. New private state/audio/session directories have current-user-only inheritable permissions. Existing user-selected parents are not re-permissioned.

One worker holds an exclusive file handle through processing and cleanup. Controllers use a current-user-only named pipe, fixed one-byte commands, three-second controller deadlines, and content-free JSON. There is no network listener. Capture is independent of meeting processes: opening, closing, or restarting a client does not stop recording. Manual stop is required after the meeting. Device failure or retention/resource faults stop capture; there is no automatic endpoint substitution.

The normal workflow is double-click Start.cmd, then Ctrl+C to stop early. The launcher opens the published executable directly in its own console and exits the batch wrapper. The app handles Console.CancelKeyPress, suppresses immediate termination, and signals the same stop path as CLI stop. Repeated Ctrl+C does not abandon pending work. The window remains until pending transcription and owned-audio cleanup finish. A capture timer starts immediately before recording and signals normal stop after MaxSessionMinutes (default 60). It does not cancel recognition. The fixed one-hour pending retention policy is independent of recording duration. Closing the console forcibly can interrupt cleanup. Start creates the next session. Configuration/output changes affect future sessions. Validate models and output writes before starting audio. No sign-in startup, automatic meeting detection, pop-up notifications, or individual remote-speaker diarization is implemented.

## Capture and recognition

1. Load local CPU model assets and open the configured mic plus selected output-device loopback in shared mode. Windows converts both to 16 kHz mono PCM. No app/PID selection or process enumeration occurs.
2. Start both sources against a common monotonic QPC reference. Mic capture is independent of meeting-app mute.
3. Capture callbacks copy borrowed packets with nonblocking TryWrite into a bounded channel. No inference, disk I/O, blocking queue writes, or transcript logging occur there.
4. One assembler writes separate PCM clips. An amplitude check can close a clip after eight seconds and half a second of quiet; the configured maximum remains a hard boundary.
5. One recognition worker reads closed clips. Silero skips nonspeech clips. Speech-containing clips go to Whisper in full, avoiding VAD cuts around individual words.
6. Offset decoded timestamps onto the session timeline, commit finalized text, then delete the corresponding audio.

The packet channel holds at most 256 packets of at most 32,000 bytes: an 8,192,000-byte ceiling. The closed-clip channel holds at most 1,500 metadata entries; disk storage has a separate shared cap. Decode only one clip at a time. Current, queued, and in-flight clips count toward the same budget.

Prefer positive, valid QPC timestamps and Windows discontinuity flags rather than assuming device-position counters represent resampled frame counts. The tested resampled output endpoint returned zero QPC with no error flag. CaptureTimeline falls back to callback arrival minus the configured 100 ms buffer, advances by sample count across batched callbacks, and re-anchors after a discontinuity or callback absence longer than three buffer lengths. Both sources share the session's monotonic reference. Retention timestamps conservatively use the earlier native/estimated time and arrival-minus-buffer. Successful metadata records whether each source needed estimated timing. This estimate has buffer/scheduling uncertainty and is not sample-exact synchronization. Long periods without playback packets preserve elapsed silence. Preserve simultaneous local/remote speech; never gate one source based on the other. Windows defines timestamp-error packets as uncertain timing, not necessarily missing audio; see [buffer flags](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags).

The remote file is labelled as output-device playback: it includes all apps routed to that endpoint, potentially notifications, music, browser audio, and shared media. It does not identify individual people or claim all speech is from the meeting. Audio routed to other endpoints is not captured. Default output changes do not reroute an active session; configure the meeting's actual output and start a new session when changing endpoints. Headphones reduce mic leakage; no echo-cancellation subsystem is included.

Hard clip boundaries can cut words; adjacent clips have no context overlap/deduplication yet. Whisper timestamps are approximate. VAD omissions, accents, short instructions, names/numbers, and boundary errors require representative evaluation. Do not switch models or raise resource limits automatically during capture.

## Retention and failure

The user approved **stop capture, preserve completed text, and mark gaps** at the one-hour limit. Apply the allowance across the common session timeline, with both streams permitted inside it. The PCM byte cap is 16,000 samples/s × 2 bytes × 2 sources × 3,600 s = 230,400,000 bytes, about 220 MiB. Files are headerless PCM.

Track monotonic capture time, including packet-queue residence, and reserve bytes before writing. A one-second watchdog triggers five seconds before age expiry, requests capture stop, and deletes only expiring owned clips before waiting on native inference. Each recognition operation has its own linked cancellation deadline. Newer clips continue processing while their own age permits; do not discard the entire queue just because its oldest clip expires. Discarded audio is removed from backlog status. Storage/capture errors and queue overflow also stop capture. No silent retention extension or automatic restart.

Expired/cancelled clips receive gap records. Failed sessions also carry an incomplete-session annotation because some failures cannot locate missing speech precisely. Do not fabricate lost text. Normal stop drains unexpired work under the same watchdog.

Each audio workspace has a unique GUID and ownership marker. Cleanup holds the exclusive worker lock, verifies ownership, skips links, and deletes recognizable GUID-named PCM files only. Preserve unknown files/subdirectories; they must not prevent removal of owned audio. Never sweep shared temp storage or delete finalized text.

After a crash, remove abandoned owned clips on next start or explicit cleanup; do not recover/replay audio. A crashed, suspended, or storage-blocked machine cannot execute deletion, so clips can remain until cleanup runs. Ordinary deletion is not forensic erasure. OS paging, process memory, dumps, backups, synchronization, or a compromised OS are outside an absolute machine-wide privacy guarantee. Custom at-rest encryption is outside this prototype.

## Durable text

Each timestamp-plus-GUID session directory contains local.txt, remote.txt, canonical per-clip JSON under segments/, and content-free session.json lifecycle metadata. Successful sessions record the resolved mic and playback endpoint IDs. Local text is labelled to include app-muted notes to self. Remote participants remain grouped, and the playback header discloses other apps' audio.

Write each canonical GUID record to a unique temporary file, flush to disk, and atomically rename. Never replace a committed identity on retry. Rebuild sorted UTF-8 text views and atomically replace them. If interrupted between canonical commit and export, repair rebuilds text without audio replay or duplication. Repair rejects unrelated folders. The two text views are not one filesystem transaction; canonical records are the recovery source.

Keep text until manual deletion. The segments directory contains the same private spoken content and has the same retention. No summaries, inferred tasks, invented titles/speakers, or transcript-bearing diagnostics.

## Setup, packaging, and licensing

The separately invoked scripts/download-models.ps1 uses scripts/model-assets.psd1 for pinned repository revisions, sizes, and SHA-256 hashes, shared with release packaging. Reuse valid files, reject corrupt existing files without overwriting, download into owned temporary files, verify, and rename. Offline import works. The application only opens local assets and never invokes a downloader.

Default assets: ggml-base.en-q5_1.bin (59,721,011 bytes) and ggml-silero-v5.1.2.bin (885,098 bytes). Optional ggml-base.en.bin is the same model family. Exact hashes/revisions live in the setup script.

scripts/build.ps1 -Publish builds/tests and publishes the self-contained x64 directory with Start.cmd and licenses/notices. The same launcher works from the repository or beside the published executable. Filter other native architectures out of publication and preserve runtime paths. scripts/package-release.ps1 requires clean committed source, publishes into a new owned directory, copies and verifies local models, writes version/commit/model metadata, checks the executable and console cancellation, and produces a ZIP plus SHA-256 file. It neither downloads models nor overwrites existing release assets. Application source is MIT; third-party/model terms remain in licenses/ and THIRD-PARTY-NOTICES.md.

## Verification and release gates

Behavioral tests cover arbitrary packet/sample preservation, silence timeline offsets, discontinuities, shared byte/age accounting, idempotent text/recovery, safe cleanup including unfamiliar files, input validation, configuration persistence, and current-user named-pipe controls.

Synthetic session tests exercise RunAsync with injected capture and recognition implementations, without opening devices or loading native models. They make the audio destination unavailable after clips from both sources are queued, restore it after capture stops, and verify that queued transcription drains without cancellation, incomplete-session gaps are written, and owned audio is deleted. Normal stop also drains while devices become inactive. These tests cover session coordination, not native inference under real disk failure or the full retention watchdog.

Explicit native tests use a hash-pinned public JFK fixture, never private meetings. They cover speech/silence, a sub-second stop fragment, cancellation/reuse, default/explicit output selection and wrong-direction/missing endpoint rejection, and output playback → clips → recognition → committed text → audio deletion. A fresh process checks the baseline CPU library. The script can download the public fixture during setup; cached assets permit local recognition checks. The capture check briefly plays quiet public speech, never opens the mic, and refuses unrelated active playback before/during capture and before persistence/transcription. This guard is not an absolute guarantee against very brief unrelated sounds; use a quiet desktop without meetings/media for native checks.

Remaining gates before a verified release:

1. Real Zoom and Teams desktop calls with configured mic and monitor-jack headphones: simultaneous speech, app-muted notes, client restarts, and device changes.
2. Sustained low-powered-laptop webcam/screen-share testing; agree measured CPU/memory/responsiveness and completion-delay budgets. Development-machine speed is not that guarantee.
3. Indian, Irish, and Danish accent samples, short instructions, names/numbers, and clip boundaries.
4. Full-worker disk/crash/expiry/duplicate-start/disconnect/restart testing, including expiry during native inference, active-session Ctrl+C backlog draining, and the full-hour recording timer. The isolated console check verifies real Ctrl+C during startup without opening the mic; unit budget accounting alone does not establish watchdog behavior.
5. Network observation during the full live workflow and recognition with networking disabled. Local-file/CPU calls and source inspection support the design but do not replace this acceptance check.

Automatic meeting detection and individual-speaker diarization remain deferred bonuses. Keep personal equipment specifications, biography, employer details, and private samples/transcripts out of the public repository.
