using System.Collections;
using UnityEngine;
using UnityEngine.AI;

// NPC 행동 실행기. Random / FSM / ML / 상대 봇 모두 이 컴포넌트로 행동을 실행하므로
// 조건 간 차이는 "행동 선택(Brain)"뿐입니다.
// 플레이어(PlayerController)와 같은 규칙을 따릅니다:
// - 공격·방어·점프 애니메이션이 끝날 때까지 다음 행동을 받지 않음
// - 예외: Slash / Slash2 중에는 다음 콤보 단계를 한 번 예약할 수 있음 (결정 기회 1회)
// - 공격 판정은 애니메이션 이벤트(칼 콜라이더가 켜지는 시점)와 같은 시각에 냄
// - 방어는 Block / CrouchBlock 애니메이션 동안만 유효 (HealthSystem이 Animator 상태로 판단)
[RequireComponent(typeof(NavMeshAgent))]
public class NpcActionExecutor : MonoBehaviour
{
    [Header("상대")]
    public CombatAgentState opponent;

    [Header("전투 설정 (기존 EnemyAI와 동일한 값 권장)")]
    [Tooltip("현재 버전에서는 사용하지 않음 (판정은 몸 중심 거리 + 정면 각도)")]
    public Transform attackPoint;
    public float attackRange = 2.5f;
    public float hitRadius = 1.5f;
    public int minDamage = 5;
    public int maxDamage = 12;
    [Tooltip("각 공격 상태에 들어간 뒤 판정까지 시간(s). 애니메이션 이벤트 EnableSlashCollider 시각과 같음")]
    public float slashHitTime = 0.58f;
    public float slash2HitTime = 0.78f;
    public float slash3HitTime = 1.32f;
    public float crouchSlashHitTime = 0.55f;
    [Tooltip("Crouch(앉아 있기) 유지 시간")]
    public float actionDuration = 1f;
    [Tooltip("트리거를 걸었는데 이 시간 안에 해당 애니메이션 상태로 못 들어가면 행동 취소")]
    public float enterTimeout = 0.8f;
    public float turnSpeed = 540f;
    [Tooltip("NavMesh 멈춤 거리 = attackRange - 이 값 (사거리 안쪽에서 확실히 멈추도록)")]
    public float stopMargin = 1.0f;
    [Tooltip("이 스크립트가 직접 데미지를 줄지 여부. 기존 SwordController(애니메이션 이벤트)가 이미 플레이어에게 데미지를 준다면 데이터 수집 때는 꺼도 됩니다. NPC끼리 대전(MatchRunner)에서는 반드시 켜야 합니다.")]
    public bool applyDamage = true;
    [Tooltip("공격 판정 각도: 상대가 정면 기준 이 값(내적) 이상일 때만 명중")]
    public float hitFacingDot = 0.3f;

    [Header("진단")]
    public bool debugLog = false;
    public bool showOverlay = true;
    public string LastHitResult { get; private set; } = "-";

    public ActionClass CurrentAction { get; private set; } = ActionClass.Idle;
    public bool IsBusy { get; private set; }
    // Brain이 지금 결정해도 되는지: 한가하거나, 콤보 예약 기회가 남아 있을 때
    public bool CanDecide => !IsBusy || (comboWindowOpen && !comboDecisionUsed);
    public readonly int[] ExecutedCounts = new int[ActionInfo.Count];
    public int HitsLanded { get; private set; }

    private NavMeshAgent agent;
    private Animator animator;
    private HealthSystem health;
    private Vector3 lastPosition;
    private float busySince;
    private ActionClass currentSlash = ActionClass.Idle;
    private ActionClass? queuedSlash;
    private bool comboWindowOpen, comboDecisionUsed;

    static readonly string[] ActionTriggers = { "Slash", "Slash2", "Slash3", "Block", "CrouchBlock", "CrouchSlash", "Jump" };

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponentInChildren<Animator>();
        health = GetComponent<HealthSystem>();
        agent.stoppingDistance = Mathf.Max(0.1f, attackRange - stopMargin);
        lastPosition = transform.position;
    }

    private void Update()
    {
        // 안전장치: 행동이 3초 넘게 끝나지 않으면 강제 해제 (행동 잠금 방지)
        if (IsBusy && Time.time - busySince > 3f)
        {
            if (debugLog) Debug.LogWarning("[Executor] 행동 잠금 감지 → 강제 해제: " + CurrentAction);
            StopAll();
        }

        // 이동 애니메이션 (기존 EnemyAI와 동일한 파라미터)
        float dt = Time.deltaTime;
        if (dt > 0f && animator != null)
        {
            Vector3 v = (transform.position - lastPosition) / dt;
            animator.SetFloat("MoveX", v.x);
            animator.SetFloat("MoveY", v.z);
            animator.SetBool("IsMoving", agent.enabled && agent.velocity.magnitude > 0.1f);
        }
        lastPosition = transform.position;

        // 정지 상태에서는 상대를 바라봄
        if (opponent != null && (agent.isStopped || !agent.hasPath))
        {
            Vector3 d = opponent.transform.position - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > 1e-4f)
            {
                Quaternion target = Quaternion.LookRotation(d);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * dt);
            }
        }
    }

    public void SetAttackRange(float range)
    {
        attackRange = range;
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        agent.stoppingDistance = Mathf.Max(0.1f, attackRange - stopMargin);
    }

    public void ResetCounts()
    {
        for (int i = 0; i < ExecutedCounts.Length; i++) ExecutedCounts[i] = 0;
        HitsLanded = 0;
    }

    public void Execute(ActionClass a)
    {
        if (health != null && health.IsDead) return;

        if (IsBusy)
        {
            // 행동 중에는 콤보 예약만 가능 (예약 기회는 한 번, 공격이 아닌 선택이면 콤보 종료)
            if (!comboWindowOpen || comboDecisionUsed) return;
            comboDecisionUsed = true;
            if (a != ActionClass.Slash && a != ActionClass.Slash2 && a != ActionClass.Slash3) return;
            ActionClass next = currentSlash == ActionClass.Slash ? ActionClass.Slash2 : ActionClass.Slash3;
            animator.SetTrigger(next.ToString());
            queuedSlash = next;
            Count(next);
            return;
        }

        // 한가할 때 Slash2/Slash3 요청은 콤보 시작(Slash)으로
        if (a == ActionClass.Slash2 || a == ActionClass.Slash3) a = ActionClass.Slash;

        Count(a);
        switch (a)
        {
            case ActionClass.Idle:
                Stop();
                CurrentAction = ActionClass.Idle;
                break;
            case ActionClass.Move:
                if (opponent != null && agent.isOnNavMesh)
                {
                    agent.isStopped = false;
                    agent.SetDestination(opponent.transform.position);
                }
                CurrentAction = ActionClass.Move;
                break;
            default:
                StartCoroutine(DoAction(a));
                break;
        }
    }

    private void Count(ActionClass a)
    {
        ExecutedCounts[(int)a]++;
        if (debugLog)
            Debug.Log(string.Format("[Executor] {0} 실행 (거리 {1:F2} m)", a,
                opponent != null ? FlatDistance(transform.position, opponent.transform.position) : -1f));
    }

    private bool InState(string state)
    {
        return animator.GetCurrentAnimatorStateInfo(0).IsName(state);
    }

    // 공격·방어·점프 상태이거나 그 상태로 넘어가는 중
    private bool AnimLocked()
    {
        if (IsActionState(animator.GetCurrentAnimatorStateInfo(0))) return true;
        return animator.IsInTransition(0) && IsActionState(animator.GetNextAnimatorStateInfo(0));
    }

    private static bool IsActionState(AnimatorStateInfo st)
    {
        foreach (string t in ActionTriggers) if (st.IsName(t)) return true;
        return false;
    }

    private float HitTime(ActionClass a)
    {
        switch (a)
        {
            case ActionClass.Slash: return slashHitTime;
            case ActionClass.Slash2: return slash2HitTime;
            case ActionClass.Slash3: return slash3HitTime;
            default: return crouchSlashHitTime;
        }
    }

    private IEnumerator WaitEnter(string state, float timeout)
    {
        float t = 0f;
        while (!InState(state) && t < timeout) { t += Time.deltaTime; yield return null; }
    }

    private IEnumerator DoAction(ActionClass a)
    {
        IsBusy = true;
        busySince = Time.time;
        Stop();
        bool crouching = a == ActionClass.Crouch || a == ActionClass.CrouchBlock || a == ActionClass.CrouchSlash;

        try
        {
            switch (a)
            {
                case ActionClass.Slash:
                    currentSlash = ActionClass.Slash;
                    animator.SetTrigger("Slash");
                    while (true)
                    {
                        string st = currentSlash.ToString();
                        yield return WaitEnter(st, enterTimeout);
                        if (!InState(st)) break;   // 상태 진입 실패 → 취소

                        CurrentAction = currentSlash;
                        busySince = Time.time;
                        queuedSlash = null;
                        comboDecisionUsed = false;
                        comboWindowOpen = currentSlash != ActionClass.Slash3;
                        float t0 = Time.time;
                        bool hit = false;
                        while (InState(st))
                        {
                            if (!hit && Time.time - t0 >= HitTime(currentSlash)) { TryHit(); hit = true; }
                            yield return null;
                        }
                        comboWindowOpen = false;
                        if (queuedSlash == null) break;
                        currentSlash = queuedSlash.Value;   // 예약한 다음 콤보 단계로
                    }
                    break;

                case ActionClass.Block:
                case ActionClass.Jump:
                {
                    string st = a.ToString();
                    animator.SetTrigger(st);
                    yield return WaitEnter(st, enterTimeout);
                    if (!InState(st)) break;
                    CurrentAction = a;
                    while (InState(st)) yield return null;
                    break;
                }

                case ActionClass.CrouchBlock:
                case ActionClass.CrouchSlash:
                {
                    string st = a.ToString();
                    animator.SetBool("IsCrouching", true);
                    animator.SetTrigger(st);
                    // Idle → Crouch → 해당 상태로 두 번 넘어가므로 대기 시간을 넉넉히
                    yield return WaitEnter(st, enterTimeout * 2f);
                    if (!InState(st)) break;
                    CurrentAction = a;
                    float t0 = Time.time;
                    bool hit = a != ActionClass.CrouchSlash;
                    while (InState(st))
                    {
                        if (!hit && Time.time - t0 >= crouchSlashHitTime) { TryHit(); hit = true; }
                        yield return null;
                    }
                    break;
                }

                case ActionClass.Crouch:
                    animator.SetBool("IsCrouching", true);
                    CurrentAction = ActionClass.Crouch;
                    yield return new WaitForSeconds(actionDuration);
                    break;
            }

            // 동작 애니메이션이 완전히 끝날 때까지 대기 (끝나는 전환 포함)
            if (crouching) animator.SetBool("IsCrouching", false);
            while (AnimLocked()) yield return null;
        }
        finally
        {
            foreach (string t in ActionTriggers) animator.ResetTrigger(t);   // 실패·취소된 예약 제거
            if (crouching) animator.SetBool("IsCrouching", false);
            comboWindowOpen = false;
            queuedSlash = null;
            CurrentAction = ActionClass.Idle;
            IsBusy = false;
        }
    }

    private void TryHit()
    {
        if (opponent == null || opponent.health == null) { LastHitResult = "상대 HealthSystem 없음"; Log(); return; }
        if (opponent.health.IsDead) { LastHitResult = "상대 사망 상태"; Log(); return; }
        if (health != null && health.IsDead) return;

        // 판정: 몸 중심 사이 거리 + 정면 각도 (attackPoint 위치 오차에 영향받지 않음)
        Vector3 d = opponent.transform.position - transform.position;
        d.y = 0f;
        float dist = d.magnitude;
        float facing = dist > 1e-4f ? Vector3.Dot(FlatForward(), d / dist) : 1f;
        if (dist > attackRange + 0.5f) { LastHitResult = string.Format("빗나감: 거리 {0:F2} m", dist); Log(); return; }
        if (facing < hitFacingDot) { LastHitResult = string.Format("빗나감: 각도 {0:F2}", facing); Log(); return; }

        if (!applyDamage) { LastHitResult = "판정 성공 (applyDamage 꺼짐, SwordController가 처리)"; Log(); return; }

        int damage = Random.Range(minDamage, maxDamage + 1);
        if (ActionInfo.IsDefense(opponent.CurrentAction)) damage = 0;   // 기존 EnemyAI의 Block 판정과 동일한 취지

        opponent.health.TakeDamage(damage, d.normalized, 3f);
        if (damage > 0) HitsLanded++;
        LastHitResult = damage > 0 ? "명중: " + damage + " 데미지" : "상대 방어로 0 데미지";
        Log();
    }

    private void Log()
    {
        if (debugLog) Debug.Log("[Executor] 공격 판정 → " + LastHitResult);
    }

    private Vector3 FlatForward()
    {
        Vector3 f = transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void OnGUI()
    {
        if (!showOverlay || opponent == null) return;
        float dist = FlatDistance(transform.position, opponent.transform.position);
        NpcBrain brain = null;
        foreach (NpcBrain b in GetComponents<NpcBrain>()) if (b.enabled) { brain = b; break; }
        string text = string.Format(
            "[{0}]\n두뇌: {1}  상태: {9}\n거리: {2:F2} m (사거리 {3:F1})\n현재 행동: {4}  바쁨: {5}\n결정 횟수: {6}\n마지막 판정: {7}\n상대 HP: {8}",
            gameObject.name,
            brain != null ? brain.GetType().Name : "없음(켜진 Brain 없음!)",
            dist, attackRange, CurrentAction, IsBusy,
            brain != null ? brain.LatenciesMs.Count : 0,
            LastHitResult,
            opponent.health != null ? opponent.health.CurrentHP.ToString() : "HealthSystem 없음",
            brain != null ? brain.Status : "-");
        GUI.Box(new Rect(10, 10 + overlayIndex * 150, 360, 140), text);
    }

    private static int overlayCounter;
    private int overlayIndex;
    private void OnEnable() { overlayIndex = overlayCounter++ % 3; }

    private void Stop()
    {
        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }
        if (animator != null) animator.SetBool("IsMoving", false);
    }

    public void StopAll()
    {
        StopAllCoroutines();
        IsBusy = false;
        comboWindowOpen = false;
        queuedSlash = null;
        CurrentAction = ActionClass.Idle;
        if (health != null) health.IsBlocking = false;
        if (animator != null)
        {
            animator.SetBool("IsCrouching", false);
            foreach (string t in ActionTriggers) animator.ResetTrigger(t);
        }
        Stop();
    }
}
