"""
MatchRunner 결과(matches_*.csv) 분석: 리뷰어 코멘트 1·2 대응용 지표.

- 승/무/패율 + Wilson 95% CI
- 행동 엔트로피 (Shannon, bits; 10개 행동 → 최대 log2(10)=3.32), 정규화 엔트로피
- 전투 행동 엔트로피: Move 제외 9개 행동 (최대 log2(9)=3.17). 접근(Move) 시간이 전체 엔트로피를 좌우하므로 함께 보고
- 경기 시간, 명중 수, 결정 지연(ms), FPS, fallback 횟수
- 조건 간 비교: 승률 Fisher exact test, 엔트로피/명중 Mann–Whitney U, Holm 보정
- run(반복 실험)별 승률 → 반복 간 평균 ± SD
- 사람다움 (--decisions + --human): 사거리 안 결정 시점의 행동 분포를 사람 데이터와 비교한
  Jensen–Shannon divergence (0 = 사람과 같음, 1 = 완전히 다름), 경기 단위 부트스트랩 95% CI와 조건 간 차이

사용 예:
  python Tools/analyze_matches.py "%USERPROFILE%/AppData/LocalLow/pvp/pvp/MatchLogs/matches_run*.csv"
      --decisions "%USERPROFILE%/AppData/LocalLow/pvp/pvp/MatchLogs/decisions_run*.csv"
      --human "%USERPROFILE%/AppData/LocalLow/pvp/pvp/CombatLogs"
  (실제로는 한 줄로 입력)
"""
import argparse
import glob
import os
import sys
from itertools import combinations

import numpy as np
import pandas as pd
from scipy.stats import fisher_exact, mannwhitneyu


def wilson(k, n, z=1.96):
    if n == 0:
        return (np.nan, np.nan)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def entropy_bits(counts):
    c = np.asarray(counts, dtype=float)
    if c.sum() == 0:
        return np.nan
    p = c[c > 0] / c.sum()
    return float(-(p * np.log2(p)).sum())


def holm(pvals):
    order = np.argsort(pvals)
    adj = np.empty(len(pvals))
    running = 0.0
    for rank, i in enumerate(order):
        running = max(running, (len(pvals) - rank) * pvals[i])
        adj[i] = min(1.0, running)
    return adj


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("patterns", nargs="*", default=["matches_*.csv"])
    ap.add_argument("--decisions", nargs="*", default=[], help="MatchRunner의 decisions_*.csv")
    ap.add_argument("--human", help="사람 데이터 CombatLogs 폴더 (사람다움 비교 기준)")
    ap.add_argument("--range", type=float, default=4.5, help="사거리 안 결정으로 볼 최대 거리 (m)")
    ap.add_argument("--boot", type=int, default=2000)
    args = ap.parse_args()
    patterns = args.patterns
    files = sorted({f for p in patterns for f in glob.glob(p)})
    if not files:
        raise SystemExit("결과 CSV가 없습니다: " + " ".join(patterns))
    df = pd.concat([pd.read_csv(f) for f in files], ignore_index=True)
    cnt_cols = [c for c in df.columns if c.startswith("cnt_")]
    df["entropy"] = df[cnt_cols].apply(entropy_bits, axis=1)
    df["entropy_norm"] = df["entropy"] / np.log2(len(cnt_cols))
    combat_cols = [c for c in cnt_cols if c != "cnt_Move"]
    df["entropy_combat"] = df[combat_cols].apply(entropy_bits, axis=1)
    df["entropy_combat_norm"] = df["entropy_combat"] / np.log2(len(combat_cols))
    df["win"] = (df["outcome"] == "win").astype(int)

    print("파일 %d개, 경기 %d개\n" % (len(files), len(df)))
    print("| 조건 | N | 승률 [95% CI] | 무 | 패 | 엔트로피(bits) | 정규화 엔트로피 | 전투 엔트로피(Move 제외) | 정규화 전투 엔트로피 | 명중 | 피격 | 시간(s) | 지연 ms | FPS | 1% low | fallback |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    conds = list(dict.fromkeys(df["condition"]))
    for c in conds:
        d = df[df["condition"] == c]
        n = len(d)
        w = d["win"].sum()
        lo, hi = wilson(w, n)
        ms = lambda col, f="%.2f": (f + " ± " + f) % (d[col].mean(), d[col].std(ddof=1))
        print("| %s | %d | %.1f%% [%.1f–%.1f] | %.1f%% | %.1f%% | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %d |" % (
            c, n, 100 * w / n, 100 * lo, 100 * hi,
            100 * (d["outcome"] == "draw").mean(), 100 * (d["outcome"] == "loss").mean(),
            ms("entropy"), ms("entropy_norm", "%.3f"),
            ms("entropy_combat"), ms("entropy_combat_norm", "%.3f"), ms("npc_hits"), ms("opp_hits"),
            ms("duration_s", "%.1f"), ms("latency_mean_ms", "%.3f"), ms("fps_mean", "%.0f"),
            ms("fps_p1", "%.0f"), d["fallbacks"].sum()))

    print("\n행동 분포 (조건별 비율 %)")
    dist = df.groupby("condition")[cnt_cols].sum()
    dist = (100 * dist.div(dist.sum(axis=1), axis=0)).round(1)
    dist.columns = [c[4:] for c in cnt_cols]
    print(dist.loc[conds].to_string())

    if df["run"].nunique() > 1:
        print("\n반복 실험(run)별 승률 → 평균 ± SD")
        per_run = df.groupby(["condition", "run"])["win"].mean().unstack()
        print((100 * per_run.loc[conds]).round(1).to_string())
        agg = per_run.agg(["mean", "std"], axis=1) * 100
        for c in conds:
            print("  %s: %.1f%% ± %.1f%% (runs=%d)" % (c, agg.loc[c, "mean"], agg.loc[c, "std"], per_run.shape[1]))

    print("\n조건 간 비교 (Holm 보정)")
    tests = []
    for a, b in combinations(conds, 2):
        da, db = df[df["condition"] == a], df[df["condition"] == b]
        table = [[da["win"].sum(), len(da) - da["win"].sum()], [db["win"].sum(), len(db) - db["win"].sum()]]
        tests.append(("승률", a, b, fisher_exact(table)[1]))
        tests.append(("엔트로피", a, b, mannwhitneyu(da["entropy"].dropna(), db["entropy"].dropna())[1]))
        tests.append(("전투 엔트로피", a, b, mannwhitneyu(da["entropy_combat"].dropna(), db["entropy_combat"].dropna())[1]))
        tests.append(("명중 수", a, b, mannwhitneyu(da["npc_hits"], db["npc_hits"])[1]))
    adj = holm(np.array([t[3] for t in tests]))
    print("| 지표 | 비교 | p | p (Holm) |")
    print("|---|---|---|---|")
    for (m, a, b, p), pa in zip(tests, adj):
        print("| %s | %s vs %s | %.4g | %.4g |" % (m, a, b, p, pa))

    if args.decisions and args.human:
        human_likeness(args)


ACTIONS = ["Idle", "Move", "Slash", "Slash2", "Slash3", "Block", "CrouchBlock", "Jump", "Crouch", "CrouchSlash"]
SLASHES = {"Slash", "Slash2", "Slash3"}


def npc_label(row):
    # 사람 결정 표본(train.py build_decisions)과 같은 의미로 변환
    if row["in_combo"] == 1:   # 콤보 예약 기회: 공격을 고르면 다음 단계, 아니면 콤보 종료(Idle)
        if row["chosen"] in SLASHES:
            return "Slash2" if row["self_action"] == "Slash" else "Slash3"
        return "Idle"
    return "Slash" if row["chosen"] in SLASHES else row["chosen"]   # 한가할 때 Slash2/3 요청은 Slash로 실행됨


def dist_vec(labels):
    c = pd.Series(labels).value_counts().reindex(ACTIONS, fill_value=0).to_numpy(float)
    return c / c.sum() if c.sum() else c


def js(p, q):
    m = 0.5 * (p + q)
    def kl(a, b):
        mask = a > 0
        return float((a[mask] * np.log2(a[mask] / b[mask])).sum())
    return 0.5 * kl(p, m) + 0.5 * kl(q, m)


def human_likeness(args):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from train import build_decisions, ACTIONS as TRAIN_ACTIONS

    hfiles = sorted(glob.glob(os.path.join(args.human, "*.csv")))
    hu = build_decisions(pd.concat([pd.read_csv(f) for f in hfiles], ignore_index=True).dropna(), 3)
    hu = hu[hu["dist"] < args.range]
    h = dist_vec([TRAIN_ACTIONS[a] for a in hu["action"]])

    dfiles = sorted({f for p in args.decisions for f in glob.glob(p)})
    dec = pd.concat([pd.read_csv(f) for f in dfiles], ignore_index=True)
    dec = dec[dec["dist"] < args.range].copy()
    dec["label"] = dec.apply(npc_label, axis=1)

    print("\n사람다움: 사거리(<%.1f m) 안 결정 시점 행동 분포 vs 사람 (Jensen–Shannon divergence, 0=같음 1=완전히 다름)" % args.range)
    print("사람 기준: 결정 표본 %d개 (파일 %d개)" % (len(hu), len(hfiles)))
    rng = np.random.default_rng(0)
    conds = list(dict.fromkeys(dec["condition"]))
    boots = {}
    print("| 조건 | 결정 수 | 경기 수 | JS divergence [95% CI] |")
    print("|---|---|---|---|")
    for c in conds:
        d = dec[dec["condition"] == c]
        keys = d.groupby(["run", "match"]).groups
        groups = [d.loc[ix, "label"].to_numpy() for ix in keys.values()]
        point = js(dist_vec(d["label"]), h)
        b = np.array([js(dist_vec(np.concatenate([groups[i] for i in rng.integers(0, len(groups), len(groups))])), h)
                      for _ in range(args.boot)])
        boots[c] = b
        print("| %s | %d | %d | %.3f [%.3f–%.3f] |" % (c, len(d), len(groups), point, *np.percentile(b, [2.5, 97.5])))

    print("\n조건 간 차이 (부트스트랩, 95% CI가 0을 포함하지 않으면 유의)")
    for a, b in combinations(conds, 2):
        diff = boots[a] - boots[b]
        lo, hi = np.percentile(diff, [2.5, 97.5])
        print("  %s − %s: %.3f [%.3f–%.3f]%s" % (a, b, diff.mean(), lo, hi, "  *" if lo > 0 or hi < 0 else ""))

    if dec["run"].nunique() > 1:
        print("\n반복 실험(run)별 사람다움 JS divergence → 평균 ± SD")
        per_run = dec.groupby(["condition", "run"])["label"].apply(lambda s: js(dist_vec(s), h)).unstack()
        print(per_run.loc[conds].round(3).to_string())
        for c in conds:
            print("  %s: %.3f ± %.3f (runs=%d)" % (c, per_run.loc[c].mean(), per_run.loc[c].std(ddof=1), per_run.shape[1]))

    print("\n행동 분포 (%)")
    tab = pd.DataFrame({c: dist_vec(dec.loc[dec["condition"] == c, "label"]) * 100 for c in conds}, index=ACTIONS)
    tab["사람"] = h * 100
    print(tab.round(1).to_string())


if __name__ == "__main__":
    main()
