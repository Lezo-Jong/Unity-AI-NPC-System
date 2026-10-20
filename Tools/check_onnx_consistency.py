"""
Unity 런타임(Sentis)과 Python 참조 계산의 출력 일치 검증 (원고 3.2절).

data/runtime_check/ml_decisions_*.csv 는 MLBrain의 Log Decisions 옵션으로 Unity가 저장한 기록입니다.
각 행에는 그 순간의 입력 19개와 Unity가 계산한 Softmax 확률 10개(소수 넷째 자리까지 기록)가 들어 있습니다.
같은 입력을 Python에서 ONNX 참조 구현(onnx.reference)으로 다시 계산해 두 확률을 비교합니다.

사용 예:
  python Tools/check_onnx_consistency.py
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd
from onnx.reference import ReferenceEvaluator

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from train import ACTIONS, FEATURES  # noqa: E402


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--logs", default="data/runtime_check/ml_decisions_*.csv")
    ap.add_argument("--model", default="Assets/player_ai.onnx")
    args = ap.parse_args()

    files = sorted(glob.glob(args.logs))
    if not files:
        raise SystemExit("기록이 없습니다: " + args.logs)
    d = pd.concat([pd.read_csv(f) for f in files], ignore_index=True)

    logits = ReferenceEvaluator(args.model).run(None, {"input": d[FEATURES].to_numpy(np.float32)})[0].astype(np.float64)
    p = np.exp(logits - logits.max(1, keepdims=True))
    p /= p.sum(1, keepdims=True)
    unity = d[["p_" + a for a in ACTIONS]].to_numpy(np.float64)

    diff = np.abs(p - unity)
    print("파일 %d개, 결정 %d개" % (len(files), len(d)))
    print("최대 절대 차이 %.2e, 평균 %.2e" % (diff.max(), diff.mean()))
    print("(참고: Unity 기록은 소수 넷째 자리까지라 반올림만으로 최대 5.0e-5 차이가 생김. 나머지는 float32 계산 차이)")
    print("가장 높은 확률의 행동 일치율 %.4f" % (p.argmax(1) == unity.argmax(1)).mean())


if __name__ == "__main__":
    main()
