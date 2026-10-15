using UnityEngine;

public class ShieldBlockChecker : MonoBehaviour
{
    [Header("Shield Settings")]
    public BoxCollider shieldCollider; // 단일 Shield Collider
    public float blockHeight = 1f;     // 서 있는 Block 높이
    public float crouchBlockHeight = 0.76f; // CrouchBlock 높이

    private Transform shieldTransform;

    void Awake()
    {
        if (shieldCollider != null)
            shieldTransform = shieldCollider.transform;
    }

    // ────────────────────────────────
    // Animation Event 호출
    // ────────────────────────────────

    // Block 시작
    public void EnableShieldForBlock()
    {
        if (shieldCollider != null && shieldTransform != null)
        {
            shieldTransform.localPosition = new Vector3(0f, blockHeight, shieldTransform.localPosition.z);
            shieldCollider.enabled = true;
        }
    }

    // CrouchBlock 시작
    public void EnableShieldForCrouchBlock()
    {
        if (shieldCollider != null && shieldTransform != null)
        {
            shieldTransform.localPosition = new Vector3(0f, crouchBlockHeight, shieldTransform.localPosition.z);
            shieldCollider.enabled = true;
        }
    }

    // Block/CrouchBlock 종료
    public void DisableShield()
    {
        if (shieldCollider != null)
            shieldCollider.enabled = false;
    }

    // ────────────────────────────────
    // HealthSystem에서 사용
    // ────────────────────────────────
    public bool IsShieldActive()
    {
        return shieldCollider != null && shieldCollider.enabled;
    }
}
