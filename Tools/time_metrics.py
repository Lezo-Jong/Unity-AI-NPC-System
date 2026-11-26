"""
시간 단위 행동 지표와 효과크기 (리뷰어 2 Major 2b, minor 6).

사람 라벨은 0.1 s 틱마다 하나씩 생기고 NPC 결정은 실행기가 받을 때만 생기므로, 결정 표본의 행동 비율은
세는 단위가 다릅니다. 여기서는 사거리(<4.5 m) 안에서 보낸 '시간'을 기준으로 다시 셉니다.

- 시간 비율: 사거리 안 시간 중 각 행동 상태로 보낸 비율 (Move는 이동, 콤보는 Slash로 묶음)
- 분당 시작 횟수: 공격(Slash 계열 시작, CrouchSlash), 방어(Block, CrouchBlock), 웅크리기, 점프
- 지속 시간: Idle, Crouch 구간 길이 중앙값
- 상대 공격 대응: 상대 공격 시작(사거리 안) 뒤 1.0 s 안에 방어를 시작한 비율과 반응 시간 중앙값
- 효과크기: 경기(NPC) 또는 세션(사람) 단위 값에 대한 Cliff's delta, 조건 간 쌍별

NPC 타임라인은 decisions 로그에서 복원합니다: 결정 시각 t_i에 실행한 행동이 다음 결정 t_{i+1}까지
지속된 것으로 봅니다(콤보 예약 기회에서 공격 아닌 선택이면 현재 공격이 이어짐). 결정 사이 간격은 최대 3 s로 자릅니다.

사용 예: python Tools/time_metrics.py --decisions "data/matches/decisions_run*.csv" --matches "data/matches/matches_run*.csv" --human data/human
"""
import argparse
import glob
import os
import sys
from itertools import combinations

import numpy as np
import pandas as pd

RANGE = 4.5
SLASHES = {"Slash", "Slash2", "Slash3"}
GROUP = {"Idle": "Idle", "Move": "Move", "Slash": "Slash*", "Slash2": "Slash*", "Slash3": "Slash*",
         "Block": "Block", "CrouchBlock": "CrouchBlock", "Jump": "Jump", "Crouch": "Crouch", "CrouchSlash": "CrouchSlash"}
STATES = ["Idle", "Move", "Slash*", "Block", "CrouchBlock", "Jump", "Crouch", "CrouchSlash"]
DEF = {"Block", "CrouchBlock"}


def cliffs_delta(a, b):
    a, b = np.asarray(a, float), np.asarray(b, float)
    a, b = a[~np.isnan(a)], b[~np.isnan(b)]
    if len(a) == 0 or len(b) == 0:
        return np.nan
    gt = (a[:, None] > b[None, :]).sum(); lt = (a[:, None] < b[None, :]).sum()
    return (gt - lt) / (len(a) * len(b))


def magnitude(d):
    d = abs(d)
    return "negligible" if d < 0.147 else "small" if d < 0.33 else "medium" if d < 0.474 else "large"


def episode_metrics(times, states, inrange, opp_att, dt_cap):
    """한 경기/세션의 시간 순 상태열로 지표 계산. times: 각 구간 시작, 다음 원소까지 지속."""
    t = np.asarray(times, float); st = np.asarray(states, object); ir = np.asarray(inrange, bool); oa = np.asarray(opp_att, int)
    dur = np.minimum(np.diff(np.r_[t, t[-1] + 0.1]), dt_cap)
    T = dur[ir].sum()
    out = {"inrange_s": T}
    if T < 1.0:
        return None
    g = np.array([GROUP[s] for s in st])
    for s in STATES:
        out["time_" + s] = dur[ir & (g == s)].sum() / T
    # 시작 이벤트: 상태가 바뀌는 지점 (콤보 단계 전환은 Slash* 안이라 제외)
    starts = np.r_[True, g[1:] != g[:-1]]
    minutes = T / 60.0
    for name, sset in [("atk", {"Slash*", "CrouchSlash"}), ("def", {"Block", "CrouchBlock"}), ("crouch", {"Crouch"}), ("jump", {"Jump"})]:
        out["starts_" + name + "_per_min"] = (starts & ir & np.isin(g, list(sset))).sum() / minutes
    # 지속 구간 길이
    for s in ["Idle", "Crouch"]:
        lens, cur = [], 0.0
        for k in range(len(g)):
            if g[k] == s and ir[k]:
                cur += dur[k]
            elif cur > 0:
                lens.append(cur); cur = 0.0
        if cur > 0:
            lens.append(cur)
        out["bout_" + s + "_median_s"] = float(np.median(lens)) if lens else np.nan
    # 상대 공격 대응
    onsets = np.where((oa[1:] == 1) & (oa[:-1] == 0) & ir[1:])[0] + 1
    rts, met = [], 0
    for i in onsets:
        j = i
        while j < len(t) and t[j] - t[i] <= 1.0:
            if g[j] in ("Block", "CrouchBlock"):
                rts.append(t[j] - t[i]); met += 1; break
            j += 1
    out["opp_attacks"] = len(onsets)
    out["defended_within_1s"] = met / len(onsets) if len(onsets) else np.nan
    out["reaction_median_s"] = float(np.median(rts)) if rts else np.nan
    return out


def human_episodes(human_dir):
    rows = []
    for f in sorted(glob.glob(os.path.join(human_dir, "*.csv"))):
        d = pd.read_csv(f).dropna(subset=["action_name"])
        d = d.sort_values("time")
        # 사망·부활 간격(>0.25 s)으로 구간을 나눔
        seg = (d["time"].diff() > 0.25).cumsum()
        for _, g in d.groupby(seg):
            if len(g) < 20:
                continue
            m = episode_metrics(g["time"], g["action_name"], g["dist"] < RANGE, g["opp_attacking"], 0.25)
            if m:
                m.update(unit=os.path.basename(f)[:7], weight=m["inrange_s"]); rows.append(m)
    return pd.DataFrame(rows)


def npc_episodes(dec):
    rows = []
    for (run, cond, match), g in dec.groupby(["run", "condition", "match"]):
        g = g.sort_values("t")
        executed = []
        for sa, ch, ic in zip(g["self_action"], g["chosen"], g["in_combo"]):
            if ic == 1:
                if ch in SLASHES:
                    executed.append("Slash2" if sa == "Slash" else "Slash3")
                else:
                    executed.append(sa)          # 공격이 이어짐(콤보 종료 예약)
            else:
                executed.append("Slash" if ch in SLASHES else ch)
        m = episode_metrics(g["t"], executed, g["dist"] < RANGE, g["opp_attacking"], 3.0)
        if m:
            m.update(condition=cond, run=run, match=match, unit="%s/%s/%d" % (cond, run, match), weight=m["inrange_s"]); rows.append(m)
    return pd.DataFrame(rows)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--decisions", nargs="+", required=True)
    ap.add_argument("--matches", nargs="+", required=True)
    ap.add_argument("--human", required=True)
    args = ap.parse_args()

    dec = pd.concat([pd.read_csv(f) for p in args.decisions for f in sorted(glob.glob(p))], ignore_index=True)
    mo = pd.concat([pd.read_csv(f) for p in args.matches for f in sorted(glob.glob(p))], ignore_index=True)
    hu = human_episodes(args.human)
    npc = npc_episodes(dec)
    conds = [c for c in ["Random", "FSM", "ML", "MLAdj", "FM", "MLA_Tick", "MLA_DP"] if c in set(npc["condition"])]

    metric_cols = ["time_" + s for s in STATES] + ["starts_atk_per_min", "starts_def_per_min", "starts_crouch_per_min",
                   "starts_jump_per_min", "bout_Idle_median_s", "bout_Crouch_median_s", "defended_within_1s", "reaction_median_s"]

    def pooled(df):
        # 시간 비율과 분당 횟수는 사거리 안 시간으로 가중 평균, 중앙값 지표는 단위별 값의 중앙값
        w = df["weight"]
        out = {}
        for c in metric_cols:
            v = df[c]
            if c.startswith("time_") or c.startswith("starts_"):
                out[c] = np.nansum(v * w) / w[~v.isna()].sum()
            elif c == "defended_within_1s":
                out[c] = np.nansum(v * df["opp_attacks"]) / df.loc[~v.isna(), "opp_attacks"].sum()
            else:
                out[c] = np.nanmedian(v)
        return out

    table = pd.DataFrame({c: pooled(npc[npc["condition"] == c]) for c in conds})
    table["Human"] = pd.Series(pooled(hu))
    fmt = table.astype(object).copy()
    for c in fmt.index:
        if c.startswith("time_") or c == "defended_within_1s":
            fmt.loc[c] = (100 * table.loc[c]).map(lambda v: "%.1f%%" % v)
        else:
            fmt.loc[c] = table.loc[c].map(lambda v: "%.2f" % v)
    print("사거리(<%.1f m) 안 시간 기준 지표 (사람: 세션 7개, 사거리 안 %.0f s; NPC: 조건별 300경기)" % (RANGE, hu["weight"].sum()))
    print(fmt.to_string())

    # 사람과의 거리: 시간 비율 분포 JS
    def js(p, q):
        p = np.array(p, float); q = np.array(q, float); p /= p.sum(); q /= q.sum(); m = 0.5 * (p + q)
        kl = lambda a, b: float((a[a > 0] * np.log2(a[a > 0] / b[a > 0])).sum())
        return 0.5 * kl(p, m) + 0.5 * kl(q, m)
    tcols = ["time_" + s for s in STATES]
    print("\n시간 비율 분포의 JS divergence (vs 사람)")
    for c in conds:
        print("  %s: %.3f" % (c, js(table.loc[tcols, c], table.loc[tcols, "Human"])))

    # 경기 단위 효과크기
    m = mo.copy()
    cnt = [c for c in m.columns if c.startswith("cnt_") and c != "cnt_Move"]
    def ent(r):
        v = r[cnt].to_numpy(float); v = v[v > 0] / v.sum() if v.sum() else v
        return float(-(v * np.log2(v)).sum()) if len(v) else np.nan
    m["entropy_combat"] = m.apply(ent, axis=1)
    per = npc.merge(m[["run", "condition", "match", "npc_hits", "entropy_combat", "duration_s"]], on=["run", "condition", "match"], how="left")
    eff_cols = ["npc_hits", "entropy_combat", "starts_atk_per_min", "starts_def_per_min", "time_Crouch", "defended_within_1s"]
    print("\n경기 단위 효과크기 (Cliff's delta, a − b; |d| < 0.147 무시할 만함, < 0.33 작음, < 0.474 중간, 그 이상 큼)")
    print("| 비교 | " + " | ".join(eff_cols) + " |")
    print("|---" * (len(eff_cols) + 1) + "|")
    for a, b in [("MLAdj", "FM"), ("MLAdj", "ML"), ("ML", "FM"), ("ML", "FSM"), ("ML", "Random"), ("FSM", "Random"), ("MLAdj", "MLA_DP"), ("MLA_DP", "FM")]:
        if a not in conds or b not in conds:
            continue
        cells = []
        for col in eff_cols:
            d = cliffs_delta(per.loc[per["condition"] == a, col], per.loc[per["condition"] == b, col])
            cells.append("%+.2f (%s)" % (d, magnitude(d)))
        print("| %s vs %s | %s |" % (a, b, " | ".join(cells)))
    npc.to_csv("Tools/results/time_metrics_per_match.csv", index=False)
    hu.to_csv("Tools/results/time_metrics_human_segments.csv", index=False)


if __name__ == "__main__":
    main()
