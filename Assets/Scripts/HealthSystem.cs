using UnityEngine;
using System;
using System.Collections;

public class HealthSystem : MonoBehaviour
{
    [Header("Health Settings")]
    public int maxHP = 100;
    private int currentHP;

    [Header("Respawn Settings")]
    public float respawnDelay = 30f;
    private Vector3 spawnPoint;
    private Quaternion spawnRotation;

    [Header("Experiment Settings")]
    [Tooltip("자동 회복 사용 (MatchRunner가 실험 중 자동으로 끔)")]
    public bool autoRegen = true;
    [Tooltip("사망 후 자동 리스폰 사용 (MatchRunner가 실험 중 자동으로 끔)")]
    public bool autoRespawn = true;

    [Header("Regen Settings")]
    public float regenDelay = 5f;
    public float regenInterval = 1f;
    public int regenAmount = 5;
    private float lastDamageTime;

    // ✅ Block 상태 체크용
    public bool IsBlocking { get; set; } = false;

    private bool isDead = false;

    public int CurrentHP => currentHP;
    public int MaxHP => maxHP;
    public bool IsDead => isDead;
    public bool IsIdle { get; set; } = true;

    public event Action<int, int> OnHealthChanged;
    public event Action OnDeath;

    private Coroutine regenCoroutine;
    private Animator animator;

    private void Start()
    {
        currentHP = maxHP;
        spawnPoint = transform.position;
        spawnRotation = transform.rotation;
        OnHealthChanged?.Invoke(currentHP, maxHP);

        animator = GetComponent<Animator>();
        regenCoroutine = StartCoroutine(AutoRegen());
    }

    // ✅ 데미지 처리
    public void TakeDamage(int amount, Vector3? knockDir = null, float knockForce = 0f)
    {
        if (isDead) return;

        // ✅ Block 중이면 데미지 무효화
        if (IsBlocking)
        {
            Debug.Log($"{gameObject.name} Block 성공! 데미지 무효화됨.");
            return;
        }

        currentHP -= amount;
        if (currentHP < 0) currentHP = 0;
        lastDamageTime = Time.time;

        Debug.Log($"{gameObject.name} → {amount} 데미지 받음! (HP {currentHP}/{maxHP})");
        OnHealthChanged?.Invoke(currentHP, maxHP);

        if (knockDir.HasValue && knockForce > 0f)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
                rb.AddForce(knockDir.Value * knockForce, ForceMode.Impulse);
        }

        if (currentHP <= 0)
            Die();
    }

    private void Heal(int amount)
    {
        if (isDead) return;
        currentHP += amount;
        if (currentHP > maxHP) currentHP = maxHP;
        OnHealthChanged?.Invoke(currentHP, maxHP);
    }

    private void Die()
    {
        isDead = true;
        Debug.Log($"{gameObject.name} 사망!");
        OnDeath?.Invoke();
        if (autoRespawn) Runner.StartCoroutine(RespawnCoroutine());
    }

    // 사망 중에는 이 오브젝트가 꺼지므로, 리스폰 타이머는 항상 켜져 있는 별도 오브젝트에서 돌립니다.
    // (자기 자신에서 돌리면 SetActive(false) 순간 코루틴도 멈춰서 영원히 부활하지 않음)
    private static MonoBehaviour runner;
    private static MonoBehaviour Runner
    {
        get
        {
            if (runner == null)
            {
                GameObject go = new GameObject("[RespawnRunner]");
                go.hideFlags = HideFlags.HideInHierarchy;
                runner = go.AddComponent<RespawnRunner>();
            }
            return runner;
        }
    }

    private class RespawnRunner : MonoBehaviour { }

    private IEnumerator RespawnCoroutine()
    {
        yield return new WaitForSeconds(0.2f);
        if (this == null || !isDead) yield break;
        gameObject.SetActive(false);
        yield return new WaitForSeconds(respawnDelay);
        if (this == null || !isDead) yield break;
        Respawn();
    }

    public void Respawn()
    {
        isDead = false;
        IsBlocking = false;
        currentHP = maxHP;
        lastDamageTime = -999f;
        transform.SetPositionAndRotation(spawnPoint, spawnRotation);   // 꺼진 상태에서 옮기므로 CharacterController/NavMeshAgent도 켜질 때 이 위치를 따름

        gameObject.SetActive(true);
        if (regenCoroutine != null) StopCoroutine(regenCoroutine);
        regenCoroutine = StartCoroutine(AutoRegen());   // 꺼질 때 멈춘 자동 회복 다시 시작
        OnHealthChanged?.Invoke(currentHP, maxHP);

        if (animator != null)
        {
            if (animator.HasState(0, Animator.StringToHash("Respawn")))
                animator.SetTrigger("Respawn");
            else
                animator.Play("Idle", 0, 0f);
        }

        Debug.Log($"{gameObject.name} 리스폰 완료!");
    }

    // ✅ 실험용 초기화: 코루틴 정지, HP·상태 초기화, 지정 위치로 이동
    public void ResetForMatch(Vector3 position, Quaternion rotation)
    {
        StopAllCoroutines();
        gameObject.SetActive(true);
        isDead = false;
        IsBlocking = false;
        currentHP = maxHP;
        lastDamageTime = -999f;
        transform.SetPositionAndRotation(position, rotation);
        OnHealthChanged?.Invoke(currentHP, maxHP);

        if (animator == null) animator = GetComponent<Animator>();
        if (animator != null && animator.HasState(0, Animator.StringToHash("Idle")))
            animator.Play("Idle", 0, 0f);

        regenCoroutine = StartCoroutine(AutoRegen());
    }

    private IEnumerator AutoRegen()
    {
        while (true)
        {
            yield return new WaitForSeconds(regenInterval);

            if (autoRegen && !isDead && IsIdle && Time.time - lastDamageTime >= regenDelay && currentHP < maxHP)
            {
                Heal(regenAmount);
            }
        }
    }
}
