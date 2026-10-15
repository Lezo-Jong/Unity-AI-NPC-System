// Baseline 2: 결정론적 유한 상태 기계.
// 추적 → (상대 공격 시) 방어 → (쿨다운 후) Slash→Slash2→Slash3 콤보 → 대기
public class FSMBrain : NpcBrain
{
    public float attackRange = 2.5f;
    public float comboCooldown = 1.0f;

    private int comboStep;

    protected override void OnEnable()
    {
        base.OnEnable();
        comboStep = 0;
    }

    protected override ActionClass Decide(float[] f)
    {
        return Rule(f, attackRange, comboCooldown, ref comboStep);
    }

    public static ActionClass Rule(float[] f, float range, float cooldown, ref int step)
    {
        if (f[0] > range) { step = 0; return ActionClass.Move; }
        if (f[8] > 0.5f) { step = 0; return f[10] > 0.5f ? ActionClass.CrouchBlock : ActionClass.Block; }

        // 콤보 진행 중이면 바로 다음 단계, 새 콤보는 쿨다운 이후
        if (step > 0 || f[14] >= cooldown)
        {
            ActionClass a = step == 0 ? ActionClass.Slash : (step == 1 ? ActionClass.Slash2 : ActionClass.Slash3);
            step = (step + 1) % 3;
            return a;
        }
        return ActionClass.Idle;
    }
}
