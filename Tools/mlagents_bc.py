"""
ML-Agents BC baseline (Unity ML-Agents Release 23 = Python mlagents 1.1.0, PyTorch).

What this does
--------------
1. Converts the human logs into ML-Agents demonstration files (.demo) with ML-Agents' own writer
   (mlagents.trainers.demo_loader.write_demo) and reads them back with ML-Agents' loader (demo_to_buffer).
2. Trains the default ML-Agents policy network (SimpleActor; NetworkSettings: 2 layers x 128 units,
   observation normalization on) with ML-Agents' behavioral cloning module (BCModule) and the
   PPO defaults that BCModule inherits (learning rate 3e-4, batch 1024, 3 epochs per update).
3. Exports the actor as ONNX (input [batch,19] raw features -> output [batch,10] logits), so that Unity
   runs it with the same MLBrain, executor and sampling as the other learned controllers.

Two demonstration types
-----------------------
- tick : the standard ML-Agents workflow. A DemonstrationRecorder on an agent driven by the player
         records (observation, action) at every decision step (10 Hz). The action recorded is the one the
         player is performing at that step (the `action` column of the logs).
- dp   : the decision-point samples used by our MLP (train.build_decisions, horizon 3 ticks),
         so that only the learning framework differs.

Why not mlagents-learn with the game running?
---------------------------------------------
In ML-Agents, BC is not a stand-alone trainer; it is an auxiliary loss added to PPO/SAC. There is no
reward that expresses "play like this person", so the RL part would receive zero reward and contribute
only its entropy bonus, which pulls the policy toward uniform. We therefore run the BC module alone,
offline, for the number of updates that mlagents-learn would perform in --updates x buffer_size
environment steps (default 49 updates = 500k steps / 10,240).

Usage
-----
  python Tools/mlagents_bc.py --data data/human --export Assets     # train on all sessions, export ONNX
  python Tools/mlagents_bc.py --data data/human --loso --seeds 0 1 2 3 4   # leave-one-session-out evaluation
Requires: Python 3.10, mlagents==1.1.0 (pip install mlagents==1.1.0), onnx, onnxruntime (check only).
"""
import argparse
import glob
import json
import os
import sys
import tempfile

import numpy as np
import pandas as pd
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from train import ACTIONS, FEATURES, build_decisions  # noqa: E402

from mlagents_envs.communicator_objects.agent_action_pb2 import AgentActionProto
from mlagents_envs.communicator_objects.agent_info_action_pair_pb2 import AgentInfoActionPairProto
from mlagents_envs.communicator_objects.agent_info_pb2 import AgentInfoProto
from mlagents_envs.communicator_objects.brain_parameters_pb2 import ActionSpecProto, BrainParametersProto
from mlagents_envs.communicator_objects.demonstration_meta_pb2 import DemonstrationMetaProto
from mlagents_envs.communicator_objects.observation_pb2 import NONE, ObservationProto
from mlagents.trainers.demo_loader import demo_to_buffer, write_demo
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.settings import BehavioralCloningSettings, NetworkSettings
from mlagents.trainers.torch_entities.components.bc.module import BCModule
from mlagents.trainers.torch_entities.networks import SimpleActor
import mlagents

K = len(ACTIONS)
BEHAVIOR = "CombatNPC"
PPO_LR, PPO_BATCH, PPO_EPOCH, BUFFER = 3e-4, 1024, 3, 10240


# ---------- demonstrations ----------
def write_demo_file(path, x, y, episode):
    """x: [n,19] float, y: [n] int, episode: [n] episode id (done=True on the last step of each episode)."""
    meta = DemonstrationMetaProto(api_version=1, demonstration_name=BEHAVIOR,
                                  number_steps=len(x), number_episodes=len(np.unique(episode)))
    brain = BrainParametersProto(brain_name=BEHAVIOR, is_training=True,
                                 action_spec=ActionSpecProto(num_continuous_actions=0,
                                                             num_discrete_actions=1,
                                                             discrete_branch_sizes=[K]))
    pairs = []
    for i in range(len(x)):
        done = i == len(x) - 1 or episode[i + 1] != episode[i]
        obs = ObservationProto(shape=[len(FEATURES)], compression_type=NONE, dimension_properties=[1])
        obs.float_data.data.extend(x[i].tolist())
        info = AgentInfoProto(reward=0.0, done=bool(done), max_step_reached=False, id=0, observations=[obs])
        act = AgentActionProto(discrete_actions=[int(y[i])])
        pairs.append(AgentInfoActionPairProto(agent_info=info, action_info=act))
    write_demo(path, meta, brain, pairs)


def tick_samples(raw):
    raw = raw.sort_values(["session_id", "time"])
    return (raw[FEATURES].to_numpy(np.float32), raw["action"].to_numpy(np.int64),
            pd.factorize(raw["session_id"])[0])


def dp_samples(dp):
    return (dp[FEATURES].to_numpy(np.float32), dp["action"].to_numpy(np.int64),
            pd.factorize(dp["session_id"])[0])


# ---------- training ----------
class LogitsActor(torch.nn.Module):
    """Raw features -> unmasked branch logits of the ML-Agents actor (normalizer included)."""

    def __init__(self, actor):
        super().__init__()
        self.actor = actor

    def forward(self, x):
        enc, _ = self.actor.network_body([x])
        return self.actor.action_model._discrete_distribution.branches[0](enc)


def train_mla(x, y, episode, seed, updates):
    torch.manual_seed(seed); np.random.seed(seed)
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "human.demo")
        write_demo_file(path, x, y, episode)
        spec, demo_buffer = demo_to_buffer(path, 1)
        policy = TorchPolicy(seed, spec, NetworkSettings(normalize=True, hidden_units=128, num_layers=2),
                             SimpleActor, {"conditional_sigma": False, "tanh_squash": False})
        bc = BCModule(policy, BehavioralCloningSettings(demo_path=path, strength=1.0, steps=0),
                      policy_learning_rate=PPO_LR, default_batch_size=PPO_BATCH, default_num_epoch=PPO_EPOCH)
    # mlagents-learn would update the normalizer with environment observations; offline we use the demonstrations
    policy.actor.update_normalization(demo_buffer)
    losses = [bc.update()["Losses/Pretraining Loss"] for _ in range(updates)]
    net = LogitsActor(policy.actor).eval()
    return net, losses, policy


def logits(net, x):
    with torch.no_grad():
        return net(torch.tensor(x, dtype=torch.float32)).numpy().astype(float)


def load_raw(data):
    files = sorted(glob.glob(os.path.join(data, "*.csv")))
    raw = pd.concat([pd.read_csv(f) for f in files], ignore_index=True).dropna(subset=FEATURES + ["action"])
    raw["cur_action"] = raw["action"]
    raw["session"] = raw["session_id"].str.extract(r"^(P\d+_S\d+)")[0]
    return raw, files


def export(net, out_path, x_check):
    net = net.eval()
    dummy = torch.zeros(1, len(FEATURES))
    torch.onnx.export(net, dummy, out_path, input_names=["input"], output_names=["logits"],
                      dynamic_axes={"input": {0: "batch"}, "logits": {0: "batch"}}, opset_version=15,
                      dynamo=False)
    import onnxruntime as ort
    sess = ort.InferenceSession(out_path)
    z_onnx = sess.run(None, {"input": x_check.astype(np.float32)})[0]
    return float(np.abs(z_onnx - logits(net, x_check)).max())


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/human")
    ap.add_argument("--updates", type=int, default=49, help="BC updates = mlagents-learn max_steps / buffer_size")
    ap.add_argument("--export", default=None, help="folder for mla_bc_tick.onnx and mla_bc_dp.onnx")
    ap.add_argument("--loso", action="store_true")
    ap.add_argument("--seeds", type=int, nargs="+", default=[0])
    ap.add_argument("--range", type=float, default=4.5)
    ap.add_argument("--out", default="Tools/results/mlagents_bc")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    raw, files = load_raw(args.data)
    dp = build_decisions(raw, 3)
    dp["session"] = dp["session_id"].str.extract(r"^(P\d+_S\d+)")[0]
    print("mlagents %s, torch %s; files %d, ticks %d, decision samples %d"
          % (mlagents.__version__ if hasattr(mlagents, "__version__") else "1.1.0", torch.__version__,
             len(files), len(raw), len(dp)))

    if args.export:
        os.makedirs(args.export, exist_ok=True)
        info = {}
        for name, (x, y, ep) in {"tick": tick_samples(raw), "dp": dp_samples(dp)}.items():
            net, losses, _ = train_mla(x, y, ep, args.seeds[0], args.updates)
            path = os.path.join(args.export, "mla_bc_%s.onnx" % name)
            err = export(net, path, x[:2000])
            p = np.bincount(y, minlength=K) / len(y)
            info[name] = {"samples": int(len(x)), "updates": args.updates, "final_loss": float(losses[-1]),
                          "onnx_max_abs_err": err, "label_freq": dict(zip(ACTIONS, p.round(4).tolist()))}
            print("%-4s n=%6d  loss %.4f -> %.4f  ONNX max |dz| = %.2e  -> %s"
                  % (name, len(x), losses[0], losses[-1], err, path))
        with open(os.path.join(args.out, "export_info.json"), "w", encoding="utf-8") as f:
            json.dump(info, f, indent=2, ensure_ascii=False)

    if args.loso:
        from heldout_eval import metrics, softmax
        rows = []
        for s in sorted(dp["session"].unique()):
            te = dp[dp["session"] == s]
            xte, yte = te[FEATURES].to_numpy(np.float32), te["action"].to_numpy(np.int64)
            for name, (x, y, ep) in {"MLA-BC-tick": tick_samples(raw[raw["session"] != s]),
                                     "MLA-BC-dp": dp_samples(dp[dp["session"] != s])}.items():
                Ps = []
                for seed in args.seeds:
                    net, _, _ = train_mla(x, y, ep, seed, args.updates)
                    Ps.append(softmax(logits(net, xte)))
                r = metrics(np.mean(Ps, 0), yte, te, args.range)
                r.update(session=s, policy=name, n=len(te))
                rows.append(r)
                print("%s %-12s NLL %.3f  Brier %.3f  top1 %.3f  react %.3f/%.3f"
                      % (s, name, r["NLL_bits"], r["Brier"], r["top1"], r["react_pred"], r["react_true"]))
        res = pd.DataFrame(rows)
        res.to_csv(os.path.join(args.out, "loso_mlagents_bc.csv"), index=False)
        print(res.groupby("policy")[["NLL_bits", "Brier", "top1", "top3", "JS_inrange", "condJS_inrange",
                                     "react_pred"]].mean().round(3))


if __name__ == "__main__":
    main()
