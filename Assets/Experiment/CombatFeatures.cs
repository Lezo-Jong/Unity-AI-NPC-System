using UnityEngine;

// 19차원 관측 벡터. 데이터 수집(플레이어 시점)과 추론(NPC 시점)에서 동일한 함수를 사용합니다.
// 순서를 바꾸면 Python(train.py)의 FEATURES도 같이 바꿔야 합니다.
public static class CombatFeatures
{
    public const int Count = 19;
    public const float MaxTimeSinceAttack = 5f;

    public static readonly string[] Names =
    {
        "dist",               // 0  상대까지 수평 거리 (m)
        "rel_x",              // 1  자기 기준 좌우 상대 위치
        "rel_z",              // 2  자기 기준 전후 상대 위치
        "self_hp",            // 3  자기 HP 비율
        "opp_hp",             // 4  상대 HP 비율
        "self_speed",         // 5  자기 수평 속도
        "opp_speed",          // 6  상대 수평 속도
        "closing_speed",      // 7  접근 속도 (+: 가까워지는 중)
        "opp_attacking",      // 8  상대 공격 중
        "opp_blocking",       // 9  상대 방어 중
        "opp_crouching",      // 10 상대 앉은 상태
        "opp_jumping",        // 11 상대 점프 중
        "self_facing",        // 12 자기가 상대를 바라보는 정도 (-1~1)
        "opp_facing",         // 13 상대가 자기를 바라보는 정도 (-1~1)
        "self_t_since_atk",   // 14 자기 마지막 공격 후 경과 시간 (최대 5 s)
        "opp_t_since_atk",    // 15 상대 마지막 공격 후 경과 시간 (최대 5 s)
        "self_cur_attack",    // 16 자기 현재 행동이 공격
        "self_cur_defense",   // 17 자기 현재 행동이 방어
        "self_cur_move"       // 18 자기 현재 행동이 이동
    };

    public static void Compute(CombatAgentState self, CombatAgentState opp, float[] f)
    {
        Vector3 d = opp.transform.position - self.transform.position;
        d.y = 0f;
        float dist = d.magnitude;
        Vector3 dir = dist > 1e-4f ? d / dist : Flat(self.transform.forward);
        Vector3 local = Quaternion.Inverse(self.transform.rotation) * d;

        f[0] = dist;
        f[1] = local.x;
        f[2] = local.z;
        f[3] = self.HpRatio;
        f[4] = opp.HpRatio;
        f[5] = self.Velocity.magnitude;
        f[6] = opp.Velocity.magnitude;
        f[7] = Vector3.Dot(self.Velocity - opp.Velocity, dir);

        ActionClass oa = opp.CurrentAction;
        f[8] = ActionInfo.IsAttack(oa) ? 1f : 0f;
        f[9] = ActionInfo.IsDefense(oa) ? 1f : 0f;
        f[10] = ActionInfo.IsCrouch(oa) ? 1f : 0f;
        f[11] = oa == ActionClass.Jump ? 1f : 0f;

        f[12] = Vector3.Dot(Flat(self.transform.forward), dir);
        f[13] = Vector3.Dot(Flat(opp.transform.forward), -dir);

        f[14] = Mathf.Min(Time.time - self.LastAttackTime, MaxTimeSinceAttack);
        f[15] = Mathf.Min(Time.time - opp.LastAttackTime, MaxTimeSinceAttack);

        ActionClass sa = self.CurrentAction;
        f[16] = ActionInfo.IsAttack(sa) ? 1f : 0f;
        f[17] = ActionInfo.IsDefense(sa) ? 1f : 0f;
        f[18] = sa == ActionClass.Move ? 1f : 0f;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
    }
}
