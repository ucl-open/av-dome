# Backlog

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
- **The trial row restructure (5 Nov 2026) on a dome.** One drawer (`DrawTrial`), the row built at the choice (`BuildTrialRow` into `TrialRow`), per-trial `stimulusOnsetTime` and `speakerIndex`. Build-checked only. Through rig-control: Go, about 20 trials, Stop during a drink or ITI; each row's `trialNumber`, `target`, `trialType` and `audioAzimuth` must match that trial (Status metrics, the speaker heard), `stimulusOnsetTime` must sit about 0.5 s before the spouts move in (`StepperDriver` logs), `choiceTime` on the counted lick (`LicketySplit` register 32), and the last answered trial must be in the file.

### Design questions for Tom

- **Zapit TTL on every trial.** `StartLaser`'s `HardwareTriggeredOptoTrial?` condition is wrapped in `Disable`, so every trial sends `Zapit.TriggerLine` into `TriggerPulse` (5 ms on DO3). Was the disable a test leftover? The fix is removing the `Disable` wrapper. Also: `StartLaser` pulses a fixed 0.1 s after `ArmZapit` without waiting for Zapit's reply on `ZapitReplies`, so a slow arm misses its pulse and the next trial's pulse fires it; and a disabled `CreateStartStimulus` to `ZapitCommands` path sits inside `StartLaser`.
- **Opto lead.** ArmZapit, HarpDelay 0.1 s, StartLaser, HarpDelay 0.5 s, ExecuteTrial: the laser leads the stimulus by about 0.6 s while `OptogeneticDelay` is logged as 0. Intended? Both delays are typed into `TrialLoop`, not read from the task file.
- **Visual stimulus ends on a 1.5 s timer** (`gl:Timer` `PT1.5S`), not on the response; the response-driven `StopStimuli` multicast is disabled (as on `main`). With `config\task-closedloop.yml` (`closedLoopDuration` 10 s) the grating is gone 8.5 s before the response window ends, and a `closedLoopDuration` under 1.5 s rewards while the grating is still on.
- **Durations typed into the graph** rather than the task file: the 1.5 s stimulus timer, the 2 s drink delay and the ITI arithmetic (`ITI - 0.1 - 0.5 - 2.0`, #35, clamped at 0 since 5 Nov 2026, so an ITI under 2.6 s runs as 2.6 s) in `ApplyConsequences`, and the 0.1 s and 0.5 s opto delays. Move them to `task.py` or document them there.
- **Never-read task fields.** `rewardProbability`/`IsRewarded`, `Quiescent`, `audioElevation` (playback snaps to the nearest wired speaker, elevation fixed at 15) and, since #35, `autoReward`/`autoRewardProbability` are drawn but change nothing; since 5 Nov 2026 none of them is a `BonsaiData_Trial` column (the row has the speaker that played). Wire them or drop them from `src\ucl_open_av_dome\task.py` and regenerate (`uv run python src\ucl_open_av_dome\regenerate.py`). #35 also notes the ITI pieces in `ApplyConsequences` "need to be applied elsewhere".
- **The Zapit config is not recorded.** `optoCondition` is a number into the Zapit config at task.yml `zapitConfigPath` (a path on the rig), and the experiment keeps only that path (TaskSchema). `ExperimentManifest`'s `ConfigFiles` is read at Start, before Go chooses the task file, so it cannot hash it; copy the file into the experiment at Go (a `LogDataSchema` of its contents, named in `K:\config\data-dictionary.json`'s `streams`) or teach the manifest a file named at Go.

### Decisions for Pip

- **Analysis by task, not by folder.** Sessions no longer have a project folder (no `datasetName`): closed-loop and 4 Hz sessions sit beside the others under `sub-<mouse>`, told apart by each session's `TaskSchema`. `analysis\analysis.ipynb` still reads `Z:\AV_Dome`, `Z:\AV_Dome_4Hz_movingStim` and `Z:\AV_Dome_ClosedLoop` (production's layout).
- **Water tile on FixedPulse lines** is nominal: ceil(µl / 2) pulses of 2 µl (`src\Extensions\RewardLine.cs`). Add a line to rig-control's water help, or calibrate the dome valves from rig-control's Calibrations page (ucl-open/coen-calibrate-valve).

### Lab standard (coen-behaviour-box as the reference)

- **No `config\main.layout` yet.** rig-control launches with `--start` when Headless is unticked, and without a layout the editor can stall. The plumbing is in, as in coen-behaviour-box: `.gitignore` tracks `config/main.layout`, `.bonsai\Setup.ps1` copies it into `Settings\src\` (keeping a different local one as `main.saved.layout`), rig-setup reruns Setup whenever `config\*.layout` changes, and `scripts\check-workflow.ps1` fails a window on a node it does not fit. Left: open the branch in the editor on a dome (it needs the Spinnaker SDK), arrange the windows wanted with Headless unticked, save, copy `.bonsai\Settings\src\main.layout` to `config\main.layout`, add its visualizer types to `$wanted` in the check, commit. Until then, any machine-local `Settings\src\main.layout` on a dome predates the `TrialLoop` regroup and must be deleted.
- **Schemas are not regenerated by update-workflows.** `pyproject.toml` has no `regenerate-schemas` script and `examples\rig.py` writes `local\UclOpenAvDomeRig.json` and throws, so lib-open-rigs' hourly run moves the open-rigs pin but leaves `src\DataSchemas` as it was (regenerated by hand so far). Fix `rig.py` into a template writer (or delete it) and add the script line together, or every hourly run fails.
- **No `Finished` phase.** No run sequence reaches `ExperimentManifest`, so every session ends "stopped" and rig-sync never sees one as complete. Needs a session end (a trial count or duration from the task file) that ends `TrialLoop` and reports `Finished`.
- Done (5 Nov 2026, Pip): the stepper drivers are two rig.yml entries, `stepperDriver: a:` and `b:` (`StepperDriver-A`, `StepperDriver-B`), in place of `lickSpoutStage: main:`, so `RecordHarpDevices` checks and records each board (WhoAmI 1130) and the manifest expects their files. They are still bare `Harp.StepperDriver` devices: their `.Data` and `.Commands` are bridged to `StepperDriver1Events`/`StepperDriver1Commands` (and 2), which `Extensions\MoveStepperMotors` and `ZEROingMotors` and the RigConfigs.cs workflows (Flush_Valves, ManualValveCalibration, MoveIndividualSpouts, main_ClosedLoop, main_NabbefeldG) use; renaming those is for when those workflows move to rig.yml.
- **No `liveView:` blocks** in `config\rig-template.yml` or the domes' rig.yml; the defaults apply.
- **Pause / Resume** existed from 6b7a25e (a Start / Pause toggle ending `TakeWhile` on `StartSession`, `Repeat` re-arming) and stopped working at b77099a, when the toggle became a plain button; the dormant nodes were removed with the `TrialLoop` regroup. A rig-control Pause command would need it rebuilt.

### Waiting on others

- `IngestScript av-dome` names a jacuzzi-ingest registry entry; the registry does not exist on jacuzzi until the ingest daemon is deployed.
