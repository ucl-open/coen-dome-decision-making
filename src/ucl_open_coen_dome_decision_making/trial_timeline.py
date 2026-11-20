"""The discrimination tasks' trial timeline page (docs/trial-timeline.html), generated from the task files.

`uv run trial-timeline` rewrites the page from config/task-discrimination-*.yml; `uv run trial-timeline --check` exits 1
when the committed page no longer matches them (tests/test_trial_timeline.py runs the same check). Each task file is
validated by the task model and each condition built into its combinations as the launch check builds them
(open_rigs.rigs.conditioned_task), so a condition's own set (a timeout, an interval, a level) shows on its card. What each
choice earns is TrialRow.cs's rules (Outcome, Rewarded, Penalised) and TrialDraw's auto-reward targets, copied here; the
C# is the rule the rig runs. tests/fixtures/outcomes.json is the C#'s answers (OutcomeTableTests.cs writes it and fails when
the C# changes); tests/test_trial_timeline.py holds this copy to it.
"""

import argparse
import copy
import json
import math
import sys
from pathlib import Path
from typing import Any

import yaml
from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_decision_making.task import UclOpenCoenDomeDecisionMakingTaskParameters as Parameters

ROOT = Path(__file__).resolve().parents[2]
TASK_GLOB = "task-discrimination-*.yml"
TEMPLATE = Path(__file__).with_name("trial_timeline.html")
OUTPUT = ROOT / "docs" / "trial-timeline.html"
DATA_MARK = "__TIMELINE_DATA__"
PULSE_SECONDS = 0.05  # each stimulus pulse, sound and grating (src/sounds' filtered_pink_pulse files)
MUTE = "mute"

TASK_LABELS = {
    "discriminationGoNoGo": ("Go / no-go (one spout)", "One spout, the left: lick on a go trial, withhold on a noGo trial. A miss is never penalised."),
    "discriminationLeftRight": ("Left / right (two spouts)", "Two spouts. The answer is the modality, not the side: each condition's target names the spout."),
}
TRIAL_TYPE_NAMES = {
    "visual": "Visual only", "auditory": "Auditory only", "audiovisualCoherent": "Audiovisual coherent",
    "audiovisualConflict": "Audiovisual conflict", "neutral": "Neutral",
}
PROBES = ("noReward", "random")


# ---- TrialRow.cs's rules (Outcome, Rewarded, Penalised, MissPenalised), choice None for a miss ----

def outcome(task_type: str, target: str, choice: str | None, auto: bool) -> str:
    """The trial's outcome word (TrialRow.Outcome)."""
    no_choice = "auto" if auto else "noResponse"
    if target in PROBES:
        return "probe"
    if target == "go":
        return "correct" if choice == "go" else no_choice
    if target == "noGo":
        return "incorrect" if choice == "go" else "correct"
    return no_choice if choice is None else "correct" if choice == target else "incorrect"


def rewarded(target: str, choice: str | None, auto: bool, heads: bool | None, out: str) -> bool:
    """Whether the valve opens (TrialRow.Rewarded)."""
    if target == "random":
        return choice is not None and bool(heads)
    return (out == "correct" and target != "noGo") or (auto and choice is None)


def penalised(target: str, choice: str | None, auto: bool, heads: bool | None, out: str, penalise_probes: bool) -> bool:
    """Whether the penalty runs (TrialRow.Penalised, MissPenalised)."""
    if target == "random" and not penalise_probes:
        return False
    if choice is None:
        return target not in ("go", "noGo", "noReward") and not auto
    if target == "random":
        return not heads
    return out == "incorrect"


def auto_reward_possible(task_type: str, target: str) -> bool:
    """Whether a trial can be auto-rewarded (TrialDraw: never a go/no-go trial but go, a noReward or random probe)."""
    if task_type == "discriminationGoNoGo":
        return target == "go"
    return target not in PROBES


# ---- one combination's timing and rows ----

def _timing(t: dict) -> dict:
    s, sp, st, opto = t["trialStructure"], t["spouts"], t["stimulus"], t["optogenetics"]
    window = s["responseWindowSeconds"]
    return {
        "lead": max(opto["laserToStimulusSeconds"], 0.0),
        "stim": st["durationSeconds"],
        "rate": st["rateHz"],
        "pulse": PULSE_SECONDS,
        "offAfterResponse": st["responseToStimulusOffSeconds"],
        "grace": s["stimulusToResponseWindowSeconds"],
        "window": window,
        "winEnd": None if window is None else s["stimulusToResponseWindowSeconds"] + window,
        "itiMin": s["itiMinSeconds"],
        "itiMax": s["itiMaxSeconds"],
        "consume": sp["rewardConsumptionSeconds"],
        "alwaysIn": sp["alwaysIn"],
        "spoutsIn": sp["stimulusToSpoutsInSeconds"],
        "lickQuiet": s["lickQuietSeconds"],
        "wheelQuiet": s["wheelQuietSeconds"],
        "laser": ({"p": opto["laserOnProbability"], "lead": opto["laserToStimulusSeconds"], "dur": opto["laserDurationSeconds"],
                   "mw": opto["laserPowerMw"]} if opto["enabled"] else None),
    }


def _reward_text(t: dict) -> str:
    r = t["reward"]
    size = f"{_num(r['sizeUl'])} µl"
    if r["largeProbability"] > 0:
        size += f" ({_num(r['sizeUl'] * r['largeMultiplier'])} µl at {_pct(r['largeProbability'])})"
    return size


def _penalty(t: dict, kind: str) -> dict:
    p = t["penalty"][kind]
    return {"kind": kind, "timeout": p["timeoutSeconds"], "sound": p["sound"]["levelDbfs"], "flash": p["flash"]["brightness"]}


def _rows(t: dict) -> list[dict]:
    task_type, target = t["taskType"], t["target"]
    p_auto = t["autoRewardProbability"]
    if task_type == "discriminationGoNoGo":
        choices = [("go", "Lick")]
    elif target in PROBES:
        choices = [("left", "Lick either spout")]  # the two spouts earn the same on a probe
    else:
        choices = [(target, f"Lick {target}"), ("right" if target == "left" else "left", f"Lick {'right' if target == 'left' else 'left'}")]
    if t["trialStructure"]["responseWindowSeconds"] is not None:
        choices.append((None, "No lick"))
    rows = []
    for choice, label in choices:
        autos = [(True, p_auto), (False, 1 - p_auto)] if choice is None and auto_reward_possible(task_type, target) else [(False, 1.0)]
        heads_options = [(True, 0.5), (False, 0.5)] if target == "random" and choice is not None else [(None, 1.0)]
        for auto, p_a in autos:
            for heads, p_h in heads_options:
                share = p_a * p_h
                if share <= 0:
                    continue
                out = outcome(task_type, target, choice, auto)
                paid = rewarded(target, choice, auto, heads, out)
                cost = penalised(target, choice, auto, heads, out, t["penaliseProbes"])
                rows.append({
                    "label": label,
                    "detail": _detail(out, paid, cost, heads, share, task_type, target, choice),
                    "lick": choice is not None,
                    "kind": "reward" if paid else "penalty" if cost else "none",
                    "penalty": _penalty(t, "noResponse" if choice is None else "incorrect") if cost else None,
                })
    return rows


def _detail(out: str, paid: bool, cost: bool, heads: bool | None, share: float, task_type: str, target: str, choice: str | None) -> str:
    if out == "probe":
        text = "probe, nothing happens" if not (paid or cost) else "probe, rewarded" if paid else "probe, penalised"
        if heads is not None:
            text = f"probe, coin {'heads' if heads else 'tails'}: " + ("rewarded" if paid else "penalised" if cost else "nothing")
    elif out == "auto":
        text = "auto-rewarded at the target spout" if task_type != "discriminationGoNoGo" else "auto-rewarded"
    elif out == "correct":
        text = "correct, rewarded" if paid else "correct, unrewarded"
    elif out == "incorrect":
        text = ("false alarm" if task_type == "discriminationGoNoGo" else "incorrect") + (", penalised" if cost else ", no penalty")
    else:
        text = "miss, penalised" if cost else "miss, no penalty"
    return text + (f" ({_pct(share)} of these trials)" if share < 1 else "")


def _shows(t: dict) -> dict:
    visual = t["visualStimulus"]["contrasts"][0] > 0
    auditory = t["auditoryStimulus"]["levelDbfs"] != MUTE
    return {"visual": visual, "auditory": auditory, "vAz": t["visualStimulus"]["initialAzimuthsDeg"][0] if visual else None,
            "aAz": t["auditoryStimulus"]["initialAzimuthsDeg"][0] if auditory else None,
            "contrast": t["visualStimulus"]["contrasts"][0], "level": t["auditoryStimulus"]["levelDbfs"]}


# ---- a task file ----

def _num(x: float) -> str:
    return f"{x:g}"


def _pct(x: float) -> str:
    return f"{x * 100:.0f}%" if abs(x * 100 - round(x * 100)) < 1e-9 else f"{x * 100:.1f}%"


def _az(a: Any) -> str:
    return str(a) if isinstance(a, str) else f"{'+' if a > 0 else '−' if a < 0 else ''}{_num(abs(a))}°"


def _describe(shows: list[dict], trial_type: str) -> str:
    def picks(key):
        return " or ".join(_az(a) for a in sorted({s[key] for s in shows}, key=lambda a: (isinstance(a, str), a if not isinstance(a, str) else 0)))
    visual, auditory = any(s["visual"] for s in shows), any(s["auditory"] for s in shows)
    contrasts = sorted({s["contrast"] for s in shows if s["visual"]})
    grating = "grating" + (f" (contrast {' or '.join(_num(c) for c in contrasts)})" if contrasts != [1.0] else "")
    if visual and auditory:
        if trial_type == "audiovisualConflict":
            return f"{grating} and sound on opposite sides ({picks('vAz')})"
        if trial_type == "audiovisualCoherent":
            return f"{grating} and sound on the same side ({picks('vAz')})"
        return f"{grating} at {picks('vAz')}, sound at {picks('aAz')}"
    if visual:
        return f"{grating} at {picks('vAz')}, silent"
    if auditory:
        return f"sound at {picks('aAz')}, no grating"
    return "no grating, no sound"


def _condition(params: Parameters, base: dict, condition, total: int) -> dict:
    trial_type = params.derive_trial_type(condition)
    combos = ct.condition_combinations(base, condition.set, condition.pairs, params.list_fields())
    tasks = []
    for combo in combos:
        data = copy.deepcopy(base)
        for path, value in combo.items():
            ct.put_path(data, path, value)
        tasks.append((combo, data))
    # Combinations that look the same on the timeline (timing, what shows, what each choice earns) share one variant.
    variants: dict[str, dict] = {}
    for combo, data in tasks:
        timing, rows, shows = _timing(data), _rows(data), _shows(data)
        key = json.dumps([timing, rows, shows["visual"], shows["auditory"], _reward_text(data)], sort_keys=True)
        v = variants.setdefault(key, {"timing": timing, "rows": rows, "reward": _reward_text(data), "shows": [], "combos": []})
        v["shows"].append(shows)
        v["combos"].append(combo)
    out = list(variants.values())
    paths = [p for p in sorted({p for v in out for c in v["combos"] for p in c}) if len({json.dumps(v["combos"][0].get(p)) for v in out}) > 1]
    for v in out:
        v["caption"] = ", ".join(f"{p} {json.dumps(v['combos'][0][p])}" for p in paths) if len(out) > 1 else ""
        v["visual"] = any(s["visual"] for s in v["shows"])
        v["auditory"] = any(s["auditory"] for s in v["shows"])
        del v["combos"], v["shows"]
    target = tasks[0][1]["target"]
    shown = [_shows(d) for _, d in tasks]
    # A role trialType (go, noGo, left, right) names the answer; the card is named by what the trial shows.
    visual, auditory = any(s["visual"] for s in shown), any(s["auditory"] for s in shown)
    modality = trial_type if trial_type in TRIAL_TYPE_NAMES else (
        "audiovisual" if visual and auditory else "visual" if visual else "auditory" if auditory else "neutral")
    return {
        "name": TRIAL_TYPE_NAMES.get(modality, "Audiovisual"),
        "trialType": trial_type,
        "target": target,
        "probe": target in PROBES,
        "weight": condition.weight,
        "share": condition.weight / total,
        "shows": _describe(shown, trial_type),
        "variants": out,
    }


def _phases(t: dict, base: dict) -> list[list[str]]:
    """The anatomy table's rows: phase, duration, parameter."""
    timing = _timing(base)
    rows = []
    if timing["lead"] > 0:
        rows.append(["Trial start, ahead of the stimulus (the laser trigger lead, kept with optogenetics off)", f"{_num(timing['lead'])} s",
                     "optogenetics.laserToStimulusSeconds"])
    if not timing["alwaysIn"]:
        rows.append(["Spouts in, from stimulus onset (negative: before it)", f"{_num(timing['spoutsIn'])} s", "spouts.stimulusToSpoutsInSeconds"])
    stim = "until the response window ends" if timing["stim"] is None else f"{_num(timing['stim'])} s"
    pulses = "" if timing["stim"] is None else f" ({math.ceil(timing['stim'] * timing['rate'] - 1e-9)} pulses)"
    rows.append([f"Stimulus: 50 ms pulses at {_num(timing['rate'])} Hz, grating and sound together{pulses}", stim, "stimulus.durationSeconds, rateHz"])
    if timing["offAfterResponse"] is not None:
        rows.append(["Stimulus off after the response (a miss: the window's end), if sooner", f"{_num(timing['offAfterResponse'])} s",
                     "stimulus.responseToStimulusOffSeconds"])
    rows.append(["Grace period: licks do not count", f"{_num(timing['grace'])} s", "trialStructure.stimulusToResponseWindowSeconds"])
    rows.append(["Response window: the first lick ends it (the outcome)" if base["response"]["thresholdLicks"] == 1 else
                 f"Response window: {base['response']['thresholdLicks']} net licks end it (the outcome)",
                 "no limit" if timing["window"] is None else f"{_num(timing['window'])} s", "trialStructure.responseWindowSeconds"])
    for kind, name in (("incorrect", "an incorrect lick"), ("noResponse", "a penalised miss")):
        p = base["penalty"][kind]
        if p is None:
            continue
        extras = [f"sound {'off' if p['sound']['levelDbfs'] == MUTE else _num(p['sound']['levelDbfs']).replace('-', '−') + ' dBFS'}",
                  "flash off" if p["flash"]["brightness"] == 0 else f"flash {_num(p['flash']['brightness'])} at {_num(p['flash']['rateHz'])} Hz"]
        rows.append([f"Penalty timeout after {name}, from the outcome ({', '.join(extras)})", f"{_num(p['timeoutSeconds'])} s", f"penalty.{kind}.timeoutSeconds"])
    rows.append(["ITI, to the next stimulus onset, drawn per trial", f"{_num(timing['itiMin'])}–{_num(timing['itiMax'])} s", "trialStructure.itiMin/MaxSeconds"])
    rows.append([f"Drinking time, inside the ITI after a reward ({_reward_text(base)})", f"{_num(timing['consume'])} s", "spouts.rewardConsumptionSeconds"])
    if timing["laser"]:
        rows.append([f"Laser on {_pct(timing['laser']['p'])} of trials, from {_num(timing['laser']['lead'])} s before the stimulus",
                     f"{_num(timing['laser']['dur'])} s", "optogenetics.laserDurationSeconds"])
    return rows


def _notes(base: dict, conditions: list[dict]) -> list[str]:
    timing = _timing(base)
    notes = []
    if timing["offAfterResponse"] is None and timing["stim"] is not None:
        notes.append(f"The stimulus does not stop at the response (responseToStimulusOffSeconds null), so it always runs its full "
                     f"{_num(timing['stim'])} s, even into the ITI after an early lick.")
    p = base["autoRewardProbability"]
    if p == 1:
        notes.append("Every target trial's miss is auto-rewarded (autoRewardProbability 1), so no scored miss is penalised."
                     + (" The noResponse penalty only applies once a stage lowers that probability." if base["penalty"]["noResponse"] else ""))
    elif p > 0:
        notes.append(f"A target trial's miss is auto-rewarded on {_pct(p)} of trials (autoRewardProbability {_num(p)}).")
    if any(c["probe"] for c in conditions):
        notes.append("A probe (target noReward) gets neither reward nor penalty, whatever the mouse does; its trial still ends at the first lick."
                     if all(c["target"] != "random" for c in conditions) else
                     "Probes: target noReward gets neither reward nor penalty; target random rewards a lick on a coin's heads"
                     + (", tails penalised (penaliseProbes)." if base["penaliseProbes"] else ", tails unpenalised."))
    if timing["alwaysIn"]:
        notes.append("The spouts stay in throughout (spouts.alwaysIn true): no spout moves within or between trials.")
    if timing["lickQuiet"] or timing["wheelQuiet"]:
        notes.append(f"Quiet waits are on (lick {_num(timing['lickQuiet'])} s, wheel {_num(timing['wheelQuiet'])} s): "
                     "a busy mouse delays the trial start, so the ITI can run longer than drawn.")
    else:
        notes.append("Quiet waits before the trial start are off (lickQuietSeconds and wheelQuietSeconds 0).")
    lo, hi = math.inf, 0.0
    for c in conditions:
        for v in c["variants"]:
            t = v["timing"]
            for r in v["rows"]:
                timeout = r["penalty"]["timeout"] if r["penalty"] else 0.0
                first = t["grace"] if r["lick"] else t["winEnd"]
                last = t["winEnd"] if t["winEnd"] is not None else t["grace"]
                lo = min(lo, first + timeout + t["itiMin"])
                hi = max(hi, last + timeout + t["itiMax"])
    notes.append(f"Onset to next onset runs from {_num(round(lo, 3))} s (the earliest outcome, its timeout if any, the shortest ITI) "
                 f"to {_num(round(hi, 3))} s (the latest outcome, the longest timeout, the longest ITI).")
    return notes


def task_timeline(path: Path) -> dict:
    """One task file's timeline data: its timing, conditions and what each choice earns."""
    data = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
    params = Parameters.model_validate(data)
    base = params.model_dump(by_alias=True, mode="json")
    total = sum(c.weight for c in params.trial_conditions)
    conditions = [_condition(params, base, c, total) for c in params.trial_conditions if c.weight > 0]
    label, blurb = TASK_LABELS.get(base["taskType"], (base["taskType"], ""))
    ends = [v["timing"]["winEnd"] if v["timing"]["winEnd"] is not None else v["timing"]["grace"] + 5 for c in conditions for v in c["variants"]]
    timeouts = [r["penalty"]["timeout"] for c in conditions for v in c["variants"] for r in v["rows"] if r["penalty"]] or [0.0]
    itis = [v["timing"]["itiMax"] for c in conditions for v in c["variants"]]
    return {
        "id": path.stem,
        "file": f"config/{path.name}",
        "taskType": base["taskType"],
        "label": label,
        "blurb": blurb,
        "base": _timing(base),
        "reward": _reward_text(base),
        "incorrectTimeout": base["penalty"]["incorrect"]["timeoutSeconds"],
        "phases": _phases(data, base),
        "notes": _notes(base, conditions),
        "conditions": conditions,
        "axisEnd": math.ceil(max(ends) + max(timeouts) + max(itis)) + 0.5,
    }


def render(config: Path = ROOT / "config") -> str:
    """The page for every discrimination task file in config, as text."""
    tasks = [task_timeline(p) for p in sorted(config.glob(TASK_GLOB))]
    if not tasks:
        raise FileNotFoundError(f"no {TASK_GLOB} in {config}")
    blob = json.dumps({"tasks": tasks}, indent=1, ensure_ascii=False).replace("</", "<\\/")
    return TEMPLATE.read_text(encoding="utf-8").replace(DATA_MARK, blob)


def main(argv: list[str] | None = None) -> int:
    """Write docs/trial-timeline.html, or with --check exit 1 when it is out of date."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="exit 1 when the committed page differs from the task files")
    args = parser.parse_args(argv)
    page = render()
    current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else None
    if args.check:
        if current != page:
            print(f"{OUTPUT.relative_to(ROOT)} is out of date with config/{TASK_GLOB}: run `uv run trial-timeline`", file=sys.stderr)
            return 1
        print(f"{OUTPUT.relative_to(ROOT)} is up to date")
        return 0
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", encoding="utf-8", newline="\n") as f:
        f.write(page)
    print(f"{'Wrote' if current != page else 'Unchanged:'} {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
