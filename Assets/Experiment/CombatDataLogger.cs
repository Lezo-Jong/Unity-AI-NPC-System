using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 학습 데이터 수집용 로거 (플레이어 오브젝트에 부착).
// - 10 Hz로 19개 feature + 플레이어의 현재 행동 라벨을 CSV로 저장
// - 실행할 때마다 새 파일 생성 (덮어쓰기 없음), 참가자·세션 ID 자동 기록
// 저장 위치: Application.persistentDataPath/CombatLogs/  (Console에 전체 경로 출력)
public class CombatDataLogger : MonoBehaviour
{
    public CombatAgentState self;       // 플레이어
    public CombatAgentState opponent;   // 상대 NPC
    public string participantId = "P01";
    public int sessionIndex = 1;
    public string opponentType = "RandomBrain";
    public float logHz = 10f;

    public string FilePath { get; private set; }
    public int RowCount { get; private set; }

    private StreamWriter writer;
    private float nextLogTime;
    private string sessionId;
    private readonly float[] f = new float[CombatFeatures.Count];
    private readonly StringBuilder sb = new StringBuilder(512);

    private void Start()
    {
        if (self == null) self = GetComponent<CombatAgentState>();
        sessionId = string.Format("{0}_S{1:D2}_{2:yyyyMMdd_HHmmss}", participantId, sessionIndex, DateTime.Now);
        string dir = Path.Combine(Application.persistentDataPath, "CombatLogs");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, sessionId + ".csv");

        writer = new StreamWriter(FilePath, false, new UTF8Encoding(false));
        writer.WriteLine("participant_id,session_id,opponent_type,time," +
                         string.Join(",", CombatFeatures.Names) + ",action,action_name");
        writer.Flush();
        UnityEngine.Debug.Log("[CombatDataLogger] 기록 시작: " + FilePath);
    }

    private void Update()
    {
        if (writer == null || self == null || opponent == null) return;
        if (Time.time < nextLogTime) return;
        nextLogTime = Time.time + 1f / logHz;
        if (self.health != null && self.health.IsDead) return;
        if (opponent.health != null && opponent.health.IsDead) return;

        CombatFeatures.Compute(self, opponent, f);
        ActionClass a = self.CurrentAction;

        CultureInfo ci = CultureInfo.InvariantCulture;
        sb.Length = 0;
        sb.Append(participantId).Append(',').Append(sessionId).Append(',').Append(opponentType).Append(',');
        sb.Append(Time.time.ToString("F3", ci));
        for (int i = 0; i < f.Length; i++) sb.Append(',').Append(f[i].ToString("0.#####", ci));
        sb.Append(',').Append((int)a).Append(',').Append(a.ToString());
        writer.WriteLine(sb.ToString());

        RowCount++;
        if (RowCount % 100 == 0) writer.Flush();
    }

    private void Close()
    {
        if (writer == null) return;
        writer.Flush();
        writer.Close();
        writer = null;
        UnityEngine.Debug.Log("[CombatDataLogger] 저장 완료 (" + RowCount + " rows): " + FilePath);
    }

    // 리스폰 때 HealthSystem이 오브젝트를 잠깐 껐다 켜므로 OnDisable에서는 닫지 않고 저장만 합니다.
    private void OnDisable() { writer?.Flush(); }
    private void OnDestroy() { Close(); }
    private void OnApplicationQuit() { Close(); }
}
