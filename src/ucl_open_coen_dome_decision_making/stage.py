"""The training stage to suggest for a mouse's next experiment: the stage of its last one.

Nothing in this repository, its README or BACKLOG says when a mouse should move up a stage (production
av-dome's stage was picked by hand in its window), so this suggests no advance: it repeats the last stage,
and says so. The experimenter changes it before Go. A mouse's av-dome experiments count as this workflow's
(plan v11, decision 3): av-dome's switch-over branch wrote this workflow's files, production av-dome
(`main`) its own (`ses-Stage <n>_date-...`, TrialSettings).

rig-control runs it (standard library only) when a mouse is chosen, and fills TrainingStage with it:

    uv run suggest-stage --subject GN012 --task-type discrimination [--root DIR ...]

prints one JSON line, {"value": 3, "why": "..."}: the contract of rig-control's [task.suggest]. Only an
experiment with trials counts. If the last one recorded its task type (this workflow's and the switch-over
branch's Settings_Task) and it is not --task-type, its stage means another table's row: value is null. If a
share among the roots does not answer, an experiment there cannot be read, or one has only its manifest left
(its data ingested elsewhere), value is null and why names it: the mouse's last experiment may be that one.
"""

import argparse
import csv
import json
import re
from dataclasses import dataclass
from pathlib import Path

# Where a mouse's experiments are: on the rig until rig-sync copies them, then in the server's staging, then
# filed in Live-Data (production av-dome's under AV_Dome...\sub-<id>\, as they were written); production
# av-dome's on its rig under C:\data\<dataset>\.
LIVE_DATA = Path(r"\\rdp.arc.ucl.ac.uk\ritd-ag-project-rd01n9-pcoen00")
ROOTS = [Path(r"C:\Coen-Lab-Local\Acquired-Data"), Path(r"\\128.40.51.157\rigs\staging"), LIVE_DATA, Path(r"C:\data")]
OURS = ("coen-dome-decision-making", "av-dome")  # manifest source.repo of an experiment that counts
PRODUCTION = re.compile(r"^ses-Stage (\d+)_")


@dataclass
class Experiment:
    """One experiment that had trials: its stage and, when it recorded it, its task type."""

    name: str
    when: str  # sortable: the folder name's date and time
    stage: int
    task_type: str | None  # None: production av-dome, which did not record it
    trials: int


def _when(folder: str) -> str:
    m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", folder)
    return m.group(1) if m else ""


def _rows(csv_file: Path) -> int:
    with open(csv_file, newline="", encoding="utf-8-sig") as f:
        return sum(1 for _ in csv.DictReader(f))


def _json(folder: Path, pattern: str) -> dict:
    files = sorted(folder.glob(pattern))
    if not files:
        raise FileNotFoundError(f"{folder.name} has trials but no {pattern}")
    return json.loads(files[-1].read_text(encoding="utf-8-sig"))


def read_new(folder: Path) -> Experiment | None:
    """This workflow's or the switch-over branch's: BonsaiData_Trial rows, the TrainingStage parameter in
    Settings_Experiment and the task type in Settings_Task."""
    trials = sum(_rows(log) for log in sorted(folder.glob("BonsaiData/BonsaiData_Trial*.csv")))
    if not trials:
        return None
    stage = int(float(_json(folder, "Settings/Settings_Experiment*.json")["parameters"]["TrainingStage"]))
    # av-dome's task file had no taskType before 8 Nov 2026 (c1ac17d), when localization was its only task.
    task_type = _json(folder, "Settings/Settings_Task*.json")["taskParameters"].get("taskType", "localization")
    return Experiment(folder.name, _when(folder.name), stage, str(task_type), trials)


def read_production(folder: Path) -> Experiment | None:
    """Production av-dome's: TrialSettings rows, the stage in the folder's name (ses-Stage 4_date-...)."""
    m = PRODUCTION.match(folder.name)
    logs = sorted(folder.glob("TrialSettings/TrialSettings*.csv"))
    trials = sum(_rows(log) for log in logs) if m else 0
    return Experiment(folder.name, _when(folder.name), int(m.group(1)), None, trials) if trials else None


def ours(manifest: Path) -> bool:
    """Whether a manifest.json is this workflow's or av-dome's (source.repo); unreadable counts as ours."""
    try:
        repo = json.loads(manifest.read_text(encoding="utf-8-sig")).get("source", {}).get("repo") or ""
    except (OSError, ValueError, AttributeError):
        return True
    return not repo or any(name in repo for name in OURS)


def unanswered(root: Path) -> bool:
    """A share (\\\\server\\...) that cannot be listed: glob reads it as empty, which would look like a mouse with
    no history. A local folder that is not there just has none."""
    if not str(root).startswith("\\\\"):
        return False
    try:
        return not root.is_dir()
    except OSError:
        return True


def _subject_folders(base: Path, subject: str, unread: list[str], eartag: str = "") -> list[Path]:
    try:
        if unanswered(base):
            raise OSError(base)
        names = [n for n in dict.fromkeys((subject, eartag)) if n]  # runs before 13 Nov 2026 were filed under the eartag
        return [f for n in names for f in (*base.glob(f"sub-{n}/ses-*"), *base.glob(f"*/sub-{n}/ses-*"))]
    except OSError:
        unread.append(f"{base} (No Answer)")
        return []


def _read(folder: Path) -> tuple[Experiment | None, bool]:
    """(the experiment or None, whether only its manifest is left)."""
    manifest = folder / "manifest.json"
    if manifest.is_file():
        if not ours(manifest):
            return None, False
        if not (folder / "BonsaiData").is_dir():
            return None, True  # ingested: staging keeps only the manifest
        return read_new(folder), False
    return read_production(folder), False


def experiments(subject: str, roots: list[Path], unread: list[str], eartag: str = "") -> list[Experiment]:
    """Every experiment of the mouse with trials, newest first; a folder seen twice (on the rig and in staging)
    counts once. A root that does not answer, an unreadable experiment and one with only its manifest left
    (its data in none of the roots) are added to `unread`."""
    found: dict[str, Experiment] = {}
    manifest_only: list[str] = []
    for base in roots:
        for folder in _subject_folders(base, subject, unread, eartag):
            if folder.name in found or not folder.is_dir():
                continue
            try:
                e, only_manifest = _read(folder)
            except (OSError, ValueError, KeyError, TypeError) as error:
                unread.append(f"{folder.name} (Unreadable: {type(error).__name__})")
                continue
            if only_manifest:
                manifest_only.append(folder.name)
            elif e is not None:
                found[folder.name] = e
    unread.extend(f"{name} (Only Its Manifest Is Left)" for name in dict.fromkeys(manifest_only) if name not in found)
    return sorted(found.values(), key=lambda e: e.when, reverse=True)


def suggest(history: list[Experiment], task_type: str) -> tuple[int | None, str]:
    """(stage, why): the last experiment's stage; None when that experiment was of another task type."""
    if not history:
        return None, "No Earlier Experiment With Trials: Choose the Stage by Hand"
    last = history[0]
    if last.task_type is not None and last.task_type != task_type:
        return None, f"The Last Experiment ({last.name}) Was {last.task_type.title()}: Choose the Stage by Hand"
    source = "Production av-dome, Task Type Not Recorded" if last.task_type is None else last.task_type.title()
    return last.stage, f"The Last Experiment's Stage ({last.name}, {source}, {last.trials} Trials): Stage {last.stage} Again"


def main() -> None:
    """Print the suggestion for --subject and --task-type as one JSON line."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--subject", required=True)
    parser.add_argument("--eartag", default="", help="also search sub-<eartag>: runs before 13 Nov 2026 were filed under it")
    parser.add_argument("--task-type", required=True, choices=["localization", "discrimination"])
    parser.add_argument("--root", action="append", type=Path, help="where the mouse's experiments are (repeatable)")
    args = parser.parse_args()
    unread: list[str] = []
    history = experiments(args.subject, args.root or ROOTS, unread, args.eartag)
    if unread:
        print(json.dumps({"value": None, "why": f"Could Not Read {' and '.join(unread)}: Choose the Stage by Hand"}))
        return
    stage, why = suggest(history, args.task_type)
    print(json.dumps({"value": stage, "why": why}))


if __name__ == "__main__":
    main()
