from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError

from ucl_open_av_dome.task import AudioStimulus, UclOpenAvDomeTaskParameters, VisualStimulus

CONFIG = Path(__file__).parent.parent / "config"
TASK_FILES = sorted(CONFIG.glob("task*.yml"))


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_validate(path):
    data = yaml.safe_load(path.read_text())
    UclOpenAvDomeTaskParameters.model_validate(data["taskParameters"])


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_use_the_discrete_azimuths(path):
    data = yaml.safe_load(path.read_text())
    assert data["taskParameters"]["visualStimulus"]["fixedAzimuthsDeg"] == [-60, 60]


def test_overwrite_azimuths_default_keeps_the_uniform_range():
    assert VisualStimulus().fixed_azimuths_deg is None


def test_overwrite_azimuths_empty_list_keeps_the_uniform_range():
    assert VisualStimulus(fixedAzimuthsDeg=[]).fixed_azimuths_deg == []


def test_overwrite_azimuths_set_alongside_min_max():
    visual = VisualStimulus(azimuthMinDeg=-30, azimuthMaxDeg=30, fixedAzimuthsDeg=[-60, 60])
    assert visual.fixed_azimuths_deg == [-60, 60]


@pytest.mark.parametrize("values", [[60], [-60], [0], [-60, 0]])
def test_overwrite_azimuths_needs_both_sides(values):
    with pytest.raises(ValidationError, match="fixed_azimuths_deg needs a value"):
        VisualStimulus(fixedAzimuthsDeg=values)


def test_overwrite_azimuths_respects_the_coherent_cap():
    with pytest.raises(ValidationError, match="right-side"):
        VisualStimulus(fixedAzimuthsDeg=[-60, 100], coherentAzimuthCapDeg=80)


def test_overwrite_azimuths_rejects_out_of_range():
    with pytest.raises(ValidationError, match="outside"):
        VisualStimulus(fixedAzimuthsDeg=[-60, 60, 200])


def test_speaker_locations_reject_a_centre_entry():
    with pytest.raises(ValidationError, match="azimuth-0"):
        AudioStimulus(
            speakerLocations=[
                {"azimuth": -60, "elevation": 15},
                {"azimuth": 0, "elevation": 15},
                {"azimuth": 60, "elevation": 15},
            ]
        )


def _task(name):
    data = yaml.safe_load((CONFIG / name).read_text())
    return UclOpenAvDomeTaskParameters.model_validate(data["taskParameters"])


def test_a_task_file_without_task_type_is_localization_with_moving_spouts():
    task = _task("task-localization.yml")
    assert task.task_type == "localization"
    assert all(stage.spout_movements for stage in task.training_stages)


def test_the_discrimination_file_keeps_the_spouts_in_for_habituation():
    task = _task("task-discrimination.yml")
    assert task.task_type == "discrimination"
    assert task.target_modality == "visual"
    assert not task.training_stages[0].spout_movements
    assert all(stage.spout_movements for stage in task.training_stages[1:])


def test_the_discrimination_iti_adds_what_gerion_s_did():
    # Gerion's ITI node waits max(ITI - 2.0, 0) after his 1.5-2.5 s draw; this workflow's waits
    # ITI - 2.6 above 2.6 s, so the file's range is his plus 0.6 s (audit of 8 Nov 2026).
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert "it &gt; 2.6 ? it - 0.1 - 0.5 - 2.0 : 0.0" in workflow
    trial = _task("task-discrimination.yml").trial_structure
    assert (trial.iti_min_seconds, trial.iti_max_seconds) == pytest.approx((1.5 + 0.6, 2.5 + 0.6))


@pytest.mark.parametrize("path", TASK_FILES + [CONFIG / "zapit-test.yml"], ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # renamed zapitConditionsPath on 9 Nov 2026, the file stopped being kept (audit, 10 Nov 2026).
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConfigPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # Pip, 10 Nov 2026: av-dome sends and logs laserPowerMw as coen-dome-navigation does, required when on.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "enabled": True}
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": on})
    with pytest.raises(ValidationError):
        UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 0}})
    UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 2.0}})
    # The same rule in the committed schema, which is what rig-control checks before Go.
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8"))
    rule = schema["$defs"]["OptogeneticsSettings"]
    assert rule["if"]["properties"]["enabled"] == {"const": True} and rule["then"]["required"] == ["laserPowerMw"]


def test_the_trial_row_logs_the_power_sent():
    source = (CONFIG.parent / "src" / "Extensions" / "TrialRow.cs").read_text(encoding="utf-8-sig")
    assert "laserPowerMw = trial.OptogeneticPower," in source
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert '<Property Name="LaserPower" Selector="OptogeneticPower" />' in workflow


def test_target_modality_is_visual_or_auditory():
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    UclOpenAvDomeTaskParameters.model_validate({**data, "targetModality": "auditory"})
    with pytest.raises(ValidationError):
        UclOpenAvDomeTaskParameters.model_validate({**data, "targetModality": "smell"})
