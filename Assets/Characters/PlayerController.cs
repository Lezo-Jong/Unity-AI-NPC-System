using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    private Animator animator;
    private CharacterController controller;

    [Header("Movement Settings")]
    public float walkSpeed = 4.5f;
    public float runSpeed = 8f;
    public float jumpHeight = 1.2f;
    public float gravity = -9.81f;
    private Vector3 velocity;
    private bool isJumping;

    [Header("Camera Settings")]
    public Transform cameraFollow;
    public float mouseSensitivity = 5f;
    private float xRotation = 0f;

    // 동작(공격·방어·점프) 중에는 새 입력을 받지 않습니다 (NPC의 IsBusy와 같은 규칙).
    // 입력이 쌓였다가 동작이 끝난 뒤 뒤늦게 실행되면, 행동 라벨과 그 행동을 고른 상황이 어긋나기 때문입니다.
    // 예외: Slash → Slash2 → Slash3 콤보는 앞 동작 중에 다음 단계를 예약할 수 있습니다.
    static readonly int[] ActionStates =
    {
        Animator.StringToHash("Base Layer.Slash"), Animator.StringToHash("Base Layer.Slash2"),
        Animator.StringToHash("Base Layer.Slash3"), Animator.StringToHash("Base Layer.Block"),
        Animator.StringToHash("Base Layer.CrouchBlock"), Animator.StringToHash("Base Layer.CrouchSlash"),
        Animator.StringToHash("Base Layer.Jump"),
    };
    static readonly string[] ActionTriggers = { "Slash", "Slash2", "Slash3", "Block", "CrouchBlock", "CrouchSlash", "Jump" };

    void Start()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        bool locked = IsActionLocked();
        if (!locked) ClearStaleTriggers();

        HandleMovement();
        HandleMouseLook();
        HandleJump(locked);
        HandleCrouch(locked);
        HandleSlashCombo(locked);
    }

    static bool IsAction(AnimatorStateInfo st)
    {
        foreach (int h in ActionStates) if (st.fullPathHash == h) return true;
        return false;
    }

    // 현재 동작 중이거나 동작으로 넘어가는 중이면 true
    bool IsActionLocked()
    {
        if (IsAction(animator.GetCurrentAnimatorStateInfo(0))) return true;
        return animator.IsInTransition(0) && IsAction(animator.GetNextAnimatorStateInfo(0));
    }

    // 동작이 끝난 뒤 남아 있는 예약 입력 제거
    void ClearStaleTriggers()
    {
        if (animator.IsInTransition(0)) return;
        foreach (string t in ActionTriggers) animator.ResetTrigger(t);
    }

    // ──────────────────────────────
    // 이동
    // ──────────────────────────────
    void HandleMovement()
    {
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");
        bool isRunning = Input.GetKey(KeyCode.LeftShift) && vertical > 0;

        float currentSpeed = isRunning ? runSpeed : walkSpeed;
        if (vertical < 0) currentSpeed = walkSpeed;

        Vector3 moveDirection = transform.forward * vertical + transform.right * horizontal;
        controller.Move(moveDirection * currentSpeed * Time.deltaTime);

        // 애니메이터 파라미터
        animator.SetFloat("MoveX", horizontal * (isRunning ? 2f : 1f));
        animator.SetFloat("MoveY", vertical * (isRunning ? 2f : 1f));
        animator.SetBool("IsMoving", horizontal != 0 || vertical != 0);

        if (controller.isGrounded && velocity.y < 0) velocity.y = -2f;
        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    // ──────────────────────────────
    // 점프
    // ──────────────────────────────
    void HandleJump(bool locked)
    {
        if (controller.isGrounded)
        {
            if (isJumping)
            {
                animator.SetBool("Jump", false);
                isJumping = false;
            }

            if (Input.GetKeyDown(KeyCode.Space) && !locked)
            {
                velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
                animator.SetBool("Jump", true);
                isJumping = true;
            }
        }
    }

    // ──────────────────────────────
    // 마우스 시점
    // ──────────────────────────────
    void HandleMouseLook()
    {
        if (cameraFollow == null) return;

        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -45f, 75f);

        transform.Rotate(Vector3.up * mouseX);
        cameraFollow.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }

    // ──────────────────────────────
    // Crouch 처리
    // ──────────────────────────────
    void HandleCrouch(bool locked)
    {
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        bool crouchKeyDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        bool isCrouchAction =
            state.IsName("CrouchSlash") ||
            state.IsName("CrouchBlock") ||
            state.IsName("ShieldCrouch");

        // Crouch 상태 갱신 (CrouchSlash, Block 중이 아니면)
        if (!isCrouchAction)
        {
            animator.SetBool("IsCrouching", crouchKeyDown);
        }

        // 앉은 상태에서 좌/우클릭 처리
        if (crouchKeyDown && state.IsName("Crouch"))
        {
            if (Input.GetMouseButtonDown(0))
            {
                animator.ResetTrigger("Slash");
                animator.ResetTrigger("Slash2");
                animator.ResetTrigger("Slash3");
                animator.SetTrigger("CrouchSlash");
            }
            else if (Input.GetMouseButtonDown(1))
            {
                animator.SetTrigger("CrouchBlock");
            }
        }

        // 서 있는 상태에서 우클릭 → Block
        if (!crouchKeyDown && !locked)
        {
            if (Input.GetMouseButtonDown(1))
            {
                animator.SetTrigger("Block");
            }
        }

        // CrouchSlash 종료 후 처리
        if (state.IsName("CrouchSlash") && state.normalizedTime >= 1f)
        {
            animator.ResetTrigger("CrouchSlash");
            if (crouchKeyDown)
            {
                animator.SetBool("IsCrouching", true);
                animator.Play("Crouch", 0, 0f);
            }
            else
            {
                animator.SetBool("IsCrouching", false);
            }
        }
    }

    // ──────────────────────────────
    // Slash 콤보 처리 (Ctrl 안 누를 때만)
    // 대기·이동 중 클릭 → Slash, Slash 중 클릭 → Slash2 예약, Slash2 중 클릭 → Slash3 예약, Slash3·다른 동작 중 클릭 → 무시
    // ──────────────────────────────
    void HandleSlashCombo(bool locked)
    {
        if (!Input.GetMouseButtonDown(0) || Input.GetKey(KeyCode.LeftControl)) return;

        AnimatorStateInfo st = animator.IsInTransition(0)
            ? animator.GetNextAnimatorStateInfo(0)
            : animator.GetCurrentAnimatorStateInfo(0);

        if (st.IsName("Slash")) animator.SetTrigger("Slash2");
        else if (st.IsName("Slash2")) animator.SetTrigger("Slash3");
        else if (!locked) animator.SetTrigger("Slash");
    }
}
