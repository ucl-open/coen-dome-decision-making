"""The stage suggestion: the mouse's last experiment's stage (no advancing rule is written down for this task), from this
workflow's session summaries and av-dome's switch-over branch's, never a guess. Summary files are
written here as claude-tools records.md "Session summaries" v1 describes them."""
import json
import subprocess
import sys
from pathlib import Path

import pytest

from ucl_open_coen_dome_decision_making import stage
from ucl_open_coen_dome_decision_making.stage import Experiment, experiments, read_experiment, suggest

STAGE_PY = Path(__file__).parents[1] / "src" / "ucl_open_coen_dome_decision_making" / "stage.py"
REPO = "https://github.com/ucl-open/coen-dome-decision-making"
DROPPED = ("itiStartTime", "trialStartTime", "laserTriggerTime")  # jacuzzi-ingest's drop list (records.md)


def summary(session: str, run: str, stage: int, rows: int, task_type: str | None = "discriminationGoNoGo", repo: str = REPO, **extra) -> dict:
    parameters = {} if task_type is None else {"taskType": task_type}
    s = {"version": 1, "run": run, "subject": "GN012", "session": session, "source": {"repo": repo, "commit": "abc"},
         "trials": {"columns": ["trialNumber", "outcome"], "rows": [[i, "Correct"] for i in range(rows)]},
         "Settings_Experiment": {"parameters": {"TrainingStage": str(stage)}}, "Settings_Task": {"taskParameters": parameters}}
    return {**s, **extra}


def put(root: Path, s: dict) -> Path:
    path = root / "sub-GN012" / f"{s['session']}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(s), encoding="utf-8")
    return path


def expect(root: Path, *runs: str) -> Path:
    path = root.parent / f"{root.name}-expected.json"
    path.write_text(json.dumps(list(runs)), encoding="utf-8")
    return path


def read(root: Path, *runs: str):
    unread: list[str] = []
    return experiments("GN012", root, expect(root, *runs), unread), unread


def test_the_last_experiment_s_stage_is_suggested_whatever_its_performance():
    history = [Experiment("b", "2026-11-12", 3, "discriminationGoNoGo", 400), Experiment("a", "2026-11-11", 2, "discriminationGoNoGo", 400)]
    stage, why = suggest(history, "discriminationGoNoGo")
    assert stage == 3 and "Stage 3 Again" in why


def test_the_newest_experiment_of_this_task_type_decides_and_none_suggests_nothing():
    # Pip, 15 Nov 2026: always the most recent session that matches the current one.
    assert suggest([], "discriminationGoNoGo")[0] is None
    stage, why = suggest([Experiment("b", "2026-11-12", 3, "localization", 400)], "discriminationGoNoGo")
    assert stage is None and "No Earlier Discrimination Go No Go Experiment" in why
    history = [Experiment("c", "2026-11-13", 3, "localization", 400), Experiment("b", "2026-11-12", 2, "discriminationGoNoGo", 400)]
    assert suggest(history, "discriminationGoNoGo")[0] == 2


@pytest.mark.parametrize("repo", ["https://github.com/Coen-Lab/coen-dome-decision-making.git", "https://github.com/someone/AV-Dome/"])
def test_the_repo_name_alone_decides_whatever_the_owner_or_fork(tmp_path, repo):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60, repo=repo))
    assert [e.stage for e in read(tmp_path, "r6")[0]] == [2]


def test_a_name_that_only_contains_ours_is_another_workflow_and_no_repo_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60, repo="https://github.com/x/av-dome-old"))
    assert read(tmp_path, "r6") == ([], [])
    put(tmp_path, summary("ses-007D1_2026-11-14T10-00-00", "r7", 2, 60, repo=None))
    assert read(tmp_path, "r6", "r7")[1] == ["ses-007D1_2026-11-14T10-00-00.json (Unreadable: KeyError)"]


def test_an_old_format_experiment_is_not_read(tmp_path):
    # A task file without taskType is the old format.
    put(tmp_path, summary("ses-005D1_2026-11-10T10-00-00", "r5", 3, 60, task_type=None, repo="av-dome"))
    assert read(tmp_path, "r5") == ([], [])


def test_the_newest_of_this_workflow_and_av_dome_decides_and_other_repos_never_do(tmp_path):
    put(tmp_path, summary("ses-007D1_2026-11-14T10-00-00", "r7", 1, 50, repo="coen-dome-navigation"))  # not this task
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60))
    put(tmp_path, summary("ses-005D1_2026-11-12T10-00-00", "r5", 3, 60, task_type="localization", repo="av-dome"))
    found, unread = read(tmp_path, "r5", "r6", "r7")
    assert not unread
    assert [(e.stage, e.task_type) for e in found] == [(2, "discriminationGoNoGo"), (3, "localization")]
    assert suggest(found, "discriminationGoNoGo")[0] == 2


def test_a_summary_with_no_trials_is_not_an_experiment(tmp_path):
    put(tmp_path, summary("ses-005D1_2026-11-12T10-00-00", "r5", 3, 0))
    assert read(tmp_path, "r5") == ([], [])


def test_a_folder_that_does_not_answer_gives_no_stage(tmp_path, capsys, monkeypatch):
    from ucl_open_coen_dome_decision_making import stage

    missing = tmp_path / "no-share"
    monkeypatch.setattr("sys.argv", ["stage.py", "--subject", "GN012", "--task-type", "discriminationGoNoGo",
                                     "--summaries", str(missing), "--expected", str(expect(tmp_path))])
    stage.main()
    answer = json.loads(capsys.readouterr().out)
    assert answer["value"] is None and str(missing) in answer["why"] and "No Answer" in answer["why"]


def test_an_expected_run_with_no_file_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60))
    _, unread = read(tmp_path, "r6", "r7")
    assert unread == ["Run r7 (No Summary File)"]


def test_a_file_that_cannot_be_read_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60))
    (tmp_path / "sub-GN012" / "ses-007D1_2026-11-14T10-00-00.json").write_text("{not json", encoding="utf-8")
    _, unread = read(tmp_path, "r6")
    assert unread == ["ses-007D1_2026-11-14T10-00-00.json (Unreadable: JSONDecodeError)"]
    # trials but no Settings_Experiment: it may be the experiment that decides
    s = summary("ses-008D1_2026-11-15T10-00-00", "r8", 2, 60)
    del s["Settings_Experiment"]
    put(tmp_path, s)
    _, unread = read(tmp_path, "r6", "r8")
    assert "ses-008D1_2026-11-15T10-00-00 (Unreadable: KeyError)" in unread


def test_a_skipped_file_is_accounted_for_and_ignored(tmp_path):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60))
    put(tmp_path, {"version": 1, "run": "r7", "subject": "GN012", "session": "ses-007D1_2026-11-14T10-00-00", "skipped": "short"})
    found, unread = read(tmp_path, "r6", "r7")
    assert unread == [] and [e.name for e in found] == ["ses-006D1_2026-11-13T10-00-00"]


def test_any_version_but_1_is_refused(tmp_path):
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 2, 60, version=2))
    found, unread = read(tmp_path, "r6")
    assert found == [] and "Version 2 Is Not Supported" in unread[0] and "No Summary File" in unread[1]
    put(tmp_path, {"run": "r7", "session": "ses-007D1_2026-11-14T10-00-00", "skipped": "short"})  # no version at all
    assert any("(Unreadable: KeyError)" in u for u in read(tmp_path, "r7")[1])


def test_the_script_runs_with_the_standard_library_alone(tmp_path):
    # rig-control runs it with its own Python, -I, outside this project: no package import.
    put(tmp_path, summary("ses-006D1_2026-11-13T10-00-00", "r6", 3, 60))
    done = subprocess.run([sys.executable, "-I", str(STAGE_PY), "--subject=GN012", "--task-type=discriminationGoNoGo",
                           f"--summaries={tmp_path}", f"--expected={expect(tmp_path, 'r6')}"],
                          capture_output=True, text=True, check=True)
    assert json.loads(done.stdout.strip().splitlines()[-1])["value"] == 3


def test_one_reader_named_as_navigation_s():
    # Round 3 F15: read_new outlived the old-format reader it was named against; both dome tasks call it read_experiment.
    assert not hasattr(stage, "read_new")
    e = read_experiment(summary("ses-002_2026-11-10T09-00-00", "r2", 3, 5))
    assert (e.stage, e.task_type, e.trials) == (3, "discriminationGoNoGo", 5)
