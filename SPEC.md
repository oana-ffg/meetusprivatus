# Meetus Privatus — Technical specification

Status: implemented desktop-app prototype, updated 2026-11-05. Live Zoom/Teams acceptance, accent accuracy, and performance on a low-powered laptop remain release gates.

## Chosen stack and scope

Windows 11 x64, English, Zoom/Teams desktop apps, headphones, CPU-only recognition, and summaries on the CPU or optionally a GPU. Desktop app with live text, summaries with action items, and meeting history. No meeting bot, server, cloud inference, automatic updates, or application networking.

| Component | Implementation |
| --- | --- |
| Runtime | C# / .NET 10; self-contained Windows x64 publication |
| Desktop app | WPF with the Windows Fluent theme, `MeetusPrivatus.exe`. Chosen over a web view: no browser engine or local server, and low idle cost during a meeting |
| Capture | NAudio.Wasapi 3.1.0; shared mic capture and output-device loopback |
| Recognition | Whisper.net 1.9.1 / whisper.cpp CPU runtimes; ONNX Runtime 1.28.0 CPU for Parakeet |
| Models | No automatic selection. Whisper base.en Q5_1 included for offline selection; Whisper tiny.en Q5_1, Whisper small.en Q5_1, and Parakeet TDT 0.6 v3 int8 as optional downloads |
| Silence detection | Local Silero VAD 5.1.2 |
| Summaries | LLamaSharp 0.30.0 / llama.cpp in-process, with CPU and Vulkan runtimes; Qwen3.5-4B Q4_K_M (Apache-2.0); any local GGUF chat model can be selected |
| Updates | Velopack portable packages from GitHub Releases, checked only on request in a helper process |
| Default inference | One coordinated worker, one thread; configurable ceiling of two |
| Scheduling | Recording process at BelowNormal priority; capture threads use Windows MMCSS Audio priority |
| PCM | 16 kHz, 16-bit, mono; independent local and remote sources |
| Clips | Default maximum 20 seconds; configurable 5–30 seconds |
| Pending audio | One-hour session timeline; shared 230,400,000-byte PCM cap |
| Recording duration | Default 60 minutes; configurable 1–1440 minutes, independent of pending retention |

Dependencies are pinned with committed package lock files. Runtime selection is restricted to CPU. Use the optimized library when AVX, AVX2, FMA, and F16C are present; include a baseline CPU fallback otherwise. No GPU engine is selected. Windows requires the x64 Visual C++ redistributable.

## Controls and state

The desktop app is the only interface. `meetus.exe` is an internal helper with no command-line interface, started by the app for work that belongs in its own process:

| Helper mode | Purpose | Network |
| --- | --- | --- |
| worker | Record and transcribe, hidden, independent of the window | Never |
| summarize | Write one meeting's summary at low priority | Never |
| check-update | Check for, download, and verify an update when the user presses the button | GitHub Releases |
| download-model | Fetch and verify the summary model when the user presses the button | The model's pinned URL |
| download-speech-model | Fetch and verify a transcription model when the user presses its button in Settings | The model's pinned URLs |

State defaults to %LOCALAPPDATA%\MeetusPrivatus; --state-dir isolates it for development and tests. An update replaces the `current` folder the program runs from, so text and the summary model live one level up beside the launcher: an unset output directory resolves to transcripts there and remains null in saved settings, and the default summary model is under models there. Outside an installed layout both resolve beside the executable. The first launch asks for a transcription model; saving the choice persists the other defaults. The previous standard state-root/transcripts destination is reset to this default for future sessions without moving old text; explicit custom destinations remain unchanged. The saved transcription model ID resolves to its included or downloaded assets; an unset VAD path resolves inside the package. A saved path to a default model file that no longer exists (as earlier previews persisted) is reset to that folder, including when its old local drive is unavailable; other model choices are never substituted. Network and linked legacy paths are still rejected without accessing their files. Reject UNC/mapped network paths and reparse-point ancestors before filesystem access; local paths without links/junctions/cloud placeholders are required. The default mic is the Windows communications input; default playback is the Windows default output (Console role). Settings select the actual input/output endpoints. Resolve and validate active endpoints once at session start; keep them for the session. New private state/audio/session directories have current-user-only inheritable permissions. Existing user-selected parents are not re-permissioned.

One worker holds an exclusive file handle through processing and cleanup. Controllers use a current-user-only named pipe, fixed one-byte commands, three-second controller deadlines, and content-free JSON. There is no network listener. Capture is independent of meeting processes: opening, closing, or restarting a client does not stop recording. Manual stop is required after the meeting. Device failure or retention/resource faults stop capture; there is no automatic endpoint substitution.

Controllers first probe the existing worker lock. With no holder, including a lock file left by a crashed worker, they return unavailable without waiting for a pipe. A lock-holding worker keeps the full three-second pipe deadline even if it has not opened its pipe or is serving another controller. The probe never grants recording ownership: cleanup, repair and the worker must still acquire the exclusive lock. Background-start readiness checks retain their 200 ms delay and 60-second bound. An unavailable worker with saved active status still reports Interrupted.

An exclusive lock acquisition retries sharing violations up to five times, pausing 50 ms between attempts (250 ms of requested retry delay). This absorbs short controller probes during startup, cleanup or repair. Every attempt still requires an exclusive open; a persistent holder produces the existing refusal after the retry budget. Other storage errors are not retried. Filesystem calls and OS scheduling can add time beyond the requested delays.

A capture timer starts immediately before recording and signals normal stop after MaxSessionMinutes (default 60). It does not cancel recognition. The fixed one-hour pending retention policy is independent of recording duration. Start creates the next session. Configuration/output changes affect future sessions. Validate models and output writes before starting audio. No sign-in startup, automatic meeting detection, pop-up notifications, or individual remote-speaker diarization is implemented.

## Desktop app

No transcription model is selected by default. Missing or unknown SpeechModelId values show a persistent setup prompt in the sidebar and disable starting a recording. This includes older preview settings, even if they contain a legacy Whisper file path. Choosing a model opens Settings scrolled to Transcription with no radio preselected; Save requires an explicit choice and validates its files. Cancel leaves setup incomplete. Saving a valid model hides the prompt and subsequent launches preserve that choice. An already running worker can still be stopped while setup is incomplete.

The app is a controller and viewer over the existing worker, in a separate process. Start validates settings, removes abandoned audio, launches the hidden worker, and returns when capture runs. Stop and status use the existing pipe, whose fixed commands and content-free replies are unchanged; no spoken text crosses it. A crash in the app cannot stop a recording, and reopening the app attaches to a running worker. Closing the window during capture asks whether to stop, because capture would otherwise continue with nothing visible, and the window stays open unless the worker acknowledges or has verifiably exited. Closing is refused while a recording is starting. An unanswered status request does not count as the recording ending while the worker still holds its lock. A taskbar overlay marks recording while minimized. Only one app instance runs per state directory; a second launch activates the first. Abandoned audio is also removed when the app opens, if no worker is running.

Live text comes from reading committed records under segments/. Each record is immutable once committed, so polling reads each file once and cannot disturb the writer. It polls once a second only while a live meeting is on screen; it polls for a worker every five seconds when idle. Indicators are static, so nothing repaints continuously during a meeting. The transcript list is virtualized. Whole-segment Whisper annotations such as [BLANK_AUDIO] are hidden and left out of summaries, and stay in the text files. Damaged or linked records are counted and reported instead of being skipped silently, and the new readers apply the same local-path rules as the rest of the application before opening anything.

On Windows, a reader holding a file open makes an atomic replacement of it fail, even with delete sharing. The app reads files a worker may replace, so JsonFiles holds them open only for the read, and replacement retries briefly on contention (up to five retries, 50 ms apart). Persistent contention still fails as before. Transcript records are never replaced, so reading them cannot cause this.

By default the window is excluded from screen capture with SetWindowDisplayAffinity, because live text can include notes spoken while muted: sharing a whole screen shows nothing where the window is. It can be turned off in Settings. This protects against capture through Windows; it does not stop someone photographing a monitor.

Settings cover microphone, output device, auto-stop duration, destination, transcription model, automatic summaries, summary model, and screen-capture exclusion. A saved device that is unplugged stays listed instead of being dropped. History lists session directories in the current destination, plus any live meeting saved elsewhere; meetings saved under an earlier destination stay in place and are not listed.

Right-clicking a meeting offers deletion, and a confirmation names the meeting by date and time only: a message box is a separate window, which the screen-capture exclusion does not cover, and a title comes from the transcript. Deletion is permanent and skips the Recycle Bin, because a meeting's text is private and the user just confirmed it. It refuses the meeting a worker is recording, asking the worker rather than trusting the window's own state; if a worker holds its lock without answering, it also refuses. A summary running for that meeting is stopped first. MeetingDeletion accepts only a directory with a session name and the session files, and removes only names the app writes: the text views, summary files, GUID-named records, session.json, and the temporary files of interrupted atomic writes. It skips links. Anything else stays, along with the folder, and the app says so. session.json goes last, so a failure part way leaves the meeting listed and retryable rather than leaving hidden private text. This is ordinary deletion, not forensic erasure.

## Transcription models

SpeechModels lists the models. Settings shows each one's state and lets the user select an installed model for future recordings, download one, stop or resume a download, and delete one. The selection is saved with Save; downloads and deletions take effect at once. A worker reads the selection when it starts, so a recording keeps its model. Validation refuses a model that is not fully present, and nothing substitutes another model. Session metadata records which model transcribed the meeting, and the meeting's heading shows it.

base.en stays in the package, so a fresh install transcribes with no download, and it cannot be deleted. Other models go to the models folder beside the launcher, which updates leave alone, using the same helper and downloader as the summary model. Parakeet is four files in its own folder and reports as one download. A model counts as installed when every file has its pinned size; the hash is checked when downloading, and is not reread at each start. Delete removes only the model's own files and partial files, refuses links, and removes the Parakeet folder only if nothing else is in it. A partly downloaded model can be resumed or deleted, which also clears a damaged file the downloader refuses to overwrite. A model that is selected, or saved as selected, cannot be deleted. Offline import means placing the same files in the same place.

Downloads and deletions are refused while a worker runs. Starting a recording pauses a running download, because it competes with the meeting for bandwidth, and says how to resume. The download continues if Settings closes, and the sidebar shows its progress.

Both engines sit behind ISpeechEngine, after the same Silero speech detection and clip handling. Parakeet uses three ONNX models: feature extraction, encoder, and decoder. Its greedy TDT decoding follows onnx-asr, with punctuation splitting one clip's text into sentence segments, and times derived from encoder frames of 80 ms. It runs on the ONNX Runtime CPU provider with the same one-thread default, BelowNormal priority, and no spinning idle threads. A clip's deadline stops a run through ONNX Runtime's terminate flag.

ONNX Runtime is used directly because a wrapper would hide both the telemetry switch and that terminate flag. Official Windows builds at this version emit Microsoft telemetry through Windows event tracing, turned off with DisableTelemetryEvents before any session is created. **Versions from 1.29 document a built-in uploader that sends telemetry over HTTPS from the process**, so the version is held at 1.28.0. A test fails if the bundled library references WinHTTP, WinINet, Winsock, or the uploader. Do not upgrade it without repeating this check and reading the runtime's privacy notes.

Parakeet v3 detects the language itself and has no setting to fix it to English, so a clip can come out in another language. Whisper's .en models have no such failure mode. Parakeet's memory use is much higher: see VALIDATION.md.

## Summaries

Summaries do not run during capture. Pressing Start stops any running summary first. A worker that appears by any other route is noticed within about two seconds, so those seconds could overlap at BelowNormal priority. After a recording, the app runs `meetus summarize` as a separate BelowNormal process, one at a time. Model memory is released when it exits, and a native failure cannot affect the app. It loads the model only for that run, with half the logical processors and a fixed 8,192-token context. Native logging is discarded so nothing derived from a prompt reaches a console or file. Only `progress N M` lines reach the app.

The CPU is the default. The GPU setting uses llama.cpp's Vulkan backend, which covers integrated and discrete GPUs without vendor-specific libraries, and offloads every layer. Native libraries are chosen once per process, so a GPU failure cannot be retried in place. A managed failure exits with its own code, and the app reruns the summary on the CPU in a fresh process; a process that crashes in GPU mode gets the same treatment. Nothing is recorded against the meeting until the outcome is known. If the GPU backend loads but finds no device, the job continues on the CPU. Each summary records and shows which was used. Transcription stays on the CPU regardless, so a GPU shared with the webcam and screen sharing stays free during a meeting. GPU hangs, as opposed to failures, have no timeout, which is one reason the CPU stays the default.

Output is written only when complete, so a stopped job leaves nothing partial. The meeting stays pending and restarts when nothing else is running. Session metadata records AutoSummarize at capture time, so meetings from before this feature are never summarized without being asked. An outcome is recorded in summary.json: Complete, NoSpeech, or Failed with a content-free reason. A process that ends without an outcome is recorded as Failed by the app, so it is not retried forever. Regenerate replaces a summary only on success: a failure keeps the earlier summary with the reason attached. Fewer than 25 words of speech produce NoSpeech with no model call.

The prompt merges both sources on the session timeline, labelled Mic and Playback, and states what those labels do and do not mean. Meetings too long for one context are split on line boundaries, each part is summarized, and the notes are merged, repeatedly if needed. Each pass must reduce the number of sets of notes or the summary fails, which bounds the loop. Repeats collapse only when the wordings differ by little words such as "the", and action items also need compatible owners and deadlines. Reworded repeats can therefore show twice, because losing a real item is worse. Output uses a plain line format (ACTION, DECISION, POINT, OVERVIEW, TITLE). On a synthetic meeting, JSON output with and without grammar constraints gave small models empty or wrong lists; this format found most action items. Models with a reasoning token get an already-closed thinking block: left to reason on a CPU, the default model ran out of tokens before answering. Models whose chat format llama.cpp's template API does not recognize, such as Gemma 4 at this version, report that clearly.

The model is told not to invent people, deadlines, or citations, and this is enforced where a reader acts on it: an owner other than Me, and a deadline, must appear in the transcript, and a timestamp must match a transcript line, or the field is dropped. Small models turn questions into tasks for the listener, and prompt wording alone did not stop that without losing real items. So a task assigned to Me is dropped when its citation is a question on the Mic or a statement on Playback, with no commitment language in it and none from the Mic speaker within 45 seconds. A phrase list cannot recognize every commitment, so other items stay, including those without a citation; losing a real task is worse than showing a doubtful one. An unusable response is retried once, then reported. A timestamp links to that moment in the transcript so items can be checked. Summaries are labelled as machine-generated and can still misstate what happened. Evidence checks catch invented details, not misattribution among real names.

summary.json and summary.md sit beside the text files, with the same private content and retention.

## Capture and recognition

1. Load local CPU model assets and open the configured mic plus selected output-device loopback in shared mode. Windows converts both to 16 kHz mono PCM. No app/PID selection or process enumeration occurs.
2. Start both sources against a common monotonic QPC reference. Mic capture is independent of meeting-app mute.
3. Capture callbacks ignore zero-frame/no-data notifications before timeline or queue updates, then copy borrowed audio packets with nonblocking TryWrite into a bounded packet queue. Empty callbacks do not provide valid audio timing. No inference, disk I/O, blocking queue writes, or transcript logging occur there. Both endpoint capture threads register for MMCSS Audio scheduling so lowering recognition/process priority does not also deprioritize time-sensitive capture.
4. One assembler writes separate PCM clips. An amplitude check can close a clip after eight seconds and half a second of quiet; the configured maximum remains a hard boundary. The assembler never writes transcripts: capture-discontinuity gap records join closed clips in one ordered queue, so a slow transcript write cannot stop it from draining capture.
5. One recognition worker reads closed clips. Silero skips nonspeech clips. Speech-containing clips go to Whisper in full, avoiding VAD cuts around individual words.
6. Offset decoded timestamps onto the session timeline, commit finalized text and queued gap records, then delete the corresponding audio. The app displays committed records as they appear. While capture runs, recognition is the only transcript writer.

The packet queue holds at most 1,920,000 audio bytes (30 seconds of 16 kHz mono PCM from each source) in at most 12,000 packets of at most 32,000 bytes. This absorbs assembly stalls from brief disk or scheduling hiccups; an earlier 256-packet limit covered only about 1.3 seconds and stopped capture during a multi-second transcript write. The closed-clip/gap queue holds at most 1,500 metadata entries; disk storage has a separate shared cap. Decode only one clip at a time. Current, queued, and in-flight clips count toward the same budget.

Prefer positive, valid QPC timestamps and Windows discontinuity flags rather than assuming device-position counters represent resampled frame counts. Zero-frame callbacks can contain unset/zero QPC and must be ignored; they do not establish missing native timing for real audio. When a nonempty packet has missing/uncertain timing, CaptureTimeline falls back to callback arrival minus the configured 100 ms buffer, advances by sample count across batched callbacks, and re-anchors after a discontinuity or callback absence longer than three buffer lengths. Both sources share the session's monotonic reference. Retention timestamps conservatively use the earlier native/estimated time and arrival-minus-buffer. Successful metadata records whether each source needed estimated timing. This estimate has buffer/scheduling uncertainty and is not sample-exact synchronization. Long periods without playback packets preserve elapsed silence. Preserve simultaneous local/remote speech; never gate one source based on the other. Windows defines timestamp-error packets as uncertain timing, not necessarily missing audio; see [buffer flags](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags).

The remote file is labelled as output-device playback: it includes all apps routed to that endpoint, potentially notifications, music, browser audio, and shared media. It does not identify individual people or claim all speech is from the meeting. Audio routed to other endpoints is not captured. Default output changes do not reroute an active session; configure the meeting's actual output and start a new session when changing endpoints. Headphones reduce mic leakage; no echo-cancellation subsystem is included.

Hard clip boundaries can cut words; adjacent clips have no context overlap/deduplication yet. Whisper timestamps are approximate. VAD omissions, accents, short instructions, names/numbers, and boundary errors require representative evaluation. Do not switch models or raise resource limits automatically during capture.

## Retention and failure

The user approved **stop capture, preserve completed text, and mark gaps** at the one-hour limit. Apply the allowance across the common session timeline, with both streams permitted inside it. The PCM byte cap is 16,000 samples/s × 2 bytes × 2 sources × 3,600 s = 230,400,000 bytes, about 220 MiB. Files are headerless PCM.

Track monotonic capture time, including packet-queue residence, and reserve bytes before writing. A one-second watchdog triggers five seconds before age expiry, requests capture stop, and deletes only expiring owned clips before waiting on native inference. Each recognition operation has its own linked cancellation deadline. Newer clips continue processing while their own age permits; do not discard the entire queue just because its oldest clip expires. Discarded audio is removed from backlog status. Storage/capture errors and queue overflow also stop capture. No silent retention extension or automatic restart.

Expired/cancelled clips receive gap records. Failed sessions also carry an incomplete-session annotation because some failures cannot locate missing speech precisely. Capture failures retain the exception type and HRESULT in content-free status/session errors; exception messages and spoken text are not copied into diagnostics. Completed/failed session metadata also contains bounded capture counters, timing/GC/queue measurements (including peak queued packet count and audio seconds), and the first 64 discontinuity events per source, without audio or text. These measurements are accumulated in memory and saved after capture/processing join, never written from callbacks. A native discontinuity can also mark a playback stream transition, so the flag alone does not establish lost speech. Do not fabricate lost text. Normal stop drains unexpired work under the same watchdog.

Each audio workspace has a unique GUID and ownership marker. Cleanup holds the exclusive worker lock, verifies ownership, skips links, and deletes recognizable GUID-named PCM files only. Preserve unknown files/subdirectories; they must not prevent removal of owned audio. Never sweep shared temp storage or delete finalized text.

After a crash, remove abandoned owned clips when the app next opens or a recording starts; do not recover/replay audio. A crashed, suspended, or storage-blocked machine cannot execute deletion, so clips can remain until cleanup runs. Ordinary deletion is not forensic erasure. OS paging, process memory, dumps, backups, synchronization, or a compromised OS are outside an absolute machine-wide privacy guarantee. Custom at-rest encryption is outside this prototype.

## Durable text

Each timestamp-plus-GUID session directory contains local.txt, remote.txt, canonical per-clip JSON under segments/, content-free session.json lifecycle metadata, and summary.json/summary.md once summarized. Successful sessions record the resolved mic and playback endpoint IDs. Local text is labelled to include app-muted notes to self. Remote participants remain grouped, and the playback header discloses other apps' audio.

Write each canonical GUID record to a unique temporary file, flush to disk, and atomically rename. Never replace a committed identity on retry. Rebuild sorted UTF-8 text views and atomically replace them. If interrupted between canonical commit and export, the text files can lag behind the records; the app reads the records, so it still shows everything, without audio replay or duplication. The two text views are not one filesystem transaction; canonical records are the recovery source.

A new silent/whitespace-only record without a gap still commits its canonical identity, but skips sorting and replacing an already-current text view. Speech and gaps export immediately. Failed exports remain pending for the next commit from that source, including silent clips; identity retries still rebuild views.

Keep text until manual deletion. The segments directory and summary files contain the same private spoken content and have the same retention. No invented speakers or transcript-bearing diagnostics.

## Setup, packaging, and licensing

The development script scripts/download-models.ps1 uses scripts/model-assets.psd1 for the pinned repository revisions, sizes, and SHA-256 hashes of the speech models, shared with release packaging. Reuse valid files, reject corrupt existing files without overwriting, download into owned temporary files, verify, and rename. The speech models ship inside the release package.

Default assets: ggml-base.en-q5_1.bin (59,721,011 bytes) and ggml-silero-v5.1.2.bin (885,098 bytes). Optional ggml-base.en.bin is the same model family. The optional transcription models' pinned URLs, sizes, and hashes live in SpeechModels. The optional summary model Qwen3.5-4B-Q4_K_M.gguf (2,740,937,888 bytes) is not part of the package, so updates do not fetch it again and recording works without it. Its pinned URL and hash live in ModelDownloader. The helper fetches it on request using the system proxy settings and no script host, which matters on managed machines. It resumes from a partial file after a dropped connection or a closed app, refuses to overwrite a mismatched existing file, starts over if a server resumes from the wrong position, stops at the pinned size, and refuses links at its file names. Nothing takes the real name until its hash is verified. Offline import means placing the file in that folder or choosing any local GGUF in Settings.

scripts/build.ps1 -Publish builds/tests and publishes both executables into one self-contained x64 directory with licenses/notices. scripts/package.ps1 publishes into a new owned directory, copies and verifies local speech models, and runs Velopack's packager, producing a portable ZIP, full package, and release metadata. scripts/release.ps1 requires clean committed source matching origin/main and uploads them to a draft GitHub release before publishing it. Application source is MIT; third-party/model terms remain in licenses/ and THIRD-PARTY-NOTICES.md.


## Explicit updates

Updates use Velopack, as portable packages with a small launcher, Update.exe, and the program in a `current` folder that each update replaces. The update feed is this repository's public GitHub Releases, including previews.

The app never checks by itself. Pressing the button runs the helper in check-update mode, which checks, downloads, and verifies the package, reporting only progress and the resulting version. The app and recording worker therefore stay offline. Applying needs no network: the app refuses while a recording is running or finishing, stops any summary and download, checks that no helper from this installation is still running under any state directory, hands over to Update.exe, and exits; Update.exe replaces `current` and restarts the app. A downloaded update stays available across restarts. Updates never apply or restart by themselves.

Transcripts and the summary model sit outside `current`, and settings live in the state directory, so all three survive. Settings refuse a destination or model inside `current`, and an update is refused if older settings point there. Package hashes detect corruption. Releases are unsigned, so authenticity relies on HTTPS and control of the repository. Velopack keeps a log under %LOCALAPPDATA%\velopack, which records update steps and paths, not meeting content.

Earlier previews used a different updater, whose package format this release does not provide, so they install this release once by hand. Offline installation extracts a new ZIP and moves the `transcripts` and `models` folders across.

## Verification and release gates

Behavioral tests cover the summary pipeline with a scripted model (splitting, merging, parsing, retry, evidence checks, recording priority), meeting history, the live transcript feed, app state, and loading the real windows; arbitrary packet/sample preservation, silence timeline offsets, discontinuities, packet-queue audio/packet bounds, shared byte/age accounting, idempotent text/recovery, safe cleanup including unfamiliar files, input validation, configuration persistence, and current-user named-pipe controls.

Synthetic session tests exercise RunAsync with injected capture and recognition implementations, without opening devices or loading native models. They make the audio destination unavailable after clips from both sources are queued, restore it after capture stops, and verify that queued transcription drains without cancellation, incomplete-session gaps are written, and owned audio is deleted. Normal stop also drains while devices become inactive. A discontinuity during busy recognition must queue its gap record rather than write it from assembly, and capture continues meanwhile. These tests cover session coordination, not native inference under real disk failure or the full retention watchdog.

Explicit native tests use a hash-pinned public JFK fixture, never private meetings. Parakeet goes through the same recognizer checks when its model is in the models folder, plus fragments as short as 10 ms. They cover speech/silence, a sub-second stop fragment, cancellation/reuse, default/explicit output selection and wrong-direction/missing endpoint rejection, and output playback → clips → recognition → committed text → audio deletion. A fresh process checks the baseline CPU library. The script can download the public fixture during setup; cached assets permit local recognition checks. The capture check briefly plays quiet public speech, never opens the mic, and refuses unrelated active playback before/during capture and before persistence/transcription. This guard is not an absolute guarantee against very brief unrelated sounds; use a quiet desktop without meetings/media for native checks.

An update test packages two versions, extracts the first, and presses the real buttons through UI Automation against a local feed. It checks that the new version restarts by itself and that synthetic meetings and the model survive.

Remaining gates before a verified release:

1. Real Zoom and Teams desktop calls with configured mic and monitor-jack headphones: simultaneous speech, app-muted notes, client restarts, and device changes.
2. Sustained low-powered-laptop webcam/screen-share testing; agree measured CPU/memory/responsiveness and completion-delay budgets. Development-machine speed is not that guarantee.
3. Indian, Irish, and Danish accent samples, short instructions, names/numbers, and clip boundaries.
4. Full-worker disk/crash/expiry/duplicate-start/disconnect/restart testing, including expiry during native inference, active-session backlog draining after Stop, and the full-hour recording timer. Unit budget accounting alone does not establish watchdog behavior.
5. Network observation during the full live workflow and recognition with networking disabled. Local-file/CPU calls and source inspection support the design but do not replace this acceptance check.

6. Summary quality on real meetings. Evaluation so far used synthetic transcripts on a development desktop; real Whisper output, accents, integrated-GPU behaviour, and low-powered-laptop duration and memory remain unmeasured. The default model peaks near 3.2 GB of memory.

Automatic meeting detection and individual-speaker diarization remain deferred bonuses. Keep personal equipment specifications, biography, employer details, and private samples/transcripts out of the public repository.
