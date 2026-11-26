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

## `matches/` (continued): FM baseline and logit adjustment (2026-11-23, 600 matches)

Added after the second review round. Same scene, opponent, attack range, seeds and settings as the main evaluation; only the NPC's brain differs. Files: `*_run1_20261123_190022.csv`, `*_run2_20261123_201213.csv`, `*_run3_20261123_212134.csv` (base seeds 1000, 2000, 3000; 100 matches per condition per run).

- **FM** (`Assets/Experiment/FrequencyMatchedBrain.cs`): ignores the state. In range, it samples actions with the human in-range decision frequencies (n = 4,853 free decisions) and continues combos with the human continuation rates (59.1% after Slash, 73.1% after Slash2). Out of range it chases, like `RandomBrain`.
- **MLAdj**: the same `player_ai.onnx` as ML, with logit adjustment at inference (`MLBrain.logitAdjust`): logit_c − log w_c, where w_c are the square-root inverse-frequency class weights used in training. This undoes the shift of the sampled policy toward rare classes. No retraining.

Analysis with state-conditioned measures (conditional JS over opponent-attacking × distance bins, defensive reactivity = P(defend | opponent attacking) − P(defend | not)):

```bash
python Tools/analyze_conditional.py --decisions "data/matches/decisions_run*.csv" --matches "data/matches/matches_run*.csv" --human data/human
```

Output: `Tools/results/conditional_experiment.txt`. Note that the human reference is still the training data (in-sample); a held-out-session evaluation is pending.

## `hit_diagnostics/`: how hits were actually registered (2026-11-24)

Reviewer 2 noted that the human's hits were registered by the sword collider and the NPCs' hits by `NpcActionExecutor.TryHit`. Logging every sword hit (`Assets/Scripts/HitDiagnostics.cs`) showed a larger problem:

- **Human → NPC (data collection, `human_vs_npc_legacy_*.csv`)**: the player's sword trigger also hit the NPC's `SphereCollider` trigger (radius 4 × scale 3 ≈ 12 m), so 28 of 32 swings registered on that sphere, including swings while facing away (facing −0.97) and at 5.3 m. In practice every swing connected unless the NPC blocked.
- **NPC → human (data collection)**: hits registered only on the player's body (`CharacterController`); the player has no such sphere.
- **NPC ↔ reference bot (evaluations up to 2026-11-23)**: `TryHit`, i.e., body-centre distance ≤ attack range + 0.5 m and facing ≥ 0.3.

Fix (`hit_mode = SwordCollider`, default from now on): everyone is hit only when the sword box touches the opponent's body collider; trigger colliders are ignored (`SwordController.legacyTriggerHits = false`). Between two NPCs, the overlap is computed every frame with `Physics.ComputePenetration` (both are kinematic and on the Enemy layer, so physics trigger events are not delivered), and a hit is applied on the frame the overlap begins, as `OnTriggerEnter` does for the player. Damage 5–10 for everyone. The old behaviour stays available (`HitMode.Distance`, `legacyTriggerHits = true`) to reproduce earlier results.

`npc_vs_bot_swordcollider_pilot_*.csv` (50 pilot matches): all 680 sword contacts were on the body capsule, at 2.76–4.83 m (median 3.38 m); 18% were blocked (0 damage).

The human demonstrations in `human/` were recorded with the legacy rule and should be re-collected under the fixed rule.

## `matches_swordcollider/`: main evaluation repeated with the unified hit rule (2026-11-24/25, 1,500 matches)

**Superseded by `matches_swordcollider_v2/`.** In these runs the sword collider sometimes stayed on after an attack animation ended (the `DisableSlashCollider` event was skipped at some transitions), so about 10% of the reference bot's hits and 5.5% of the NPC's hits landed more than 1 s after a swing. Kept for transparency.

Same scene, opponent, attack range (4 m), seeds (run1/2/3 = 1000/2000/3000) and 100 matches per condition per run as `matches/`, with `hit_mode = SwordCollider` (see `hit_diagnostics/`) and five conditions: Random, FSM, ML, MLAdj, FM. Run with `MatchRunner.repeatRuns = 3`, run label `sw`.

```bash
python Tools/analyze_conditional.py --decisions "data/matches_swordcollider/decisions_*.csv" --matches "data/matches_swordcollider/matches_*.csv" --human data/human
python Tools/time_metrics.py --decisions "data/matches_swordcollider/decisions_*.csv" --matches "data/matches_swordcollider/matches_*.csv" --human data/human
```

Outputs: `Tools/results/conditional_experiment_swordcollider.txt`, `Tools/results/time_metrics_swordcollider.txt`.

## `matches_swordcollider_v2/`: final evaluation (2026-11-25/26, 1,500 matches)

As `matches_swordcollider/`, with the sword collider switched off automatically outside attack animations (`SwordController.autoDisableOutsideAttack`, 0.8 s safety timeout). Run label `sw2`, seeds 1000/2000/3000. Hit log check: all 20,531 hits on body colliders, none later than 1 s after a swing, at most one hit per swing; 1,646 automatic switch-offs, all because the animation had left the attack state.

```bash
python Tools/analyze_conditional.py --decisions "data/matches_swordcollider_v2/decisions_*.csv" --matches "data/matches_swordcollider_v2/matches_*.csv" --human data/human
python Tools/analyze_conditional.py --decisions "data/matches_swordcollider_v2/decisions_*.csv" --matches "data/matches_swordcollider_v2/matches_*.csv" --human data/human_baseline
python Tools/time_metrics.py --decisions "data/matches_swordcollider_v2/decisions_*.csv" --matches "data/matches_swordcollider_v2/matches_*.csv" --human data/human
```

The second command uses the human-baseline log (`P01_S90`, same opponent and rules, not used in training) as the human reference. Outputs: `Tools/results/*_swordcollider_v2*.txt`.

## `matches_mlagents_bc/`: ML-Agents BC baselines (2026-11-26, 600 matches)

Two NPCs trained with the behavioral cloning module of Unity ML-Agents (Python `mlagents` 1.1.0) on the same demonstrations as `Assets/player_ai.onnx` (Section 2.3.1 and 3.5 of the paper):

- `MLA_Tick` (`Assets/mla_bc_tick.onnx`): standard ML-Agents demonstrations, i.e. the action in progress at every 0.1 s step (10,449 samples).
- `MLA_DP` (`Assets/mla_bc_dp.onnx`): the 6,274 decision-point samples used by our MLP.

Run with the same MatchRunner settings and base seeds as `matches_swordcollider_v2/` (SwordCollider hit rule, attack range 4 m, time scale 1, 3 runs x 100 matches per controller, label `sw2_mla`). Models and held-out evaluation: `python Tools/mlagents_bc.py --data data/human --export Assets` and `--loso --seeds 0 1 2 3 4` (results in `Tools/results/mlagents_bc/`; requires Python 3.10 and `pip install mlagents==1.1.0`).

```
python Tools/analyze_conditional.py --decisions "data/matches_swordcollider_v2/decisions_*.csv" "data/matches_mlagents_bc/decisions_*.csv" --matches "data/matches_swordcollider_v2/matches_*.csv" "data/matches_mlagents_bc/matches_*.csv" --human data/human
python Tools/time_metrics.py --decisions "data/matches_swordcollider_v2/decisions_*.csv" "data/matches_mlagents_bc/decisions_*.csv" --matches "data/matches_swordcollider_v2/matches_*.csv" "data/matches_mlagents_bc/matches_*.csv" --human data/human
```

Results: `Tools/results/conditional_experiment_with_mlagents*.txt`, `Tools/results/time_metrics_with_mlagents.txt`.

## `human_baseline/`: the demonstrator against the evaluation opponent (2026-11-25, 100 matches)

The same participant (P01) played 100 matches against `ReferenceBotBrain` with `Assets/Experiment/HumanBaselineRunner.cs`: same opponent, attack range (4 m), HP (50) and seed sequence (1000 + match) as the NPC evaluation, unified sword-collider hit rule (body contact only, damage 5–10), and the sword collider switched off automatically outside attack animations (`SwordController.autoDisableOutsideAttack`).

- `matches_human_baseline_*.csv`: one row per match, same columns as `matches_*.csv` (condition = Human; `cnt_*` are action starts)
- `P01_S90_*.csv`: `CombatDataLogger` output at 10 Hz during these matches (session 90, opponent `ReferenceBotBrain`); not used for training, so it is a held-out human reference in the same context as the NPCs
- `hits_human_baseline_*.csv`: `HitDiagnostics` log (all hits on body colliders, no hit later than 1 s after a swing)

Result: 89 wins, 11 losses (89.0%, Wilson 95% CI 81.4–93.7).

## `runtime_check/`: Unity vs. Python output check (Section 3.2)

Four logs written by `MLBrain` with **Log Decisions** on, during preliminary ML matches with the final `player_ai.onnx` (4,050 decisions). Each row holds the 19 inputs, the 10 softmax probabilities computed in Unity (rounded to 4 decimals), and the chosen action.

`python Tools/check_onnx_consistency.py` recomputes the probabilities in Python (`onnx.reference`) from the same inputs. Result: maximum absolute difference 5.5 × 10⁻⁵ (rounding alone allows up to 5 × 10⁻⁵), and the most probable action matches in all 4,050 decisions.

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
