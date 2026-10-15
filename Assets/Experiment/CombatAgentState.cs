using UnityEngine;

// 플레이어와 NPC 양쪽에 붙이는 상태 추적 컴포넌트.
// - 속도, 현재 행동, 마지막 공격 시각을 매 프레임 갱신합니다.
// - NPC(NpcActionExecutor 보유)는 실행 중인 행동을 그대로 사용하고,
//   플레이어는 Animator 상태 이름으로 행동을 판별합니다(학습 라벨).
[DefaultExecutionOrder(-50)]
public class CombatAgentState : MonoBehaviour
{
    public HealthSystem health;
    public Animator animator;

    [Header("Animator 상태 이름 → 행동 라벨 매핑 (플레이어 Animator 기준으로 확인 필요)")]
    public string[] slashStates = { "Slash" };
    public string[] slash2States = { "Slash2" };
    public string[] slash3States = { "Slash3" };
    public string[] blockStates = { "Block" };
    public string[] crouchBlockStates = { "CrouchBlock" };
    public string[] jumpStates = { "Jump" };
    public string[] crouchStates = { "Crouch" };
    public string[] crouchSlashStates = { "CrouchSlash" };

    [Tooltip("이 속도(m/s)보다 빠르면 Move, 아니면 Idle")]
    public float moveSpeedThreshold = 0.1f;

    public Vector3 Velocity { get; private set; }
    public ActionClass CurrentAction { get; private set; } = ActionClass.Idle;
    public float LastAttackTime { get; private set; } = -999f;

    public float HpRatio
    {
        get
        {
            if (health == null || health.MaxHP <= 0) return 1f;
            return Mathf.Clamp01((float)health.CurrentHP / health.MaxHP);
        }
    }

    private NpcActionExecutor executor;
    private Vector3 lastPosition;

    private void Awake()
    {
        if (health == null) health = GetComponent<HealthSystem>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        executor = GetComponent<NpcActionExecutor>();
        lastPosition = transform.position;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0f)
        {
            Vector3 v = (transform.position - lastPosition) / dt;
            v.y = 0f;
            Velocity = Vector3.Lerp(Velocity, v, 0.5f);
        }
        lastPosition = transform.position;

        CurrentAction = executor != null ? executor.CurrentAction : ReadFromAnimator();
        if (ActionInfo.IsAttack(CurrentAction)) LastAttackTime = Time.time;
    }

    public void ResetState()
    {
        Velocity = Vector3.zero;
        lastPosition = transform.position;
        LastAttackTime = -999f;
        CurrentAction = ActionClass.Idle;
    }

    private ActionClass ReadFromAnimator()
    {
        if (animator != null && animator.isActiveAndEnabled)
        {
            AnimatorStateInfo st = animator.GetCurrentAnimatorStateInfo(0);
            // 구체적인 상태부터 검사
            if (Match(st, crouchSlashStates)) return ActionClass.CrouchSlash;
            if (Match(st, crouchBlockStates)) return ActionClass.CrouchBlock;
            if (Match(st, slash3States)) return ActionClass.Slash3;
            if (Match(st, slash2States)) return ActionClass.Slash2;
            if (Match(st, slashStates)) return ActionClass.Slash;
            if (Match(st, blockStates)) return ActionClass.Block;
            if (Match(st, jumpStates)) return ActionClass.Jump;
            if (Match(st, crouchStates)) return ActionClass.Crouch;
        }
        return Velocity.magnitude > moveSpeedThreshold ? ActionClass.Move : ActionClass.Idle;
    }

    private static bool Match(AnimatorStateInfo st, string[] names)
    {
        if (names == null) return false;
        foreach (string n in names)
            if (!string.IsNullOrEmpty(n) && st.IsName(n)) return true;
        return false;
    }
}
