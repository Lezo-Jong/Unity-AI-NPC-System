using UnityEngine;

public class SlashCombo : MonoBehaviour
{
    private Animator animator;
    private int comboStep = 0;

    private bool canReceiveInput = true;
    private float comboInputTimer = 0f;
    private float lastComboTime = 0f;

    public float inputBufferTime = 0.5f;  // 콤보 입력 유예 시간
    public float comboResetTime = 1.0f;   // 전체 콤보 리셋 시간

    void Start()
    {
        animator = GetComponent<Animator>();
    }

    void Update()
    {
        // 입력 감지 (왼쪽 클릭)
        if (Input.GetMouseButtonDown(0) && canReceiveInput)
        {
            // 앉은 상태에서 클릭하면 콤보를 진행하지 않음
            if (animator.GetBool("IsCrouching")) return; // ❗ 앉은 상태일 땐 무시

            // 콤보 단계에 따라 애니메이션 트리거 실행
            switch (comboStep)
            {
                case 0:
                    animator.SetTrigger("Slash");   // 첫 번째 Slash 트리거
                    break;
                case 1:
                    animator.SetTrigger("Slash2");  // 두 번째 Slash2 트리거
                    break;
                case 2:
                    animator.SetTrigger("Slash3");  // 세 번째 Slash3 트리거
                    break;
            }

            // 콤보 단계 증가
            comboStep++;

            // 콤보가 끝나면 초기화
            if (comboStep >= 3)
            {
                comboStep = 0;
            }

            // 마지막 콤보 타이밍 갱신
            lastComboTime = Time.time;

            // 입력을 받은 후에는 다시 입력을 받을 수 없도록 설정
            canReceiveInput = false;
        }

        // 유예 시간 타이머
        if (!canReceiveInput)
        {
            comboInputTimer += Time.deltaTime;

            if (comboInputTimer >= inputBufferTime)
            {
                // 유예 시간이 끝나면 콤보 리셋
                comboStep = 0;
                canReceiveInput = true;
                comboInputTimer = 0f;
            }
            else
            {
                // 유예 시간 내에 다시 클릭하면 콤보 계속 진행
                canReceiveInput = true;
            }
        }

        // 입력 후 시간이 너무 지나면 콤보 리셋
        if (Time.time - lastComboTime > comboResetTime)
        {
            comboStep = 0;
        }
    }
}
