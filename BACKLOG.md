# Backlog

## 2026-11-16 Task-side includes (lib-open-rigs 5ea5729; Audio .11, Control .12, Core .19, Devices .22, Logging .15, Manifest .23, Video .17)

- `LaunchSettings` (OpenRigs.Control) publishes `SubjectId`, `TaskFilePath` and `RunSettings` (subject, session); Initialization keeps ExperimentControl and the training stage, now `ParameterValue TrainingStage` at Go into its own `TrainingStage` AsyncSubject, as coen-dome-navigation's, instead of `RunSettings`' third item. One inner `ExternalizedMapping` maps `SubjectId` onto ExperimentControl and LaunchSettings.
- `SpoutStepSize`: DomeHardware's `LickSpoutStage` takes the parameter live and shows its default (150); the task's subject, `OnParameterSet`, `ParameterDefault` and the mapping onto DomeHardware are gone. The parameter's declaration stays on ExperimentControl.
- `BackgroundNoise` (OpenRigs.Audio) after Audio reads `LocalFiles\background_white_noise.wav` (`ReadWave`, refusing another rate, which `CheckWaveRate` did), plays it and stops Go on a changed `backgroundNoise`. `ReadWave` maps 16-bit PCM onto -1 to 1 as `(x + 32768) * 2 / 65535 - 1` where the old `AudioReader` path took the samples as they were: after levelling to `levelDbfs` the largest sample difference is -62 dB re the noise's RMS and the level changes by 3e-6 dB (a DC offset of half a 16-bit step), so the mouse hears the same noise.
- `ExperimentRecords` (OpenRigs.Logging), after TaskLogic and before LiveView, holds `DeliverReward`, `LogControlEvents`, `RecordHarpDevices` and `ExperimentManifest`, with the same `ConfigRoot`, `IngestScript` and `ExpectedFiles`.
- ShowGrating's `TaskConfig` subscription fed both DrawGratings' mapping and the `Aperture` transform (`shared_mapping_problems`): `Aperture` has its own subscription now. A shared source hands a ready value on only once every successor has subscribed, so the temporal and spatial frequency and orientation could reach DrawGratings after it started (its XML defaults: 0.0667 cpd, 0 Hz, 0 deg); whether a frame was ever drawn before them was not measured (no run).
- Nothing written changes: trial columns, `ExpectedFiles`, async events, `Settings_*` and device log names are as before (checked against the old workflow). `tests\test_workflow.py` calls `launch_settings_problems`, `spout_step_size_problems`, `background_noise_problems` and `experiment_records_problems`.

## Audit of the device-pieces cut (16 Nov 2026): not changed

- Rig lists are registered in the lint: deliberate, documented and tested; an unregistered list fails loudly.
- `PulseOutputs` and `ZapitTriggerLine` are not in `ONE_EACH`: no rig has two boards, and a collision fails the build.
- The 7-line `check-workflow` wrappers are identical, and each repo's lint test calls are thin, by design.
- The Ready-announcer rule is not run over expanded rig lists: lib's own scan covers every rig list in lib.
- No package dependency from `OpenRigs.Devices` on `OpenRigs.Audio` or `OpenRigs.Vision` for `DomeHardware`: a missing package fails the build check, the existing convention.
- `ReadyWaitsFor` is not exposed on `DomeHardware`: Ready waits for exactly what the pieces announce.
- Package version reuse drift: the push script refuses a reused version with different contents.
- `WindowState` is not exposed on `DomeHardware`; it was not before the cut either.

## 2026-11-15 Rig hardware as device pieces (lib-open-rigs, Pip: "NO BACKWARDS COMPATIBILITY")

- The hardware is lib-open-rigs' `DomeHardware` rig list (one include per device, the Ready gate, the timestamp generator's clock, the camera triggers), after `RigSetup`'s `ReadLabRig`; `RigHardware`, `HarpLogs` and `LogCameras` are gone from lib. Ready waits for the Harp clock, the spouts homed, a first frame from each camera and the mixer (`ReadyWaitsFor`). `LogLabExperiment` is its own top-level node before `Hardware` (a piece's log waits for `PathPrefix`; Bonsai builds a subject's whole top-level node when a subscriber comes first, so inside `Logging`, beside `DeliverReward`'s `Timestamp`, it built as "defined in terms of itself"); coen-dome-navigation now has the same groups.
- Zapit is `Zapit.ConnectForTask`, the manual rewards `BehaviorBoard.ManualRewards` (`ManualRewardDropUl` and the `Commands` group gone), the shared logs `LogLabExperiment` and `LogControlEvents`; the manifest's `ConfigFiles` is lib's default `{RigFiles}` (rig.yml, every file it names, the mouse's spout positions). The background noise waits for `Ready` itself, not a copy of its stages. The quiet-wait checks are lib's (`QuietWait.SettingsProblems` at Go, open_rigs.core.task's caps in task.py), so task.py has no constants of its own.
- **Data from this change on:** every video and its CSV include the first frame (the old include skipped it), and a dropped frame is a jump in `frameId` with the triggers around it kept (frames were paired to triggers in order). The cameras' trigger starts once all three cameras acquire (it was 2 s after Start).
- **Phase 1 camera finding (recorded, past video left as is; Pip):** 7 av-dome sessions (12-13 Nov 2026, 4 rigs; av-dome's experiments count as this workflow's) had no frameId gaps, no non-Event register-92 messages, and camera clock and `harpTime` steps agreeing within 10 ms on every row, so no mid-run shift. Cameras in one session started 0 to 2 triggers apart; whether a late starter missed its first trigger or is paired one trigger late (at most 20 ms, constant) cannot be told from logs. The first test experiment after this change measures it again (k = 0 expected).
- 16 Nov 2026: `{RigFiles}` also records `valve-DO1.json` and `valve-DO2.json` now, which the old hand-typed `ConfigFiles` lists did not.

## 15 Nov 2026: Pip's decisions after the audit cycles

- swc-aeon is pinned to the commit uv.lock held (`rev = "af0d7a7..."`), not aeon_api's `main` branch.
- The repo is BSD 3-Clause, as lib-open-rigs (`LICENSE`, "Coen Lab, University College London", as coen-behaviour-box's; lib's names upstream's holder).
- The quiet waits have a maximum, in task.py and at Go (`TrialDraw.CheckTask`): `lickQuietSeconds` and `wheelQuietSeconds` 10 s (a longer wait and a licking mouse would hold Go with no message), `wheelQuietCm` 20 cm (under one turn of the 75 mm wheel, 23.6 cm: a whole turn within the wait is running, not stillness). Every stage of both task files has them at 0, so nothing changes for the mice.
- The WAVs' sample rate is already checked against rig.yml's `speakerArray.device.sampleRate` at Start (`CheckWaveRate` for the background noise, `Wave.Read` with the speakers' rate for the stimuli and the lab's penalty sound); coen-dome-navigation's waveforms are now checked the same way.
- "Go" is only the experimenter's (app-rig-control's button, `ExperimentGo`, `CheckTaskAtGo`); each trial's start after its interval and quiet waits is "the trial start": `WaitForTrialStart`, `TrialStartWaiter`, `TrialStartFor`, the `TrialStart` subject, `Trial.startTime` and `triggerAfterStartSeconds` (were `WaitForGo`, `GoWaiter`, `TrialGoFor`, `TrialGo`, `goTime`, `triggerAfterGoSeconds`). Nothing on disk changed: the column is still `trialStartTime`, task files and schema as before. The quiet waits' schema descriptions and `QuietWait.GoTime` are lib-open-rigs'.
- `THIRD-PARTY-NOTICES.md` is coen-behaviour-box's: one section, every `local_packages\OpenRigs.*` package (the 11 in `.bonsai\Bonsai.config`) carries lib-open-rigs' BSD 3-Clause `LICENSE`, which it quotes (there was none, though it carries the same packages).

## 2026-11-15 The background noise from Ready (Pip)

- **Changed for the mice:** the background noise starts at Ready, not Go, faded in over 2 s. Ready waits for the mixer (RigHardware's `ReadyWaitsFor`: `HarpClockSet, SpoutsHomed, MixerStarted`), so the noise never reaches the mixer before it starts. Its level is the task file's as rig-control wrote it at launch, read at Start (`LaunchTaskConfig`) and checked then to peak under -1 dBFS; the stimuli and penalty sound still take theirs at Go.
- Go stops the workflow, saying so, if the form changed `backgroundNoise` (`enabled` or `levelDbfs`) after launch: the noise already plays at the launch's level. To play it at another level, launch again with a task file that has it.
- `TaskFilePath` is published at Start (it was at Go); `TaskConfig` is still read at Go.

## Audit cycles 1-3 (15 Nov 2026)

- Done, details in `git log`: names and descriptions say what the code does; `TrialDraw.CheckTask` refuses a NaN or infinite time at Go (test); RigSetup reads rig.yml with lib-open-rigs' `ReadLabRig` (a test holds its `ConfigRoot`, as check-workflow does not look inside an include).

## Audit cycle 4 (15 Nov 2026)

- Deleted what nothing used: the `pydantic-settings` dependency, the unused `dsp` xmlns, `.gitignore`'s line for the Zapit demo's output, the CI's `--with` packages the dev group already has, and BACKLOG history (sections before the rebuild of 13 Nov 2026 are one line each; their decisions and not-changed reasons stay). The task's own blocks (`TrialStructure`, `VisualStimulus`, `RewardSettings`, `ResponseSettings`, the trial-type weights and rules, `RepeatSettings`) are lib-open-rigs `TaskBlock`s, so the task file refuses inf and NaN in them (the schema is unchanged; test). A test runs lib-open-rigs' `fixed_width_pulses_on_reward_valves` against the lab's rig template.

## Audit cycle 5 (15 Nov 2026)

- Tests now fail when the model changes without `regenerate-schemas` (the committed schema is the model's, as coen-dome-navigation's; `Generated.cs` names every schema field) and when a `trainingStages` row is added without raising TrainingStage's `Maximum`. check-workflow sees ReadLabRig's `ConfigRoot` again (lib-open-rigs bb1b388, 4 nodes), so the test of it holds only the root's value to the lab's.

## Open

- Old log readers deleted 15 Nov 2026 (Pip); migrating every mouse's past sessions into the current format is planned in its own session.

## Audit rounds 1-3 of 3 (15 Nov 2026, stage C): items not changed

- `stage.py` reads the mouse's session summaries (jacuzzi-ingest's `sub-<lab ID>/<ses folder>.json`) from `--summaries`, with `--expected` listing the run ids that must each have a file: app-rig-control runs it with its own Python, `-I`, outside this project (`[task.suggest]`, standard library only), so it keeps no search of experiment folders and imports no `open_rigs`.
- `regenerate.py` is the same in coen-dome-navigation and coen-dome-decision-making but for the package name: it is tiny, and each repo owns its schemas.
- `.bonsai\Setup.ps1` is the same 15 lines in five task repos (coen-dome-navigation, coen-dome-decision-making, coen-behaviour-box, coen-qc-calibrate-valve, coen-qc-preflight-rig): each repo needs its own bootstrap.
- A task file that leaves out a sound's `levelDbfs` plays at lib's default: Pip chose defaults in lib (15 Nov 2026); see coen-dome-navigation's BACKLOG.
- The noise and penalty chains duplicate navigation's: the noise sources differ and lib's lint forces the mapping.

## 2026-11-15 One speaker mixer and sound levels (lib-open-rigs OpenRigs.Audio)

- The mixer, its 3 s boot delay and the speakers (`Speakers`) are lib-open-rigs' `SpeakerMixer`, shared with coen-dome-navigation; Ready, its 180 s `StartupWatchdog` and the cameras' start 2 s after Start are `RigHardware`'s. This repo keeps none of them.
- Every sound's level is the task file's `levelDbfs`, in dBFS RMS of the sound as stored, before equalisation: `audioStimulus.levelDbfs` -47.2, `backgroundNoise.levelDbfs` -60.1, `penalty.sound.levelDbfs` -44.6, the levels played until now (the lab's defaults are -30, -50 and -30). The stimuli's level is set at Go (`LevelAuditoryStimulusSet`), which stops the workflow naming any waveform and speaker that would peak at -1 dBFS or more after equalisation.
- **Changed for the mice:** the background noise starts at Go, faded in over 2 s, where it played from about 5 s after Start (since 15 Nov 2026: from Ready, top of this file). As played it sits 12.9 dB below the stimuli (white noise at 0.05 of the file's -34.1 dBFS RMS against the pulse trains' -47.2, each over the file's whole length), not 3.5 dB above as an earlier estimate said; kept as it was.
- Unit-tested and build-checked only: the mixer's start, and every sound at the same loudness as before, need a rig run (Pip's yes).

## 2026-11-14 Dome-task alignment audit: changed

- **For the mice:** the penalty sound is the lab's (OpenRigs.Audio `PenaltySoundBuffer`, the same sound as before): rig.yml's `penaltySoundChannel` alone, equalised, at the task file's level. Also done (`git log`): `RngSeed` at trial 1's Go, the shared `laserTriggerTime` column, lib's `ZapitTrial.CheckArmed`.

## Dome-task alignment audit (14 Nov 2026): not changed

- `outcomeTime` duplicates `harpTime` by choice, so every moment of a trial is named.
- coen-dome-navigation's `audiovisual` and this task's `audiovisualCoherent` are different tasks' trial types, so their names stay.
- `rngSeed` is both in the task file and an `RngSeed` event: the file's is what was asked (null draws one), the event's what ran.
- The response window, spouts in and stimulus off are not columns: each follows from the task file and the trial's columns.
- A trial cut by Stop gets no row; its Go is in `BonsaiData_Async`.
- Trial 1 starts about 1.2 s after Go here and 2.3 s in coen-dome-navigation: each task's own start-up, neither a bug.
- `history` stays in trial-columns.yml until the old data's refactor.
- Trial 1's consequence (reward, penalty sound, spouts) comes 8 to 20 ms after its outcome; from trial 2 on, 0 to 2 ms. The choice, row and consequence nodes run for the first time at trial 1's outcome and pay .NET's first-run compile then; no single node is slow. Measured on BLUE-OPTO-2 (scripted test mouse, 2 runs each, trial 1 rewarded): as now +10 and +12 ms; the trial log moved to its own thread +8 to +12 ms (dropped: no gain beyond run-to-run spread, and a row sent just before Stop could be lost); every method of the task's C# and the lab's packages compiled at Start +9 and +16 ms (the cost is not there); one earlier outcome through the same nodes +1 and +2 ms. The fix, if needed: this task's outcome logic in one C# operator that runs on every tick, as coen-dome-navigation's `CorridorState` (whose trial 1 is on time), or a hidden warm-up outcome after Go, wired by hand.

## 2026-11-14 Camera video on GREEN-DOME-2 (test mouse, open)

The log is `K:\config\machines\GREEN-DOME-2\switch\iterate-log.md` (round 3).

- **The last video file of a run loses the frames still queued at Stop.** LogVideo (lib-open-rigs OpenRigs.Logging) uses Bonsai.Vision's `VideoWriter` with `Buffered` true: frames wait on Rx's EventLoopScheduler, whose thread is a background thread, and headless Bonsai exits within 1 s of the workflow ending, so the queue and the file's closing are cut off. The last AVI's final RIFF AVIX and LIST movi declare size 0 and the OpenDML total is 0; readers that trust the index see only up to the last 1 GB boundary. Measured: 7 to 1,231 Pupil frames lost per run (1,231 with 1,067 queued at Stop, reproduced on demand under CPU load); files closed by the hourly roll-over are complete; the camera CSVs list the lost frames, so they do not show the loss. Being replaced by the ffmpeg recording (another session, 14 Nov 2026); whatever writes video must finish its queue and close its file before Bonsai exits, with a bounded wait that fails loudly (a test writer whose thread kept Bonsai alive hung at Stop until the agent killed it at 30 s, leaving every file unfinished).
- **Add a check that each video holds every frame its CSV lists** (count the frames in the file, not the index), after every run.
- **The Pupil camera's noise makes its video expensive.** rig.yml `cameras.pupil.gain: 20` (Body and Face 0): frame-to-frame noise 3.7 grey levels, 139 KB a frame (Body 11, Face 6), about 6.4 GB per 15 min, and its writer thread near a full core; when it falls behind, queued 1440x1080 frames hold 1 to 4 GB of memory until written. Proven by a gain-0 copy: 2 KB a frame, noise 0.01, but the image 6.5 times darker. Pip, 14 Nov 2026: camera settings stay as they are until a better lens arrives; re-measure frame size and writer load then.

## 2026-11-14 First runs on hardware (BLUE-OPTO-2, test mouse, unattended)

The log is `K:\config\machines\BLUE-OPTO-2\switch\iterate-log.md`.

- Fixed then, each with a test: the auditory stimuli are the task's own files (`src\LocalFiles\AuditoryStimuli`); `OnNextTick` starts the trial loop on the clock's thread (the Harp clock stopped at trial 1); shared event subjects are BehaviorSubjects (`test_no_trial_graph_closes_a_shared_subject`); a lick that ended a trial could deadlock `HarpDelay` against the clock (about 1 in 30 scripted Correct trials), fixed in lib-open-rigs 3203626 (Core coen-lab.7; this repo has coen-lab.17).
- Measured on the rig after the fixes (localization stage 4, 100 trials): stimulus onset 0.49 ms after its scheduled time (sd 0.28, max 0.98), outcome 1.500 s after onset on every miss, timeout 4.000 s, drift 0.6 ms an hour, no dropped camera frame, Stop to idle 2 s.

### Not changed

- `useZapit: true` with 3 mW stays the localization file's default (set for BLUE-OPTO-2 on 10 Nov 2026); a decision for Pip, since discrimination and navigation default to off.
- One camera frame at the start of a run carries a timestamp 12 ms early (frame 40 of 46896, when the clock settles); every later frame is within 1.5 ms of its trigger.
- **A rare hang at Stop** (1 of 9 sweep runs, after 100 trials; the agent killed Bonsai 30 s later and the kill cut the camera CSVs mid-line). Not caught again in 31 start/stop cycles. A Stop while the cameras open takes 8-9 s instead of 1-3 s; its stacks show one camera's thread in SpinnakerNET's native `ManagedCamera.Dispose` while the finalizer disposes another camera wrapper: Bonsai.Spinnaker 0.9.1's `SpinnakerCapture` leaves the cameras it did not pick to the finalizer. A possible cause, unproved; the HarpDelay deadlock above (the same lock pair, with Stop's dispose as the other thread) is another (fixed since). Re-check on the next rig runs.
- **After a lick ends a trial, the next interval starts 0 to 14 ms late** (mean 3 ms; scripted licks, 46 trials): the timeout and interval are timed from when their HarpDelay receives the outcome, after the lick's thread has done the outcome's other work, not from the outcome's time. `itiStartTime` records the real start and the next stimulus is exact from it (under 1 ms); after a miss (the clock's thread) it is exact. Fix if it matters: time the delay from the outcome's own timestamp.
- Scripted licks (`_diag.bonsai`, not committed; the method is in claude-tools `/lab:bonsai-workflows`' rig-testing notes) gave the predicted outcome on all 46 trials of localization stage 4 and discrimination stages 0 and 3: correct, wrong, miss, a late lick and a lick before the stimulus both ignored, wrong then right (the first lick decides), auto-reward on every discrimination stage 0 miss. Response window exact; choice to outcome 0 ms; outcome to reward event 0 to 12 ms.

### Still untried

- Zapit: the simulated run stopped on the per-trial ZapitCommands closure (fixed since); not re-run, as the rig's presence check asks for an Override while app-zapit runs and an unattended session does not override it.
- Licks, choices, rewards and the wheel need a mouse; the valves need a calibration (a person, the USB balance camera, about 35 ml).

## 2026-11-14 Pip's Zapit line decision

- **Pip, 14 Nov 2026: the ZapitTrigger line (DO3) is a held level, never a board-timed pulse.** It goes high at the trigger and stays high until the workflow clears it `laserDurationSeconds` later, so the masking light on it lasts as long as the laser. lib-open-rigs `LabRig` (954353d) refuses a rig.yml whose `outputPulseEnable` lists the Zapit line (the board would end it after its `PulseDO3` width: 5 ms, or 3000 ms on BLUE-OPTO-2); open-rigs' `rigs\rig-template.yml` has `"DO1, DO2"`. RigHardware's `WithoutPulseLine` already dropped the line at run time. Every dome's rig.yml needs `outputPulseEnable: "DO1, DO2"`.
- **Open: check on the first test run** (BLUE-OPTO-2, `test` mouse): on the scope, or in Zapit's log and the Behavior board log, DO3 rises at the trigger and falls `laserDurationSeconds` later, with the masking light lit for all of it and Zapit started on the rising edge.

## 2026-11-13 Pip's timing decisions

- **Pip, 13 Nov 2026: Zapit adds no time; laserToStimulusSeconds is signed (before/after the stimulus).** `armToTriggerSeconds` is gone. Each trial is drawn at the start of its interval (the outcome, after any timeout) and, with optogenetics, Zapit is sent its StartStimulus then (`Laser`); the stimulus comes at the interval's end whether optogenetics is on or off, and the trigger at the stimulus less `laserToStimulusSeconds`. A Zapit reply not in by the trigger stops the workflow (`RequireZapitArmed`); the trigger is never moved.
- **Pip, 13 Nov 2026: quiet waits per stage** (`lickQuietSeconds`, `wheelQuietSeconds`, `wheelQuietCm`, the same names as coen-dome-navigation's; 0 in every stage, so nothing changes until someone sets them). Go is the first moment at or after the interval's end less the lead (max(`laserToStimulusSeconds`, 0)) with both waits held; the trigger rises at Go and the stimulus comes the lead after it, in every experiment. `quietWaitSeconds` (new column) says how much the waits added.
- **Arming waits for the last laser to end.** Zapit refuses a StartStimulus while a timed stimulus and its ramp-down play (app-zapit `pointer.send_samples`), so a trial's arm waits for the last trial's `laserDurationSeconds` and the longest ramp-down a conditions file may have (0.5 s, lib-open-rigs `ZapitConditions.MaximumRampDownMs`) when they end after its interval starts. Hence `laserDurationSeconds` is now at most `openLoopSeconds + itiMinSeconds - 0.7 s`: 0.8 s in the discrimination file (whose 2.5 s is refused once `useZapit` is ticked) and 2.7 s in localization's (2.5 s kept). **For Pip**: shorten the discrimination laser, or say if a shorter ramp should be read from the conditions file.
- **Trial 1 now has an interval** (`itiSeconds`, `itiStartTime`): from the first draw, once the experiment folder exists, so Zapit can be armed before its trigger. Every trial, a repeat too, draws its own `itiSeconds`, which is what ran.
- **Untried**: build-checked and unit-tested only (`tests\Extensions.Tests` `GoTests`); no run against Zapit or a rig yet.

## 2026-11-13 Audit round 1: what the mice hear changes

- **The penalty sound and the background noise are resampled to 48 kHz** (Pip's default, taken during the audit): the penalty sound's file was 50 kHz and `background_white_noise.wav` 44.1 kHz, and nothing converts a WAV's rate, so on the 48 kHz mixer they played 4% low and 9% high in pitch and speed. Both are now 48 kHz, each file's duration and RMS kept (the penalty sound loses the 4% of its energy above 24 kHz, which 48 kHz cannot carry). This changes what the mice hear against production av-dome and every earlier experiment. Each is checked at its load (`CheckWaveRate`, as the stimulus WAVs are), so a file at another rate stops the workflow.

## 2026-11-13 Audit round 2: Pip's quiet-wait and laser decisions

- The wheel's quiet wait counts the travel in the last `wheelQuietSeconds` (was all travel since the last restart, so slow fidgeting delayed Go); the lick wait needs both spouts released for `lickQuietSeconds` (a held contact holds Go); a trial answered before its trigger (negative `laserToStimulusSeconds`) gets no laser and logs `laserPowerMw` 0, as in coen-dome-navigation (`UnlessAnswered`). Unit-tested and build-checked only.

## 2026-11-13 Audit rounds 3-5: changed

- Done, details in `git log`: Zapit's connection is lib-open-rigs' `Zapit.ConnectZapit`; on a rig without hardware triggering Zapit gets the StartStimulus at the trigger; `UnlessAnswered` and `laserPowerMw` read whether the trigger or the outcome came first; one reward cap, `MAX_REWARD_UL`, checked at Go with `largeRewardMultiplier`; `config\main.layout` tracked.

## Audit round 5 (13 Nov 2026): not changed

- `wheelQuietCm` 0 is refused unless the wheel wait is off: any travel would hold Go on encoder jitter.
- The 20 ul cap is written in three places in lib-open-rigs, tied by tests.
- A calibration `pulseGapMs` of 0 is allowed; the domes fire at the calibrated gap.
- Lab IDs are not checked unique over time; the lab never reuses them.

## Audit round 4 (13 Nov 2026): not changed

- coen-qc-calibrate-valve and the domes pace pulses on different Harp-synced devices, about 1 ms apart.
- No trigger or Go time column; both follow from `itiStartTime`, `itiSeconds`, the lead and `quietWaitSeconds`.
- `outcomeTime` (then `choiceTime`) is the latest Harp tick when the choice is read, not the deciding lick's own timestamp.
- A skipped trial leaves Zapit armed; the next trial disarms it.

## Audit round 3 (13 Nov 2026): not changed

- coen-dome-decision-making: a laser-off control trial and a laser trial skipped because it was answered first look alike in the row (`laserPowerMw` 0); which one followed from `choiceTime` and the lead, and from 14 Nov 2026 `laserTriggerTime` says (a time, or NaN).
- coen-dome-decision-making: discrimination's minimum interval equals its `drinkSeconds`; both are Gerion's measured values.
- jsonschema accepts K:'s old rig.yml keys, but the generated deserializer and app-rig-control's key walk refuse them.
- `usedBy` is filled by app-rig-control's `python -m app_rig_control.names add <clone> --apply` at switch step 8.
- A skipped trial leaves Zapit armed; app-zapit disarms it.
- coen-dome-navigation arms the next trial during the last one's ramp-down; the trigger hold is at least the longest ramp-down.
- coen-dome-decision-making has no `goTime` column; it is `itiStartTime + itiSeconds - lead + quietWaitSeconds`.

## Audit round 2 (13 Nov 2026): not changed

- app-rig-setup's `check-workflow-builds.ps1` fails with "Could not load file or assembly" on a clone whose `.bonsai\Packages` predates its Bonsai.config, not "run .bonsai\Setup.ps1": carried, since app-rig-setup held another session's unpushed commit and a push there deploys.

## Audit round 1 (13 Nov 2026): not changed

- **Discrimination's interval** (measured from 18 of Gerion's sessions): his discrimination ran 1.5 s drink + max(ITI - 2, 0) (ITI uniform 1.5-2.5 s, stage 0 0) = 1.5-2.0 s from the outcome to the next stimulus after a correct choice, and the 4 s timeout + the same = 5.5-6.0 s after an incorrect choice or a miss. Pip, 13 Nov 2026, chose to match it: `itiMinSeconds` 1.5, `itiMaxSeconds` 2.0, `drinkSeconds` 1.5 (the interval includes the drink); stage 0 is 1.5 fixed (both set to 1.5 on the form, as the task file has no per-stage interval).

## 2026-11-13 Rebuilt on the shared dome blocks (plan v11, W2)

`src\main.bonsai` was rebuilt on lib-open-rigs' dome blocks (README, Shared Blocks): RigHardware, HarpLogs, DeliverReward, ProjectorDisplay, Core HarpDelay on `Timestamp` for every task wait, the trial draw, row and consequences in tested C# (`tests\Extensions.Tests`). The sections below it are from the graph before the rebuild, cut to what still holds (the rest is in `git log`); node names they cite may be gone.

- **The laser-off Zapit command** (condition sent with the laser off at 0 mW, as navigation's) and the ZapitTrigger line held for `laserDurationSeconds` as the masking light: untried against Zapit.
- **`config\main.layout` is empty**: `Setup.ps1` replaces any machine-local `Settings\src\main.layout` with it; the real layout is still to be saved on a dome with the Spinnaker SDK.
- **No `Finished` phase.** No run sequence reaches `ExperimentManifest`, so every session ends "stopped" and the overnight copy (jacuzzi-ingest's ingest-rig) never holds one for a missing expected file. Needs a session end (a trial count or duration from the task file) that reports `Finished`.

## 2026-11-10 Independent audits, rounds 1-5 of 5 (the 9-10 Nov naming and laser work)

Decided by Pip (10 Nov 2026):

- **A trial goes on only once Zapit says it armed the trial's condition**, as the corridor's, and any Zapit error reply stops the experiment.
- **Real mice need a hardware-triggered rig, as in the corridor**: an optogenetics experiment with any mouse but `test` stops when rig.yml has `zapit.hardwareTriggered: false` (BLUE-OPTO-1 today).
- **The laser stays timed.** `laserDurationSeconds` runs whatever the mouse does; the corridor's stops at the outcome. Kept as this task's design.

Not changed, each on its merits:

- **"Required with optogenetics on" in the power's description, though the switch is `useZapit`**: plain English for the one switch of the `optogenetics` block; the schema and validator messages name `useZapit`.
- **No ramp-down check as the corridor's `ZapitConditions.CheckRampDowns`**: Zapit plays a timed trial's own ramp-down, and the corridor's limits come from holding its masking light to 1 s after the outcome; a minimum ramp for these trials is an experiment-design choice.
- **The trial draw's state carries over between Start and Stop in the Bonsai editor** (raised of `InitialiseTrial`): rig-control starts a fresh Bonsai for every experiment, and the editor is for development.
- **`triggerLine: 0` allowed by the rig model**: every rig.yml on the share says 3.
- **`laserToStimulusSeconds` in the optogenetics block though it times every trial, Zapit or not**: Pip's word (10 Nov 2026).
- **Random trial types can leave a short experiment without a rare type** (neutral, 1 in 31 at stage 4): Pip chose independent draws.
- **Speaker positions keep `SphericalPosition`'s `azimuth`/`elevation`**: an open-rigs type whose descriptions say degrees.
- **Internal Trial fields keep old names** (`visualAzimuth`, `openLoopDuration`, `Optogenetic*`): only recorded names, task fields and metrics are the lab's.
- **Laser timing names unlike the corridor's**: `laserDurationSeconds` is timed, the corridor's `laserMaxSeconds` a cap.
- **`degreesPerLick`, `thresholdLicks`**: a rate and a count, read correctly as they are.

## 2026-11-08 Independent audit of the discrimination task and auto-reward: not changed

- **Auto-reward as a flag, outcome `NoResponse`**: the same reward-the-target path as Gerion's `AutorewardTarget`; the flag keeps the lab's three outcomes.
- **Distractor answered at the right spout**: every Gerion stage offers both spouts; his "inaccessible spout" comments are a question for him.
- **Coherent trials take a random side**: Gerion's C# draws the side and the stimulus independently too.
- **Contrasts copied literally**: as Gerion has them.
- **Open, Pip's decision**: Target Modality resets to the file's value every launch; a warning at Go when it differs from the mouse's last experiment would need rig-control to read that experiment's task file.

## 2026-11-07 The sync square does not reach the photodiode (GREEN-DOME-1, open)

The photodiode square should show on the Behavior board's DIPort0 (register 32, bit 0) at the stimulus's flicker rate. On GREEN-DOME-1 it never did: all 25 experiments of 4 to 6 Nov 2026 show DI0 pulsing at 120.0 Hz, high at most 1.1 ms, the projector's own frame flashes, the same with or without a stimulus. Whether it ever worked there is unknown. Until it is fixed, `stimulusOnsetTime` is Bonsai's render time, not a measured onset. rig-control's Board tab shows DIPort0 live as "Photodiode".

Needs someone at the rig (Pip's OK, never while `rig-presence.json` reads `acquiring`): a test experiment with stimuli on, the photodiode covered for about 5 s then uncovered, DI0 decoded (pulses that stop while covered: the sensor is live and sees the projector everywhere; pulses that do not: DI0 is not the sensor); see where the square lands against the sensor and the comparator's threshold. Report to Pip before changing any wiring, mesh or workflow.

## 2026-11-04 Workflow audit of `pip/main-switchover` (what still holds)

- **Analysis by task, not by folder** (for Pip): sessions have no project folder; closed-loop and 4 Hz sessions sit beside the others under `sub-<mouse>`, told apart by each session's `TaskSchema`. `analysis\analysis.ipynb` still reads `Z:\AV_Dome`, `Z:\AV_Dome_4Hz_movingStim` and `Z:\AV_Dome_ClosedLoop` (production's layout).
- **Water tile on FixedPulse lines**: settled. Every reward goes through lib-open-rigs' `DeliverReward`, whose `LoadRewardLine` stops the workflow on a reward line that is a FixedPulse or has no valve calibration.
- **No `liveView:` blocks** in the lab's rig template or the domes' rig.yml; the defaults apply.
- **Pause / Resume** stopped working at b77099a and its nodes are gone; a rig-control Pause command would need it rebuilt.
- `IngestScript coen-dome-decision-making` names a jacuzzi-ingest registry entry; the registry does not exist on jacuzzi until the ingest daemon is deployed.
