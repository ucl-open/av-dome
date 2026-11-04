# Backlog

## 2026-11-04 Workflow audit of `pip/main-switchover`

The certain findings were fixed in 0078311, 97358e6, 1234f38 and d565649. NoResponse trials running the Incorrect branch as well was fixed on `main` by #35 and merged here. What is left:

### Needs a real session

- **`StimulusStartEvents/` in `ExpectedFiles`.** A session stopped before trial 1's stimulus may leave no `StimulusStartEvents\` folder, so rig-sync would alarm. If a session confirms it, drop it from `ExpectedFiles` (`SessionManifest` in `src\main.bonsai`) and from `scripts\check-workflow.ps1`; the onsets are also in `BonsaiData_Trial`'s `stimulusOnsetSeconds`.
- **Session record against the view.** Check a staged session's files against `ExpectedFiles`, `BonsaiData_Trial`'s header against `config\rig-control.toml` `[trials]`, and that `BonsaiData_Async` has a `Go` row.

### Design questions for Tom

- **Zapit TTL on every trial.** `StartLaser`'s `HardwareTriggeredOptoTrial?` condition is wrapped in `Disable`, so every trial sends `Zapit.TriggerLine` into `TriggerPulse` (5 ms on DO3). Was the disable a test leftover? The fix is removing the `Disable` wrapper.
- **Opto lead.** ArmZapit, HarpDelay 0.1 s, StartLaser, HarpDelay 0.5 s, ExecuteTrial: the laser leads the stimulus by about 0.6 s while `OptogeneticDelay` is logged as 0. Intended?
- **Visual stimulus ends on a 1.5 s timer** (`gl:Timer` `PT1.5S`), not on the response; the response-driven `StopStimuli` multicast is disabled (as on `main`). With `config\task-closedloop.yml` (`closedLoopDuration` 10 s) the grating is gone 8.5 s before the response window ends.
- **Never-read task fields.** `rewardProbability`/`IsRewarded`, `Quiescent` and, since #35, `autoReward`/`autoRewardProbability` are drawn and logged but change nothing. Wire them or drop them from `src\ucl_open_av_dome\task.py` and regenerate (`uv run python src\ucl_open_av_dome\regenerate.py`). If auto-reward goes for good, drop `auto` and `IncorrectAutoRewarded` from `config\rig-control.toml`. #35 also notes the ITI pieces in `ApplyConsequences` "need to be applied elsewhere".

### Decisions for Pip

- **rootPath.** GREEN-DOME-1's rig.yml has `rootPath: C:\Coen-Lab-Local\Acquired-Data` and the workflow appends the task file's `datasetName`; rig-setup expects `rootPath` to be the project folder itself. Either set `rootPath` to `...\Acquired-Data\AV_Dome` and stop appending `datasetName` (README, template comment and `rig-control.toml` `dataset` change together), or accept rig-setup's flag.
- **Water tile on FixedPulse lines** is nominal: ceil(µl / 2) pulses of 2 µl (`src\Extensions\RewardLine.cs`). Add a line to rig-control's water help, or calibrate the dome valves with `src\CalibrateValve.bonsai`.

### Waiting on others

- `IngestScript av-dome` names a jacuzzi-ingest registry entry; the registry does not exist on jacuzzi until the ingest daemon is deployed.
