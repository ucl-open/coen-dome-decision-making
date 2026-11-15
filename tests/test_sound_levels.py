import math
import wave
from pathlib import Path

import numpy as np
import pytest
import yaml

ROOT = Path(__file__).parent.parent
SOUNDS = ROOT / "src" / "LocalFiles"
TASK_FILES = sorted((ROOT / "config").glob("task*.yml"))
# Until 15 Nov 2026 (7e9b184) the stimuli played as stored and the background noise at ConvertScale 0.05.
BACKGROUND_SCALE_UNTIL_15_NOV_2026 = 0.05


def _dbfs_rms(path: Path) -> float:
    """dBFS RMS of the whole stored sound, full scale 1 (a full-scale sine is -3 dBFS), as OpenRigs.Audio SoundLevel."""
    with wave.open(str(path)) as w:
        assert (w.getnchannels(), w.getsampwidth()) == (1, 2), f"{path.name} is not mono 16-bit PCM"
        sound = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768
    return 20 * math.log10(float(np.sqrt(np.mean(sound**2))))


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_the_task_files_state_the_levels_played_until_15_nov_2026(path):
    p = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
    stimuli = [SOUNDS / "AuditoryStimuli" / f"filtered_pink_pulse_train_{i}.wav" for i in range(100)]
    assert sorted(stimuli) == sorted((SOUNDS / "AuditoryStimuli").glob("*.wav"))
    for stimulus in stimuli:
        assert abs(p["audioStimulus"]["levelDbfs"] - _dbfs_rms(stimulus)) < 0.05, stimulus.name
    noise = _dbfs_rms(SOUNDS / "background_white_noise.wav") + 20 * math.log10(BACKGROUND_SCALE_UNTIL_15_NOV_2026)
    assert abs(p["backgroundNoise"]["levelDbfs"] - noise) < 0.05
