"""Load coen-dome-decision-making experiments and score them from WorkflowData_Trials (schemas/trial-columns.yml).

Nothing here runs on import. Finding and loading experiments, and their stage and conditions, are lib-open-rigs'
open_rigs.experiments (an experiment is `<root>/sub-<mouse>/ses-<id>_<DateTime>`, as the lab's ingest puts them on jacuzzi,
Live-Data; its columns from the experiment's own manifest.json); this module scores them."""

import pandas as pd
from open_rigs import experiments
from open_rigs.experiments import curriculum_stage, trial_conditions, trial_weights  # noqa: F401 (this module's API)

REPO = "coen-dome-decision-making"   # the repository whose experiments count, by name alone (any owner, fork or branch)
SCORED = ("correct", "incorrect")   # outcome probe has no right answer and noResponse is no choice: neither is counted


def is_this_workflow(experiment):
    """True for this workflow's experiments (Live-Data holds every workflow's): the manifest's source.repo is REPO."""
    return experiments.is_workflow(experiment, REPO)


def find_experiments(root, subjects=None):
    """The experiment folders of this workflow under root, by `sub-` folder (all mice when subjects is None)."""
    return experiments.find_experiments(root, REPO, subjects)


def load_experiments(root, subjects=None):
    """Every trial of this workflow's experiments as one DataFrame: the typed columns plus subject, experiment and experiment_path."""
    return experiments.load_experiments(root, REPO, subjects)


def scored(df):
    """The trials new_draw_accuracy counts: a correct or incorrect choice on a new draw (no repeat). An auto-rewarded trial
    is a noResponse, and a probe (target noReward or random) has no right answer: neither is in it."""
    return df[df["outcome"].isin(SCORED) & (df["repeatCount"] == 0)]


def new_draw_accuracy(df):
    """(fraction correct, number of scored trials) for one experiment's trials: every new draw with a choice, one spout or
    two. Not lib-open-rigs' performance (summarise-experiment, app-rig-control), which counts the trials with both spouts offered (singleSpoutOffered false)
    and the go/no-go trials, repeats included."""
    s = scored(df)
    return (float((s["outcome"] == "correct").mean()) if len(s) else float("nan")), len(s)


def water_ul(df):
    """Microlitres offered and rewarded: the sum of rewardOfferedUl over the trials that were rewarded."""
    return float(df.loc[df["rewarded"].astype(bool), "rewardOfferedUl"].sum())


def probe_rates(df):
    """Per probe target (noReward, random): trials, fraction rewarded and fraction penalised."""
    p = df[df["outcome"] == "probe"].copy()
    if p.empty:
        return pd.DataFrame(columns=["target", "trials", "rewardedRate", "penalisedRate"])
    p["rewarded"] = p["rewarded"].astype(bool)
    p["penalised"] = p["penalised"].astype(bool)
    out = p.groupby("target").agg(trials=("outcome", "size"), rewardedRate=("rewarded", "mean"), penalisedRate=("penalised", "mean"))
    return out.reset_index()


def experiment_summary(df):
    """One row per experiment: trials, scored trials, new-draw accuracy, water (ul) and the probe rates."""
    rows = []
    for (subject, experiment), s in df.groupby(["subject", "experiment"], sort=True):
        accuracy, n = new_draw_accuracy(s)
        probes = s[s["outcome"] == "probe"]
        rows.append({
            "subject": subject, "experiment": experiment, "trials": len(s), "scored": n, "newDrawAccuracy": accuracy,
            "waterUl": water_ul(s), "probes": len(probes),
            "probeRewardedRate": float(probes["rewarded"].astype(bool).mean()) if len(probes) else float("nan"),
            "probePenalisedRate": float(probes["penalised"].astype(bool).mean()) if len(probes) else float("nan"),
        })
    return pd.DataFrame(rows)
