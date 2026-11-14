# Backlog

## 2026-11-14 Pip's Zapit line decision

- **Pip, 14 Nov 2026: the ZapitTrigger line (DO3) is a held level, never a board-timed pulse.** It goes high at the trigger and stays high until the workflow clears it `laserDurationSeconds` later, so the masking light on it lasts as long as the laser. lib-open-rigs `DomeRig` (954353d) refuses a rig.yml whose `outputPulseEnable` lists the Zapit line (the board would end it after its `PulseDO3` width: 5 ms, or 3000 ms on BLUE-OPTO-2); `config\rig-template.yml` has `"DO1, DO2"`. DomeHardware's `WithoutPulseLine` already dropped the line at run time. Every dome's rig.yml needs `outputPulseEnable: "DO1, DO2"`.
- **Open: check on the first test run** (BLUE-OPTO-2, `test` mouse): on the scope, or in Zapit's log and the Behavior board log, DO3 rises at the trigger and falls `laserDurationSeconds` later, with the masking light lit for all of it and Zapit started on the rising edge.

## 2026-11-13 Pip's timing decisions

- **Pip, 13 Nov 2026: Zapit adds no time; laserToStimulusSeconds is signed (before/after the stimulus).** `armToTriggerSeconds` is gone. Each trial is drawn at the start of its interval (the outcome, after any timeout) and, with optogenetics, Zapit is sent its StartStimulus then (`Laser`); the stimulus comes at the interval's end whether optogenetics is on or off, and the trigger at the stimulus less `laserToStimulusSeconds`. A Zapit reply not in by the trigger stops the workflow (`RequireZapitArmed`); the trigger is never moved.
- **Pip, 13 Nov 2026: quiet waits per stage** (`lickQuietSeconds`, `wheelQuietSeconds`, `wheelQuietCm`, the same names as coen-dome-navigation's; 0 in every stage, so nothing changes until someone sets them). Go is the first moment at or after the interval's end less the lead (max(`laserToStimulusSeconds`, 0)) with both waits held; the trigger rises at Go and the stimulus comes the lead after it, in every experiment. `quietWaitSeconds` (new column) says how much the waits added.
- **Arming waits for the last laser to end.** Zapit refuses a StartStimulus while a timed stimulus and its ramp-down play (app-zapit `pointer.send_samples`), so a trial's arm waits for the last trial's `laserDurationSeconds` and the longest ramp-down a conditions file may have (0.5 s, lib-open-rigs `ZapitConditions.MaximumRampDownMs`) when they end after its interval starts. Hence `laserDurationSeconds` is now at most `openLoopSeconds + itiMinSeconds - 0.7 s`: 0.8 s in the discrimination file (whose 2.5 s is refused once `useZapit` is ticked) and 2.7 s in localization's (2.5 s kept). **For Pip**: shorten the discrimination laser, or say if a shorter ramp should be read from the conditions file.
- **Trial 1 now has an interval** (`itiSeconds`, `itiStartTime`): from the first draw, once the experiment folder exists, so Zapit can be armed before its trigger. Every trial, a repeat too, draws its own `itiSeconds`, which is what ran.
- **Untried**: build-checked and unit-tested only (`tests\Extensions.Tests` `GoTests`); no run against Zapit or a rig yet.

## 2026-11-13 Audit round 1: what the mice hear changes

- **The error tone and the background noise are resampled to 48 kHz** (Pip's default, taken during the audit): `src\LocalFiles\error_tone.wav` was 50 kHz and `background_white_noise.wav` 44.1 kHz, and nothing converts a WAV's rate, so on the 48 kHz mixer they played 4% low and 9% high in pitch and speed. Both are now 48 kHz, each file's duration and RMS kept (the error tone loses the 4% of its energy above 24 kHz, which 48 kHz cannot carry). This changes what the mice hear against production av-dome and every earlier experiment. Each is checked at its load (`CheckWaveRate`, as the stimulus WAVs are), so a file at another rate stops the workflow.

## 2026-11-13 Audit round 2: Pip's quiet-wait and laser decisions

- The wheel's quiet wait counts the travel in the last `wheelQuietSeconds` (was all travel since the last restart, so slow fidgeting delayed Go); the lick wait needs both spouts released for `lickQuietSeconds` (a held contact holds Go); a trial answered before its trigger (negative `laserToStimulusSeconds`) gets no laser and logs `laserPowerMw` 0, as in coen-dome-navigation (`UnlessAnswered`). Unit-tested and build-checked only.

## 2026-11-13 Audit round 3: changed

- Zapit's connection, its start checks and the refusal of a real mouse on a rig without hardware triggering are lib-open-rigs' `Zapit.ConnectZapit` (Devices coen-lab.8), shared with coen-dome-navigation; the 0.2 s reply margin is its `ZapitTrial.ReplyMarginSeconds` and `ZAPIT_REPLY_MARGIN_SECONDS`.
- On a rig without hardware triggering (test mouse only) Zapit gets the StartStimulus at the trigger, not at the interval's start: Zapit starts the laser on receipt, so it came 3.4 to 4.4 s early.
- A trial records whether its trigger or its outcome came first, and both `UnlessAnswered` and the row's `laserPowerMw` read that record, so a row no longer logs 0 mW when the laser fired.
- Every rig.yml speaker needs a filter (`DomeSpeakers.RequireFilters`, as coen-dome-navigation); the manual reward's subject is `ManualRewardUl`. Unit-tested and build-checked only.

## 2026-11-13 Audit round 5: changed

- The check at Go (`TrialDraw.Check`) refuses a stage whose `rewardSizeUl` times `largeRewardMultiplier` passes lib-open-rigs' cap (`LoadRewardLine.DefaultMaxRewardUl`, 20 ul, tied there to `MAX_REWARD_UL`) while large rewards are on, as task.py does: app-rig-control checks task files against the JSON schema only, so such a file stopped mid-session when `DeliverReward` refused the reward (test).
- `config\main.layout` is tracked, an empty layout for now, as coen-dome-navigation's, so `Setup.ps1` replaces a stale per-machine one (test); save the real one on a dome with the Spinnaker SDK.

## Audit round 5 (13 Nov 2026): not changed

- `wheelQuietCm` 0 is refused unless the wheel wait is off: any travel would hold Go on encoder jitter.
- The 20 ul cap is written in three places in lib-open-rigs, tied by tests.
- A calibration `pulseGapMs` of 0 is allowed; the domes fire at the calibrated gap.
- Lab IDs are not checked unique over time; the lab never reuses them.

## 2026-11-13 Audit round 4: changed

- One cap on a reward, lib-open-rigs' `MAX_REWARD_UL` (20 ul, tied there to `LoadRewardLine.DefaultMaxRewardUl` and `DeliverReward`'s `MaxRewardUl`): each stage's `rewardSizeUl` is at most it, the task refuses a stage whose `rewardSizeUl` times `largeRewardMultiplier` passes it while large rewards are on, and `ManualRewardUl`'s `Maximum` is it (tests).
- The longest Zapit ramp-down is lib-open-rigs' `ZAPIT_MAX_RAMP_DOWN_SECONDS` (tied there to `ZapitConditions.MaximumRampDownMs`); `RampDown`'s 0.5 s is tested against it.

## Audit round 4 (13 Nov 2026): not changed

- coen-qc-calibrate-valve and the domes pace pulses on different Harp-synced devices, about 1 ms apart.
- `ReadTextFile.cs` is duplicated in both dome workflows; moving it to a lab package costs a package bump.
- No trigger or Go time column; both follow from `itiStartTime`, `itiSeconds`, the lead and `quietWaitSeconds`.
- `choiceTime` is the latest Harp tick when the choice is read, not the deciding lick's own timestamp.
- A skipped trial leaves Zapit armed; the next trial disarms it.

## Audit round 3 (13 Nov 2026): not changed

- coen-dome-decision-making: a laser-off control trial and a laser trial skipped because it was answered first look alike in the row (`laserPowerMw` 0); which one follows from `choiceTime` and the lead.
- coen-dome-decision-making: discrimination's minimum interval equals its `drinkSeconds`; both are Gerion's measured values.
- jsonschema accepts K:'s old rig.yml keys, but the generated deserializer and app-rig-control's key walk refuse them.
- `usedBy` stays incomplete until `add-names.ps1` runs at switch step 8.
- A skipped trial leaves Zapit armed; app-zapit disarms it.
- coen-dome-navigation arms the next trial during the last one's ramp-down; the trigger hold is at least the longest ramp-down.
- coen-dome-decision-making has no `goTime` column; it is `itiStartTime + itiSeconds - lead + quietWaitSeconds`.

## Audit round 2 (13 Nov 2026): not changed

- `QuietWait` is the same class here and in coen-dome-navigation's `Corridor.cs`: moving it into the fork needs a package bump pushed into every in-use workflow, and each copy is tested where it runs. Move it if a third workflow needs it, and change both copies together until then.
- app-rig-setup's `check-workflow-builds.ps1` fails with "Could not load file or assembly" on a clone whose `.bonsai\Packages` predates its Bonsai.config, not "run .bonsai\Setup.ps1": carried, since app-rig-setup held another session's unpushed commit and a push there deploys.

## Audit round 1 (13 Nov 2026): not changed

- `TrialNumber`, `TrialOutcome` and `StimulusOnsetTime` are published and read by nothing here: the plan's shared names, kept for any reader (the Declarations description says so).
- **Discrimination's interval** (measured from 18 of Gerion's sessions): his discrimination ran 1.5 s drink + max(ITI - 2, 0) (ITI uniform 1.5-2.5 s, stage 0 0) = 1.5-2.0 s from the outcome to the next stimulus after a correct choice, and the 4 s timeout + the same = 5.5-6.0 s after an incorrect choice or a miss. Pip, 13 Nov 2026, chose to match it: `itiMinSeconds` 1.5, `itiMaxSeconds` 2.0, `drinkSeconds` 1.5 (the interval includes the drink); stage 0 is 1.5 fixed (both set to 1.5 on the form, as the task file has no per-stage interval).

## 2026-11-13 Rebuilt on the shared dome blocks (plan v11, W2)

`src\main.bonsai` was rebuilt on lib-open-rigs' dome blocks (README, Shared Blocks): DomeHardware, DomeHarpLogs, DeliverReward, DomeDisplay, Core HarpDelay on `Timestamp` for every task wait, the trial draw, row and consequences in tested C# (`tests\Extensions.Tests`). Entries below this one describe the graph before the rebuild; node names and files they cite may be gone.

### Not yet tried

- **Nothing has run.** Checked by `check-workflow.ps1` (C# 5 compile, Extensions.Tests, Bonsai's build against the filled template, the task files) and the Python tests only; no headless run (DomeDisplay opens a full-screen window) and no rig. First: a `test` run on one dome (uncalibrated line delivers 0 and says so; ITI on Harp time within 2 ms of the drawn value; trial rows against what was shown), then Zapit on BLUE-OPTO-2.
- **The laser-off Zapit command** (condition sent with the laser off at 0 mW, as navigation's) and the ZapitTrigger line held for `laserDurationSeconds` as the masking light: untried against Zapit.
- **`config\main.layout` is empty** (audit round 5): `Setup.ps1` replaces any machine-local `Settings\src\main.layout` with it; the real layout is still to be saved on a dome.
- **No `Finished` phase**, as before.

## 2026-11-10 Independent audit, round 5 of 5 (the 9-10 Nov naming and laser work)

Fixed: `src\Extensions\README.md`'s Zapit wiring and timing model described the graph before the open-rigs branch: StartStimulus sent "in StimuliPresentation" "at stimulus onset", the trigger written "when Zapit's reply confirms it is armed", the laser lagging the stimulus, and power "not sent". Traced by node index: `ArmZapit`, HarpDelay 0.1 s, `StartLaser` (no wait for the reply), HarpDelay `laserToStimulusSeconds`, `ExecuteTrial`; the laser leads the stimulus and `laserPowerMw` goes with each StartStimulus. Rewritten. `itiMinSeconds`, `itiMaxSeconds` and the `itiSeconds` column (its meaning goes into every manifest) now say the draw runs from the outcome (after any timeout) to the next stimulus and includes 2 s to drink, 0.1 s to arm and `laserToStimulusSeconds`, a shorter draw running as their sum, as rig-control's form already said (test).

### Decided by Pip after round 5 (10 Nov 2026)

- **ArmZapit waits for Zapit to arm the trial, as the corridor's.** A laser-on trial goes on only once Zapit's StartStimulus reply says it armed the trial's condition with the laser on (`CheckZapitArmed`; `ZapitClient` fails after 5 s without an answer), and any Zapit error reply stops the experiment (`ConnectZapit`'s `ZapitError?`). Laser-on trials therefore start Zapit's round trip (tens of ms) later after their ITI than laser-off trials, which send nothing. Checked: build check, `check-workflow.ps1` step 4 (the operator), `tests\test_workflow.py` (the wiring); untried against a running Zapit.
- **Real mice need a hardware-triggered rig, as in the corridor.** `ConnectZapit` stops an optogenetics experiment with any mouse but `test` when rig.yml has `zapit.hardwareTriggered: false` (BLUE-OPTO-1 today).
- **The laser stays timed.** `laserDurationSeconds` (2.5 s from the trigger) runs whatever the mouse does; the corridor's stops at the outcome. Kept as this task's design.

## 2026-11-10 Independent audit, round 4 of 5 (the 9-10 Nov naming and laser work)

Fixed: `laserToStimulusSeconds` said the stimulus comes "a further 0.1-0.25 s" after the wait and that it is not exact as the corridor's. ExecuteTrial has no quiescent wait: StimuliPresentation goes from the trial straight to `StartAuditoryStimulus`, where `stimulusOnsetTime` is stamped, with no delay node between (test), so the stimulus is commanded within a few ms of the wait's end; 0.1-0.25 s was the unused quiescent draw. Description, both task files' comments, ExecuteTrial's description ("the quiescent wait") and the Opto lead item corrected. `quiescentMin/MaxSeconds` and `rewardProbability` now say they are not used (no node reads `Quiescent`, `RewardProbability` or `IsRewarded`; Correct? rewards every correct response); wiring or dropping them stays Tom's (Design questions). The Never-read item no longer says `autoReward` changes nothing. `trialsBeforeLaser` counts as `trialNumber` does, not "by TrialCount".

Not recommended, each kept on its merits:

- **"Required with optogenetics on" in the power's description, though the switch is `useZapit`**: plain English for the one switch of the `optogenetics` block; the schema and validator messages name `useZapit`.

## 2026-11-10 Independent audit, round 3 of 5 (the 9-10 Nov naming and laser work)

Fixed: `trainingStages.rewardSizeUl` said every reward is ceil(size/2) valve pulses, true only of an uncalibrated valve; `RewardLine` plans a calibrated valve's pulses from its calibration (test). rig-control's ITI Min and Max help now says 2.1 s plus `laserToStimulusSeconds` comes out of the drawn ITI. The lab command's test-run hint named a `Zapit` parameter and a `task.yml` that do not exist: a shell launch takes `task-discrimination.yml`, Zapit off.

Not recommended, each kept on its merits:

- **`task-localization.yml` has Zapit on, with BLUE-OPTO-2's file**: on a rig without Zapit the window refuses Go loudly (Zapit Not Answering) until it is unticked, and the default fits the opto rig the file was set for.
- **No ramp-down check as the corridor's `ZapitConditions.CheckRampDowns`**: Zapit plays a timed trial's own ramp-down, and the corridor's limits come from holding its masking light to 1 s after the outcome; a minimum ramp for these trials is an experiment-design choice.
- **`TrainingStage` declared 0 to 5 while both task files have rows 0 to 4**: a row past the end runs the last, as its description says, and the form offers 0 to 4.

## 2026-11-10 Independent audit, round 2 of 5 (the 9-10 Nov naming and laser work)

Fixed: nothing checked that Zapit had the task's Zapit file loaded. The workflow asked Zapit `GetNumConditions` at startup and ignored the answer, and it took condition numbers from the file's keys, while Zapit numbers conditions by position, so a different file or one numbered with a gap stimulated the wrong sites with no error. Now `CheckZapitConditionCount` (as coen-dome-navigation's) stops the experiment at startup when Zapit's count differs from the file's or Zapit answers with an error, and `InitialiseTrial.ReadZapitConditions` refuses a file not numbered exactly 1 to N (`check-workflow.ps1` step 4; untried against a running Zapit). The ITI subtracted a typed 0.5 s for the wait before ExecuteTrial after that wait became the task's `laserToStimulusSeconds`, so any other value changed the ITI: it subtracts the task's value now (test). The schema and model refuse `useZapit` with an empty `zapitConfigPath`, as the corridor's do and as the form said (test, with jsonschema). rig-control's view help says both.

### For Pip

- Done after round 5 (Pip, 10 Nov 2026; above), with the next item. **ArmZapit does not wait for Zapit to be armed.** It sends StartStimulus and StartLaser raises the trigger 0.1 s later whatever Zapit has answered; the corridor waits for Zapit's reply confirming the condition and laser state before its trial starts. A slow arm would leave a laser-on trial dark while its row gives `laserPowerMw > 0`. Needs a run against Zapit on BLUE-OPTO-2 to measure the arm time before changing the trial's timing.
- **Zapit error replies are logged, not acted on.** A refused StartStimulus is in `BonsaiData_ZapitReplies` but the trial runs and logs its power; the corridor stops the experiment on any error reply. Same decision as above, as it changes what a run does when Zapit misbehaves.

Not recommended, each kept on its merits:

- **`InitialiseTrial`'s state (random stream, trial count, repeats) carries over between Start and Stop in the Bonsai editor**: rig-control starts a fresh Bonsai for every experiment, and the editor is for development.
- **`triggerLine: 0` allowed by the rig model**: every rig.yml on the share says 3.

## 2026-11-10 Independent audit, round 1 of 5 (the 9-10 Nov naming and laser work)

Fixed: a Zapit file that is missing, unreadable or lists no condition stops the experiment at its first trial. It was a console warning, and every trial ran with the laser off (reproduced: 300 trials, 0 laser-on); the list was also static, so a second experiment in one Bonsai process kept the first's conditions for 5 minutes. `check-workflow.ps1` step 4 now draws trials with Zapit on. Descriptions: `useZapit` (laser-off trials send Zapit nothing here), `laserToStimulusSeconds` (from the trigger, with or without Zapit; without hardware triggering the laser starts 0.1 s earlier), ArmZapit (no delay). `spoutInDelaySeconds` is `stimulusToSpoutsInSeconds` (Pip's rule: a gap is named by its ends). rig-control's view shows Zapit Conditions and Laser Power, as the corridor's does.

### For Pip

- Done after round 5 (Pip, 10 Nov 2026: refused for any mouse but `test`; above). **Optogenetics on a rig without hardware triggering.** BLUE-OPTO-1's rig.yml has `zapit.hardwareTriggered: false`, so Zapit starts the laser when ArmZapit's command arrives, about 0.1 s before StartLaser's trigger plus Zapit's latency, with no onset in the Harp log. coen-dome-navigation refuses this for any mouse but `test`; this branch runs it. Refuse it here too, or keep it for BLUE-OPTO-1's mice.

Not recommended, each kept on its merits:

- **`laserToStimulusSeconds` in the optogenetics block though it times every trial, Zapit or not**: Pip's word (10 Nov 2026); it keeps production's 0.5 s, and its description now says so.
- **Random trial types can leave a short experiment without a rare type** (neutral, 1 in 31 at stage 4): Pip chose independent draws.
- **The disabled `HardwareTriggeredOptoTrial?` in StartLaser**: Bonsai passes values through a disabled node (probed), so every trial pulses the line, as its description says.
- **Two `add-names.ps1` copies and the Zapit C# in both repos**: one shared script or a lab package is a platform decision for Pip.

## 2026-11-10 Independent audit of the 9 Nov naming pass

Fixed: `zapitConfigPath` again (renamed `zapitConditionsPath` on 9 Nov, rig-control's `keep_files`, which copies fields ending ConfigPath, stopped keeping the Zapit file at Go); `laserPowerMw` sent with each laser-on StartStimulus and logged (Pip: required with optogenetics on; task-localization.yml 3.0, the laserPowerInMW of BLUE-OPTO-2's avdome_main_initialstim.yml; zapit-test.yml 2.0, left_right_pair.yml's; later that night Pip took Tom Childs' Zapit test harness off the branch: ZapitDemo.bonsai, ZapitTrialTest.bonsai, config\zapit-test.yml and docs\zapit-testing.md, still on main), so laser on is `laserPowerMw > 0` here as in coen-dome-navigation; `scripts\add-names.ps1` (was a one-off); `visualContrast`'s description (stages draw 3.0). rig-control's names check now reads only the workflows `main.bonsai` includes: the unused `src\Extensions\StallDiagnostics.bonsai` had failed every dome since its three logs left the lab list on 9 Nov. Not recommended, each kept on its merits:

- **Speaker positions keep `SphericalPosition`'s `azimuth`/`elevation`**: an open-rigs type whose descriptions say degrees.
- **Internal Trial fields keep old names** (`visualAzimuth`, `openLoopDuration`, `Optogenetic*`): only recorded names, task fields and metrics are the lab's.
- **Laser timing names unlike the corridor's**: `laserDurationSeconds` is timed, the corridor's `laserMaxSeconds` a cap. (`laserDelaySeconds`, 0 in every task file since it was added as `stimDelay` on 24 Sep 2026, was dropped the same night, Pip.)
- **`degreesPerLick`, `thresholdLicks`**: a rate and a count, read correctly as they are.
- **`test_the_committed_schema_is_the_model_s` failing**: open-rigs' upstream description of `WiredLine.device`, unrelated; regenerate when the open-rigs pin moves. (Regenerated 11 Nov 2026, and update-workflows now does it: Lab standard, below.)
- **Legacy variants** (`schemas\session.yml`, `main_ClosedLoop.bonsai`, `main_NabbefeldG.bonsai`, `TrialTypeInitialise.cs`): untouched on purpose.

## 2026-11-08 Independent audit of the discrimination task, auto-reward and leak fix

Fixed: the discrimination ITI (his 1.5-2.5 s plus the 0.6 s more this workflow subtracts), the auto-reward description (a rewarded miss still counts as a timeout for repeats); rig-control's view labels and help (rig-control, same day). Not recommended, each kept on its merits:

- **Auto-reward as a flag, outcome `NoResponse`**: the same reward-the-target path as Gerion's `AutorewardTarget`; the flag keeps the lab's three outcomes.
- **Distractor answered at the right spout**: every Gerion stage offers both spouts; his "inaccessible spout" comments are a question for him.
- **Coherent trials take a random side**: Gerion's C# draws the side and the stimulus independently too.
- **Contrasts copied literally; PresentTargetSpoutOnly not gated by `spoutMovements`**: both as Gerion has them.
- **`StartAuditoryStimulus` starting with a false value**: `AudioEnabled?` would filter it.
- **`targetModality` "visual" on localization rows**: moot since 9 Nov 2026, when `taskType` and `targetModality` left the trial log (task-file copies, in the manifest).
- **Open, Pip's decision**: Target Modality resets to the file's value every launch; a warning at Go when it differs from the mouse's last experiment would need rig-control to read that experiment's task file.

## 2026-11-08 Independent audit of 7 Nov work

- **A calibrated reward valve on DO3 is never snapshotted.** `rewardValves` may give a side DO3 when the rig has no `zapit` on that line, but the Logging group snapshots only `ValveCalibrationDO1` and `ValveCalibrationDO2`. No rig uses DO3 for a valve today; add `ValveCalibrationDO3` if one does.

## 2026-11-07 The sync square does not reach the photodiode (GREEN-DOME-1)

`PhotodiodeIndicator` (a 30° quad at azimuth 130°, elevation -22.5°, flipped every 63 ms during a stimulus) should show on the board's DIPort0 (register 32 `DigitalInputState`, bit 0) as 63 ms on, 63 ms off. On GREEN-DOME-1 it never does. All 25 experiments on the rig (4 to 6 Nov 2026, this branch and `port-pr30`, 1 s to 7 min) show DI0 pulsing at 120.0 Hz, high at most 1.1 ms, never high for more than 20 ms and never low for 50 to 80 ms: the projector's own frame flashes, the same whether or not a stimulus is on. Not a camera (they run at 50 Hz on DO0/DO1). AnalogInput0/1 are noise (0 to 12 counts): nothing plugged in. No production experiment from before the 27 Oct 2026 `MeshMap.csv` change is on the rig or on staging, so whether it ever worked here is unknown. Until it is fixed, `stimulusOnsetTime` is Bonsai's render time, not a measured onset.

What the code says (7 Nov 2026):
- The square is drawn **black** (`DrawQuad` colour 0, 0, 0) on even ticks only, over the window's `ClearColor Gray`, so it alternates black and the grey background, not black and white. Its period is `1 / (2 * visualFlickerFrequency)` from the task (the 63 ms is the node's default). Black is no light, so a sensor under it should still go quiet for half of each cycle, which DI0 never does.
- Production's `Flush_Valves.bonsai` (Gerion, 17 Oct 2026) reads DIPort0 as the photodiode (averaged over 200 ms, high if any light) and draws test squares at azimuth -130° and +130°, elevation -22.5°. main draws one, at +130°. Which side the sensor is on is not written anywhere.
- rig-control's Board tab (rig-control 15bc837 and later, OpenRigs.LiveView 0.1.1-coen-lab.6) shows DIPort0 live as "Photodiode": a working square shows as on/off blocks at the flicker rate.

### Needs someone at the rig (Pip's OK, never while `rig-presence.json` reads `acquiring`)

- A test experiment from rig-control with stimuli on. Cover the photodiode for about 5 s, then uncover it, then decode DI0. If the pulses stop while it is covered, the sensor is live and sees the projector everywhere. If they don't stop, DI0 is not the sensor.
- See where the square lands on the dome compared with the sensor (the current `C:\RigConfigs\MeshMap.csv` against `MeshMap_old.csv`), and what threshold the photodiode's comparator is set to.
- Report to Pip before changing any wiring, mesh or workflow.

## 2026-11-04 Workflow audit of `pip/main-switchover`

The certain findings were fixed in 0078311, 97358e6, 1234f38 and d565649. NoResponse trials running the Incorrect branch as well was fixed on `main` by #35 and merged here. What is left:

### Needs a real session

- **Session record against the view.** Check a staged session's files against `ExpectedFiles`, `BonsaiData_Trial`'s header against `config\rig-control.toml` `[trials]`, and that `BonsaiData_Async` has a `Go` row.
- **The trial row restructure (5 Nov 2026) on a dome.** One drawer (`DrawTrial`), the row built at the choice (`BuildTrialRow` into `TrialRow`), per-trial `stimulusOnsetTime` and `speakerIndex`. Build-checked only. Through rig-control: Go, about 20 trials, Stop during a drink or ITI; each row's `trialNumber`, `target`, `trialType` and `audioAzimuthDeg` must match that trial (Status metrics, the speaker heard), `stimulusOnsetTime` must sit about 0.5 s before the spouts move in (`StepperDriver` logs), `choiceTime` on the counted lick (`LicketySplit` register 32), and the last answered trial must be in the file.

### Design questions for Tom

- **Zapit TTL on every trial.** `StartLaser`'s `HardwareTriggeredOptoTrial?` condition is wrapped in `Disable`, so every trial sends `Zapit.TriggerLine` into `TriggerPulse` (5 ms on DO3). Was the disable a test leftover? The fix is removing the `Disable` wrapper. Also: `StartLaser` pulses a fixed 0.1 s after `ArmZapit` without waiting for Zapit's reply on `ZapitReplies`, so a slow arm misses its pulse and the next trial's pulse fires it; and a disabled `CreateStartStimulus` to `ZapitCommands` path sits inside `StartLaser`.
- **Opto lead.** ArmZapit, HarpDelay 0.1 s, StartLaser, HarpDelay 0.5 s, ExecuteTrial: the laser leads the stimulus by about 0.6 s (the task's delay field, always 0, was dropped 10 Nov 2026). Since 10 Nov 2026 the 0.5 s wait before ExecuteTrial is the task's `optogenetics.laserToStimulusSeconds` (Pip), on every trial; the 0.1 s after arming is still typed in. `balancedConditions` (always true) dropped the same night: each laser-on trial's condition is drawn at random, no queue (Pip: a queue made sequences predictable); the trial-type draw too: each new trial's type is drawn at random by the stage's trialWeights, no shuffled blocks (Pip). The stimulus is commanded as the wait ends (`stimulusOnsetTime`, within a few ms: ExecuteTrial has no quiescent wait; the 0.1-0.25 s once written here was the unused quiescent draw, audit round 4).
- **Visual stimulus ends on a 1.5 s timer** (`gl:Timer` `PT1.5S`), not on the response; the response-driven `StopStimuli` multicast is disabled (as on `main`). A `closedLoopSeconds` under 1.5 s rewards while the grating is still on.
- **Durations typed into the graph** rather than the task file: the 1.5 s stimulus timer, the 2 s drink delay and the ITI arithmetic (`ITI - 0.1 - laserToStimulusSeconds - 2.0`, #35, clamped at 0 since 5 Nov 2026, so an ITI under 2.6 s runs as 2.6 s at the task files' 0.5) in `ApplyConsequences`, and the 0.1 s and 0.5 s opto delays. Move them to `task.py` or document them there.
- **Never-read task fields.** `rewardProbability`/`IsRewarded`, `Quiescent`, and `audioElevation` (playback snaps to the nearest wired speaker, elevation fixed at 15) are drawn but change nothing (`autoReward` does act: a miss on an auto-reward trial is rewarded at the target spout, ApplyConsequences' `AutoReward?`, and logged as `autoRewarded`); since 10 Nov 2026 (audit round 4) the task file's descriptions of `quiescentMin/MaxSeconds` and `rewardProbability` say they are not used; since 5 Nov 2026 none of them is a `BonsaiData_Trial` column (the row has the speaker that played). Wire them or drop them from `src\ucl_open_coen_dome_decision_making\task.py` and regenerate (`uv run python src\ucl_open_coen_dome_decision_making\regenerate.py`). #35 also notes the ITI pieces in `ApplyConsequences` "need to be applied elsewhere".
- **The Zapit config is not recorded.** `laserCondition` is a number into the Zapit config at task-localization.yml `zapitConfigPath` (a path on the rig), and the experiment keeps only that path (TaskSchema). `ExperimentManifest`'s `ConfigFiles` is read at Start, before Go chooses the task file, so it cannot hash it; copy the file into the experiment at Go (a `LogDataSchema` of its contents, named in `K:\config\data-dictionary.json`'s `streams`) or teach the manifest a file named at Go.

### Decisions for Pip

- **Analysis by task, not by folder.** Sessions no longer have a project folder (no `datasetName`): closed-loop and 4 Hz sessions sit beside the others under `sub-<mouse>`, told apart by each session's `TaskSchema`. `analysis\analysis.ipynb` still reads `Z:\AV_Dome`, `Z:\AV_Dome_4Hz_movingStim` and `Z:\AV_Dome_ClosedLoop` (production's layout).
- **Water tile on FixedPulse lines** is nominal: ceil(µl / 2) pulses of 2 µl (`src\Extensions\RewardLine.cs`). Add a line to rig-control's water help, or calibrate the dome valves from rig-control's Calibrations page (ucl-open/coen-qc-calibrate-valve).

### Lab standard (coen-behaviour-box as the reference)

- **No `config\main.layout` yet.** rig-control launches with `--start` when Headless is unticked, and without a layout the editor can stall. The plumbing is in, as in coen-behaviour-box: `.gitignore` tracks `config/main.layout`, `.bonsai\Setup.ps1` copies it into `Settings\src\` (keeping a different local one as `main.saved.layout`), rig-setup reruns Setup whenever `config\*.layout` changes, and `scripts\check-workflow.ps1` fails a window on a node it does not fit. Left: open the branch in the editor on a dome (it needs the Spinnaker SDK), arrange the windows wanted with Headless unticked, save, copy `.bonsai\Settings\src\main.layout` to `config\main.layout`, add its visualizer types to `$wanted` in the check, commit. Until then, any machine-local `Settings\src\main.layout` on a dome predates the `TrialLoop` regroup and must be deleted.
- **Schemas regenerated by update-workflows** (fixed 11 Nov 2026): `pyproject.toml` has the `regenerate-schemas` script, so lib-open-rigs' run regenerates `src\DataSchemas` and the Sgen C# whenever it moves the open-rigs pin. `examples\rig.py` (it wrote `local\UclOpenCoenDomeDecisionMakingRig.json` and threw) is deleted, as nothing used it, so the run writes no rig template: `config\rig-template.yml` stays hand-written with its comments, as coen-dome-navigation's, and the build check holds it to the schema. Regenerated the same night: stale in descriptions only (6c24d19's spout motor numbers, open-rigs' `WiredLine.device`); `tests\test_rig_schema.py` passes again.
- **No `Finished` phase.** No run sequence reaches `ExperimentManifest`, so every session ends "stopped" and rig-sync never sees one as complete. Needs a session end (a trial count or duration from the task file) that ends `TrialLoop` and reports `Finished`.
- Done (5 Nov 2026, Pip): the stepper drivers are two rig.yml entries, `stepperDriver: a:` and `b:` (`StepperDriver-A`, `StepperDriver-B`), in place of `lickSpoutStage: main:`, so `RecordHarpDevices` checks and records each board (WhoAmI 1130) and the manifest expects their files. They are still bare `Harp.StepperDriver` devices: their `.Data` and `.Commands` are bridged to `StepperDriver1Events`/`StepperDriver1Commands` (and 2), which `Extensions\MoveStepperMotors` and `ZEROingMotors` and the RigConfigs.cs workflows (Flush_Valves, ManualValveCalibration, MoveIndividualSpouts, main_ClosedLoop, main_NabbefeldG) use; renaming those is for when those workflows move to rig.yml.
- **No `liveView:` blocks** in `config\rig-template.yml` or the domes' rig.yml; the defaults apply.
- **Pause / Resume** existed from 6b7a25e (a Start / Pause toggle ending `TakeWhile` on `StartSession`, `Repeat` re-arming) and stopped working at b77099a, when the toggle became a plain button; the dormant nodes were removed with the `TrialLoop` regroup. A rig-control Pause command would need it rebuilt.

### Waiting on others

- `IngestScript coen-dome-decision-making` names a jacuzzi-ingest registry entry; the registry does not exist on jacuzzi until the ingest daemon is deployed.
