using UnityEngine;

/// <summary>
/// Unity-AI-NPC-System(EnemyAI.cs, HealthSystem.cs)의 기존 코드는 한 줄도 건드리지 않고,
/// public 이벤트 구독 + public 필드 조정만으로 붙는 실력 기반 난이도 조정기입니다.
///
/// 전제(레포 구조 기준으로 확인한 값):
/// - HealthSystem: OnHealthChanged(Action&lt;int,int&gt;), CurrentHP, TakeDamage(...)  → 플레이어/적 모두에 붙어있음
/// - EnemyAI: chaseRange, attackRange, actionCooldown, minDamage, maxDamage 등 public 필드
///
/// ※ minDamage/maxDamage/actionCooldown의 정확한 타입(int vs float)은 레포 요약만으로는
///   100% 확신할 수 없었습니다. 컴파일 에러가 나면 아래 Apply 부분의 타입 캐스팅만 맞춰주시면 됩니다.
/// </summary>
public class DuelDifficultyController : MonoBehaviour
{
    [Header("연결 (인스펙터에서 드래그)")]
    public EnemyAI enemyAI;
    public HealthSystem playerHealth;
    public HealthSystem enemyHealth;

    [Header("난이도 커브 (x = skillScore 0~1)")]
    [Tooltip("적의 행동 쿨다운(초). 값이 낮을수록 더 자주/공격적으로 행동")]
    public AnimationCurve actionCooldownCurve = AnimationCurve.Linear(0, 2.2f, 1, 0.8f);

    [Tooltip("적의 최소 피해량")]
    public AnimationCurve minDamageCurve = AnimationCurve.Linear(0, 4, 1, 10);

    [Tooltip("적의 최대 피해량")]
    public AnimationCurve maxDamageCurve = AnimationCurve.Linear(0, 8, 1, 18);

    [Header("갱신 주기 (초)")]
    public float recalcInterval = 2f;

    [Header("현재 상태 (읽기 전용, 확인용)")]
    [SerializeField] private float skillScore = 0.5f;
    [SerializeField] private float damageDealtTotal;
    [SerializeField] private float damageTakenTotal;

    public float SkillScore => skillScore;

    private int lastPlayerHp;
    private int lastEnemyHp;
    private float recalcTimer;
    private bool initialized;

    private void Start()
    {
        if (playerHealth != null)
        {
            lastPlayerHp = playerHealth.CurrentHP;
            playerHealth.OnHealthChanged += HandlePlayerHealthChanged;
        }
        if (enemyHealth != null)
        {
            lastEnemyHp = enemyHealth.CurrentHP;
            enemyHealth.OnHealthChanged += HandleEnemyHealthChanged;
        }
        initialized = true;
        ApplyDifficulty(); // 시작할 때 기본값(skillScore=0.5) 한 번 적용
    }

    private void OnDestroy()
    {
        if (playerHealth != null) playerHealth.OnHealthChanged -= HandlePlayerHealthChanged;
        if (enemyHealth != null) enemyHealth.OnHealthChanged -= HandleEnemyHealthChanged;
    }

    // OnHealthChanged가 (currentHp, maxHp)로 온다고 가정 — 델타는 직접 계산하므로
    // 실제 시그니처가 (currentHp, delta)여도 아래 로직 자체는 안전합니다 (currentHp만 신뢰).
    private void HandlePlayerHealthChanged(int currentHp, int _unused)
    {
        int delta = lastPlayerHp - currentHp;
        if (delta > 0) damageTakenTotal += delta;
        lastPlayerHp = currentHp;
    }

    private void HandleEnemyHealthChanged(int currentHp, int _unused)
    {
        int delta = lastEnemyHp - currentHp;
        if (delta > 0) damageDealtTotal += delta;
        lastEnemyHp = currentHp;
    }

    private void Update()
    {
        if (!initialized) return;
        recalcTimer += Time.deltaTime;
        if (recalcTimer >= recalcInterval)
        {
            recalcTimer = 0f;
            RecalculateSkillScore();
            ApplyDifficulty();
        }
    }

    private void RecalculateSkillScore()
    {
        float total = damageDealtTotal + damageTakenTotal;
        float raw = total > 0.01f ? damageDealtTotal / total : 0.5f;
        // 급격한 변화 방지용 이동평균
        skillScore = Mathf.Lerp(skillScore, raw, 0.25f);
    }

    private void ApplyDifficulty()
    {
        if (enemyAI == null) return;

        // ↓↓↓ 컴파일 에러가 나면 이 3줄의 타입 캐스팅만 실제 EnemyAI 필드 타입에 맞게 고쳐주세요 ↓↓↓
        enemyAI.actionCooldown = actionCooldownCurve.Evaluate(skillScore);
        enemyAI.minDamage = Mathf.RoundToInt(minDamageCurve.Evaluate(skillScore));
        enemyAI.maxDamage = Mathf.RoundToInt(maxDamageCurve.Evaluate(skillScore));
    }

    /// <summary>새 대결(라운드) 시작 시 누적 피해량만 초기화하고 skillScore는 유지하고 싶을 때 호출</summary>
    public void ResetRoundDamageCounters()
    {
        damageDealtTotal = 0f;
        damageTakenTotal = 0f;
    }
}
