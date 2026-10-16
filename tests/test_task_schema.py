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
    assert data["taskParameters"]["visualStimulus"]["overwriteAzimuths"] == [-60, 60]


def test_overwrite_azimuths_default_keeps_the_uniform_range():
    assert VisualStimulus().overwrite_azimuths is None


def test_overwrite_azimuths_empty_list_keeps_the_uniform_range():
    assert VisualStimulus(overwriteAzimuths=[]).overwrite_azimuths == []


def test_overwrite_azimuths_set_alongside_min_max():
    visual = VisualStimulus(azimuthMin=-30, azimuthMax=30, overwriteAzimuths=[-60, 60])
    assert visual.overwrite_azimuths == [-60, 60]


@pytest.mark.parametrize("values", [[60], [-60], [0], [-60, 0]])
def test_overwrite_azimuths_needs_both_sides(values):
    with pytest.raises(ValidationError, match="overwrite_azimuths needs a value"):
        VisualStimulus(overwriteAzimuths=values)


def test_overwrite_azimuths_respects_the_coherent_cap():
    with pytest.raises(ValidationError, match="right-side"):
        VisualStimulus(overwriteAzimuths=[-60, 100], coherentAzimuthCap=80)


def test_overwrite_azimuths_rejects_out_of_range():
    with pytest.raises(ValidationError, match="outside"):
        VisualStimulus(overwriteAzimuths=[-60, 60, 200])


def test_speaker_locations_reject_a_centre_entry():
    with pytest.raises(ValidationError, match="azimuth-0"):
        AudioStimulus(
            speakerLocations=[
                {"azimuth": -60, "elevation": 15},
                {"azimuth": 0, "elevation": 15},
                {"azimuth": 60, "elevation": 15},
            ]
        )
