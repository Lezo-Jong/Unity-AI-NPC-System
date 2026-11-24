"""
학습에 쓰지 않은 세션으로 평가 (리뷰어 3: 순환 평가, 리뷰어 2 minor 5: 세션 단위 분할, Major 3: 확률 지표).

세션 하나를 빼고(leave-one-session-out, 7개 세션) 나머지로 학습한 뒤, 빼 둔 세션의 사람 결정을
모델이 얼마나 잘 설명하는지 확률 지표로 봅니다. NPC는 softmax에서 행동을 뽑으므로
argmax 지표(macro-F1)보다 확률 지표가 실제 정책과 맞습니다.

비교 정책 (모두 학습 세션만으로 추정):
- Uniform        : 10개 행동 균등
- FM             : 상태를 보지 않는 빈도 일치. 결정 유형(한가함 / Slash 중 / Slash2 중) × 사거리 안팎별 빈도
- LogReg         : 다항 로지스틱 회귀 (19개 입력 표준화)
- MLP-sqrt       : 논문 모델과 같은 설정 (sqrt 역빈도 클래스 가중치)
- MLP-sqrt+LA    : 같은 모델, 추론 때 logit − log w_c (Unity의 MLAdj)
- MLP-none       : 클래스 가중치 없이 학습

지표 (빼 둔 세션 기준, 세션 평균 ± 세션 간 95% CI):
- NLL (bits/결정), Brier, top-1 / top-3 정확도
- 사거리(<4.5 m) 안: 기대 행동 분포 vs 실제 분포 JS, (상대 공격 중 × 거리 구간) 조건부 JS,
  방어 반응도 P(방어|상대 공격) − P(방어|아님)의 예측값 vs 실제값

사용 예: python Tools/heldout_eval.py --data data/human --seeds 0 1 2 3 4
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd
import torch
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from train import ACTIONS, FEATURES, build_decisions, fit  # noqa: E402

K = len(ACTIONS)
DEF = [ACTIONS.index("Block"), ACTIONS.index("CrouchBlock")]
EPS = 1e-6
DIST_BINS = [0, 3.2, 3.6, 4.5]


def class_log_weights(y, mode):
    counts = np.bincount(y, minlength=K).astype(float)
    w = np.where(counts > 0, counts.sum() / (K * np.maximum(counts, 1)), 0.0)
    if mode == "sqrt":
        w = np.sqrt(w)
    return np.log(np.maximum(w, EPS))


def softmax(z):
    z = z - z.max(1, keepdims=True)
    e = np.exp(z)
    return e / e.sum(1, keepdims=True)


def js(p, q):
    p = p / p.sum(); q = q / q.sum(); m = 0.5 * (p + q)
    def kl(a, b):
        k = a > 0
        return float((a[k] * np.log2(a[k] / b[k])).sum())
    return 0.5 * kl(p, m) + 0.5 * kl(q, m)


def dtype_key(df, rng_m):
    # 결정 유형: 0 = 한가함, 1 = Slash 중(콤보 예약), 2 = Slash2 중 / 사거리 안팎
    cur = df["cur_action"].to_numpy()
    t = np.where(cur == ACTIONS.index("Slash"), 1, np.where(cur == ACTIONS.index("Slash2"), 2, 0))
    return t * 2 + (df["dist"].to_numpy() < rng_m).astype(int)


def metrics(P, y, te, rng_m):
    n = len(y)
    py = np.clip(P[np.arange(n), y], EPS, 1)
    onehot = np.eye(K)[y]
    out = {
        "NLL_bits": float(-np.log2(py).mean()),
        "Brier": float(((P - onehot) ** 2).sum(1).mean()),
        "top1": float((P.argmax(1) == y).mean()),
        "top3": float((np.argsort(-P, 1)[:, :3] == y[:, None]).any(1).mean()),
    }
    inr = te["dist"].to_numpy() < rng_m
    Pi, yi, ti = P[inr], y[inr], te[inr]
    actual = np.bincount(yi, minlength=K).astype(float)
    out["JS_inrange"] = js(Pi.mean(0), actual)
    oa = ti["opp_attacking"].to_numpy().astype(int)
    b = pd.cut(ti["dist"], DIST_BINS).astype(str).to_numpy()
    key = np.char.add(oa.astype(str), b.astype(str))
    s = t = 0.0
    for k in np.unique(key):
        m = key == k
        if m.sum() < 20:
            continue
        s += m.sum() * js(Pi[m].mean(0), np.bincount(yi[m], minlength=K).astype(float)); t += m.sum()
    out["condJS_inrange"] = s / t
    free = ti["cur_action"].isin([0, 1, 8]).to_numpy()
    def react(defprob):
        a = defprob[free & (oa == 1)].mean(); na = defprob[free & (oa == 0)].mean(); return a - na
    out["react_pred"] = float(react(Pi[:, DEF].sum(1)))
    out["react_true"] = float(react(np.isin(yi, DEF).astype(float)))
    return out


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/human")
    ap.add_argument("--seeds", type=int, nargs="+", default=[0, 1, 2, 3, 4])
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--range", type=float, default=4.5)
    ap.add_argument("--out", default="Tools/results/heldout_eval.csv")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.data, "*.csv")))
    raw = pd.concat([pd.read_csv(f) for f in files], ignore_index=True).dropna(subset=FEATURES + ["action"])
    raw["cur_action"] = raw["action"]
    df = build_decisions(raw, 3)
    df["session"] = df["session_id"].str.extract(r"^(P\d+_S\d+)")[0]
    sessions = sorted(df["session"].unique())
    print("결정 표본 %d개, 세션 %d개: %s" % (len(df), len(sessions), ", ".join(sessions)))

    rows = []
    for s in sessions:
        tr, te = df[df["session"] != s], df[df["session"] == s]
        xtr, ytr = tr[FEATURES].to_numpy(np.float32), tr["action"].to_numpy(np.int64)
        xte, yte = te[FEATURES].to_numpy(np.float32), te["action"].to_numpy(np.int64)
        preds = {"Uniform": np.full((len(te), K), 1.0 / K)}

        ktr, kte = dtype_key(tr, args.range), dtype_key(te, args.range)
        P = np.zeros((len(te), K))
        for k in np.unique(kte):
            c = np.bincount(ytr[ktr == k], minlength=K).astype(float) + 0.5   # 가산 평활
            P[kte == k] = c / c.sum()
        preds["FM"] = P

        sc = StandardScaler().fit(xtr)
        lr = LogisticRegression(max_iter=2000, C=1.0).fit(sc.transform(xtr), ytr)
        Plr = np.full((len(te), K), EPS); Plr[:, lr.classes_] = lr.predict_proba(sc.transform(xte))
        preds["LogReg"] = Plr / Plr.sum(1, keepdims=True)

        lw = class_log_weights(ytr, "sqrt")
        acc = {"MLP-sqrt": [], "MLP-sqrt+LA": [], "MLP-none": []}
        for seed in args.seeds:
            m = fit(xtr, ytr, seed, args.epochs, 1e-3, 128, 64, "sqrt")
            with torch.no_grad():
                z = m(torch.tensor(xte)).numpy().astype(float)
            acc["MLP-sqrt"].append(softmax(z)); acc["MLP-sqrt+LA"].append(softmax(z - lw))
            m0 = fit(xtr, ytr, seed, args.epochs, 1e-3, 128, 64, "none")
            with torch.no_grad():
                acc["MLP-none"].append(softmax(m0(torch.tensor(xte)).numpy().astype(float)))
        for name, Ps in acc.items():
            for seed, Pm in zip(args.seeds, Ps):
                rows.append(dict(session=s, policy=name, seed=seed, n=len(te), **metrics(Pm, yte, te, args.range)))
        for name, Pm in preds.items():
            rows.append(dict(session=s, policy=name, seed=-1, n=len(te), **metrics(Pm, yte, te, args.range)))
        print("  %s 완료 (평가 표본 %d)" % (s, len(te)))

    res = pd.DataFrame(rows)
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    res.to_csv(args.out, index=False)

    # 세션별 평균(시드 평균) → 세션 간 평균 ± t 기반 95% CI
    per = res.groupby(["policy", "session"]).mean(numeric_only=True).reset_index()
    cols = ["NLL_bits", "Brier", "top1", "top3", "JS_inrange", "condJS_inrange", "react_pred"]
    order = ["Uniform", "FM", "LogReg", "MLP-sqrt", "MLP-sqrt+LA", "MLP-none"]
    from scipy.stats import t as tdist, wilcoxon
    n = per["session"].nunique(); tq = tdist.ppf(0.975, n - 1)
    print("\n빼 둔 세션 평가 (세션 %d개 평균 ± 95%% CI, MLP는 시드 %d개 평균)" % (n, len(args.seeds)))
    print("| 정책 | " + " | ".join(cols) + " |")
    print("|---" * (len(cols) + 1) + "|")
    for p in order:
        d = per[per["policy"] == p]
        cells = ["%.3f ± %.3f" % (d[c].mean(), tq * d[c].std(ddof=1) / np.sqrt(n)) for c in cols]
        print("| %s | %s |" % (p, " | ".join(cells)))
    rt = per[per["policy"] == "FM"]["react_true"]
    print("| 사람(실제) | – | – | – | – | 0 | 0 | %.3f ± %.3f |" % (rt.mean(), tq * rt.std(ddof=1) / np.sqrt(n)))

    print("\n세션별 짝 비교 (MLP-sqrt+LA − FM), 7개 세션 중 MLP가 나은 세션 수 / Wilcoxon p")
    a = per[per["policy"] == "MLP-sqrt+LA"].set_index("session")
    b = per[per["policy"] == "FM"].set_index("session")
    for c, lower_better in [("NLL_bits", True), ("Brier", True), ("top1", False), ("JS_inrange", True), ("condJS_inrange", True)]:
        d = (a[c] - b[c]).to_numpy()
        better = int((d < 0).sum() if lower_better else (d > 0).sum())
        p = wilcoxon(d).pvalue if np.any(d != 0) else 1.0
        print("  %-15s 차이 평균 %+.3f, 더 나은 세션 %d/%d, p = %.3g" % (c, d.mean(), better, len(d), p))
    print("\n세션별 NLL (bits)")
    print(per.pivot(index="session", columns="policy", values="NLL_bits")[order].round(3).to_string())


if __name__ == "__main__":
    main()
