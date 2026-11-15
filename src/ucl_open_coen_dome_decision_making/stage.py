"""The training stage to suggest for a mouse's next experiment: the stage of its last one.

Nothing in this repository, its README or BACKLOG says when a mouse should move up a stage (production
av-dome's stage was picked by hand in its window), so this suggests no advance: it repeats the last stage,
and says so. The experimenter changes it before Go. A mouse's experiments from av-dome's switch-over branch
count as this workflow's (plan v11, decision 3): it wrote this workflow's files. It reads the mouse's session
summaries (jacuzzi-ingest's `sub-<lab ID>/<ses folder>.json`, version 1: claude-tools records.md, "Session
summaries"): trials, Settings_Experiment and a Settings_Task with taskType. An experiment in an old format is
not read (deleted on 15 Nov 2026, Pip).

rig-control runs it (standard library only) when a mouse is chosen, and fills TrainingStage with it:

    python -I stage.py --subject GN012 --task-type discrimination --summaries DIR --expected FILE.json

--summaries is the folder holding the sub-<lab ID> folders and --expected a JSON list of the run ids the
mouse has had (rig-control writes it). Prints one JSON line, {"value": 3, "why": "..."}: the contract of
rig-control's [task.suggest]. Only an experiment with trials counts. If the last one's task type is not
--task-type, its stage means another table's row: value is null. If the folder does not answer, an expected
run has no file, or a file cannot be read, value is null and why names it. A skipped file (a run too short
or old to count) is accounted for and otherwise ignored.
"""

import argparse
import json
import os
import re
from dataclasses import dataclass
from pathlib import Path

OURS = ("coen-dome-decision-making", "av-dome")  # source.repo of an experiment that counts
SUMMARY_VERSION = 1


@dataclass
class Experiment:
    """One experiment that had trials: its stage and task type."""

    name: str
    when: str  # sortable: the folder name's date and time
    stage: int
    task_type: str
    trials: int


def _when(folder: str) -> str:
    m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", folder)
    return m.group(1) if m else ""


def read_new(summary: dict) -> Experiment | None:
    """This workflow's or the switch-over branch's: the trials, the TrainingStage parameter in Settings_Experiment
    and the task type in Settings_Task. None without trials, or with a task file in the old format (no taskType)."""
    trials = len(summary["trials"]["rows"])
    if not trials:
        return None
    stage = int(float(summary["Settings_Experiment"]["parameters"]["TrainingStage"]))
    task_type = summary["Settings_Task"]["taskParameters"].get("taskType")
    if task_type is None:
        return None
    name = summary["session"]
    return Experiment(name, _when(name), stage, str(task_type), trials)


def ours(summary: dict) -> bool:
    """Whether a summary is this workflow's or av-dome's (source.repo); one without a repo counts as ours."""
    repo = (summary.get("source") or {}).get("repo") or ""
    return not repo or any(name in repo for name in OURS)


def summaries_of(subject: str, folder: Path, expected: Path, unread: list[str]) -> list[dict]:
    """The mouse's full summaries (skipped and other workflows' left out). Every problem is added to `unread`: a
    folder that does not answer, an expected run id with no file, a file that cannot be read or is not version 1."""
    try:
        os.listdir(folder)
    except OSError:
        unread.append(f"{folder} (No Answer)")
        return []
    try:
        wanted = json.loads(Path(expected).read_text(encoding="utf-8-sig"))
        if not isinstance(wanted, list):
            raise ValueError(expected)
    except (OSError, ValueError):
        unread.append(f"{expected} (Unreadable)")
        return []
    runs: set = set()
    full: dict[str, dict] = {}
    for file in sorted((folder / f"sub-{subject}").glob("*.json")):
        try:
            with open(file, encoding="utf-8-sig") as f:
                s = json.load(f)
            if s["version"] != SUMMARY_VERSION:
                unread.append(f"{file.name} (Version {s['version']} Is Not Supported)")
                continue
            runs.add(s["run"])
            if not s.get("skipped") and ours(s):
                full[s["run"]] = s
        except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
            unread.append(f"{file.name} (Unreadable: {type(error).__name__})")
    unread.extend(f"Run {run} (No Summary File)" for run in wanted if run not in runs)
    return list(full.values())


def experiments(subject: str, folder: Path, expected: Path, unread: list[str]) -> list[Experiment]:
    """Every experiment of the mouse with trials, newest first. Problems are added to `unread`."""
    found = []
    for s in summaries_of(subject, folder, expected, unread):
        try:
            e = read_new(s)
        except (OSError, ValueError, KeyError, TypeError) as error:
            unread.append(f"{s['session']} (Unreadable: {type(error).__name__})")
            continue
        if e is not None:
            found.append(e)
    return sorted(found, key=lambda e: e.when, reverse=True)


def suggest(history: list[Experiment], task_type: str) -> tuple[int | None, str]:
    """(stage, why): the last experiment's stage; None when that experiment was of another task type."""
    if not history:
        return None, "No Earlier Experiment With Trials: Choose the Stage by Hand"
    last = history[0]
    if last.task_type != task_type:
        return None, f"The Last Experiment ({last.name}) Was {last.task_type.title()}: Choose the Stage by Hand"
    return last.stage, f"The Last Experiment's Stage ({last.name}, {last.task_type.title()}, {last.trials} Trials): Stage {last.stage} Again"


def main() -> None:
    """Print the suggestion for --subject and --task-type as one JSON line."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--subject", required=True)
    parser.add_argument("--task-type", required=True, choices=["localization", "discrimination"])
    parser.add_argument("--summaries", required=True, type=Path, help="the folder of sub-<lab ID> folders of session summaries")
    parser.add_argument("--expected", required=True, type=Path, help="a JSON file listing the run ids the mouse has had")
    args = parser.parse_args()
    unread: list[str] = []
    history = experiments(args.subject, args.summaries, args.expected, unread)
    if unread:
        print(json.dumps({"value": None, "why": f"Could Not Read {' and '.join(unread)}: Choose the Stage by Hand"}))
        return
    stage, why = suggest(history, args.task_type)
    print(json.dumps({"value": stage, "why": why}))


if __name__ == "__main__":
    main()
