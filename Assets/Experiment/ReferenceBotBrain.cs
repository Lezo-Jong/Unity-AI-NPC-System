using UnityEngine;

// 자동 평가용 고정 상대(Reference Opponent). 세 NPC 조건 모두 이 상대와 대전합니다.
// 확률 규칙 + 시드 고정으로 매 판 같은 조건을 재현합니다.
public class ReferenceBotBrain : NpcBrain
{
    public float attackRange = 2.5f;
    [Range(0f, 1f)] public float pBlockWhenAttacked = 0.5f;
    [Range(0f, 1f)] public float pAttack = 0.4f;
    [Range(0f, 1f)] public float pEvade = 0.1f;

    protected override ActionClass Decide(float[] f)
    {
        if (f[0] > attackRange) return ActionClass.Move;

        if (f[8] > 0.5f && Random.value < pBlockWhenAttacked)
            return f[10] > 0.5f ? ActionClass.CrouchBlock : ActionClass.Block;

        float r = Random.value;
        if (r < pAttack)
        {
            // 콤보 진행은 Executor 규칙이 처리 (Slash3 요청 → 가능한 단계로 대체)
            float k = Random.value;
            if (k < 0.7f) return ActionClass.Slash3;
            return ActionClass.CrouchSlash;
        }
        if (r < pAttack + pEvade)
            return Random.value < 0.5f ? ActionClass.Jump : ActionClass.Crouch;
        return ActionClass.Idle;
    }
}
