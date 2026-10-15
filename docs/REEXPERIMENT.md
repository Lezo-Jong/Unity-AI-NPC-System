# 재실험 가이드 (Unity 6.3 LTS + Inference Engine)

리뷰어 코멘트 대응을 위한 재실험 절차입니다.

## 0. 이번에 바뀐 것

| 항목 | 이전 | 이후 |
|---|---|---|
| 에디터 | 6000.1.0f1 | **6000.3.26f1 (Unity 6.3 LTS)** |
| 추론 패키지 | `com.unity.barracuda` 3.0 (지원 종료) | **`com.unity.ai.inference` 2.6.1** (Sentis의 새 이름, Unity 6.3 공식 지원) |
| 스크립트 | `NNModel`, `IWorker`, `WorkerFactory` | `ModelAsset`, `Worker`, `BackendType` (`MLBrain.cs`) |
| 모델 출력 | 14개 뉴런 (기존 `player_ai.onnx`) | **10개 뉴런 = 행동 클래스 10개** (`Tools/train.py`로 재학습) |

> 논문에서는 "Barracuda" 대신 **"Unity Inference Engine 2.6.1 (formerly Sentis)"**라고 쓰는 것이 정확합니다.

## 1. Unity에서 할 일

1. 에디터가 다시 컴파일한 뒤 Console에 에러가 없는지 확인합니다.
2. `Assets/player_ai.onnx`를 선택했을 때 Inspector에 Inference Engine 모델 정보(입력 `[batch,19]`, 출력 `[batch,10]`)가 보여야 합니다. 안 보이면 우클릭 → **Reimport**.
3. 모델 참조 다시 연결: 타입이 `NNModel` → `ModelAsset`으로 바뀌었기 때문에 `MLBrain`의 **Model Asset** 칸에 연결합니다. `player_ai.onnx`를 다시 드래그하세요.
4. `MLBrain`의 **Backend Type**은 `CPU`(기본값)로 둡니다. 19→64→64→10 크기의 작은 MLP는 CPU(Burst)가 GPU보다 지연이 낮습니다.

## 2. 데이터 수집 (코멘트 2·3 대응)

현재 `CombatLogs`에 있는 데이터는 821행이고 Idle/Move 두 클래스만 있어서 학습에 쓸 수 없습니다. 다시 수집해야 합니다.

- 플레이어 오브젝트에 `CombatDataLogger`를 붙이고 참가자마다 `participantId`(P01, P02, …)와 `sessionIndex`를 바꿔 가며 플레이합니다.
- 권장 규모: **참가자 5명 이상 × 세션 3개 이상**. 10개 행동이 모두 충분히 나오도록 플레이해 주세요(클래스별 최소 수백 행).
- 저장 위치: `%USERPROFILE%\AppData\LocalLow\pvp\pvp\CombatLogs\`

## 3. 학습 + 반복 평가 (코멘트 2·3 대응)

```bash
python Tools/train.py --data "C:/Users/User/AppData/LocalLow/pvp/pvp/CombatLogs" --out Assets/player_ai.onnx --folds 5 --seeds 0 1 2 3 4
```

- **세션 단위 5-fold 교차검증 × 5개 시드 = 25회** 평가 → accuracy, macro-F1의 평균 ± SD와 95% CI를 출력합니다. 단일 실험 수치 대신 이 값을 논문에 보고하세요.
- 다수 클래스 baseline 정확도도 함께 출력되므로 "모델이 단순히 Idle만 찍는 것보다 낫다"를 보일 수 있습니다.
- 결과: `Tools/results/train_report.json`(요약 + confusion matrix), `train_report_runs.csv`(25회 각각의 결과)
- 출력 뉴런은 10개로 고정되므로 Table 1(출력층 = 10)과 본문의 "10 distinct action classes"가 일치합니다.

## 4. 대전 실험 (코멘트 1 대응)

1. 실험용 씬에 NPC 오브젝트(`RandomBrain`, `FSMBrain`, `MLBrain`, `NpcActionExecutor`, `CombatAgentState`, `HealthSystem`)와 상대 오브젝트(`ReferenceBotBrain` 등)를 두고 `MatchRunner`를 연결합니다.
2. `matchesPerCondition = 100`, `timeScale = 1`로 실행합니다. 같은 판 번호는 모든 조건에 같은 시드를 쓰므로 공정한 비교가 됩니다.
3. 반복 실험: `runLabel`과 `baseSeed`를 바꿔 **최소 3번** 실행합니다(예: `run1`/1000, `run2`/2000, `run3`/3000).
4. 결과: `%USERPROFILE%\AppData\LocalLow\pvp\pvp\MatchLogs\matches_*.csv`

```bash
python Tools/analyze_matches.py "C:/Users/User/AppData/LocalLow/pvp/pvp/MatchLogs/matches_*.csv"
```

출력되는 항목:
- **승률 [Wilson 95% CI]**, 무승부율, 패배율 — 코멘트 1의 "win rate"
- **행동 엔트로피**(bits, 최대 log2 10 = 3.32)와 정규화 엔트로피, 조건별 행동 분포 — "behavioral entropy"
- Random / FSM / ML **baseline 비교**: 승률은 Fisher exact test, 엔트로피와 명중 수는 Mann–Whitney U, Holm 보정 — "baseline comparison"
- run별 승률의 평균 ± SD — 반복 실험 간 일관성
- 결정 지연(ms), FPS / 1% low, fallback 횟수 — 실시간성 근거

## 5. (권장) 사용자 평가

코멘트 1은 user study도 언급합니다. 소규모라도 넣으면 좋습니다.
- 참가자 N명이 Random / FSM / ML NPC와 **순서를 바꿔 가며(counterbalanced)** 대전합니다. 상대가 어느 조건인지는 알려 주지 않습니다(blind).
- 대전마다 5~7점 리커트 척도로 "지능적으로 느껴졌다 / 예측하기 어려웠다 / 재미있었다"를 묻습니다.
- 분석은 Friedman test와 Wilcoxon 사후검정(Holm 보정)으로 합니다.
- 사람을 대상으로 하므로 학교 IRB 절차를 확인하세요.

## 6. 리뷰어 응답 정리

| 코멘트 | 대응 | 원고에서 수정할 곳 |
|---|---|---|
| 1. 지능적 행동을 어떻게 검증했는가 | 3개 조건 × 100판 × 3회 반복 대전, 승률(CI)·엔트로피·통계 검정, (선택) 사용자 평가 | 새 Experiment 절, 결과 표, 재현 정보(시드, Unity 버전, 하드웨어, 코드 공개) |
| 2. 단일 실험이라 신뢰성이 낮음 | 세션 단위 5-fold × 5 시드 = 25회, 평균 ± SD, 95% CI | 정확도 수치, confusion matrix |
| 3. 10 클래스 vs 14 뉴런, Fig 2 = Fig 9 | 출력층 10개로 재학습, Table 1 수정 | Table 1, 그림 9 교체(새 결과 그림) 또는 삭제 |
| 4. Barracuda가 지원 종료됨 | Unity 6.3 LTS + Inference Engine 2.6.1로 이전 | 본문, 그림, 키워드에서 Barracuda → Inference Engine |
