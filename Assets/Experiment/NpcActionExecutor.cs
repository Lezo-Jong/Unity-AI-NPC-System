using System.Collections;
using UnityEngine;
using UnityEngine.AI;

// NPC 행동 실행기. Random / FSM / ML / 상대 봇 모두 이 컴포넌트로 행동을 실행하므로
// 조건 간 차이는 "행동 선택(Brain)"뿐입니다. (기존 EnemyAI의 타이밍·데미지 규칙을 그대로 따름)
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
    public float slashHitDelay = 0.5f;   // Slash 계열: 트리거 후 판정까지
    public float actionDuration = 1f;    // Block, Jump, Crouch, CrouchSlash 지속 시간
    public float comboWindow = 1.0f;     // Slash2/Slash3가 이어질 수 있는 시간
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
    public readonly int[] ExecutedCounts = new int[ActionInfo.Count];
    public int HitsLanded { get; private set; }

    private NavMeshAgent agent;
    private Animator animator;
    private HealthSystem health;
    private Vector3 lastPosition;
    private float lastSlashTime = -999f;
    private ActionClass lastSlash = ActionClass.Idle;
    private float busySince;

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
        if (IsBusy) return;
        if (health != null && health.IsDead) return;

        // 콤보 규칙: Slash2는 Slash 직후, Slash3는 Slash2 직후에만 가능 (아니면 앞 단계로 대체)
        bool inWindow = Time.time - lastSlashTime <= comboWindow;
        if (a == ActionClass.Slash3 && !(inWindow && lastSlash == ActionClass.Slash2))
            a = (inWindow && lastSlash == ActionClass.Slash) ? ActionClass.Slash2 : ActionClass.Slash;
        if (a == ActionClass.Slash2 && !(inWindow && lastSlash == ActionClass.Slash))
            a = ActionClass.Slash;

        ExecutedCounts[(int)a]++;
        if (debugLog)
            Debug.Log(string.Format("[Executor] {0} 실행 (거리 {1:F2} m)", a,
                opponent != null ? FlatDistance(transform.position, opponent.transform.position) : -1f));

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

    private IEnumerator DoAction(ActionClass a)
    {
        IsBusy = true;
        busySince = Time.time;
        CurrentAction = a;
        Stop();

        try
        {
        switch (a)
        {
            case ActionClass.Slash:
            case ActionClass.Slash2:
            case ActionClass.Slash3:
                animator.SetTrigger(a.ToString());
                yield return new WaitForSeconds(slashHitDelay);
                TryHit();
                lastSlashTime = Time.time;
                lastSlash = a;
                break;

            case ActionClass.Block:
                animator.SetTrigger("Block");
                if (health != null) health.IsBlocking = true;
                yield return new WaitForSeconds(actionDuration);
                if (health != null) health.IsBlocking = false;
                break;

            case ActionClass.CrouchBlock:
                animator.SetBool("IsCrouching", true);
                animator.SetTrigger("CrouchBlock");
                if (health != null) health.IsBlocking = true;
                yield return new WaitForSeconds(actionDuration);
                if (health != null) health.IsBlocking = false;
                animator.SetBool("IsCrouching", false);
                break;

            case ActionClass.Jump:
                animator.SetTrigger("Jump");
                yield return new WaitForSeconds(actionDuration);
                break;

            case ActionClass.Crouch:
                animator.SetBool("IsCrouching", true);
                yield return new WaitForSeconds(actionDuration);
                animator.SetBool("IsCrouching", false);
                break;

            case ActionClass.CrouchSlash:
                animator.SetBool("IsCrouching", true);
                animator.SetTrigger("CrouchSlash");
                yield return new WaitForSeconds(actionDuration);
                TryHit();
                animator.SetBool("IsCrouching", false);
                break;
        }
        }
        finally
        {
            if (health != null && ActionInfo.IsDefense(a)) health.IsBlocking = false;
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
        CurrentAction = ActionClass.Idle;
        if (health != null) health.IsBlocking = false;
        if (animator != null) animator.SetBool("IsCrouching", false);
        Stop();
    }
}
