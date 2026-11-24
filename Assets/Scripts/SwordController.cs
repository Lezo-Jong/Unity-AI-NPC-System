using UnityEngine;
using System.Collections;

public class SwordController : MonoBehaviour
{
    [Header("Sword Collider")]
    [SerializeField] private BoxCollider swordCollider; // 검 Collider
    [Header("Attack Settings")]
    public int minDamage = 5;
    public int maxDamage = 10;
    public float attackDelay = 0.15f;
    public float damageCooldown = 0.15f;

    [Header("Collider Heights")]
    public float slashHeight = 1f;        // Slash 높이
    public float crouchSlashHeight = 0.76f; // CrouchSlash 높이

    [Header("Hit Rule (리뷰어 2, Major 4)")]
    [Tooltip("켜면 예전 동작: 트리거 콜라이더(예: NPC의 반지름 4 SphereCollider, 실제 약 12 m)에 닿아도 명중. " +
             "2026-10 데이터 수집은 이 상태였음. 끄면(기본) 상대 몸 콜라이더에 실제로 닿을 때만 명중")]
    public bool legacyTriggerHits = false;
    [Tooltip("설정하면 레이어와 관계없이 이 대상만 맞힘. NPC끼리 대전에서 NpcActionExecutor가 설정 (HitMode.SwordCollider)")]
    public HealthSystem allowedTarget;
    [Tooltip("켜면 물리 트리거 이벤트 대신 NpcActionExecutor가 매 프레임 겹침을 직접 계산해 HandleHit을 호출 (NPC끼리 대전용)")]
    public bool manualOverlap;
    public BoxCollider SwordCollider => swordCollider;

    // 명중 알림: (맞은 대상, 실제 HP 감소량). 방어로 막히면 감소량 0
    public event System.Action<HealthSystem, int> Hit;

    private bool canAttack = true;
    private bool canDealDamage = true;
    private Transform swordTransform;

    private void Awake()
    {
        if (swordCollider != null)
        {
            swordTransform = swordCollider.transform;
            swordCollider.isTrigger = true;
            swordCollider.enabled = false;
        }
    }

    // 동작이 중간에 끊기면(사망·리스폰 등) Disable 이벤트가 안 불릴 수 있으므로 꺼질 때 칼 판정도 끔
    private void OnDisable()
    {
        if (swordCollider != null) swordCollider.enabled = false;
    }

    // 쿨다운 중에 죽어서 꺼지면 쿨다운 코루틴이 멈춰 canAttack/canDealDamage가 false로 남으므로,
    // 다시 켜질 때(리스폰) 초기화. 이게 없으면 부활 후 그 캐릭터의 칼은 영원히 데미지를 못 줌.
    private void OnEnable()
    {
        canAttack = true;
        canDealDamage = true;
    }

    // ────────────────────────────────
    // 일반 Slash Animation Event
    // ────────────────────────────────
    public void EnableSlashCollider()
    {
        if (swordCollider != null && swordTransform != null)
        {
            swordTransform.localPosition = new Vector3(0f, slashHeight, swordTransform.localPosition.z);
            swordCollider.enabled = true;
            HitDiagnostics.Swing(gameObject);
            Debug.Log($"[{gameObject.name}] Slash Collider ENABLE (Height {slashHeight})");
        }
    }

    public void DisableSlashCollider()
    {
        if (swordCollider != null)
        {
            swordCollider.enabled = false;
            Debug.Log($"[{gameObject.name}] Slash Collider DISABLE");
        }
    }

    // ────────────────────────────────
    // CrouchSlash Animation Event
    // ────────────────────────────────
    public void EnableCrouchSlashCollider()
    {
        if (swordCollider != null && swordTransform != null)
        {
            swordTransform.localPosition = new Vector3(0f, crouchSlashHeight, swordTransform.localPosition.z);
            swordCollider.enabled = true;
            HitDiagnostics.Swing(gameObject);
            Debug.Log($"[{gameObject.name}] CrouchSlash Collider ENABLE (Height {crouchSlashHeight})");
        }
    }

    public void DisableCrouchSlashCollider()
    {
        if (swordCollider != null)
        {
            swordCollider.enabled = false;
            Debug.Log($"[{gameObject.name}] CrouchSlash Collider DISABLE");
        }
    }

    // ────────────────────────────────
    // SwordHitbox.cs에서 호출
    // ────────────────────────────────
    public void HandleHit(Collider other)
    {
        if (!swordCollider.enabled) return;
        if (!canAttack || !canDealDamage) return;
        // 트리거(전투 범위 구체 등)는 몸이 아니므로 무시
        if (other.isTrigger && !legacyTriggerHits) return;

        HealthSystem health = other.GetComponentInParent<HealthSystem>();
        if (health == null || health.IsDead) return;

        if (allowedTarget != null)
        {
            if (health != allowedTarget) return;
        }
        else
        {
            int myLayer = gameObject.layer;
            int targetLayer = other.gameObject.layer;

            // PlayerWeapon → Enemy만, EnemyWeapon → Player만
            if ((myLayer == LayerMask.NameToLayer("PlayerWeapon") && targetLayer != LayerMask.NameToLayer("Enemy")) ||
                (myLayer == LayerMask.NameToLayer("EnemyWeapon") && targetLayer != LayerMask.NameToLayer("Player")))
                return;
        }

        int damage = Random.Range(minDamage, maxDamage + 1);
        Vector3 knockDir = (other.transform.position - transform.position).normalized;

        int hpBefore = health.CurrentHP;
        health.TakeDamage(damage, knockDir, 3f);
        HitDiagnostics.Hit(gameObject, health, other, damage, hpBefore - health.CurrentHP);
        Hit?.Invoke(health, hpBefore - health.CurrentHP);
        Debug.Log($"[{gameObject.name}] → {other.name}에게 {damage} 데미지!");

        StartCoroutine(AttackCooldown());
        StartCoroutine(DamageCooldown());
    }

    private IEnumerator AttackCooldown()
    {
        canAttack = false;
        yield return new WaitForSeconds(attackDelay);
        canAttack = true;
    }

    private IEnumerator DamageCooldown()
    {
        canDealDamage = false;
        yield return new WaitForSeconds(damageCooldown);
        canDealDamage = true;
    }
}
