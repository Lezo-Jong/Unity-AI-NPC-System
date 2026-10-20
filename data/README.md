# Data

Raw data for the re-experiment (human demonstrations and the 900-match evaluation). Everything in `Tools/results/` can be regenerated from this folder.

- Unity 6000.3.26f1 (6.3 LTS) with Unity Sentis 2.6.1 (package `com.unity.ai.inference`; named "Inference Engine" only in versions 2.2–2.3)
- AMD Ryzen 7 7800X3D, NVIDIA GeForce RTX 4070 Ti
- All values were logged at 10 Hz in game time.

## `human/`: human demonstrations used for training (9 files, 10,449 rows)

These were recorded by `CombatDataLogger` on the player while one participant (P01) fought an NPC. The NPC opponent ran `RandomBrain` in 4,544 rows and `FSMBrain` in 5,905 rows.

| Column | Meaning |
|---|---|
| `participant_id`, `session_id`, `opponent_type` | Who played, which session, and which brain the opponent NPC ran |
| `time` | Game time in seconds. A gap longer than 0.25 s means death and respawn |
| `dist` … `self_cur_move` | The 19 input features (see `Assets/Experiment/CombatFeatures.cs`) |
| `action`, `action_name` | The player's current action (0–9, see `Assets/Experiment/ActionClass.cs`) |

Training does not use these rows directly. `Tools/train.py --target decision` (the default) turns them into **decision-point samples**, 6,274 in total:

- At a free moment (Idle, Move or Crouch), the label is the action the player started within the next 3 ticks. If nothing new started, the label is the current action.
- At the start of Slash or Slash2, the label is the next combo step if the combo continued. Otherwise the label is Idle.

## `human_excluded/`: recorded but not used as-is (kept for transparency)

| Folder | What | Why |
|---|---|---|
| `before_sword_trim/` | Original, untrimmed versions of 4 files in `human/` | A bug left one character unable to deal sword damage after respawning: the cooldown flag stayed false. In `human/` each file is cut at the first death after which that side never hit again (S01 at 52.8 s, S03 at 166.8 s, S04 at 100.8 s, S05 at 190.3 s). The bug was fixed in commit `57fbfbd` |
| `before_npc_fix/` | One session against the FSM opponent recorded before the NPC animation-lock fix | Before commit `d713dec`, the opponent NPC could block while its attack animation was still playing |
| `opponent_label_original/` | The two S02 files as originally saved | `opponent_type` was saved as `RandomBrain` although the NPC ran `FSMBrain`. The participant confirmed the actual brain. In `human/` only this column was corrected; nothing else was changed |

Short test sessions recorded on 2026-10-16 before the gameplay fixes (18:10–18:59) are not included.

**Note on S01.** The two S01 files in `human/` (`P01_S01_20261016_190515.csv` and `P01_S01_20261016_190646.csv`; 756 rows, random-action opponent) were also recorded before the NPC animation-lock fix (`d713dec`, 19:36). Unlike the excluded FSM session, they were kept. A sensitivity analysis without them gives nearly the same cross-validation results (macro-F1 0.268 ± 0.029 vs. 0.264 ± 0.028); see `Tools/results/sensitivity_without_S01/` and the third command below.

## `matches/`: main evaluation (900 matches)

The NPC ran Random, FSM or ML in turn against a fixed `ReferenceBotBrain` opponent, 100 matches per condition in each of 3 runs.

| Run | Base seed | Files |
|---|---|---|
| run1 | 1000 | `matches_run1_*.csv`, `decisions_run1_*.csv` |
| run2 | 2000 | `matches_run2_*.csv`, `decisions_run2_*.csv` |
| run3 | 3000 | `matches_run3_*.csv`, `decisions_run3_*.csv` |

Settings used in every run:

- HP 50 for both NPCs
- Attack range 4 m
- NavMesh agent avoidance off
- Time scale 1
- ML samples its action from the softmax output, seeded per match

**`matches_*.csv`** has one row per match:

- outcome, duration, HP at the end, hits landed
- frame rate (mean and 1% low)
- decision latency (mean, SD and maximum, in ms)
- ML fallbacks
- executed action counts (`cnt_*`)
- hardware and Unity version

**`decisions_*.csv`** has one row per NPC decision:

- the 19 input features
- the NPC's current action (`self_action`)
- the chosen action (`chosen`)
- whether the decision was a combo follow-up opportunity (`in_combo`)

## Reproduce

Run these from the project root. Each one is a single command.

```bash
python Tools/train.py --data data/human --out Assets/player_ai.onnx --folds 5 --seeds 0 1 2 3 4
```

```bash
python Tools/analyze_matches.py "data/matches/matches_run*.csv" --decisions "data/matches/decisions_run*.csv" --human data/human
```

The first command reproduces `Tools/results/train_report*.{json,csv}` and the shipped `Assets/player_ai.onnx`. The second reproduces `Tools/results/main_experiment.txt`.

Sensitivity analysis without S01 (writes the model to a separate file so the shipped one is not overwritten):

```bash
python Tools/train.py --data data/human --exclude "P01_S01_*.csv" --out player_ai_without_S01.onnx --report Tools/results/sensitivity_without_S01/train_report.json
```
