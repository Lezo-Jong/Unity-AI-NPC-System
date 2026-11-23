"""
상태 조건부 사람다움 분석 (리뷰어 2·3 대응): FM baseline과 logit adjustment(MLAdj) 비교.

analyze_matches.py의 JS divergence는 사거리 안 행동의 '전체 빈도'만 비교하므로,
상태를 보지 않는 빈도 일치 무작위 컨트롤러(FM)가 가장 낮은 값을 얻습니다.
이 스크립트는 그 지표와 함께 상태를 반영한 지표 두 가지를 보고합니다.

- JS: 사거리(<4.5 m) 안 결정 시점 행동 분포 vs 사람 (analyze_matches.py와 같은 정의)
- 조건부 JS: (상대 공격 중 여부 × 거리 구간 3개)별 JS를 사람 표본 비율로 가중 평균
- 방어 반응도: P(방어 | 상대 공격 중) − P(방어 | 상대 공격 아님), 콤보 예약 결정 제외
- 95% CI: 경기 단위 부트스트랩, 조건 간 차이도 같은 방식

사용 예 (프로젝트 루트에서):
  python Tools/analyze_conditional.py --decisions "data/matches/decisions_run*.csv" --matches "data/matches/matches_run*.csv" --human data/human
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd
from scipy.stats import fisher_exact

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_matches as am
from train import build_decisions, ACTIONS as TRAIN_ACTIONS

DEF = {"Block", "CrouchBlock"}
ATK = {"Slash", "CrouchSlash"}
DIST_BINS = [0, 3.2, 3.6, 4.5]


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--decisions", nargs="+", required=True)
    ap.add_argument("--matches", nargs="+", required=True)
    ap.add_argument("--human", required=True)
    ap.add_argument("--range", type=float, default=4.5)
    ap.add_argument("--boot", type=int, default=400)
    args = ap.parse_args()

    hfiles = sorted(glob.glob(os.path.join(args.human, "*.csv")))
    hu = build_decisions(pd.concat([pd.read_csv(f) for f in hfiles], ignore_index=True).dropna(), 3)
    hu = hu[hu["dist"] < args.range].copy()
    hu["label"] = [TRAIN_ACTIONS[a] for a in hu["action"]]
    h = am.dist_vec(hu["label"])

    dfiles = sorted({f for p in args.decisions for f in glob.glob(p)})
    mfiles = sorted({f for p in args.matches for f in glob.glob(p)})
    dec = pd.concat([pd.read_csv(f) for f in dfiles], ignore_index=True)
    mo = pd.concat([pd.read_csv(f) for f in mfiles], ignore_index=True)
    dec = dec[dec["dist"] < args.range].copy()
    dec["label"] = dec.apply(am.npc_label, axis=1)

    print("경기 수 (조건 × run)")
    print(mo.groupby(["condition", "run"]).size().unstack().to_string())

    def binid(d):
        return d["opp_attacking"].astype(int).astype(str) + "_" + pd.cut(d["dist"], DIST_BINS).astype(str)

    hu["b"] = binid(hu)
    dec["b"] = binid(dec)
    weights = hu["b"].value_counts(normalize=True)
    human_bin = {b: am.dist_vec(hu.loc[hu["b"] == b, "label"]) for b in weights.index}

    def mjs(d):
        return am.js(am.dist_vec(d["label"]), h)

    def cjs(d):
        s = t = 0.0
        for b, w in weights.items():
            x = d[d["b"] == b]
            if len(x) < 30:
                continue
            s += w * am.js(am.dist_vec(x["label"]), human_bin[b])
            t += w
        return s / t

    def rate(d, labels, opp_attacking):
        f = d[d["in_combo"] == 0] if "in_combo" in d else d
        f = f[f["opp_attacking"] == opp_attacking]
        return f["label"].isin(labels).mean()

    def react(d):
        return rate(d, DEF, 1) - rate(d, DEF, 0)

    hfree = hu[~hu["label"].isin(["Slash2", "Slash3"]) & (hu["self_cur_attack"] == 0)]
    conds = list(dict.fromkeys(mo["condition"]))
    rng = np.random.default_rng(0)
    groups = {c: list(dec[dec["condition"] == c].groupby(["run", "match"]).groups.values()) for c in conds}
    boots = {c: {"JS": [], "cJS": [], "react": []} for c in conds}
    for _ in range(args.boot):
        for c in conds:
            k = groups[c]
            x = dec.loc[np.concatenate([k[i] for i in rng.integers(0, len(k), len(k))])]
            boots[c]["JS"].append(mjs(x))
            boots[c]["cJS"].append(cjs(x))
            boots[c]["react"].append(react(x))

    def ci(c, k):
        return "[%.3f–%.3f]" % tuple(np.percentile(boots[c][k], [2.5, 97.5]))

    print("\n| 조건 | N | 승률 [95% CI] | 명중 | JS [95% CI] | 조건부 JS [95% CI] | 방어 반응도 [95% CI] | 방어 % (공격 중/아님) | 공격 시작 % (공격 중/아님) |")
    print("|---|---|---|---|---|---|---|---|---|")
    for c in conds:
        d = dec[dec["condition"] == c]
        m = mo[mo["condition"] == c]
        w = (m["outcome"] == "win").sum()
        lo, hi = am.wilson(w, len(m))
        print("| %s | %d | %.1f%% [%.1f–%.1f] | %.2f ± %.2f | %.3f %s | %.3f %s | %.3f %s | %.0f / %.0f | %.0f / %.0f |" % (
            c, len(m), 100 * w / len(m), 100 * lo, 100 * hi, m["npc_hits"].mean(), m["npc_hits"].std(),
            mjs(d), ci(c, "JS"), cjs(d), ci(c, "cJS"), react(d), ci(c, "react"),
            100 * rate(d, DEF, 1), 100 * rate(d, DEF, 0), 100 * rate(d, ATK, 1), 100 * rate(d, ATK, 0)))
    print("| 사람 | – | – | – | 0 | 0 | %.3f | %.0f / %.0f | %.0f / %.0f |" % (
        react(hfree), 100 * rate(hfree, DEF, 1), 100 * rate(hfree, DEF, 0),
        100 * rate(hfree, ATK, 1), 100 * rate(hfree, ATK, 0)))

    print("\n조건 간 차이 (a − b, 경기 단위 부트스트랩 95% CI, * = 0 미포함)")
    for a, b in [("MLAdj", "FM"), ("MLAdj", "ML"), ("ML", "FM")]:
        if a not in boots or b not in boots:
            continue
        for k, name in [("JS", "JS"), ("cJS", "조건부 JS"), ("react", "방어 반응도")]:
            dd = np.array(boots[a][k]) - np.array(boots[b][k])
            lo, hi = np.percentile(dd, [2.5, 97.5])
            print("  %s %s − %s: %.3f [%.3f, %.3f]%s" % (name, a, b, dd.mean(), lo, hi, "  *" if lo > 0 or hi < 0 else ""))

    hr = react(hfree)
    print("\n방어 반응도 95%% CI가 사람 값(%.3f)을 포함하는가" % hr)
    for c in conds:
        lo, hi = np.percentile(boots[c]["react"], [2.5, 97.5])
        print("  %s: %s" % (c, "예" if lo <= hr <= hi else "아니오"))

    print("\n승률 비교 (Fisher exact, Holm 보정)")
    pairs = [p for p in [("MLAdj", "ML"), ("FM", "ML"), ("MLAdj", "FM"), ("MLAdj", "Random")] if p[0] in conds and p[1] in conds]
    pvals = []
    for a, b in pairs:
        x, y = mo[mo["condition"] == a], mo[mo["condition"] == b]
        table = [[(x["outcome"] == "win").sum(), (x["outcome"] != "win").sum()],
                 [(y["outcome"] == "win").sum(), (y["outcome"] != "win").sum()]]
        pvals.append(fisher_exact(table)[1])
    for (a, b), p, pa in zip(pairs, pvals, am.holm(np.array(pvals))):
        print("  %s vs %s: p = %.3g, Holm p = %.3g" % (a, b, p, pa))

    print("\n행동 분포 (%)")
    tab = pd.DataFrame({c: am.dist_vec(dec.loc[dec["condition"] == c, "label"]) * 100 for c in conds}, index=am.ACTIONS)
    tab["사람"] = h * 100
    print(tab.round(1).to_string())


if __name__ == "__main__":
    main()
