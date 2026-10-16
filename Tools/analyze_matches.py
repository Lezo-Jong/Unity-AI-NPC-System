"""
MatchRunner 결과(matches_*.csv) 분석: 리뷰어 코멘트 1·2 대응용 지표.

- 승/무/패율 + Wilson 95% CI
- 행동 엔트로피 (Shannon, bits; 10개 행동 → 최대 log2(10)=3.32), 정규화 엔트로피
- 전투 행동 엔트로피: Move 제외 9개 행동 (최대 log2(9)=3.17). 접근(Move) 시간이 전체 엔트로피를 좌우하므로 함께 보고
- 경기 시간, 명중 수, 결정 지연(ms), FPS, fallback 횟수
- 조건 간 비교: 승률 Fisher exact test, 엔트로피/명중 Mann–Whitney U, Holm 보정
- run(반복 실험)별 승률 → 반복 간 평균 ± SD

사용 예:
  python Tools/analyze_matches.py "%USERPROFILE%/AppData/LocalLow/pvp/pvp/MatchLogs/matches_*.csv"
"""
import glob
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
    patterns = sys.argv[1:] or ["matches_*.csv"]
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


if __name__ == "__main__":
    main()
