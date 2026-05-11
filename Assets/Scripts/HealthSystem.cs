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
        StartCoroutine(RespawnCoroutine());
    }

    private IEnumerator RespawnCoroutine()
    {
        yield return new WaitForSeconds(0.2f);
        gameObject.SetActive(false);
        yield return new WaitForSeconds(respawnDelay);

        transform.position = spawnPoint;
        currentHP = maxHP;
        isDead = false;

        gameObject.SetActive(true);
        Debug.Log($"{gameObject.name} 리스폰 완료!");
    }

    public void Respawn()
    {
        isDead = false;
        currentHP = maxHP;
        transform.position = spawnPoint;
        transform.rotation = spawnRotation;

        gameObject.SetActive(true);
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

    private IEnumerator AutoRegen()
    {
        while (true)
        {
            yield return new WaitForSeconds(regenInterval);

            if (!isDead && IsIdle && Time.time - lastDamageTime >= regenDelay && currentHP < maxHP)
            {
                Heal(regenAmount);
            }
        }
    }
}
