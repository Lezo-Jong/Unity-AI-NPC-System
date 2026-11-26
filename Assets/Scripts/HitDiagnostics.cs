using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 진단용 (리뷰어 2, Major 4): 칼 콜라이더 판정이 실제로 무엇에 맞았는지 기록합니다.
// - swing: 칼 판정이 켜지는 순간(애니메이션 이벤트) 가장 가까운 상대까지의 거리·각도
// - hit  : SwordController.HandleHit가 데미지를 준 순간, 맞은 콜라이더 종류(몸/트리거 구체)·거리·각도
// 저장: Application.persistentDataPath/HitLogs/hits_*.csv
// 끄려면 enabled = false (기본값 true). 실험 결과에는 영향 없음 (기록만 함).
public static class HitDiagnostics
{
    public static bool enabled = true;
    private static StreamWriter writer;

    private static void Write(string kind, GameObject attacker, HealthSystem target, Collider other, int damage, int hpLoss, string note = null)
    {
        if (!enabled || attacker == null) return;
        try
        {
            if (writer == null)
            {
                string dir = Path.Combine(Application.persistentDataPath, "HitLogs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, string.Format("hits_{0:yyyyMMdd_HHmmss}.csv", DateTime.Now));
                writer = new StreamWriter(path, false, new UTF8Encoding(false));
                writer.WriteLine("time,kind,attacker,target,dist,facing,collider,collider_type,is_trigger,radius,damage,hp_loss");
                Application.quitting += Close;
                Debug.Log("[HitDiagnostics] 기록: " + path);
            }
            if (target == null) target = Nearest(attacker);
            float dist = -1f, facing = 0f;
            if (target != null)
            {
                Vector3 d = target.transform.position - attacker.transform.position; d.y = 0f;
                dist = d.magnitude;
                Vector3 f = attacker.transform.forward; f.y = 0f;
                facing = dist > 1e-4f && f.sqrMagnitude > 1e-8f ? Vector3.Dot(f.normalized, d / dist) : 1f;
            }
            float radius = -1f;
            if (other is SphereCollider sc) radius = sc.radius * MaxScale(sc.transform);
            else if (other is CapsuleCollider cc) radius = cc.radius * MaxScale(cc.transform);
            else if (other is CharacterController ch) radius = ch.radius * MaxScale(ch.transform);
            CultureInfo ci = CultureInfo.InvariantCulture;
            writer.WriteLine(string.Join(",",
                Time.time.ToString("F3", ci), kind, attacker.name, target != null ? target.name : "-",
                dist.ToString("F3", ci), facing.ToString("F3", ci),
                other != null ? other.name : (note ?? "-"), other != null ? other.GetType().Name : "-",
                other != null ? (other.isTrigger ? "1" : "0") : "-", radius.ToString("F2", ci),
                damage.ToString(ci), hpLoss.ToString(ci)));
            writer.Flush();
        }
        catch (Exception e) { Debug.LogWarning("[HitDiagnostics] " + e.Message); enabled = false; }
    }

    public static void Swing(GameObject attacker) { Write("swing", attacker, null, null, 0, 0); }

    // 칼 판정이 애니메이션 이벤트가 아니라 안전장치로 꺼진 경우 (collider 열에 이유 기록)
    public static void AutoOff(GameObject attacker, string reason)
    {
        Write("auto_off", attacker, null, null, 0, 0, reason);
    }

    public static void Hit(GameObject attacker, HealthSystem target, Collider other, int damage, int hpLoss)
    {
        Write("hit", attacker, target, other, damage, hpLoss);
    }

    private static HealthSystem Nearest(GameObject attacker)
    {
        HealthSystem best = null; float bd = float.MaxValue;
        HealthSystem own = attacker.GetComponentInParent<HealthSystem>();
        foreach (HealthSystem h in UnityEngine.Object.FindObjectsByType<HealthSystem>(FindObjectsSortMode.None))
        {
            if (h == own || h.IsDead) continue;
            float d = (h.transform.position - attacker.transform.position).sqrMagnitude;
            if (d < bd) { bd = d; best = h; }
        }
        return best;
    }

    private static float MaxScale(Transform t)
    {
        Vector3 s = t.lossyScale; return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }

    private static void Close()
    {
        if (writer != null) { writer.Flush(); writer.Close(); writer = null; }
    }
}
