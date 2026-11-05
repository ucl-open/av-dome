# av-dome

The audiovisual dome task, as an open-rigs project: `src\main.bonsai` in the Bonsai environment under `.bonsai\`, C# operators in `src\Extensions\`, the task files in `config\`, and the rig's hardware in `C:\Coen-Lab-Local\RigConfigs\<MACHINE-NAME>\rig.yml` (start from `config\rig-template.yml`).

## Running an experiment

The workflow has no window of its own. It runs under rig-control (Coen-Lab/rig-control): the rig agent launches it with `--property:SubjectId=<mouse> --property:SessionId=<n>`, and it starts into ready mode, with the boards connected, the spouts zeroed and at that mouse's saved positions, the cameras streaming to the live view, and nothing recorded. From the Coen Rig Control window, or `rig-control` on the command line, set the parameters, send commands, then Go.

| Parameter | Default | |
|-----------|---------|-|
| `TrainingStage` | 4 | Row of the task file's `trainingStages`, 0 to 4 (a row past the end runs the last) |
| `TaskFile` | `task.yml` | The task file in `config\`, read at Go |
| `RewardSizeUl`, `ItiMin`, `ItiMax` | blank | Override the task file's reward size and inter-trial interval; blank keeps the file's |
| `Zapit` | `off` | `on` or `off`; blank keeps the file's `optogenetics.enabled` |
| `StepSize` | 150 | Motor steps per spout nudge, taken live |

Commands, while ready or recording: `RewardLeft` and `RewardRight` (a flush), `SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions` (to `<rig config>\calibration\LickSpoutPositions\<SubjectId>.csv`), and the nudges `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`.

Go reads the task file, draws the first trial and starts the trial loop; the experiment folder is `<rootPath>\sub-<SubjectId>\ses-<SessionId>_<DateTime>` (rootPath is the rig's acquisition folder itself, no project folder), with a folder per device, `RigSchema`, `TaskSchema` and `BonsaiData`, and `manifest.json` for the overnight copy. `BonsaiData` holds the lab's two experiment files: `BonsaiData_Trial`, one row per trial, written when the choice is made, with its settings, outcome (`outcome`), side licked (`choice`), the speaker that played and two Harp times, `stimulusOnsetTime` and `choiceTime` (columns named and typed as lib-open-rigs' `data_dictionary.yml` lists them), and `BonsaiData_Async`, one row per event outside a trial (every rig-control command, Set and Go, and every valve delivery as `RewardDelivered` with the microlitres delivered), before Go as well as after. Status shows the current trial's facts (`target`, `trialType`, azimuths, `rewardSizeUl`); rig-control computes trials, performance, water and licks from the files and the live view. An experiment ends with Stop from the controller.

## Checking a change

`scripts\check-workflow.ps1` compiles the Extensions under C# 5 and builds every workflow that reads rig.yml with Bonsai's own builders, against the template filled in as a rig's, without a window or a device. `uv run --frozen --with pytest --with pyyaml python -m pytest -q` runs the schema tests.
