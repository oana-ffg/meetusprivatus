# Repository guidance

## Scope and privacy

Meetus Privatus is an MIT-licensed Windows 11 local meeting transcriber for Zoom/Teams desktop apps. Communicate in English, work autonomously within authorization, and state uncertainty clearly. Read REQUIREMENTS.md and SPEC.md before changing behavior.

- The running app must make no outbound calls, uploads, telemetry, automatic downloads/updates, cloud inference, or crash reporting. Do not add an HTTP server.
- Explicit setup scripts may fetch public models with pinned revisions and integrity checks; offline import must work. Reject network paths and reparse-point ancestors before application filesystem access. Development may research public docs, never send private meeting content externally.
- Use Whisper base.en and CPU runtimes. Prioritize low-powered-laptop webcam/screen-share responsiveness. No speculative unused engines or GPU requirement.
- Capture configured mic speech throughout an active session, **including while muted in Zoom/Teams**. Label notes to self honestly; do not add app-mute tracking. Assistant stop controls its own capture.
- Capture all playback on the selected output device, independently of Zoom/Teams processes. Default to the Windows output device; allow explicit endpoint configuration. Do not silently switch devices during a session. Label playback honestly, including other apps' audio, without invented identities.
- Baseline: headphones on a monitor audio jack, laptop built-in mic. No headset requirement or costly echo processing without evidence.
- Minimize user effort: double-click Start.cmd, Ctrl+C to stop early, no typed startup/shutdown or required configuration. The status console must not display live text. Default output is local and automatic. No sign-in startup or GUI; automatic meeting detection and individual-speaker diarization are deferred bonuses.
- Auto-stop capture after 60 minutes by default; duration is configurable. Ctrl+C and timed stop end capture and drain queued transcription before deleting processed clips, under the existing retention/failure limits. Do not interpret Ctrl+C as abandoning the backlog or terminating the process immediately.
- Short clips are allowed, maximum 30 seconds. Delete processed audio promptly. Both sources share the one-hour pending timeline and documented byte cap.
- Confirmed overload policy: stop capture, preserve committed text, and mark gaps. Give each clip a cancellation deadline and delete only expiring audio; let newer queued speech finish within its own bounds. No silent retention/resource escalation or auto-restart.
- Cleanup holds the worker lock, verifies owned workspaces, skips links, and preserves unfamiliar/user files and text. Never sweep shared temp storage.
- Be honest about crash/suspension cleanup, memory, OS paging/dumps/backups/sync, and forensic-erasure limits. No absolute machine-wide privacy promise.
- No meeting content in diagnostics/public artifacts. Do not commit personal equipment makes/models, exact personal specs, biography, employer info, private recordings, or private generated text.
- Public MIT attribution: Meetus Privatus contributors. Preserve dependency/model licenses and notices; never invent attribution.

## Maintainability and correctness

- Clear names, cohesive functions, simple control flow, small explicit interfaces. No temporary hacks, copied logic where sharing fits, speculative features, or unnecessary abstraction.
- Warn immediately about dead exports, unused helpers, orphaned code, or stale branches. Remove them in the relevant change; no "needed later" exemption.
- Bound audio buffers, storage, concurrency, and retention. Capture callbacks cannot infer, write files/logs, or block on queues.
- Maintain a common monotonic timeline. Prefer valid QPC and native discontinuity flags. Some resampled output endpoints return zero QPC; use a documented arrival/buffer estimate and sample progression when timestamps are missing/uncertain. Preserve playback silence, and conservatively account for retention.
- Preserve overlap from both sources. Do not gate local speech on remote activity. Fixed clip boundaries currently have no context overlap; test/document the accuracy tradeoff.
- Canonical clip identities are idempotent. Flush/atomically replace records and derived views. Repair cannot duplicate text or replay abandoned audio.
- Current-user-only controls and new private directories. Configuration/output changes affect future sessions; do not redirect failed output silently.
- Keep generated models/audio/transcripts/tests/publication ignored. Source, docs, package locks, and verified notices belong in version control.

## Validation

- Run scripts/build.ps1 for warnings-as-errors build and behavioral checks; -Publish verifies publication. Never conceal failures.
- Run scripts/test-native.ps1 when capture/recognition/native packaging changes. It uses a hash-pinned public fixture and quiet public playback, **never the mic**. It can download the public fixture during explicit setup; cached recognition stays local.
- Native checks cover output-device playback-to-text/audio deletion, endpoint selection, speech/silence/cancellation, and baseline CPU in a separate process. Refuse unrelated active playback before or during the public capture check. Do not call these live-client or weak-laptop acceptance.
- Live gates: real Zoom/Teams, app-muted notes, actual routing/overlap, device/client failures, sustained webcam/screen share, low-powered-laptop budgets, and named accents.
- Verify full-worker disk/crash/expiry/restart/duplicate-start handling and network observation/offline use. Unit budget checks alone do not verify the whole watchdog.
- Use public/synthetic/authorized recordings. Do not capture private ambient audio or upload samples for tests. State which checks ran and which did not.

## Collaboration and safety

- Emphasize outcomes, needed actions, and unresolved choices; keep implementation detail secondary.
- Ask about confusing requests or actual unresolved product choices. An acknowledgement is not selection between alternatives. Ordinary implementation choices within authorization need no repeated approval.
- Never invent facts or use placeholders in product content/attribution; mark genuine unresolved validation explicitly.
- Explain materially unsafe/unsuitable proposals and seek clarification. No major destructive operations, resets, broad reverts, or unrelated deletion without permission.
- Preserve unrelated work. Never modify skills or .claude unless explicitly requested.
