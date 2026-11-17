"""Load coen-dome-decision-making sessions and score them from WorkflowData_Trials (schemas/trial-columns.yml).

Nothing here runs on import. A session is `<root>/sub-<mouse>/ses-<id>_<DateTime>`, as the lab's ingest puts them
on jacuzzi (Live-Data); its columns come from the session's own manifest.json (open_rigs.trial_log)."""

import json
from pathlib import Path

import pandas as pd
from open_rigs import trial_log

# The repositories whose sessions count, by name alone (stage.py's OURS): this workflow and av-dome's switch-over branch,
# which wrote this workflow's files before the move (README); production av-dome's are told apart by their task file.
OURS = ("coen-dome-decision-making", "av-dome")
SCORED = ("Correct", "Incorrect")   # outcome Probe has no right answer and NoResponse is no choice: neither is counted


def _manifest(session):
    try:
        return json.loads((session / "manifest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def _settings(session, kind):
    """The session's Settings_<kind>_*.json (the first, as stage.py's summaries carry), or {} when it has none."""
    files = sorted((Path(session) / "Settings").glob(f"Settings_{kind}_*.json"))
    return json.loads(files[0].read_text(encoding="utf-8-sig")) if files else {}


def is_this_workflow(session):
    """True for this workflow's sessions (Live-Data holds every workflow's): the manifest's source.repo named as stage.py's
    rule (any owner, fork or branch), and a Settings_Task with taskType, so production av-dome's are left out."""
    repo = ((_manifest(session).get("source") or {}).get("repo") or "").rstrip("/").rsplit("/", 1)[-1].lower().removesuffix(".git")
    if repo not in OURS:
        return False
    task = _settings(session, "Task")
    return "taskType" in task.get("taskParameters", task)


def find_sessions(root, subjects=None):
    """The session folders of this workflow under root, by `sub-` folder (all mice when subjects is None)."""
    root = Path(root)
    found = []
    for mouse in sorted(root.glob("sub-*")):
        if subjects is not None and mouse.name[4:] not in subjects:
            continue
        found += [s for s in sorted(mouse.glob("ses-*")) if s.is_dir() and is_this_workflow(s)]
    return found


def load_sessions(root, subjects=None):
    """Every trial of this workflow's sessions as one DataFrame: the typed columns plus subject, session and session_path."""
    frames = []
    for session in find_sessions(root, subjects):
        rows = trial_log.read(session)
        if not rows:
            continue
        frame = pd.DataFrame(rows)
        frame["subject"] = session.parent.name[4:]
        frame["session"] = session.name
        frame["session_path"] = str(session)
        frames.append(frame)
    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()


def scored(df):
    """The trials new_draw_accuracy counts: a Correct or Incorrect choice on a new draw (no repeat). An auto-rewarded trial
    is a NoResponse, and a Probe (target NoReward or Random) has no right answer: neither is in it."""
    return df[df["outcome"].isin(SCORED) & (df["repeatCount"] == 0)]


def new_draw_accuracy(df):
    """(fraction Correct, number of scored trials) for one session's trials: every new draw with a choice, one spout or
    two. Not lib-open-rigs' performance (summarise-experiment, app-rig-control), which counts the both-spout and go/no-go
    trials, repeats included."""
    s = scored(df)
    return (float((s["outcome"] == "Correct").mean()) if len(s) else float("nan")), len(s)


def water_ul(df):
    """Microlitres offered and rewarded: the sum of rewardOfferedUl over the trials that were rewarded."""
    return float(df.loc[df["rewarded"].astype(bool), "rewardOfferedUl"].sum())


def probe_rates(df):
    """Per probe target (NoReward, Random): trials, fraction rewarded and fraction penalised."""
    p = df[df["outcome"] == "Probe"].copy()
    if p.empty:
        return pd.DataFrame(columns=["target", "trials", "rewardedRate", "penalisedRate"])
    p["rewarded"] = p["rewarded"].astype(bool)
    p["penalised"] = p["penalised"].astype(bool)
    out = p.groupby("target").agg(trials=("outcome", "size"), rewardedRate=("rewarded", "mean"), penalisedRate=("penalised", "mean"))
    return out.reset_index()


def session_summary(df):
    """One row per session: trials, scored trials, new-draw accuracy, water (ul) and the probe rates."""
    rows = []
    for (subject, session), s in df.groupby(["subject", "session"], sort=True):
        accuracy, n = new_draw_accuracy(s)
        probes = s[s["outcome"] == "Probe"]
        rows.append({
            "subject": subject, "session": session, "trials": len(s), "scored": n, "newDrawAccuracy": accuracy,
            "waterUl": water_ul(s), "probes": len(probes),
            "probeRewardedRate": float(probes["rewarded"].astype(bool).mean()) if len(probes) else float("nan"),
            "probePenalisedRate": float(probes["penalised"].astype(bool).mean()) if len(probes) else float("nan"),
        })
    return pd.DataFrame(rows)


def trial_weights(session):
    """The trialWeights a session ran with, from its Settings_Task_*.json at its TrainingStage (Settings_Experiment_*.json's
    parameters): the stage's own when it has them, else the top-level one; None without a task file. A task file from the
    training pipeline on (no trainingStages) holds the stage's values at the top level."""
    task = _settings(session, "Task")
    if not task:
        return None
    task = task.get("taskParameters", task)
    if "trainingStages" not in task:
        return task.get("trialWeights")
    stage = int(float(_settings(session, "Experiment")["parameters"]["TrainingStage"]))
    own = (task.get("trainingStages") or [])[stage].get("trialWeights")
    return own or task.get("trialWeights")
