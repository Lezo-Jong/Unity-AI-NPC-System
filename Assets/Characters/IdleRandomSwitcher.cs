using UnityEngine;

public class IdleRandomSwitcher : MonoBehaviour
{
    private Animator animator;
    private float idleTimer = 0f;
    public float idleDelay = 10f;

    private bool isPlayingVariant = false;

    void Start()
    {
        animator = GetComponent<Animator>();
    }

    void Update()
    {
        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);

        // Idle 상태일 때
        if (stateInfo.IsName("Idle"))
        {
            idleTimer += Time.deltaTime;

            if (idleTimer >= idleDelay && !isPlayingVariant)
            {
                // 무작위 값 생성 (1 = Idle2, 2 = Idle3, 3 = Idle4)
                int variant = Random.Range(1, 4);
                animator.SetInteger("IdleVariant", variant); // IdleVariant 파라미터에 무작위 값 설정
                isPlayingVariant = true;
                idleTimer = 0f;
            }
        }
        // Idle2, Idle3, Idle4 상태일 때
        else if (stateInfo.IsName("Idle2") || stateInfo.IsName("Idle3") || stateInfo.IsName("Idle4"))
        {
            // 애니메이션이 끝났을 때 Idle 상태로 돌아가도록 처리
            if (stateInfo.normalizedTime >= 1f && !stateInfo.loop)
            {
                animator.SetInteger("IdleVariant", 0); // Idle 상태로 돌아가도록 설정
                isPlayingVariant = false;
            }
        }
        else
        {
            // Idle 상태가 아닐 때는 타이머를 초기화
            idleTimer = 0f;
        }
    }
}
