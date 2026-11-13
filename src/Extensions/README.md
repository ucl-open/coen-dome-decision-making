# Local Bonsai extensions

Bonsai compiles every `*.cs` file here when `src\main.bonsai` loads, as C# 5 (`src\Extensions.csproj` sets `LangVersion` 5): no `$"..."`, `?.`, `nameof`, expression-bodied members or property initialisers. `tests\Extensions.Tests` (MSTest, net472, C# 5) tests them; `scripts\check-workflow.ps1` compiles and runs both. Everything shared between dome workflows lives in lib-open-rigs (see the README's Shared Blocks); only this task's logic is here.

| File | What it holds |
|---|---|
| `TrialLogic.cs` | `Trial`; `DrawTrial` and its tested core `TrialDraw` (trial types by the stage's weights, repeats by its rules, stimuli, rig.yml's nearest speaker, the Zapit condition, the interval before and after the trial; refuses a stage past the table, empty contrasts or a side with no azimuth, location or speaker); `StartTrials`, `EndTrial` (the loop); `Consequence`/`TrialConsequence` (reward, spouts, error sound, timeout from the trial's row); `SpoutsIn`; `ZapitTrialCommand` (the trial's fields into lib-open-rigs' `ZapitTrial.Command`), `CheckZapitArmed` |
| `TrialRow.cs` | `TrialRow`, one `BonsaiData_Trial` row, its fields the columns of `schemas\trial-columns.yml` in order (a test holds them equal), and `CreateTrialRow` |
| `AuditoryStimulusSet.cs` | The stimulus set, each waveform equalised for each speaker by its filter; refuses a speaker with no filter, a filter from another machine or rate, and any missing or malformed file, naming it |
| `Broadcast.cs`, `ChannelActivation.cs` | A waveform onto the mixer's channels |
| `ReadTextFile.cs` | Reads the task file named at Go |
| `*.Generated.cs` | Bonsai.Sgen 0.9.0 classes for rig.yml (`DomeRig`) and the task file; regenerate with `uv run python -m ucl_open_coen_dome_decision_making.regenerate`, never edit |
