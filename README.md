# av-dome

The audiovisual dome task, as an open-rigs project: `src\main.bonsai` in the Bonsai environment under `.bonsai\`, C# operators in `src\Extensions\`, the task files in `config\`, and the rig's hardware in `C:\Coen-Lab-Local\RigConfigs\<MACHINE-NAME>\rig.yml` (start from `config\rig-template.yml`).

## Running an experiment

The workflow has no window of its own. It runs under rig-control (Coen-Lab/rig-control): the rig agent launches it with `--property:SubjectId=<mouse> --property:SessionId=<n>`, and it starts into ready mode, with the boards connected, the spouts zeroed and at that mouse's saved positions, the cameras streaming to the live view, and nothing recorded. From the Coen Rig Control window, or `rig-control` on the command line, set the parameters, send commands, then Go.

| Parameter | Default | |
|-----------|---------|-|
| `TrainingStage` | 4 | Row of the task file's `trainingStages`, 0 to 4 (a row past the end runs the last) |
| `ManualRewardUl` | 3 | Microlitres `RewardLeft` and `RewardRight` deliver before Go, taken live; after Go a manual reward is the current trial's size |
| `StepSize` | 150 | Motor steps per spout nudge, taken live |

The task's own settings (reward sizes, inter-trial intervals, optogenetics and the rest) are the task file's. rig-control writes it (lib-open-rigs settings-files plan): the file chosen at launch from `config\task*.yml`, with the fields edited on the Experiment page and any null `rngSeed` drawn, checked against `src\DataSchemas\ucl_open_av_dome_task.json`, at the path it passes as `TaskPath`, which the workflow reads at Go. From the editor `TaskPath` is `config\task.yml` as it is.

Commands, while ready or recording: `RewardLeft` and `RewardRight` (a flush), `SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions` (to `<rig config>\calibration\LickSpoutPositions\<SubjectId>.csv`), and the nudges `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`.

Go reads the task file, draws the first trial and starts the trial loop; the experiment folder is `<rootPath>\sub-<SubjectId>\ses-<SessionId>_<DateTime>` (rootPath is the rig's acquisition folder itself, no project folder), with a folder per device, `Settings` (the three settings files: `Settings_Rig_<DateTime>.json`, rig.yml as read; `Settings_Task_<DateTime>.json`, the task as read at Go; `Settings_Experiment_<DateTime>.json`, the subject, session, workflow, commit, who launched and sent Go, and every ExperimentControl parameter at Go; from `4c95bae` until `72d7d39`, both 6 Nov 2026, they were `Schemas\Schemas_Rig_`, `Schemas_Task_` and `Schemas_Experiment_`, and before that `RigSchema\` and `TaskSchema\`), `BonsaiData`, and `manifest.json` for the overnight copy. `BonsaiData` holds the lab's two experiment files: `BonsaiData_Trial`, one row per trial, written when the choice is made, with its settings, outcome (`outcome`), side licked (`choice`), the speaker that played and two Harp times, `stimulusOnsetTime` and `choiceTime` (columns named and typed as the lab's list on K: allows, their meaning in `schemas\trial-columns.yml`, copied into each experiment's `manifest.json`), and `BonsaiData_Async`, one row per event outside a trial (every rig-control command, Set and Go, and every valve delivery as `RewardDelivered` with the microlitres delivered), before Go as well as after. Status shows the current trial's facts (`target`, `trialType`, azimuths, `rewardSizeUl`); rig-control computes trials, performance, water and licks from the files and the live view. An experiment ends with Stop from the controller.

## Checking a change

`scripts\check-workflow.ps1` compiles the Extensions under C# 5 and builds every workflow that reads rig.yml with Bonsai's own builders, against the template filled in as a rig's, without a window or a device. `uv run --frozen --with pytest --with pyyaml python -m pytest -q` runs the schema tests.
