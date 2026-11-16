"""Load coen-dome-decision-making sessions and score them from BonsaiData_Trial (schemas/trial-columns.yml).

Nothing here runs on import. A session is `<root>/sub-<mouse>/ses-<id>_<DateTime>`, as the lab's ingest puts them
on jacuzzi (Live-Data); its columns come from the session's own manifest.json (open_rigs.trials)."""

import json
from pathlib import Path

import pandas as pd
from open_rigs import trials

WORKFLOW = "coen-dome-decision-making"
SCORED = ("Correct", "Incorrect")   # outcome Probe has no right answer and NoResponse is no choice: neither is performance


def _manifest(session):
    try:
        return json.loads((session / "manifest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def is_this_workflow(session):
    """True when the session's manifest names this workflow's repository (Live-Data holds every workflow's sessions)."""
    source = _manifest(session).get("source") or {}
    return WORKFLOW in f"{source.get('repo', '')} {source.get('workflow', '')} {source.get('root', '')}".lower()


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
        rows = trials.read(session)
        if not rows:
            continue
        frame = pd.DataFrame(rows)
        frame["subject"] = session.parent.name[4:]
        frame["session"] = session.name
        frame["session_path"] = str(session)
        frames.append(frame)
    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()


def scored(df):
    """The trials that count towards performance: a Correct or Incorrect choice on a new draw (no repeat, no auto-reward).
    Probe trials (target NoReward or Random) are never in it."""
    return df[df["outcome"].isin(SCORED) & (df["repeatCount"] == 0) & ~df["autoRewarded"].astype(bool)]


def performance(df):
    """(fraction Correct, number of scored trials) for one session's trials."""
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
    """One row per session: trials, scored trials, performance, water (ul) and the probe rates."""
    rows = []
    for (subject, session), s in df.groupby(["subject", "session"], sort=True):
        perf, n = performance(s)
        probes = s[s["outcome"] == "Probe"]
        rows.append({
            "subject": subject, "session": session, "trials": len(s), "scored": n, "performance": perf,
            "waterUl": water_ul(s), "probes": len(probes),
            "probeRewardedRate": float(probes["rewarded"].astype(bool).mean()) if len(probes) else float("nan"),
            "probePenalisedRate": float(probes["penalised"].astype(bool).mean()) if len(probes) else float("nan"),
        })
    return pd.DataFrame(rows)


def trial_weights(session, stage=None):
    """The trialWeights a session ran with, from its Settings_Task_*.json: the stage's own when it has them, else the
    top-level one (stage: the TrainingStage row, or None for the top level)."""
    files = sorted((Path(session) / "Settings").glob("Settings_Task_*.json"))
    if not files:
        return None
    task = json.loads(files[0].read_text(encoding="utf-8-sig"))
    task = task.get("taskParameters", task)
    stages = task.get("trainingStages") or []
    if stage is not None and 0 <= stage < len(stages) and stages[stage].get("trialWeights"):
        return stages[stage]["trialWeights"]
    return task.get("trialWeights")
