using UnityEngine;

// Baseline 1: 사거리 밖이면 추적, 사거리 안이면 9개 행동 중 균등 무작위 선택.
public class RandomBrain : NpcBrain
{
    public float attackRange = 2.5f;

    private static readonly ActionClass[] InRange =
    {
        ActionClass.Idle, ActionClass.Slash, ActionClass.Slash2, ActionClass.Slash3,
        ActionClass.Block, ActionClass.CrouchBlock, ActionClass.Jump,
        ActionClass.Crouch, ActionClass.CrouchSlash
    };

    protected override ActionClass Decide(float[] f)
    {
        if (f[0] > attackRange) return ActionClass.Move;
        return InRange[Random.Range(0, InRange.Length)];
    }
}
