"""
SuavoAgent vs Opus — benchmark scorer + statistics.

The experimental spine for the amortization-moat head-to-head (see bench/README.md).
Pure stdlib so it runs anywhere (Mac, the Windows laptop, CI) with no numpy.

Design invariants (from the adversarial methodology review):
  * Four arms reported SEPARATELY: A1 (shipped PricingWorkflow), A2 (Qwen3 cold-reason),
    A3 (verified-skill replay), B (Opus computer-use). Headline = A1 vs B + the
    A2->A3 amortization curve.
  * The oracle is BLINDED to the arm: judge_success(reported, truth) never sees which
    arm produced `reported`. The arm label lives only on the trial record.
  * Completion rate -> Wilson 95% CI (never normal-approx). Latency -> median + IQR
    (fat-tailed, never mean). A-vs-B gap -> two-proportion bootstrap.
  * Cross-arm "effort" is PRIMITIVE INPUT EVENTS only (clicks+keys+scrolls, logged
    identically at SendInput level). Raw LLM "steps" are within-arm only.
  * Cost: Opus = measured tokens x price; SuavoAgent = wall-clock x machine-rate +
    $0 marginal-API stated separately. PHI egress counted (screenshots leaving the box).
"""
from __future__ import annotations

import csv
import json
import math
import random
import statistics
from dataclasses import dataclass, asdict, field
from typing import Callable

# Arms ---------------------------------------------------------------------
ARM_A1 = "A1_shipped_pricingworkflow"   # deterministic UiaFirst chain (the wedge as shipped)
ARM_A2 = "A2_qwen3_cold_reason"         # agentic loop, no pre-baked rule (forces the brain)
ARM_A3 = "A3_verified_replay"           # zero-LLM replay of the A2-banked skill
ARM_B  = "B_opus_computer_use"          # cloud Opus computer-use baseline (red-teamed)
ARMS = (ARM_A1, ARM_A2, ARM_A3, ARM_B)

# Opus 4.8 pricing (USD per 1M tokens) — claude-api reference, cached 2026-06-04.
OPUS_USD_PER_MTOK_IN = 5.00
OPUS_USD_PER_MTOK_OUT = 25.00


@dataclass
class Trial:
    """One run of one arm on one sim variant. `run_index` is the per-(arm,variant)
    cumulative run counter that drives the amortization curve (0 = first encounter)."""
    arm: str
    variant: str               # faithful | renamed-cost | slow-grid | ... | vision-hostile-*
    trial_idx: int
    run_index: int
    reported_value: str | None # the supplier cost the arm reported (None = no answer)
    oracle_truth: str          # the correct cheapest non-discontinued cost
    success: bool              # set by judge_success(reported_value, oracle_truth) — blinded
    wall_clock_warm_s: float   # start=first action after focus, end=oracle-confirmed success
    wall_clock_cold_s: float   # warm + model-load (cold = first task of the day)
    primitive_input_events: int  # clicks + keystrokes + scrolls (cross-arm effort unit)
    cloud_calls: int           # A-arms must be 0
    tokens_in: int = 0         # Opus only
    tokens_out: int = 0        # Opus only
    phi_screenshots_egressed: int = 0  # B = many, A = 0
    failure_mode: str = ""     # which trap caught it (taxonomy)
    temperature: str = ""      # fixed/seed policy, recorded for the report
    notes: str = ""

    def usd_cost(self) -> float:
        """Marginal API cost. Opus = real tokens; A-arms = $0 marginal (compute cost
        is reported separately as wall-clock x machine-rate, not folded in here)."""
        if self.arm == ARM_B:
            return (self.tokens_in / 1e6) * OPUS_USD_PER_MTOK_IN + \
                   (self.tokens_out / 1e6) * OPUS_USD_PER_MTOK_OUT
        return 0.0


# --- Blinded oracle -------------------------------------------------------
def judge_success(reported_value: str | None, oracle_truth: str) -> bool:
    """The arm-blinded success oracle. Takes ONLY the reported answer and ground truth —
    it cannot see which arm produced `reported_value`, so it cannot be tuned to an arm.
    Normalizes currency/whitespace; a wrong-or-missing answer fails closed."""
    if reported_value is None:
        return False
    def norm(s: str) -> str:
        s = s.strip().lower().replace("$", "").replace(",", "")
        try:
            return f"{float(s):.4f}"   # sub-cent precision, like PricingWorkflow
        except ValueError:
            return s
    return norm(reported_value) == norm(oracle_truth)


# --- Statistics -----------------------------------------------------------
def wilson_ci(successes: int, n: int, z: float = 1.96) -> tuple[float, float, float]:
    """Wilson score 95% CI for a binomial proportion. Returns (point, low, high).
    Correct in the tails and at small n, unlike the normal approximation."""
    if n == 0:
        return (0.0, 0.0, 0.0)
    p = successes / n
    denom = 1 + z * z / n
    center = (p + z * z / (2 * n)) / denom
    half = (z * math.sqrt((p * (1 - p) + z * z / (4 * n)) / n)) / denom
    return (p, max(0.0, center - half), min(1.0, center + half))


def median_iqr(values: list[float]) -> tuple[float, float, float]:
    """Median + interquartile range (Q1, Q3). Latency is fat-tailed; never report a mean."""
    if not values:
        return (0.0, 0.0, 0.0)
    vs = sorted(values)
    med = statistics.median(vs)
    if len(vs) < 4:
        return (med, vs[0], vs[-1])
    q = statistics.quantiles(vs, n=4)  # [Q1, Q2, Q3]
    return (med, q[0], q[2])


def two_proportion_bootstrap(a_succ: int, a_n: int, b_succ: int, b_n: int,
                             iters: int = 20000, seed: int = 1) -> dict:
    """Bootstrap the A-vs-B completion-rate difference. Returns the observed gap, a 95%
    CI on it, and a two-sided p-value for H0: p_A == p_B. seed fixed for reproducibility."""
    if a_n == 0 or b_n == 0:
        return {"gap": 0.0, "ci_low": 0.0, "ci_high": 0.0, "p_value": 1.0}
    rng = random.Random(seed)
    a = [1] * a_succ + [0] * (a_n - a_succ)
    b = [1] * b_succ + [0] * (b_n - b_succ)
    obs = a_succ / a_n - b_succ / b_n
    diffs = []
    for _ in range(iters):
        ra = sum(rng.choice(a) for _ in range(a_n)) / a_n
        rb = sum(rng.choice(b) for _ in range(b_n)) / b_n
        diffs.append(ra - rb)
    diffs.sort()
    lo = diffs[int(0.025 * iters)]
    hi = diffs[int(0.975 * iters)]
    # Permutation-style two-sided p: fraction of bootstrap diffs at least as extreme as 0.
    centered = [d - obs for d in diffs]
    p = sum(1 for d in centered if abs(d) >= abs(obs)) / iters
    return {"gap": obs, "ci_low": lo, "ci_high": hi, "p_value": min(1.0, p)}


# --- Aggregation ----------------------------------------------------------
def aggregate(trials: list[Trial]) -> dict:
    """Per-(arm,variant) success+Wilson CI and latency median/IQR, plus the per-arm
    amortization curve (cost/latency/egress vs cumulative run_index)."""
    cells: dict[tuple[str, str], dict] = {}
    for arm in ARMS:
        for variant in sorted({t.variant for t in trials}):
            cell = [t for t in trials if t.arm == arm and t.variant == variant]
            if not cell:
                continue
            succ = sum(1 for t in cell if t.success)
            p, lo, hi = wilson_ci(succ, len(cell))
            med, q1, q3 = median_iqr([t.wall_clock_warm_s for t in cell])
            cells[(arm, variant)] = {
                "n": len(cell), "successes": succ,
                "success_rate": p, "wilson_lo": lo, "wilson_hi": hi,
                "latency_med_s": med, "latency_q1_s": q1, "latency_q3_s": q3,
                "events_med": median_iqr([float(t.primitive_input_events) for t in cell])[0],
                "usd_med": median_iqr([t.usd_cost() for t in cell])[0],
                "phi_egress_total": sum(t.phi_screenshots_egressed for t in cell),
                "cloud_calls_total": sum(t.cloud_calls for t in cell),
            }
    amort: dict[str, list[dict]] = {}
    for arm in ARMS:
        by_run: dict[int, list[Trial]] = {}
        for t in trials:
            if t.arm == arm:
                by_run.setdefault(t.run_index, []).append(t)
        curve = []
        for run_index in sorted(by_run):
            grp = by_run[run_index]
            curve.append({
                "run_index": run_index,
                "usd_med": median_iqr([t.usd_cost() for t in grp])[0],
                "latency_med_s": median_iqr([t.wall_clock_warm_s for t in grp])[0],
                "phi_egress_med": median_iqr([float(t.phi_screenshots_egressed) for t in grp])[0],
            })
        if curve:
            amort[arm] = curve
    headline = None
    a1 = [(a, v) for (a, v) in cells if a == ARM_A1]
    if a1:
        a1_succ = sum(cells[k]["successes"] for k in cells if k[0] == ARM_A1)
        a1_n = sum(cells[k]["n"] for k in cells if k[0] == ARM_A1)
        b_succ = sum(cells[k]["successes"] for k in cells if k[0] == ARM_B)
        b_n = sum(cells[k]["n"] for k in cells if k[0] == ARM_B)
        if a1_n and b_n:
            headline = two_proportion_bootstrap(a1_succ, a1_n, b_succ, b_n)
    return {"cells": cells, "amortization": amort, "headline_A1_vs_B": headline}


def write_csv(trials: list[Trial], path: str) -> None:
    """Per-trial raw CSV so every aggregate is traceable to its trials."""
    if not trials:
        return
    rows = [asdict(t) | {"usd_cost": t.usd_cost()} for t in trials]
    with open(path, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


def render_report(agg: dict) -> str:
    out = ["# SuavoAgent vs Opus — benchmark report", ""]
    out.append("## Completion rate (Wilson 95% CI) + warm latency (median [IQR])")
    out.append("| arm | variant | n | success | latency s | events | $/task | PHI egress | cloud calls |")
    out.append("|---|---|--:|---|--:|--:|--:|--:|--:|")
    for (arm, variant), c in sorted(agg["cells"].items()):
        out.append(
            f"| {arm} | {variant} | {c['n']} | "
            f"{c['success_rate']:.0%} [{c['wilson_lo']:.0%},{c['wilson_hi']:.0%}] | "
            f"{c['latency_med_s']:.1f} [{c['latency_q1_s']:.1f},{c['latency_q3_s']:.1f}] | "
            f"{c['events_med']:.0f} | ${c['usd_med']:.4f} | "
            f"{c['phi_egress_total']} | {c['cloud_calls_total']} |"
        )
    h = agg.get("headline_A1_vs_B")
    if h:
        out += ["", "## Headline: A1 (shipped) vs B (Opus) — pooled completion-rate gap",
                f"- gap = **{h['gap']:+.1%}** (95% CI [{h['ci_low']:+.1%}, {h['ci_high']:+.1%}]), "
                f"two-sided p = {h['p_value']:.3f}"]
    out += ["", "## Amortization curve (per-task cost/latency/PHI-egress vs run #)"]
    for arm, curve in agg["amortization"].items():
        out.append(f"- **{arm}**: " + " -> ".join(
            f"run{p['run_index']}(${p['usd_med']:.4f}/{p['latency_med_s']:.1f}s/"
            f"{p['phi_egress_med']:.0f}img)" for p in curve))
    return "\n".join(out)


# --- self-test (runs on any machine; verifies the statistics) -------------
if __name__ == "__main__":
    rng = random.Random(7)
    trials: list[Trial] = []
    variants = ["faithful", "renamed-cost", "virtual-depth", "vision-hostile-canvas"]
    # Synthetic ground truth: A1 deterministic (perfect, $0, fast); A2 weak first-run reasoning;
    # A3 perfect replay ($0, instant); B strong but pays $ + PHI egress every run.
    profiles = {
        ARM_A1: dict(p=0.99, lat=3.0, ev=22, tin=0, tout=0, phi=0),
        ARM_A2: dict(p=0.55, lat=45.0, ev=30, tin=0, tout=0, phi=0),
        ARM_A3: dict(p=0.985, lat=1.2, ev=22, tin=0, tout=0, phi=0),
        ARM_B:  dict(p=0.90, lat=18.0, ev=26, tin=9000, tout=1200, phi=12),
    }
    for arm in ARMS:
        pr = profiles[arm]
        for variant in variants:
            for i in range(20):
                ok = rng.random() < pr["p"]
                truth = "0.3719"
                trials.append(Trial(
                    arm=arm, variant=variant, trial_idx=i, run_index=i,
                    reported_value=(truth if ok else None), oracle_truth=truth,
                    success=judge_success(truth if ok else None, truth),
                    wall_clock_warm_s=pr["lat"] * rng.uniform(0.7, 1.6),
                    wall_clock_cold_s=pr["lat"] * rng.uniform(0.7, 1.6) + (4.0 if arm in (ARM_A2,) else 0),
                    primitive_input_events=pr["ev"] + rng.randint(-3, 3),
                    cloud_calls=(1 if arm == ARM_B else 0),
                    tokens_in=pr["tin"], tokens_out=pr["tout"],
                    phi_screenshots_egressed=pr["phi"],
                    failure_mode=("" if ok else "no_usable_supplier_rows"),
                    temperature=("n/a" if arm in (ARM_A1, ARM_A3) else "0"),
                ))
    agg = aggregate(trials)
    print(render_report(agg))
    # sanity assertions on the stats themselves
    assert wilson_ci(20, 20)[1] > 0.80, "Wilson CI lower bound wrong at p=1"
    assert wilson_ci(0, 20)[2] < 0.20, "Wilson CI upper bound wrong at p=0"
    print("\n[self-test] statistics + report render OK")
