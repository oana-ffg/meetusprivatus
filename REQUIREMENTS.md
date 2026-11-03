# Meetus Privatus — Requirements

Status: confirmed product requirements and implementation defaults, updated 2026-11-03. SPEC.md describes the prototype. Live acceptance and quantitative resource budgets remain unverified.

## Purpose

Produce useful text that jogs the user's memory about meeting discussions and instructions. Perfect transcription is not expected; maximize practical accuracy within privacy and meeting-responsiveness constraints. The product is a Windows desktop app: start and stop a recording, follow the text as it is transcribed (delays are acceptable), get a summary with action items at the end from a local LLM, and view prior meetings.

## Confirmed scope

- Windows 11; Zoom and Teams **desktop clients**. Browser meetings are outside initial scope.
- English, including Indian, Irish, and Danish accents.
- Capture local mic speech and all playback on the selected output device. Starting must not require selecting Zoom/Teams or finding a meeting process. Two transcripts are acceptable; individual remote-speaker diarization is a bonus. Other apps' speech on the same output can appear in the playback transcript.
- Capture mic speech even while muted in Zoom/Teams, including notes to self. Do not imply all local speech was heard by participants.
- Headphones connected to a monitor audio jack and a laptop's built-in mic. No headset requirement; loudspeaker/echo-cancellation support is outside initial scope.
- Transcription runs on the CPU on a low-powered laptop, without a discrete GPU or noticeable lag during webcam/screen sharing/messages. Summaries run after capture, on the CPU by default, with an optional setting to use a GPU, including integrated ones.
- Incremental transcription during meetings, displayed live inside the app. Delays are acceptable. Live text appears only in the app window; it stays out of status, diagnostics, and notifications.
- Minimal effort for normal use: open the app, press Start, press Stop. No typed commands or app selection. Default local output works without configuration. Device and duration settings are available in the app.
- At the end of a meeting, generate a summary with action items using a local LLM. The transcript remains the record; a summary is a fallible aid and is labelled as machine-generated.
- View prior meetings: list, transcript, and summary.
- Manual startup is sufficient. Automatic meeting detection/start is an optional bonus. No Windows sign-in startup.
- Default transcript output is the `transcripts` folder beside the program's launcher, which updates leave alone. The app updates itself when the user presses a button, without browser/GitHub setup on each update, preserving transcripts and settings.
- Auto-stop capture after one hour by default, with configurable duration. Stop and timed stop must finish queued transcription before deleting its audio, subject to the existing pending-audio retention/failure policy.
- Open source under MIT, attributed to Meetus Privatus contributors.

## Privacy and retention

- The running application makes **no external calls**, uploads, telemetry, cloud inference, automatic downloads/updates, or crash reporting.
- Local models only, including for summaries. No cloud meeting integrations, bots, calendars, or automatic meeting joining.
- Downloading a public model when the user asks is approved; offline import must work. Setup/build networking never authorizes disclosure of meeting content or application networking.
- Checking for updates runs only when the user presses the button, in a separate helper process. It may fetch public release metadata and packages; it must never read or upload meeting content. The same applies to downloading a public summary model. No automatic invocation. Offline installation must remain possible.
- No durable audio recordings. Short temporary clips are allowed and must be deleted promptly after transcript commit.
- Allow up to **one hour of pending audio**, bounded across both sources by age and bytes; never retain processed audio for the entire allowance unnecessarily.
- Confirmed overload behavior: stop capture, preserve completed text, mark gaps, and delete audio before expiry. Do not silently extend retention or auto-restart.
- Preserve committed text on failure. Cleanup only verified app-owned audio; never unrelated user files or transcripts.
- No spoken content in diagnostics, status, notifications, public tests, or support artifacts.
- Reject UNC/mapped network paths and link/junction/cloud-placeholder paths before application filesystem access. User-managed local synchronization and backups remain outside the app's control.
- No personal equipment makes/models, exact personal specifications, user biography, or employer information in the public repository.

The app does not control OS paging, dumps, memory, backups, user-selected synchronization, a compromised OS, or meeting-client networking. Ordinary deletion is not forensic erasure. Crashes/suspension can leave clips until cleanup runs; document these limits honestly.

## Initial engineering defaults

The implementation request authorizes ordinary choices needed to make the manual workflow usable:

- Whisper **base.en**, default Q5_1, plus local Silero speech detection to skip nonspeech clips.
- One coordinated CPU worker, one inference thread initially, BelowNormal priority, configurable thread ceiling of two.
- Default 20-second maximum clips, configurable 5–30 seconds; independent 16 kHz mono PCM streams share a 230,400,000-byte pending cap.
- Output-device loopback, defaulting to the Windows default output at session start; an explicit output endpoint can be configured. Keep that endpoint for the session.
- The desktop app is the only interface. There is no command line.
- Timestamped UTF-8 local.txt and remote.txt in unique session directories, with canonical text records for idempotent recovery.
- Retain text until manual deletion. No automatic text purge or invented speaker identities. Meeting titles come from the local summary, derived from the transcript; without one, the start time is used.
- Content-free inspectable errors, shown in the app, without notification pop-ups.
- First run saves defaults; an unset transcript destination resolves beside the launcher without pinning its install location. The previous standard AppData destination migrates for future sessions; preserve old text and explicit custom destinations. Settings in the app configure future sessions. Recording duration accepts 1–1440 minutes; default 60.

These are implementation decisions, not separately answered product choices or measured accuracy/performance claims. Desired completion delay and CPU/memory/responsiveness budgets remain to be agreed from live measurements.

## Priorities and acceptance

Priorities: local privacy, meeting responsiveness, reliable useful text from both sources, accuracy within those constraints, then optional detection/speaker labels.

Before a verified release:

- Real Zoom and Teams calls produce useful local/remote text with intended routing, simultaneous speech, and app-muted notes.
- Recognition works with networking disabled after setup; observe the full worker for unintended networking and inspect dependency behavior.
- No GPU use during capture. Capture callbacks never infer or block on storage/recognition; concurrency and queues remain bounded.
- Sustained webcam/screen-share use on a representative low-powered laptop meets agreed budgets; measure backlog and completion delay.
- Useful English memory-aid accuracy with Indian, Irish, and Danish accents, names/numbers, short instructions, silence, and clip boundaries, using public/synthetic/authorized samples.
- Playback captures the selected output regardless of which apps are running. Labels explain that other apps' audio is included. Never silently switch output devices.
- Normal stop, overload, write failures, interruption, and restart respect temporary-audio policy; missing speech is marked.
- Configuration persists, unusable output is inspectable, committed text survives where storage permitted a commit, and retries/recovery do not duplicate it.
- Double-click startup works without typing/configuration; Stop ends capture and finishes queued work before cleanup. Timed stop does the same. Any future automatic detection is opt-in and tested for false/missed starts without unrelated recording.
- Source and redistribution include MIT and required third-party/model terms with offline setup instructions.

Actual client compatibility, accent accuracy, low-powered-laptop performance, and full-worker crash/expiry/network checks remain open. Do not report unit/public-audio tests as passing these live acceptance gates.
