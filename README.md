# Unity-AI-NPC-System

> **2026-10 업데이트:** 논문 수정(리뷰어 의견 대응)을 위해 Unity 6.3 LTS와 Unity Sentis 2.6(패키지 com.unity.ai.inference)으로 이전하고, 데이터를 다시 수집해 재학습과 통제 실험(900판)을 진행했습니다. 이전 버전의 결과(테스트 정확도 93.47%)는 평가 방식의 문제로 철회했습니다.
>
> 평가 설계는 후속 연구 [LLM-unity-DualArena](https://github.com/Lezo-Jong/LLM-unity-DualArena)의 방식을 따랐습니다. Brain 교체 구조와 공통 실행기, 고정 시드의 대본 상대, 조건 라벨이 붙은 로그, 원본 데이터 공개가 그 예입니다. 다만 NPC의 의사결정은 LLM이 아니라, 이 프로젝트의 원래 방식인 사람 플레이 로그 기반 행동 모방(MLP)입니다.

AI-driven NPC behavior system in Unity using PyTorch, ONNX, and Unity Sentis (the successor of Barracuda).

## Overview

This project implements an NPC combat system for PvP interactions in Unity.
A multi-layer perceptron is trained on logged human play (behavioral cloning) and drives the NPC in real time.
The learned NPC is compared with a random-action baseline and a finite state machine (FSM) in controlled, seeded matches.

- Unity 6.3 LTS (6000.3.26f1), Unity Sentis 2.6.1 (`com.unity.ai.inference`)
- PyTorch 2.8.0, ONNX opset 15

## Features

- Player behavior logging (`CombatDataLogger`, 10 Hz, 19 features + 10 action classes)
- Deep learning model training (`Tools/train.py`, decision-point samples, cross-validation grouped by log file)
- ONNX integration (`MLBrain`, CPU backend, softmax sampling)
- Real-time AI inference (0.1 ms per decision)
- Animation-based combat system (the player and the NPCs follow the same animation-lock and combo rules)
- Automated evaluation (`MatchRunner`, `Tools/analyze_matches.py`)

## System Architecture
<img width="331" height="561" alt="System Architecture" src="https://github.com/user-attachments/assets/ce38f12b-8266-4b79-86d9-50fae2bcca60" />



## AI Decision Flow

<img width="341" height="281" alt="AI Decision Flow" src="https://github.com/user-attachments/assets/b88dd3e7-46bd-4d79-ab32-ced183b9e19b" />


## Combat Demo
<img width="183" height="169" alt="Slash" src="https://github.com/user-attachments/assets/a0db54de-243f-4ebb-a3c9-53808638d7ad" />
<img width="175" height="176" alt="Shied   Jump" src="https://github.com/user-attachments/assets/dfd17fb2-5598-4214-9835-2f49161dddda" />
<img width="288" height="248" alt="AI tracking" src="https://github.com/user-attachments/assets/28566b87-99fa-4181-aebc-9c99a46c1eea" />


## Result

### Action prediction

5-fold cross-validation grouped by log file × 5 seeds (25 runs, mean ± SD). The data are from one participant: 7 sessions in 9 log files, 6,274 decision samples. The partitions are the same for every seed (seeds change only the network initialization and batch order), so the runs are not independent.

| Metric | MLP | Majority-class baseline |
|---|---|---|
| Macro-F1 | 0.264 ± 0.028 | 0.055 ± 0.009 |
| Balanced accuracy | 0.298 ± 0.032 | 0.102 ± 0.005 |
| Accuracy | 0.311 ± 0.042 | 0.370 ± 0.062 |

Accuracy was below the majority-class baseline in 17 of the 25 runs, because the class weighting trades accuracy on the frequent Idle class for rarer actions.

The earlier figure of 93.47% test accuracy came from a single random split in which the current action leaked into the inputs, so it has been withdrawn.

### Gameplay evaluation

The setup was 900 matches: 3 runs × 3 controllers × 100 matches against a fixed reference opponent. Values are mean ± SD unless noted otherwise.

| Metric | Random | FSM | ML (proposed) |
|---|---|---|---|
| Win rate [95% CI] | 24.7% [20.1–29.8] | **83.3%** [78.7–87.1] | 17.3% [13.5–22.0] |
| Behavioral entropy (bits) | 1.29 ± 0.24 | 1.70 ± 0.20 | **1.90 ± 0.20** |
| Combat entropy, excl. Move (bits) | **2.42 ± 0.33** | 1.88 ± 0.27 | 1.97 ± 0.34 |
| JS divergence to human, lower = more human-like [95% CI] | 0.122 [0.114–0.129] | 0.263 [0.259–0.266] | **0.081 [0.074–0.090]** |
| Decision latency (ms) | 0.002 | 0.002 | 0.099 |

The learned NPC is the most human-like controller. All pairwise differences are significant, and the ranking is the same in every run. It is not the strongest controller, though: it reproduces a human play style rather than maximizing win rate.

Full reports are in `Tools/results/`.

## Reproduce

The raw data are in `data/`; see `data/README.md` for details. Run these commands from the project root:

```bash
python Tools/train.py --data data/human --out Assets/player_ai.onnx
python Tools/analyze_matches.py "data/matches/matches_run*.csv" --decisions "data/matches/decisions_run*.csv" --human data/human
```

## Demo Video

This video shows the previous version.

(https://drive.google.com/file/d/1mm_qhzdvw99q1qXaASs0vNn-2HXXiql6/view?usp=drive_link)

## Future Work

- Reinforcement Learning
- XR Interaction
- Adaptive NPC Behavior
- User study (blinded) and demonstrations from multiple players
