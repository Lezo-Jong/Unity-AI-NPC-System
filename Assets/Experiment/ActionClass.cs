// 행동 클래스 정의. Python(train.py)의 ACTIONS 순서와 반드시 동일해야 합니다.
public enum ActionClass
{
    Idle = 0,
    Move = 1,
    Slash = 2,
    Slash2 = 3,
    Slash3 = 4,
    Block = 5,
    CrouchBlock = 6,
    Jump = 7,
    Crouch = 8,
    CrouchSlash = 9
}

public static class ActionInfo
{
    public const int Count = 10;

    public static bool IsAttack(ActionClass a)
    {
        return a == ActionClass.Slash || a == ActionClass.Slash2 ||
               a == ActionClass.Slash3 || a == ActionClass.CrouchSlash;
    }

    public static bool IsDefense(ActionClass a)
    {
        return a == ActionClass.Block || a == ActionClass.CrouchBlock;
    }

    public static bool IsCrouch(ActionClass a)
    {
        return a == ActionClass.Crouch || a == ActionClass.CrouchBlock ||
               a == ActionClass.CrouchSlash;
    }
}
