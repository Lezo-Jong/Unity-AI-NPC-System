"""
Damage negation by blocking (Reviewer 3: mediating variable between behavior and strength).

Assigns every 'hit' event of the HitDiagnostics log (HitLogs/hits_*.csv, one file per Play session) to the
match in which it happened, by walking the matches in order and accumulating hp_loss until both
characters' HP losses equal those recorded in the match log. A hit with hp_loss = 0 was blocked
(HealthSystem negates damage during Block / CrouchBlock).

Usage: python Tools/block_mediation.py --hits <hits.csv> --matches "<matches_run*.csv>" [--hits ... --matches ...]
"""
import argparse, glob, sys
import numpy as np, pandas as pd

def assign(h, m):
    hit = h[h.kind == "hit"].reset_index(drop=True)
    rows, i = [], 0
    for _, r in m.iterrows():
        need_npc, need_bot = 50 - r.npc_hp_end, 50 - r.opp_hp_end
        got_npc = got_bot = 0
        start = i
        while i < len(hit) and (got_npc < need_npc or got_bot < need_bot):
            e = hit.iloc[i]
            if e.attacker == "OpponentBot": got_npc += e.hp_loss
            else: got_bot += e.hp_loss
            i += 1
        # blocked hits that follow in the same match (only possible when the match did not end by a death)
        if r.outcome == "draw":
            t_last = hit.iloc[i - 1].time if i > start else -1
            while i < len(hit) and hit.iloc[i].hp_loss == 0 and hit.iloc[i].time - t_last < 5:
                t_last = hit.iloc[i].time; i += 1
        seg = hit.iloc[start:i]
        inc = seg[seg.attacker == "OpponentBot"]; out = seg[seg.attacker != "OpponentBot"]
        rows.append(dict(condition=r.condition, run=r.run, match=r.match, outcome=r.outcome,
                         bot_hits=len(inc), bot_blocked=int((inc.hp_loss == 0).sum()),
                         npc_hits=len(out), npc_blocked=int((out.hp_loss == 0).sum()),
                         ok=(got_npc == need_npc and got_bot == need_bot)))
    return pd.DataFrame(rows), len(hit) - i

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hits", action="append", required=True)
    ap.add_argument("--matches", action="append", required=True)
    ap.add_argument("--out", default="Tools/results/block_mediation.csv")
    a = ap.parse_args()
    allr = []
    for hf, mp in zip(a.hits, a.matches):
        h = pd.read_csv(hf)
        m = pd.concat([pd.read_csv(f) for f in sorted(glob.glob(mp))], ignore_index=True)
        r, left = assign(h, m)
        print("%s: %d matches, %d not reconciled, %d hit events left over" % (hf, len(r), (~r.ok).sum(), left))
        allr.append(r)
    r = pd.concat(allr, ignore_index=True)
    r.to_csv(a.out, index=False)
    g = r.groupby("condition", sort=False)
    t = pd.DataFrame({
        "matches": g.size(),
        "win_%": g.outcome.apply(lambda x: 100 * (x == "win").mean()),
        "bot_swings_hitting": g.bot_hits.mean(),
        "blocked_incoming_%": 100 * g.bot_blocked.sum() / g.bot_hits.sum(),
        "damaging_received": g.apply(lambda d: (d.bot_hits - d.bot_blocked).mean()),
        "npc_hits": g.npc_hits.mean(),
        "npc_blocked_by_bot_%": 100 * g.npc_blocked.sum() / g.npc_hits.sum().clip(lower=1),
    }).round(2)
    print(t.to_string())
    # correlation across controllers between blocking share and win rate
    print("Spearman (controllers) blocked_incoming vs win:", round(t["blocked_incoming_%"].corr(t["win_%"], method="spearman"), 3))

if __name__ == "__main__":
    main()
