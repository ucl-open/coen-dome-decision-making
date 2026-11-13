"""The stage suggestion: the mouse's last experiment's stage (no advancing rule is written down for this task), from this
workflow's experiments and av-dome's (plan v11, decision 3), never a guess."""
import csv
import json
import subprocess
import sys
from pathlib import Path

from ucl_open_coen_dome_decision_making.stage import Experiment, experiments, suggest

STAGE_PY = Path(__file__).parents[1] / "src" / "ucl_open_coen_dome_decision_making" / "stage.py"


def write(path: Path, header: list[str], rows: int):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows([[i] * len(header) for i in range(rows)])


def write_new(folder: Path, stage: int, rows: int, task_type: str | None = "discrimination", repo: str = "coen-dome-decision-making"):
    """An experiment as this workflow (or av-dome's switch-over branch) writes it."""
    write(folder / "BonsaiData" / "BonsaiData_Trial_x.csv", ["trialNumber", "outcome"], rows)
    (folder / "Settings").mkdir()
    (folder / "Settings" / "Settings_Experiment_x.json").write_text(json.dumps({"parameters": {"TrainingStage": str(stage)}}), encoding="utf-8")
    parameters = {} if task_type is None else {"taskType": task_type}
    (folder / "Settings" / "Settings_Task_x.json").write_text(json.dumps({"taskParameters": parameters}), encoding="utf-8")
    (folder / "manifest.json").write_text(json.dumps({"source": {"repo": f"https://github.com/ucl-open/{repo}"}}), encoding="utf-8")


def test_the_last_experiment_s_stage_is_suggested_whatever_its_performance():
    history = [Experiment("b", "2026-11-12", 3, "discrimination", 400), Experiment("a", "2026-11-11", 2, "discrimination", 400)]
    stage, why = suggest(history, "discrimination")
    assert stage == 3 and "Stage 3 Again" in why


def test_another_task_type_or_no_history_suggests_nothing():
    assert suggest([], "discrimination")[0] is None
    stage, why = suggest([Experiment("b", "2026-11-12", 3, "localization", 400)], "discrimination")
    assert stage is None and "Localization" in why


def test_production_av_dome_counts_and_says_its_task_type_is_unknown(tmp_path):
    live = tmp_path / "Live-Data"
    write(live / "AV_Dome" / "sub-GN012" / "ses-Stage 4_date-2026-11-12T12-43-44" / "TrialSettings" / "TrialSettings.csv", ["Seconds", "trialType"], 200)
    write(live / "AV_Dome" / "sub-GN012" / "ses-Stage 2_date-2026-11-01T12-00-00" / "TrialSettings" / "TrialSettings.csv", ["Seconds", "trialType"], 200)
    (live / "AV_Dome" / "sub-GN012" / "ses-Stage 3_date-2026-11-13T09-00-00").mkdir()  # Go then Stop: no trials
    unread: list[str] = []
    found = experiments("GN012", [live], unread)
    assert not unread and [e.stage for e in found] == [4, 2]
    stage, why = suggest(found, "localization")
    assert stage == 4 and "Production av-dome" in why


def test_the_newest_of_every_source_decides_and_others_repos_never_do(tmp_path):
    rig, live = tmp_path / "rig", tmp_path / "Live-Data"
    write_new(rig / "sub-GN012" / "ses-007D1_2026-11-14T10-00-00", 1, 50, repo="coen-dome-navigation")  # not this task
    write_new(rig / "sub-GN012" / "ses-006D1_2026-11-13T10-00-00", 2, 60)
    write_new(rig / "sub-GN012" / "ses-005D1_2026-11-10T10-00-00", 3, 60, task_type=None, repo="av-dome")  # before taskType: localization
    write(live / "AV_Dome" / "sub-GN012" / "ses-Stage 4_date-2026-11-12T12-43-44" / "TrialSettings" / "TrialSettings.csv", ["Seconds"], 10)
    unread: list[str] = []
    found = experiments("GN012", [rig, live], unread)
    assert not unread
    assert [(e.stage, e.task_type) for e in found] == [(2, "discrimination"), (4, None), (3, "localization")]
    assert suggest(found, "discrimination")[0] == 2


def test_an_unreadable_or_ingested_experiment_or_a_silent_share_suggests_nothing(tmp_path):
    rig = tmp_path / "rig"
    write_new(rig / "sub-GN012" / "ses-006D1_2026-11-13T10-00-00", 2, 60)
    (rig / "sub-GN012" / "ses-006D1_2026-11-13T10-00-00" / "Settings" / "Settings_Experiment_x.json").unlink()
    unread: list[str] = []
    experiments("GN012", [rig], unread)
    assert unread == ["ses-006D1_2026-11-13T10-00-00 (Unreadable: FileNotFoundError)"]
    staging = tmp_path / "staging" / "BLUE-DOME-1" / "sub-GN012" / "ses-008D1_2026-11-14T10-00-00"
    staging.mkdir(parents=True)
    (staging / "manifest.json").write_text(json.dumps({"source": {"repo": "coen-dome-decision-making"}}), encoding="utf-8")
    unread = []
    experiments("GN012", [tmp_path / "staging"], unread)
    assert unread == ["ses-008D1_2026-11-14T10-00-00 (Only Its Manifest Is Left)"]
    unread = []
    experiments("GN012", [Path(r"\\no-such-server-for-a-test\share")], unread)
    assert unread and "No Answer" in unread[0]


def test_the_script_runs_with_the_standard_library_alone(tmp_path):
    # rig-control runs it with its own Python, -I, outside this project: no package import.
    write_new(tmp_path / "sub-GN012" / "ses-006D1_2026-11-13T10-00-00", 3, 60)
    done = subprocess.run([sys.executable, "-I", str(STAGE_PY), "--subject=GN012", "--task-type=discrimination", f"--root={tmp_path}"],
                          capture_output=True, text=True, check=True)
    assert json.loads(done.stdout.strip().splitlines()[-1])["value"] == 3
