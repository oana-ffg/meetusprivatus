# Prototype validation — 2026-11-03

## Desktop app and local summaries — 2026-11-03

Run on a development desktop with synthetic meetings only. No microphone was opened and no private meeting text was used.

**Automated.** Warnings-as-errors build and publication of both executables. 117 tests passed with native checks enabled (real public speech, real output-device playback to text, and the real summary model), and the separate baseline CPU check passed. 

**The real published app, driven through Windows UI Automation against a stand-in worker speaking the real pipe protocol:**

- Start with a nonexistent microphone shows the failure and returns to Start.
- A second launch exits and leaves the first instance in charge.
- The app attaches to a running worker, shows live text in order, and Stop moves through finishing to idle.
- The automatic summary ran with the real model and appeared in the app.
- A worker starting during a summary ended that process within seconds, and an existing summary stayed unchanged.
- Closing while recording asks first. No keeps recording; Yes stops the recording and closes.

An independent code review found and this work fixed: closing during startup could leave capture running with no window; an unanswered status request was treated as the recording ending; attaching to a worker still starting stopped status polling; a failed regeneration replaced a good summary; overlapping refreshes could duplicate lines; repeat removal could drop different owners or wording such as enable and disable; changing the destination hid the live meeting; the new readers skipped the reparse-point rule; and the merge loop had no termination guarantee. The stop-during-startup and closing guards have source review and partial test coverage, but no end-to-end run closing the window during a real startup.

**Summary model evaluation.** Candidates were compared on a synthetic meeting with seven known action items. These are single runs on one invented transcript, so they guided the choice and are not accuracy measurements.

| Setup | Result |
| --- | --- |
| Qwen3.5-4B, JSON with grammar constraint | Empty lists or unusable output, and one output that misstated a result |
| Qwen3.5-2B, JSON with grammar constraint | Two to four items |
| Gemma 4 E2B | Refused: its chat template is not recognized by llama.cpp's template API at this version |
| Qwen3.5-4B, line format, reasoning allowed | 3,000-token limit reached in 449 seconds without an answer |
| Qwen3.5-2B, line format, no reasoning | Four items in 22 seconds, with owners wrong |
| Qwen3.5-4B, line format, no reasoning | Six of seven in 65 seconds, with correct owners and deadlines |

Three further runs of the chosen setup varied: four or five items, with Playback speakers' tasks sometimes assigned to Me. A prompt rule stating that "I'll" on a Playback line is someone else fixed the owners.

A five-line script in which nobody commits to anything then exposed the opposite failure: the model invented a task for Me, such as "prepare a contingency plan", in every run. Softening the prompt's "meetings usually have several" reduced invented items but lost most real ones, leaving two of seven, so it was restored. A check in code now drops a task assigned to Me unless its cited line asks or commits to something, or the Mic speaker committed to something within 45 seconds. Five runs of each script afterwards, on the GPU: no invented item on the five-line script in any run, and five or six entries covering at least six of the seven real items on the longer one. One earlier run assigned a task to the wrong person, so owners can still be wrong. These are two invented scripts, and summaries can miss or misstate things.

Through the finished pipeline at three threads, the three-minute meeting took 80 seconds, peaking at about 3.2 GB. A transcript of about 6,800 words, roughly 40 minutes of speech, took 961 seconds over three steps at the same peak. That transcript repeats its topics, which left many reworded items after merging and prompted the repeat removal; the result was not re-measured on it afterwards.

**Privacy observation.** 102 samples across a whole summary run saw no TCP or UDP endpoints on the process, BelowNormal priority throughout, and no network-capable modules loaded. The llama.cpp libraries contain no Winsock, WinHTTP, WinINet, URLMon, or libcurl references. This covers the summarizer and not the full live workflow, which remains an open gate.

**Corrected during this work:**

- Windows PowerShell 5 leaves $PSScriptRoot empty in parameter defaults, which broke download-models.ps1 and test-native.ps1 when run without arguments.

**Command line removed.** The isolated console Ctrl+C check failed on this machine at this point, and on unmodified main too, so this work did not cause it; the cause was not established. It was removed along with the command-line run path it tested.

**Portable update.** scripts/test-updates.ps1 passed with the real buttons: 0.0.1 downloaded 0.0.2 from a local feed, restarted itself on the new version, kept synthetic meetings and the model file, and then reported itself current. Checking against real GitHub Releases is exercised by the first published update, not by this test.

**GPU.** On a mid-range discrete GPU through Vulkan, the transcript of about 6,800 words took 42 seconds against 961 on the CPU, and the three-minute meeting took 60 seconds against about 80. With Vulkan drivers hidden, the summary ran on the CPU and said so. Rerunning after a GPU crash was not exercised with a real driver failure. Integrated GPUs were not tested.

**Not checked:** integrated GPUs; the real on-screen window, including its title bar and backdrop, the live view, and minimum size (windows were rendered off-screen in both themes, and driven through UI Automation without being looked at); real Zoom or Teams calls; real Whisper output through the summarizer; accents; low-powered-laptop duration or memory; real microphone use with the app; the 2.7 GB download through the app's button (the downloader's resume, checksum, and refusal behaviour have tests against a scripted server); recording with screen sharing active.

# Prototype validation — 2026-10-13

## Capture queue overflow during a transcript stall — 2026-10-14

A real worker session failed after 288.75 seconds with "Audio capture queue filled." Its content-free metadata showed maximum callback intervals of approximately 350 ms on both endpoints and native discontinuity flags near the failure (GC pause 0 ms at those events), with the packet queue already at 125–151 of 256. The maximum assembly operation took 9,992 ms, and the maximum transcript commit took 9,085 ms including lock waiting. These maxima do not establish whether the two operations overlapped. Source inspection found that assembly synchronously committed discontinuity gap records under the transcript lock shared with recognition; each commit flushes two files to disk. That blocking path is consistent with the overflow: the 256-packet queue covered only about 1.3 seconds of both active sources. The cause of the long operations and callback delays was not established.

- Gap records now travel through the closed-clip queue and are committed by recognition, so assembly never waits for transcript storage. The transcript lock is removed; recognition is the only writer during capture.
- The packet queue now holds 30 seconds of audio from each source (1,920,000 bytes, at most 12,000 packets). Session metadata adds peak queued audio seconds.
- A new synthetic session check fails on the previous code: with recognition paused, a discontinuity no longer writes a gap record, and capture continues into the next clip. A queue check verifies the byte/packet bounds.
- Warnings-as-errors build and all 28 behavioral checks passed. The two native speech/playback checks, separate baseline CPU check, and console cancellation/cleanup check passed.

A real multi-second disk stall was not reproduced. The synthetic session check verifies writer separation and continued assembly while recognition is paused; it does not inject a slow transcript flush or reproduce the original incident. Meeting-client, target-laptop, and the other gates below remain open.

## Release update path limitation — 2026-10-14

An actual preview.3-to-preview.4 offline update check in a deeply nested isolated installation failed before file replacement at the incoming executable's startup check. A separate reproduction with a 263-character executable path failed in Windows PowerShell with an argument-parsing error and failed to launch from PowerShell 7/direct process invocation. The identical packaged executable passed its help check in a shallower directory. The updater stages beneath the installation folder, which lengthens incoming paths. This existing path limitation is unresolved; installations should use a short local folder path. The failed update left the previous installation and synthetic user files intact.

## Capture discontinuity correction — 2026-10-13

The normal worker was run with its real microphone and output-device capture on a development desktop. Speaking with the desktop idle reproduced recurring microphone discontinuities. No meeting-client or target-laptop acceptance is implied by these runs.

- Capture was inheriting the worker's BelowNormal scheduling without MMCSS registration. With empty callbacks already ignored, a 195.56-second run still produced 38 microphone discontinuities and a maximum 1,019.08 ms interval between audio callbacks. Many occurred without recognition active; the capture packet queue did not overflow.
- Both endpoint capture threads now request MMCSS Audio priority, while recognition stays CPU-only in the BelowNormal worker. A 211.23-second comparison produced microphone and playback text, zero microphone discontinuities, and a maximum 13.86 ms microphone callback interval. Both capture threads reported priority level 15. Two playback flags coincided with deliberate player restarts between public clips.
- Roughly half the recorder callbacks carried zero frames and unset timing. Treating these as audio falsely marked native timing as unavailable and consumed packet-queue slots. No-data callbacks are now ignored before timeline/queue updates; real silence packets with nonzero frame counts are retained. Real audio in the corrected runs used valid native timestamps for both sources.
- A final 135.20-second normal-worker run with real mic capture and six quiet public speech clips concatenated into one continuous playback stream produced zero discontinuities on either source, committed playback text, completed gracefully, and left zero temporary audio files. The maximum capture packet queue depth was 23 of 256. This final run contained no recognized microphone speech; microphone text was verified in the preceding comparison.
- Content-free session metadata now retains bounded timing/queue/GC measurements and up to 64 discontinuity events per source. Capture errors retain type/HRESULT without exception messages or spoken text. The regression check verifies that empty callbacks cannot split clips, create gaps, or replace valid native timing.
- Warnings-as-errors build/publication and all 24 behavioral checks passed. The two native checks, separate baseline CPU check, and console cancellation/cleanup check also passed. An existing repair-lock check failed once on a rebuild, then passed in isolation and in the full final suite; that transient failure's cause was not established.

Playback stream transitions can legitimately produce discontinuity flags even when no speech was playing during the interval. The flag alone does not quantify lost speech; gap reporting has not been suppressed. The earlier fatal capture-queue overflow did not reproduce during this work and still requires separate validation. Meeting-client, sustained target-laptop, accent, full-retention-watchdog, and network-observation gates below remain open.

## Manual updater and adjacent transcripts — 2026-10-13

Warnings-as-errors build/publication and all 26 behavioral checks passed. The two native speech/playback checks and separate baseline CPU check passed. The console cancellation check initially exposed an existing minimal configuration that did not materialize implicit defaults; startup now saves the validated defaults, and the corrected console check passed. Default-output checks cover the adjacent transcripts folder, migration of the previous standard destination, preservation of old text/custom choices, and an explicit return to default.

The actual updater passed isolated offline checks with a fresh published executable, verified models, and synthetic user files: installation-lease exclusion, bad checksum and archive-traversal rejection, unrelated-file collision preservation, persistent file-replacement failure, recording exclusion during a pending transaction, rollback/retry, committed-transaction cleanup, obsolete owned-file removal, per-file integrity, and transcript preservation. These checks opened no microphone and made no network requests.

One earlier replacement run returned Windows ERROR_UNABLE_TO_REMOVE_REPLACED after rollback; the previous package was restored and a subsequent independent update succeeded. The holder/cause was not identified. Replacement now reports the file/HRESULT and waits at most 1.8 seconds only for sharing/lock errors and the Windows error that preserves both original names; persistent contention still fails and rolls back. Both persistent failure/recovery and the final complete updater check passed.

The recording app remains separate from the manually invoked downloader. These checks do not establish full-worker network-observation, power-loss recovery, meeting-client, target-laptop, or fatal capture-queue acceptance.

## Initial validation — 2026-10-10

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
