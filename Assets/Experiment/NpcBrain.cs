using System;
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
    [Tooltip("이 거리(m) 안이면 공격/방어, 밖이면 추적. MatchRunner가 실행 시 덮어씁니다.")]
    public float attackRange = 2.5f;

    public readonly List<double> LatenciesMs = new List<double>();
    public string Status { get; private set; } = "대기";

    // 결정할 때마다 (입력, 선택 행동, 콤보 예약 중이었는지)를 알림. MatchRunner가 기록에 사용.
    public event Action<NpcBrain, float[], ActionClass, bool> Decided;

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
        if (!executor.CanDecide) { Status = "행동 중"; return; }
        Status = executor.IsBusy ? "콤보 결정" : "결정 중";

        stopwatch.Restart();
        CombatFeatures.Compute(self, opponent, features);
        ActionClass a = Decide(features);
        stopwatch.Stop();
        LatenciesMs.Add(stopwatch.Elapsed.TotalMilliseconds);

        Decided?.Invoke(this, features, a, executor.IsBusy);
        executor.Execute(a);
    }

    protected abstract ActionClass Decide(float[] f);
}
