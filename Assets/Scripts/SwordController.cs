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

    // ────────────────────────────────
    // 일반 Slash Animation Event
    // ────────────────────────────────
    public void EnableSlashCollider()
    {
        if (swordCollider != null && swordTransform != null)
        {
            swordTransform.localPosition = new Vector3(0f, slashHeight, swordTransform.localPosition.z);
            swordCollider.enabled = true;
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

        int myLayer = gameObject.layer;
        int targetLayer = other.gameObject.layer;

        // PlayerWeapon → Enemy만, EnemyWeapon → Player만
        if ((myLayer == LayerMask.NameToLayer("PlayerWeapon") && targetLayer != LayerMask.NameToLayer("Enemy")) ||
            (myLayer == LayerMask.NameToLayer("EnemyWeapon") && targetLayer != LayerMask.NameToLayer("Player")))
            return;

        HealthSystem health = other.GetComponentInParent<HealthSystem>();
        if (health == null || health.IsDead) return;

        int damage = Random.Range(minDamage, maxDamage + 1);
        Vector3 knockDir = (other.transform.position - transform.position).normalized;

        health.TakeDamage(damage, knockDir, 3f);
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
