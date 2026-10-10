# Prototype validation — 2026-10-10

The manual-start prototype builds and publishes successfully with warnings treated as errors. Seventeen distinct tests pass with native checks explicitly enabled: fourteen behavioral/control checks, two native pipeline checks, and one baseline CPU check in a fresh process. The ordinary build intentionally skips the three native tests. The separate console check verifies real Windows Ctrl+C delivery during startup without opening a microphone.

## Checks performed

- Windows x64 self-contained publication with only x64 CPU native assets, local base.en Q5_1/Silero models, application/dependency notices, and the actual bundled .NET runtime notices.
- Public JFK speech recognition, silence suppression, a 750 ms speech fragment, cancellation during inference, and successful processor reuse afterward. One inference thread, GPU use disabled.
- Output-device WASAPI capture: the production recorder captures quiet public playback on the default output endpoint, using the same endpoint for the test player. Explicit endpoint selection matches the default; wrong-direction and nonexistent endpoints are rejected. The joined playback → PCM clips → local recognition → canonical/text commit → audio deletion test passes. The transcript header discloses other apps' audio. No microphone was opened. A polling guard refuses unrelated active playback before/during recording and before persistence/transcription; it cannot guarantee detection of very brief unrelated sounds.
- The tested resampled output endpoint supplied zero QPC with no error flag. The original timestamp assertion failed; CaptureTimeline now uses documented monotonic arrival/sample estimates when native timestamps are missing/uncertain. Native pipeline and synthetic batched-packet/silence/retention-clock checks pass with this handling.
- Sample preservation across arbitrary packet boundaries, playback-silence timeline offsets, and native discontinuity annotations.
- Shared storage/age accounting and selective expiry: expiring audio is discarded without discarding newer work.
- Idempotent canonical commits and text repair, protected cleanup with unfamiliar files, input/UNC path rejection, playback/duration configuration persistence, removed-process-option rejection, current-user named-pipe controls, and cancelling before recording starts. Duration defaults to 60 minutes with bounded configuration.
- Real Windows Ctrl+C delivery to a separately created hidden console: the foreground run exited successfully through Complete, cleaned owned audio including a seeded abandoned synthetic clip, and saved automatic output/default-duration settings. A deliberately nonexistent microphone guards against unintended recording. This establishes console cancellation during startup; it does not establish live microphone/client acceptance or backlog draining under a real call.
- Published CLI help/configuration and hidden-worker startup failure with a deliberately nonexistent input endpoint. Failure was inspectable and no owned audio remained. The sandbox blocked the initial launch attempt; the same isolated check passed outside the sandbox.
- Model setup: exact pinned size/SHA-256 validation, reuse of verified existing assets, and corrupt-file rejection without overwrite or temporary-download leftovers.
- Private generated assets are Git-ignored. Published project debug paths are mapped to a generic source root. No personal hardware specifications or employer details are included in tracked source/docs.

The native test fixture is public development data in ignored artifacts. These checks establish a functioning local pipeline; they do not establish target-laptop meeting responsiveness or actual desktop-client compatibility.

## Still required before a verified release

- Real Zoom and Teams calls, actual mic/headphone routing, simultaneous speech, app-muted notes, client restarts, and device changes. Ctrl+C and timed stop while actively capturing/finishing a real call, plus measured 60-minute auto-stop. The recording timer signals normal stop without cancelling inference; a full-hour timed run was not performed.
- Indian, Irish, and Danish accent accuracy; instructions, names/numbers, and clip boundaries.
- Sustained low-powered-laptop webcam/screen-share measurements and agreed resource/completion-delay budgets.
- Full-worker long-running expiry/crash/disk-error/disconnect/duplicate-start injection. Unit expiry checks and native cancellation/reuse do not replace the combined watchdog test.
- Network observation of the full live worker and recognition with networking disabled after local setup. Source inspection/local-file calls are not a substitute for that observation.

Automatic meeting detection and individual remote-speaker diarization are deferred. Keep private meeting samples and transcripts local; never upload them to complete these checks.
