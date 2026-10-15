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

    [Header("Crouch & Combo Settings")]
    public float comboInputBufferTime = 0.5f;
    public float comboResetTime = 1f;

    private int comboStep = 0;
    private bool canReceiveComboInput = true;
    private float comboInputTimer = 0f;
    private float lastComboTime = 0f;

    void Start()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleMovement();
        HandleMouseLook();
        HandleJump();
        HandleCrouch();
        HandleSlashCombo();
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
    void HandleJump()
    {
        if (controller.isGrounded)
        {
            if (isJumping)
            {
                animator.SetBool("Jump", false);
                isJumping = false;
            }

            if (Input.GetKeyDown(KeyCode.Space))
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
    void HandleCrouch()
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
        if (!crouchKeyDown && !state.IsName("CrouchBlock"))
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
    // ──────────────────────────────
    void HandleSlashCombo()
    {
        bool crouchKeyDown = Input.GetKey(KeyCode.LeftControl);

        if (Input.GetMouseButtonDown(0) && canReceiveComboInput && !crouchKeyDown)
        {
            switch (comboStep)
            {
                case 0: animator.SetTrigger("Slash"); break;
                case 1: animator.SetTrigger("Slash2"); break;
                case 2: animator.SetTrigger("Slash3"); break;
            }

            comboStep++;
            if (comboStep >= 3) comboStep = 0;

            lastComboTime = Time.time;
            canReceiveComboInput = false;
        }

        // 유예 시간
        if (!canReceiveComboInput)
        {
            comboInputTimer += Time.deltaTime;
            if (comboInputTimer >= comboInputBufferTime)
            {
                comboStep = 0;
                canReceiveComboInput = true;
                comboInputTimer = 0f;
            }
            else
            {
                canReceiveComboInput = true;
            }
        }

        // 전체 콤보 리셋
        if (Time.time - lastComboTime > comboResetTime)
        {
            comboStep = 0;
        }
    }
}
