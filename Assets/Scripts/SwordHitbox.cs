using UnityEngine;

public class SwordHitbox : MonoBehaviour
{
    private SwordController swordController;
    private int myWeaponLayer;

    private void Awake()
    {
        // SwordController 찾기 (본체에 있음)
        swordController = GetComponentInParent<SwordController>();
        myWeaponLayer = gameObject.layer; // 내 무기 레이어
    }

    private void OnTriggerEnter(Collider other)
    {
        if (swordController == null) return;

        int targetLayer = other.gameObject.layer;

        // ✅ Player 무기 → Enemy만 감지
        if (myWeaponLayer == LayerMask.NameToLayer("PlayerWeapon") &&
            targetLayer != LayerMask.NameToLayer("Enemy"))
            return;

        // ✅ Enemy 무기 → Player만 감지
        if (myWeaponLayer == LayerMask.NameToLayer("EnemyWeapon") &&
            targetLayer != LayerMask.NameToLayer("Player"))
            return;

        // ✅ 자기 본체 충돌 무시 (예: PlayerWeapon → Player)
        if (other.GetComponentInParent<SwordController>() == swordController)
            return;

        // 통과된 경우에만 데미지 처리
        swordController.HandleHit(other);
    }
}
