"""schemas/trial-columns.yml starts with the lab's shared block, its core with the optogenetics and quietWait blocks (open_rigs.rigs.trials):
the same names, order, types, units and descriptions in every dome task, so one analysis reads them all."""

from pathlib import Path

import yaml
from open_rigs.rigs.trials import shared_block_problems


def test_the_trial_columns_start_with_the_shared_block():
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert shared_block_problems(columns, ("optogenetics", "quietWait")) == []


def test_penalty_sound_has_the_lab_s_one_description():
    # PenaltySound is the shared block's (lib-open-rigs' CORE_COLUMNS), one description in every dome task;
    # which trials are penalised is in penalised's note.
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert columns["penaltySound"]["description"] == "True when the trial was penalised and the task's penalty sound is enabled."
