"""
NPC 행동 분류 MLP 학습 + 반복 평가 + ONNX 내보내기 (Unity Inference Engine용).

- 입력: CombatDataLogger가 저장한 CSV (19 features + action 라벨, 10 classes)
- 평가: 세션 단위 GroupKFold(k) × 여러 시드 → accuracy / macro-F1 평균 ± SD
        (같은 세션의 연속 프레임이 train/test에 동시에 들어가는 누수를 막기 위함)
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
from sklearn.metrics import accuracy_score, confusion_matrix, f1_score
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


def set_seed(seed):
    np.random.seed(seed)
    torch.manual_seed(seed)


def fit(x, y, seed, epochs, lr, batch, hidden):
    set_seed(seed)
    mean = x.mean(0)
    std = x.std(0)
    std[std < 1e-6] = 1.0
    model = PolicyMLP(mean, std, hidden)
    # 클래스 불균형 보정 (Idle/Move가 대부분을 차지)
    counts = np.bincount(y, minlength=len(ACTIONS)).astype(np.float32)
    w = np.where(counts > 0, counts.sum() / (len(ACTIONS) * np.maximum(counts, 1)), 0.0)
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
    args = ap.parse_args()

    df, files = load(args.data)
    x = df[FEATURES].to_numpy(np.float32)
    y = df["action"].to_numpy(np.int64)
    groups = df["session_id"].to_numpy()
    n_sessions = len(np.unique(groups))
    folds = min(args.folds, n_sessions)
    if folds < 2:
        raise SystemExit("세션이 %d개뿐입니다. 교차검증에는 최소 2개 세션이 필요합니다." % n_sessions)

    print("파일 %d개, 샘플 %d개, 세션 %d개, 참가자 %d명" %
          (len(files), len(df), n_sessions, df["participant_id"].nunique()))
    print("클래스 분포:", {ACTIONS[i]: int(c) for i, c in enumerate(np.bincount(y, minlength=len(ACTIONS)))})

    rows = []
    cm_total = np.zeros((len(ACTIONS), len(ACTIONS)), dtype=np.int64)
    for seed in args.seeds:
        # 시드마다 세션→fold 배정을 섞어 fold 분할 자체의 변동도 반영
        rng = np.random.RandomState(seed)
        uniq = np.unique(groups)
        shuffled = dict(zip(uniq, rng.permutation(len(uniq))))
        g_shuffled = np.array([shuffled[s] for s in groups])
        for k, (tr, te) in enumerate(GroupKFold(n_splits=folds).split(x, y, g_shuffled)):
            model = fit(x[tr], y[tr], seed, args.epochs, args.lr, args.batch, args.hidden)
            pred = predict(model, x[te])
            majority = np.bincount(y[tr], minlength=len(ACTIONS)).argmax()
            rows.append({
                "seed": seed, "fold": k, "n_test": int(len(te)),
                "accuracy": accuracy_score(y[te], pred),
                "macro_f1": f1_score(y[te], pred, average="macro", labels=np.unique(y[te]), zero_division=0),
                "majority_baseline_acc": float((y[te] == majority).mean()),
            })
            cm_total += confusion_matrix(y[te], pred, labels=range(len(ACTIONS)))
            print("seed %d fold %d: acc=%.4f macroF1=%.4f (majority=%.4f)" %
                  (seed, k, rows[-1]["accuracy"], rows[-1]["macro_f1"], rows[-1]["majority_baseline_acc"]))

    res = pd.DataFrame(rows)
    summary = {
        "n_runs": len(res), "folds": folds, "seeds": args.seeds,
        "n_samples": int(len(df)), "n_sessions": int(n_sessions),
        "n_participants": int(df["participant_id"].nunique()),
    }
    for col in ["accuracy", "macro_f1", "majority_baseline_acc"]:
        m, s = res[col].mean(), res[col].std(ddof=1)
        ci = 1.96 * s / np.sqrt(len(res))
        summary[col] = {"mean": m, "sd": s, "ci95": [m - ci, m + ci]}
        print("%-22s %.4f ± %.4f (95%% CI %.4f–%.4f)" % (col, m, s, m - ci, m + ci))
    summary["confusion_matrix"] = {"labels": ACTIONS, "counts": cm_total.tolist()}

    os.makedirs(os.path.dirname(args.report), exist_ok=True)
    res.to_csv(os.path.splitext(args.report)[0] + "_runs.csv", index=False)
    with open(args.report, "w", encoding="utf-8") as fp:
        json.dump(summary, fp, indent=2, ensure_ascii=False)

    # 최종 모델: 전체 데이터, 첫 번째 시드
    final = fit(x, y, args.seeds[0], args.epochs, args.lr, args.batch, args.hidden)
    torch.onnx.export(
        final, torch.zeros(1, len(FEATURES)), args.out,
        input_names=["input"], output_names=["output"],
        dynamic_axes={"input": {0: "batch_size"}, "output": {0: "batch_size"}},
        opset_version=15, dynamo=False,
    )
    print("ONNX 저장:", args.out, "(입력 [batch,%d] → 출력 [batch,%d])" % (len(FEATURES), len(ACTIONS)))


if __name__ == "__main__":
    main()
