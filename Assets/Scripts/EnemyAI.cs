using UnityEngine;
using UnityEngine.AI;
using System.Collections;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(HealthSystem))]
public class EnemyAI : MonoBehaviour
{
    [Header("AI Settings")]
    public Transform player;
    public float chaseRange = 10f;
    public float attackRange = 2.5f;
    public float actionCooldown = 2f;

    [Header("Combat Settings")]
    public int minDamage = 5;
    public int maxDamage = 12;
    public Transform attackPoint;
    public float attackRadius = 1.5f;

    private NavMeshAgent agent;
    private Animator animator;
    private HealthSystem healthSystem;

    private bool isBusy = false; // 행동 중일 때
    private Vector3 lastPosition;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        healthSystem = GetComponent<HealthSystem>();

        if (player == null)
        {
            GameObject pObj = GameObject.FindGameObjectWithTag("Player");
            if (pObj != null) player = pObj.transform;
        }
    }

    private void Update()
    {
        if (healthSystem.IsDead) return;
        if (player == null) return;

        float distance = Vector3.Distance(transform.position, player.position);

        // 이동 애니메이션
        Vector3 velocity = (transform.position - lastPosition) / Time.deltaTime;
        lastPosition = transform.position;
        animator.SetFloat("MoveX", velocity.x);
        animator.SetFloat("MoveY", velocity.z);
        animator.SetBool("IsMoving", agent.velocity.magnitude > 0.1f);

        if (isBusy) return;

        if (distance <= attackRange)
            StartCoroutine(DoRandomAction());
        else if (distance <= chaseRange)
        {
            agent.isStopped = false;
            agent.SetDestination(player.position);
        }
        else
        {
            agent.isStopped = true;
        }
    }

   private IEnumerator DoRandomAction()
{
    isBusy = true;
    agent.isStopped = true;
    animator.SetBool("IsMoving", false);

    int action = Random.Range(0, 6);

    switch (action)
    {
        // ───────────── 일반 Slash 콤보 ─────────────
        case 0:
        case 1:
        case 2:
            int comboSteps = Random.Range(1, 4);
            yield return StartCoroutine(PlaySlashCombo(comboSteps));
            break;

        // ───────────── Block ─────────────
        case 3:
            animator.SetTrigger("Block");
            Debug.Log("[EnemyAI] Block");
            yield return new WaitForSeconds(1f);
            break;

        // ───────────── Jump ─────────────
        case 4:
            animator.SetTrigger("Jump");
            Debug.Log("[EnemyAI] Jump");
            yield return new WaitForSeconds(1f);
            break;

        // ───────────── Crouch 계열 ─────────────
        case 5:
            int crouchType = Random.Range(0, 3);

            if (crouchType == 0)
            {
                // 단순 앉기
                animator.SetBool("IsCrouching", true);
                Debug.Log("[EnemyAI] Crouch");
                yield return new WaitForSeconds(1f);
                animator.SetBool("IsCrouching", false);
            }
            else if (crouchType == 1)
            {
                // 앉아서 공격
                animator.SetBool("IsCrouching", true);
                animator.SetTrigger("CrouchSlash");
                Debug.Log("[EnemyAI] CrouchSlash");

                yield return new WaitForSeconds(1f);
                DealCrouchSlashDamage();

                animator.SetBool("IsCrouching", false);
            }
            else
            {
                // 앉아서 방어
                animator.SetBool("IsCrouching", true);
                animator.SetTrigger("CrouchBlock");
                Debug.Log("[EnemyAI] CrouchBlock");

                // ShieldBlockChecker와 연동해서 Block 상태 활성화
                ShieldBlockChecker shield = GetComponentInChildren<ShieldBlockChecker>();
                if (shield != null) shield.EnableShieldForCrouchBlock();

                yield return new WaitForSeconds(1f);

                if (shield != null) shield.DisableShield();
                animator.SetBool("IsCrouching", false);
            }
            break;
    }

    // 행동 끝나면 이동 재개
    agent.isStopped = false;
    isBusy = false;
}



    private IEnumerator PlaySlashCombo(int steps)
    {
        if (steps >= 1)
        {
            animator.SetTrigger("Slash");
            yield return new WaitForSeconds(0.5f);
            DealSlashDamage();
        }
        if (steps >= 2)
        {
            animator.SetTrigger("Slash2");
            yield return new WaitForSeconds(0.5f);
            DealSlashDamage();
        }
        if (steps == 3)
        {
            animator.SetTrigger("Slash3");
            yield return new WaitForSeconds(0.5f);
            DealSlashDamage();
        }
    }

    private void DealSlashDamage()
    {
        if (attackPoint == null) return;
        Collider[] hitPlayers = Physics.OverlapSphere(attackPoint.position, attackRadius, LayerMask.GetMask("Player"));

        foreach (var hit in hitPlayers)
        {
            HealthSystem playerHealth = hit.GetComponentInParent<HealthSystem>();
            if (playerHealth != null && !playerHealth.IsDead)
            {
                int damage = Random.Range(minDamage, maxDamage + 1);

                // Block 상태일 때 데미지 무효화
                Animator playerAnim = hit.GetComponentInParent<Animator>();
                if (playerAnim != null && playerAnim.GetCurrentAnimatorStateInfo(0).IsName("Block"))
                    damage = 0;

                playerHealth.TakeDamage(damage, (hit.transform.position - transform.position).normalized, 3f);
                Debug.Log($"[EnemyAI] → {hit.name}에게 {damage} 데미지!");
            }
        }
    }

    private void DealCrouchSlashDamage()
    {
        if (attackPoint == null) return;
        Collider[] hitPlayers = Physics.OverlapSphere(attackPoint.position, attackRadius, LayerMask.GetMask("Player"));

        foreach (var hit in hitPlayers)
        {
            HealthSystem playerHealth = hit.GetComponentInParent<HealthSystem>();
            if (playerHealth != null && !playerHealth.IsDead)
            {
                int damage = Random.Range(minDamage, maxDamage + 1);
                playerHealth.TakeDamage(damage, (hit.transform.position - transform.position).normalized, 3f);
                Debug.Log($"[EnemyAI] → {hit.name}에게 CrouchSlash {damage} 데미지!");
            }
        }
    }

    // 공격 범위 시각화
    private void OnDrawGizmosSelected()
    {
        if (attackPoint != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(attackPoint.position, attackRadius);
        }
    }
}
