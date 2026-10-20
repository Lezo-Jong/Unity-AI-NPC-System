"""
NPC 행동 분류 MLP 학습 + 반복 평가 + ONNX 내보내기 (Unity Sentis 2.6, 패키지 com.unity.ai.inference용).

- 입력: CombatDataLogger가 저장한 CSV (19 features + action 라벨, 10 classes)
- 학습 표본 (--target decision, 기본값): NPC가 실제로 결정을 내리는 순간과 같은 조건으로 만듭니다.
    * 한가한 순간(Idle / Move / Crouch)의 상황 → 그 직후(--horizon 틱 이내) 새로 시작한 행동, 없으면 지금 행동 유지
    * Slash / Slash2가 시작된 순간의 상황 → 다음 콤보 단계로 이어졌으면 그 단계, 아니면 Idle(콤보 종료)
  같은 순간의 행동을 라벨로 쓰면(--target current) self_cur_* · self_speed 입력에 정답이 섞여
  "다음에 할 행동"이 아니라 "지금 하는 행동"을 배우게 되므로 기본값으로 쓰지 않습니다.
- 평가: 로그 파일 단위 GroupKFold(k) × 여러 시드 → accuracy / macro-F1 / balanced accuracy 평균 ± SD
        (같은 파일의 연속 프레임이 train/test에 동시에 들어가는 누수를 막기 위함)
        GroupKFold는 그룹(파일)을 크기 순서로 폴드에 배정하므로 분할은 모든 시드에서 같습니다.
        시드는 신경망 초기값과 미니배치 순서만 바꿉니다. 따라서 반복 실행들은 서로 독립이 아니고,
        보고하는 95% CI(평균 ± 1.96·SD/√n, 정규근사)는 근사값으로 해석해야 합니다.
- 내보내기: 정규화(mean/std)를 그래프 안에 포함, 입력 [batch,19] → 출력 [batch,10] logits

사용 예:
  python Tools/train.py --data "%USERPROFILE%/AppData/LocalLow/pvp/pvp/CombatLogs" \
      --out Assets/player_ai.onnx --folds 5 --seeds 0 1 2 3 4
"""
import argparse
import glob
import json
import os
import sys

import numpy as np
import pandas as pd
import torch
import torch.nn as nn
from sklearn.metrics import accuracy_score, balanced_accuracy_score, confusion_matrix, f1_score
from sklearn.model_selection import GroupKFold

# Unity의 CombatFeatures.Names / ActionClass 순서와 반드시 동일해야 합니다.
FEATURES = [
    "dist", "rel_x", "rel_z", "self_hp", "opp_hp", "self_speed", "opp_speed",
    "closing_speed", "opp_attacking", "opp_blocking", "opp_crouching", "opp_jumping",
    "self_facing", "opp_facing", "self_t_since_atk", "opp_t_since_atk",
    "self_cur_attack", "self_cur_defense", "self_cur_move",
]
ACTIONS = ["Idle", "Move", "Slash", "Slash2", "Slash3", "Block",
           "CrouchBlock", "Jump", "Crouch", "CrouchSlash"]


class PolicyMLP(nn.Module):
    def __init__(self, mean, std, hidden=64, dropout=0.1):
        super().__init__()
        self.register_buffer("mean", torch.tensor(mean, dtype=torch.float32))
        self.register_buffer("std", torch.tensor(std, dtype=torch.float32))
        self.net = nn.Sequential(
            nn.Linear(len(FEATURES), hidden), nn.ReLU(), nn.Dropout(dropout),
            nn.Linear(hidden, hidden), nn.ReLU(), nn.Dropout(dropout),
            nn.Linear(hidden, len(ACTIONS)),
        )

    def forward(self, x):
        return self.net((x - self.mean) / self.std)


def load(data_dir):
    files = sorted(glob.glob(os.path.join(data_dir, "*.csv")))
    if not files:
        raise SystemExit("CSV가 없습니다: " + data_dir)
    df = pd.concat([pd.read_csv(f) for f in files], ignore_index=True)
    df = df.dropna(subset=FEATURES + ["action"])
    return df, files


FREE = {0, 1, 8}          # Idle, Move, Crouch: 다음 행동을 고를 수 있는 상태
COMBO = {2: 3, 3: 4}      # Slash → Slash2, Slash2 → Slash3
MAX_GAP = 0.25            # 이보다 긴 시간 간격(사망·부활)은 연속 구간으로 보지 않음


def build_decisions(df, horizon):
    rows, labels = [], []
    for _, g in df.groupby("session_id", sort=False):
        g = g.sort_values("time")
        a = g["action"].to_numpy(np.int64)
        t = g["time"].to_numpy(np.float64)
        idx = g.index.to_numpy()
        n = len(a)
        for i in range(n - 1):
            if t[i + 1] - t[i] > MAX_GAP:
                continue
            if a[i] in FREE:
                label = a[i + 1]
                for j in range(i + 1, min(n, i + 1 + horizon)):
                    if t[j] - t[j - 1] > MAX_GAP:
                        break
                    if a[j] != a[i]:
                        label = a[j]
                        break
            elif a[i] in COMBO and (i == 0 or a[i - 1] != a[i]):
                e = i
                while e + 1 < n and a[e + 1] == a[i] and t[e + 1] - t[e] <= MAX_GAP:
                    e += 1
                follows = e + 1 < n and t[e + 1] - t[e] <= MAX_GAP and a[e + 1] == COMBO[a[i]]
                label = COMBO[a[i]] if follows else 0
            else:
                continue
            rows.append(idx[i])
            labels.append(label)
    out = df.loc[rows].copy()
    out["action"] = labels
    return out


def set_seed(seed):
    np.random.seed(seed)
    torch.manual_seed(seed)


def fit(x, y, seed, epochs, lr, batch, hidden, class_weight="sqrt"):
    set_seed(seed)
    mean = x.mean(0)
    std = x.std(0)
    std[std < 1e-6] = 1.0
    model = PolicyMLP(mean, std, hidden)
    # 클래스 불균형 보정 (Idle/Move가 대부분을 차지)
    counts = np.bincount(y, minlength=len(ACTIONS)).astype(np.float32)
    w = np.where(counts > 0, counts.sum() / (len(ACTIONS) * np.maximum(counts, 1)), 0.0)
    if class_weight == "sqrt":
        w = np.sqrt(w)
    elif class_weight == "none":
        w = (counts > 0).astype(np.float32)
    loss_fn = nn.CrossEntropyLoss(weight=torch.tensor(w, dtype=torch.float32))
    opt = torch.optim.Adam(model.parameters(), lr=lr, weight_decay=1e-4)

    xt = torch.tensor(x, dtype=torch.float32)
    yt = torch.tensor(y, dtype=torch.long)
    g = torch.Generator().manual_seed(seed)
    model.train()
    for _ in range(epochs):
        perm = torch.randperm(len(xt), generator=g)
        for i in range(0, len(xt), batch):
            idx = perm[i:i + batch]
            opt.zero_grad()
            loss_fn(model(xt[idx]), yt[idx]).backward()
            opt.step()
    model.eval()
    return model


def predict(model, x):
    with torch.no_grad():
        return model(torch.tensor(x, dtype=torch.float32)).argmax(1).numpy()


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", default="Assets/player_ai.onnx")
    ap.add_argument("--report", default="Tools/results/train_report.json")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seeds", type=int, nargs="+", default=[0, 1, 2, 3, 4])
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--batch", type=int, default=128)
    ap.add_argument("--hidden", type=int, default=64)
    ap.add_argument("--class-weight", choices=["balanced", "sqrt", "none"], default="sqrt",
                    help="클래스 불균형 보정: balanced(빈도 역수), sqrt(그 제곱근, 약하게), none(보정 없음)")
    ap.add_argument("--target", choices=["decision", "current"], default="decision")
    ap.add_argument("--horizon", type=int, default=3, help="한가한 순간 이후 새 행동을 찾을 틱 수 (10 Hz 기준 3 = 0.3초)")
    args = ap.parse_args()

    df, files = load(args.data)
    n_raw = len(df)
    if args.target == "decision":
        df = build_decisions(df, args.horizon)
        print("결정 시점 표본: 원본 %d행 → %d개 (horizon %d틱)" % (n_raw, len(df), args.horizon))
    x = df[FEATURES].to_numpy(np.float32)
    y = df["action"].to_numpy(np.int64)
    groups = df["session_id"].to_numpy()   # session_id는 로그 파일마다 고유 → 그룹 = 로그 파일
    n_files = len(np.unique(groups))
    folds = min(args.folds, n_files)
    if folds < 2:
        raise SystemExit("로그 파일이 %d개뿐입니다. 교차검증에는 최소 2개가 필요합니다." % n_files)

    print("파일 %d개, 샘플 %d개, 참가자 %d명" % (n_files, len(df), df["participant_id"].nunique()))
    print("클래스 분포:", {ACTIONS[i]: int(c) for i, c in enumerate(np.bincount(y, minlength=len(ACTIONS)))})

    rows = []
    cm_total = np.zeros((len(ACTIONS), len(ACTIONS)), dtype=np.int64)
    # 분할은 고정(GroupKFold는 크기 순서 배정), 시드는 모델 초기값과 미니배치 순서만 바꿈
    splits = list(GroupKFold(n_splits=folds).split(x, y, groups))
    for seed in args.seeds:
        for k, (tr, te) in enumerate(splits):
            model = fit(x[tr], y[tr], seed, args.epochs, args.lr, args.batch, args.hidden, args.class_weight)
            pred = predict(model, x[te])
            majority = np.bincount(y[tr], minlength=len(ACTIONS)).argmax()
            rows.append({
                "seed": seed, "fold": k, "n_test": int(len(te)),
                "accuracy": accuracy_score(y[te], pred),
                "macro_f1": f1_score(y[te], pred, average="macro", labels=np.unique(y[te]), zero_division=0),
                "balanced_acc": balanced_accuracy_score(y[te], pred),
                "majority_baseline_acc": float((y[te] == majority).mean()),
                "majority_baseline_balanced_acc": balanced_accuracy_score(y[te], np.full(len(te), majority)),
                "majority_baseline_macro_f1": f1_score(y[te], np.full(len(te), majority), average="macro",
                                                       labels=np.unique(y[te]), zero_division=0),
            })
            cm_total += confusion_matrix(y[te], pred, labels=range(len(ACTIONS)))
            print("seed %d fold %d: acc=%.4f macroF1=%.4f (majority=%.4f)" %
                  (seed, k, rows[-1]["accuracy"], rows[-1]["macro_f1"], rows[-1]["majority_baseline_acc"]))

    res = pd.DataFrame(rows)
    summary = {
        "n_runs": len(res), "folds": folds, "seeds": args.seeds,
        "n_samples": int(len(df)), "n_log_files": int(n_files),
        "split": "GroupKFold by log file; identical partitions for every seed (seeds vary initialization and batch order)",
        "ci95_method": "mean ± 1.96·SD/sqrt(n_runs), normal approximation; runs share partitions, so approximate",
        "n_participants": int(df["participant_id"].nunique()),
        "class_weight": args.class_weight, "target": args.target, "horizon": args.horizon, "n_raw_rows": int(n_raw),
    }
    for col in ["accuracy", "balanced_acc", "macro_f1", "majority_baseline_acc", "majority_baseline_balanced_acc",
                "majority_baseline_macro_f1"]:
        m, s = res[col].mean(), res[col].std(ddof=1)
        ci = 1.96 * s / np.sqrt(len(res))
        summary[col] = {"mean": m, "sd": s, "ci95": [m - ci, m + ci]}
        print("%-28s %.4f ± %.4f (95%% CI %.4f–%.4f)" % (col, m, s, m - ci, m + ci))
    below = int((res["accuracy"] < res["majority_baseline_acc"]).sum())
    summary["runs_accuracy_below_majority"] = below
    print("accuracy가 다수 클래스 기준보다 낮은 실행: %d / %d" % (below, len(res)))
    summary["confusion_matrix"] = {"labels": ACTIONS, "counts": cm_total.tolist()}

    os.makedirs(os.path.dirname(args.report), exist_ok=True)
    res.to_csv(os.path.splitext(args.report)[0] + "_runs.csv", index=False)
    with open(args.report, "w", encoding="utf-8") as fp:
        json.dump(summary, fp, indent=2, ensure_ascii=False)

    # 최종 모델: 전체 데이터, 첫 번째 시드
    final = fit(x, y, args.seeds[0], args.epochs, args.lr, args.batch, args.hidden, args.class_weight)
    torch.onnx.export(
        final, torch.zeros(1, len(FEATURES)), args.out,
        input_names=["input"], output_names=["output"],
        dynamic_axes={"input": {0: "batch_size"}, "output": {0: "batch_size"}},
        opset_version=15, dynamo=False,
    )
    print("ONNX 저장:", args.out, "(입력 [batch,%d] → 출력 [batch,%d])" % (len(FEATURES), len(ACTIONS)))


if __name__ == "__main__":
    main()
