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
    # ITI - 0.1 - laserToStimulusSeconds - 2.0, so the file's range is his plus 0.6 s at the files'
    # 0.5 s (audit of 8 Nov 2026). The wait it subtracts is the task's own since 10 Nov 2026 (round 2):
    # it was a typed 0.5 after laserToStimulusSeconds became the task's, so any other value changed the ITI.
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert "it.Item1 &gt; 2.1 + it.Item2 ? it.Item1 - 0.1 - it.Item2 - 2.0 : 0.0" in workflow
    iti = workflow.index("it.Item1 &gt; 2.1 + it.Item2")
    assert "TaskParameters.Optogenetics.LaserToStimulusSeconds" in workflow[iti : iti + 3000]
    task = _task("task-discrimination.yml")
    trial = task.trial_structure
    lead = 0.1 + task.optogenetics.laser_to_stimulus_seconds
    assert (trial.iti_min_seconds, trial.iti_max_seconds) == pytest.approx((1.5 + lead, 2.5 + lead))


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # renamed zapitConditionsPath on 9 Nov 2026, the file stopped being kept (audit, 10 Nov 2026).
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConfigPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # Pip, 10 Nov 2026: av-dome sends and logs laserPowerMw as coen-dome-navigation does, required when on.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "useZapit": True, "zapitConfigPath": "C:\\zapit\\conditions.yml"}
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": on})
    with pytest.raises(ValidationError):
        UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 0}})
    UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 2.0}})
    # The same rule in the committed schema, which is what rig-control checks before Go.
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8"))
    rule = schema["$defs"]["OptogeneticsSettings"]
    assert rule["if"]["properties"]["useZapit"] == {"const": True} and rule["then"]["required"] == ["laserPowerMw"]


def test_zapit_needs_its_file_in_the_model_and_the_schema():
    # Audit, 10 Nov 2026, round 2: an empty zapitConfigPath with Zapit ticked passed rig-control's schema check and
    # stopped the experiment at its first trial; the form says to set it first, and the corridor's schema refuses it.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "useZapit": True, "laserPowerMw": 2.0, "zapitConfigPath": ""}
    with pytest.raises(ValidationError, match="zapit_config_path is empty"):
        UclOpenAvDomeTaskParameters.model_validate({**data, "optogenetics": on})
    jsonschema = pytest.importorskip("jsonschema")
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8"))
    task = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())
    task["taskParameters"]["optogenetics"] = on
    assert not jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "zapitConfigPath": "C:\\zapit\\conditions.yml"}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "useZapit": False}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)


def test_the_trial_row_logs_the_power_sent():
    source = (CONFIG.parent / "src" / "Extensions" / "TrialRow.cs").read_text(encoding="utf-8-sig")
    assert "laserPowerMw = trial.OptogeneticPower," in source
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert '<Property Name="LaserPower" Selector="OptogeneticPower" />' in workflow


def test_the_laser_delay_is_gone():
    # Pip, 10 Nov 2026: laserDelaySeconds (stimDelay) was 0 in every task file since 24 Sep 2026; dropped.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8")
    assert "laserDelaySeconds" not in schema and "stimDelay" not in schema
    assert "OptogeneticDelay" not in (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_laser_to_stimulus_is_the_task_s_and_sets_the_wait(path):
    # Pip, 10 Nov 2026: the fixed 0.5 s wait between the laser's trigger and ExecuteTrial is the task's
    # laserToStimulusSeconds (0.5 keeps the timing), on every trial.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert opto["laserToStimulusSeconds"] == 0.5
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert '<Property Name="DueSeconds" Selector="TaskParameters.Optogenetics.LaserToStimulusSeconds" />' in workflow


def test_each_laser_on_trial_draws_its_condition_at_random():
    # Pip, 10 Nov 2026: balancedConditions (always true) dropped; a shuffled queue made a site unlikely to follow
    # itself and the end of each cycle predictable, which confounds trial-history analyses.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8")
    assert "balancedConditions" not in schema and "pseudorandomSelection" not in schema
    source = (CONFIG.parent / "src" / "Extensions" / "InitialiseTrial.cs").read_text(encoding="utf-8-sig")
    assert "_conditionQueue" not in source and "zapitConditions[rand.Next(zapitConditions.Count)]" in source


def test_the_spout_wait_is_named_by_its_ends():
    # Pip's rule (10 Nov 2026): a gap between two moments is named by its ends, as laserToStimulusSeconds.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_av_dome_task.json").read_text(encoding="utf-8")
    assert '"stimulusToSpoutsInSeconds"' in schema and '"spoutInDelaySeconds"' not in schema
    for path in TASK_FILES:
        assert yaml.safe_load(path.read_text())["taskParameters"]["trialStructure"]["stimulusToSpoutsInSeconds"] == 0.5


def test_each_trial_type_is_drawn_at_random_by_weight():
    # Pip, 10 Nov 2026: trial types were dealt from shuffled blocks of the weights, which made a type unlikely to follow
    # itself and each block's end predictable; now each new trial draws its type independently, weighted by trialWeights.
    source = (CONFIG.parent / "src" / "Extensions" / "InitialiseTrial.cs").read_text(encoding="utf-8-sig")
    assert "shuffledTrialSequence" not in source and "int draw = random.Next(total);" in source


def test_target_modality_is_visual_or_auditory():
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    UclOpenAvDomeTaskParameters.model_validate({**data, "targetModality": "auditory"})
    with pytest.raises(ValidationError):
        UclOpenAvDomeTaskParameters.model_validate({**data, "targetModality": "smell"})

def test_reward_size_says_a_calibrated_valve_plans_its_pulses():
    # Audit, 10 Nov 2026, round 3: it said every reward is ceil(size/2) pulses, true only of an uncalibrated valve (RewardLine.cs).
    from ucl_open_av_dome.task import TrainingStage
    description = TrainingStage.model_fields["reward_size_ul"].description
    assert "calibration" in description and "uncalibrated" in description
