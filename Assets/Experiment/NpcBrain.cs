using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

// 모든 행동 선택기(Random / FSM / ML / 상대 봇)의 공통 부모.
// decisionHz 주기로 관측 → 행동 선택 → 실행하며, 선택에 걸린 시간(ms)을 기록합니다.
[RequireComponent(typeof(NpcActionExecutor))]
public abstract class NpcBrain : MonoBehaviour
{
    public NpcActionExecutor executor;
    public CombatAgentState self;
    public CombatAgentState opponent;
    public float decisionHz = 10f;

    public readonly List<double> LatenciesMs = new List<double>();
    public string Status { get; private set; } = "대기";

    protected readonly float[] features = new float[CombatFeatures.Count];
    private float nextDecisionTime;
    private readonly Stopwatch stopwatch = new Stopwatch();

    protected virtual void Awake()
    {
        if (executor == null) executor = GetComponent<NpcActionExecutor>();
        if (self == null) self = GetComponent<CombatAgentState>();
    }

    protected virtual void OnEnable()
    {
        nextDecisionTime = Time.time;
    }

    private void Update()
    {
        if (opponent == null) opponent = executor.opponent;
        if (self == null) { Status = "CombatAgentState 없음(NPC)"; return; }
        if (opponent == null) { Status = "상대 미지정(Executor의 Opponent 칸)"; return; }
        if (self.health != null && self.health.IsDead) { Status = "자신 사망"; return; }
        if (opponent.health != null && opponent.health.IsDead) { Status = "상대 사망"; return; }

        if (Time.time < nextDecisionTime) return;
        nextDecisionTime = Time.time + 1f / decisionHz;
        if (executor.IsBusy) { Status = "행동 중"; return; }
        Status = "결정 중";

        stopwatch.Restart();
        CombatFeatures.Compute(self, opponent, features);
        ActionClass a = Decide(features);
        stopwatch.Stop();
        LatenciesMs.Add(stopwatch.Elapsed.TotalMilliseconds);

        executor.Execute(a);
    }

    protected abstract ActionClass Decide(float[] f);
}
