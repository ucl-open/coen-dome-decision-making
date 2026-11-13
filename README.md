# coen-dome-decision-making

The audiovisual dome decision-making tasks (localisation and discrimination), as an open-rigs project on the lab's shared dome blocks (lib-open-rigs, [Dome Building Blocks](https://github.com/Coen-Lab/lib-open-rigs/blob/main/docs/articles/dome-building-blocks.md)). It was `ucl-open/av-dome`'s `pip/main-switchover` branch until 13 Nov 2026, moved here with its history (Pip) so production av-dome is never touched. A mouse's experiments recorded as av-dome before the move count as this workflow's: anything that reads a mouse's history (a stage suggestion, for one) reads both.

The workflow is `src\main.bonsai` in the Bonsai environment under `.bonsai\`, the task's C# in `src\Extensions\` (tested in `tests\Extensions.Tests`), the task files in `config\`, and the rig's hardware in `C:\Coen-Lab-Local\RigConfigs\<MACHINE-NAME>\rig.yml`, the dome rig model `open_rigs.rigs.dome.DomeRig` (start from `config\rig-template.yml`).

## Running an experiment

The workflow has no window of its own. app-rig-control launches it with `--property:SubjectId=<lab ID> --property:SessionId=<n>` (and `MissingCalibration=DeliverNothing` for the `test` mouse only), and it starts into ready mode: the boards connected, the spouts homed and at that mouse's saved positions, the cameras streaming to the live view, nothing recorded. Set the parameters, send commands, then Go.

| Parameter | Default | |
|-----------|---------|-|
| `TrainingStage` | 4 | Row of the task file's `trainingStages`, 0 to 4; a row past the end of the file's table stops the workflow at Go |
| `ManualRewardUl` | 3 | Microlitres `RewardLeft` and `RewardRight` deliver, taken live; after Go a manual reward is the current trial's size |
| `SpoutStepSize` | 150 | Motor steps per spout nudge, taken live |

The task's own settings are the task file's: app-rig-control writes the file chosen at launch from `config\task*.yml`, with the Experiment page's edits, checked against `src\DataSchemas\ucl_open_coen_dome_decision_making_task.json`, at the path it passes as `TaskPath`, read at Go. From the editor `TaskPath` is `config\task-localization.yml`. `trialWeights` and `repeats.rulesPerType` name each trial type by what it shows (`visual`, `auditory`, `audiovisualCoherent`, `audiovisualConflict`, `neutral`). In discrimination the task files' rows are written for a visual-target mouse (visual trials are the target trials): for an auditory-target mouse, set the stage's `visual` and `auditory` weights on the form accordingly.

Commands, while ready or recording: `RewardLeft` and `RewardRight`, and the stage's own (`HarpLickSpoutStage`): `SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions` (to `<rig config>\calibration\LickSpoutPositions\<SubjectId>.csv`) and the nudges `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`; a nudge moves the spouts at once and is saved for that mouse.

## A trial

Every wait is on the Harp clock (Core `HarpDelay` on `Timestamp`); the trial's arithmetic is tested C# (`TrialLogic.cs`). Go reads the task file; once the experiment folder exists the first trial is drawn (`DrawTrial`: type by the stage's weights, repeats by its rules, the speaker that plays is rig.yml's nearest to the drawn location, and with optogenetics a Zapit condition on every trial, the laser off at 0 mW on laser-off trials). Then: arm Zapit and wait for its reply; `armToTriggerSeconds`; set the ZapitTrigger line (Zapit's trigger and the masking light, cleared `laserDurationSeconds` later); `laserToStimulusSeconds`; the stimulus (`stimulusOnsetTime`), the grating off after `stimulusSeconds`, the spouts in after `stimulusToSpoutsInSeconds`, the response window after `openLoopSeconds` for `closedLoopSeconds`. At the outcome the trial's row is written and the outcome's consequence runs (reward at the target, spouts, error sound); then the timeout, and the inter-trial interval from its end (`itiStartTime`): `drinkSeconds`, the spouts out, the rest of the drawn `itiSeconds`.

The experiment folder is `<rootPath>\sub-<SubjectId>\ses-<SessionId>_<DateTime>`, with a folder per device, `Settings` (`Settings_Rig_`, `Settings_Task_`, `Settings_Experiment_`), each valve's calibration (`ValveCalibrationDO<n>`), `BonsaiData` and `manifest.json`. `BonsaiData_Trial` has one row per trial, written when the outcome is known (columns and meanings in `schemas\trial-columns.yml`); `BonsaiData_Async` one row per event outside a trial (commands, Go, each reward as `RewardDelivered` with the microlitres delivered). Status reports `trialNumber`, `trainingStage`, `target`, `trialType`, the azimuths, `rewardSizeUl` and `laserCondition`; app-rig-control computes trials, performance and water from the files.

## Shared Blocks

Each lib-open-rigs block the workflow uses, its job here and where it lives. `tests\test_workflow.py` fails when the workflow uses one this table does not list.

| Block | Job in this task | File |
|---|---|---|
| `DomeHardware` | Every dome device from rig.yml, the three cameras, the lick spout stage, `Timestamp`, `HarpClockSet`, `ZapitTriggerLine`, the lick states | `OpenRigs.Devices:DomeHardware.bonsai` |
| `DomeHarpLogs` | Each Harp device's log, once the experiment folder exists and the clock is set | `OpenRigs.Devices:DomeHarpLogs.bonsai` |
| `BehaviorBoard.DeliverReward` | Every reward (side, microlitres) on the valve's calibration, fired on the Harp clock, logged as `RewardDelivered` | `OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai` |
| `ToLickSpoutCommand` | The spouts out after drinking | `OpenRigs.Devices` |
| `DomeDisplay` | The projector window, mesh mapping and gamma correction | `OpenRigs.Vision:DomeDisplay.bonsai` |
| `ZapitClient`, `CreateZapitQuery`, `CheckZapitConditionCount`, `ZapitConditionsSnapshot` | Zapit's TCP link, the start-up check of its conditions file, and the file's copy in the experiment (`Settings_ZapitConditions`) | `OpenRigs.Devices` (`OpenRigs.Devices.Zapit`) |
| `HarpDelay` | Every task wait, on `Timestamp` | `OpenRigs.Core` |
| `StartupWatchdog` | Names whichever of the spouts' homing and the Harp clock has not come before Ready | `OpenRigs.Core` |
| `ThrowError` | Stops the workflow on Zapit's refusals | `OpenRigs.Core` |
| `ReadRigConfig`, `ResolveRigConfigDirectory` | Reads this machine's rig.yml | `OpenRigs.Core:ReadRigConfig.bonsai` |
| `ExperimentControl` | Parameters, commands, Go and phases for app-rig-control | `OpenRigs.Control:ExperimentControl.bonsai` |
| `LogController`, `LogAsync`, `LogTrial`, `LogVideo`, `LogData`, `LogDataSchema` | The experiment folder and its files | `OpenRigs.Logging` |

## Checking a change

`scripts\check-workflow.ps1` compiles the Extensions under C# 5, runs `tests\Extensions.Tests`, builds `src\main.bonsai` with Bonsai's own builders against the template filled in as a rig's (no window, no device), and reads each task file as at Go. `uv run --frozen --with pytest --with pyyaml --with jsonschema python -m pytest -q` runs the model and workflow tests.
