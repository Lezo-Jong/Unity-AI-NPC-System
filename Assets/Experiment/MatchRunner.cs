using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// 자동 비교 실험: 같은 NPC 오브젝트에서 Random / FSM / ML 행동 선택기만 바꿔가며
// 고정 상대(ReferenceBotBrain)와 N판씩 대전시키고 결과를 CSV로 저장합니다.
// - 매 판 HP·위치·상태 초기화, 같은 판 번호에는 모든 조건에 같은 시드 사용
// - 저장 위치: Application.persistentDataPath/MatchLogs/
public class MatchRunner : MonoBehaviour
{
    public enum Condition { Random, FSM, ML }

    [Header("대전 오브젝트")]
    public GameObject npc;           // RandomBrain, FSMBrain, MLBrain 모두 부착
    public GameObject opponentBot;   // ReferenceBotBrain 부착
    public Transform npcSpawn;
    public Transform opponentSpawn;

    [Header("실험 프로토콜")]
    public Condition[] conditions = { Condition.Random, Condition.FSM, Condition.ML };
    public int matchesPerCondition = 100;
    public int baseSeed = 1000;
    public float matchTimeout = 120f;
    public float settleTime = 0.5f;
    [Tooltip("최종 측정(FPS/지연)은 반드시 1로 실행하세요")]
    public float timeScale = 1f;
    public string runLabel = "main";
    [Tooltip("모든 Brain·Executor에 똑같이 적용되는 공격 사거리(m). 캐릭터 Scale에 맞춰 설정 (NavMeshAgent 반지름 × Scale × 2 = 최소 접근 거리보다 커야 함)")]
    public float attackRange = 4f;
    [Tooltip("NPC끼리 NavMeshAgent 회피를 끔. 켜 두면 서로 밀어내서 3.75 m 안으로 못 들어감 (사람과 싸울 때는 약 3.0 m)")]
    public bool disableAgentAvoidance = true;

    private NpcActionExecutor npcExec, botExec;
    private CombatAgentState npcState, botState;
    private HealthSystem npcHealth, botHealth;
    private RandomBrain randomBrain;
    private FSMBrain fsmBrain;
    private MLBrain mlBrain;
    private ReferenceBotBrain botBrain;
    private StreamWriter writer;

    [Header("진행 표시")]
    public bool showProgress = true;

    // 진행 상황 (화면 표시용)
    private int totalMatches, doneMatches;
    private Condition currentCondition;
    private int currentIndex;
    private float experimentStartRealtime;
    private readonly Dictionary<Condition, int[]> tally = new Dictionary<Condition, int[]>();   // win, draw, loss
    private bool finished;

    private IEnumerator Start()
    {
        npcExec = npc.GetComponent<NpcActionExecutor>();
        npcState = npc.GetComponent<CombatAgentState>();
        npcHealth = npc.GetComponent<HealthSystem>();
        randomBrain = npc.GetComponent<RandomBrain>();
        fsmBrain = npc.GetComponent<FSMBrain>();
        mlBrain = npc.GetComponent<MLBrain>();

        botExec = opponentBot.GetComponent<NpcActionExecutor>();
        botState = opponentBot.GetComponent<CombatAgentState>();
        botHealth = opponentBot.GetComponent<HealthSystem>();
        botBrain = opponentBot.GetComponent<ReferenceBotBrain>();

        // 서로를 상대로 지정
        npcExec.opponent = botState;
        botExec.opponent = npcState;
        foreach (NpcBrain b in new NpcBrain[] { randomBrain, fsmBrain, mlBrain })
            if (b != null) b.opponent = botState;
        botBrain.opponent = npcState;

        // 사람 대 NPC(학습 데이터)와 같은 교전 거리가 되도록 NPC끼리 회피 끄기
        if (disableAgentAvoidance)
        {
            foreach (GameObject go in new[] { npc, opponentBot })
            {
                NavMeshAgent ag = go.GetComponent<NavMeshAgent>();
                if (ag != null) ag.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
            }
        }

        // 공격 사거리를 한 곳에서 통일
        foreach (NpcBrain b in new NpcBrain[] { randomBrain, fsmBrain, mlBrain, botBrain })
            if (b != null) b.attackRange = attackRange;
        npcExec.SetAttackRange(attackRange);
        botExec.SetAttackRange(attackRange);

        // 실험 중 자동 회복·자동 리스폰 끄기
        npcHealth.autoRegen = false; npcHealth.autoRespawn = false;
        botHealth.autoRegen = false; botHealth.autoRespawn = false;

        SetBrains(null);
        botBrain.enabled = false;

        string dir = Path.Combine(Application.persistentDataPath, "MatchLogs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, string.Format("matches_{0}_{1:yyyyMMdd_HHmmss}.csv", runLabel, DateTime.Now));
        writer = new StreamWriter(path, false, new UTF8Encoding(false));
        string counts = string.Join(",", Enum.GetNames(typeof(ActionClass)).Select(n => "cnt_" + n));
        writer.WriteLine("run,condition,match,seed,outcome,duration_s,npc_hp_end,opp_hp_end,npc_hits,opp_hits," +
                         "frames,fps_mean,fps_p1,decisions,latency_mean_ms,latency_sd_ms,latency_max_ms,fallbacks," +
                         counts + ",time_scale,attack_range,device,gpu,unity_version");
        writer.Flush();
        UnityEngine.Debug.Log("[MatchRunner] 결과 파일: " + path);

        yield return null;
        Application.runInBackground = true;   // 다른 창을 눌러도 실험 계속 진행
        Time.timeScale = timeScale;
        totalMatches = conditions.Length * matchesPerCondition;
        doneMatches = 0;
        foreach (Condition c in conditions) tally[c] = new int[3];
        experimentStartRealtime = Time.realtimeSinceStartup;

        foreach (Condition c in conditions)
        {
            for (int i = 0; i < matchesPerCondition; i++)
            {
                currentCondition = c;
                currentIndex = i;
                yield return RunMatch(c, i);
                doneMatches++;
            }
        }

        Time.timeScale = 1f;
        writer.Close();
        writer = null;
        finished = true;
        UnityEngine.Debug.Log("[MatchRunner] 전체 실험 완료: " + path);
    }

    private NpcBrain BrainFor(Condition c)
    {
        switch (c)
        {
            case Condition.Random: return randomBrain;
            case Condition.FSM: return fsmBrain;
            default: return mlBrain;
        }
    }

    private void SetBrains(NpcBrain active)
    {
        if (randomBrain != null) randomBrain.enabled = active == randomBrain;
        if (fsmBrain != null) fsmBrain.enabled = active == fsmBrain;
        if (mlBrain != null) mlBrain.enabled = active == mlBrain;
    }

    private void ResetAgent(GameObject go, HealthSystem h, NpcActionExecutor ex, CombatAgentState st, Transform spawn)
    {
        ex.StopAll();
        h.ResetForMatch(spawn.position, spawn.rotation);
        NavMeshAgent ag = go.GetComponent<NavMeshAgent>();
        if (ag != null && ag.isOnNavMesh) ag.Warp(spawn.position);
        go.transform.rotation = spawn.rotation;
        st.ResetState();
        ex.ResetCounts();
    }

    private IEnumerator RunMatch(Condition c, int index)
    {
        int seed = baseSeed + index;
        NpcBrain brain = BrainFor(c);
        if (brain == null)
        {
            UnityEngine.Debug.LogError("[MatchRunner] " + c + " Brain이 NPC에 없습니다.");
            yield break;
        }

        SetBrains(null);
        botBrain.enabled = false;
        ResetAgent(npc, npcHealth, npcExec, npcState, npcSpawn);
        ResetAgent(opponentBot, botHealth, botExec, botState, opponentSpawn);
        yield return new WaitForSeconds(settleTime);
        npcState.ResetState();
        botState.ResetState();

        UnityEngine.Random.InitState(seed);
        brain.LatenciesMs.Clear();
        if (mlBrain != null) mlBrain.ResetFallbackCount();
        SetBrains(brain);
        botBrain.enabled = true;

        float t0 = Time.time;
        List<float> frameTimes = new List<float>(8192);
        while (!npcHealth.IsDead && !botHealth.IsDead && Time.time - t0 < matchTimeout)
        {
            yield return null;
            frameTimes.Add(Time.unscaledDeltaTime);
        }
        float duration = Time.time - t0;

        SetBrains(null);
        botBrain.enabled = false;
        npcExec.StopAll();
        botExec.StopAll();

        string outcome;
        if (npcHealth.IsDead && !botHealth.IsDead) outcome = "loss";
        else if (botHealth.IsDead && !npcHealth.IsDead) outcome = "win";
        else outcome = "draw";
        if (tally.TryGetValue(c, out int[] t)) t[outcome == "win" ? 0 : outcome == "draw" ? 1 : 2]++;

        // FPS
        float totalReal = frameTimes.Sum();
        float fpsMean = totalReal > 0f ? frameTimes.Count / totalReal : 0f;
        float fpsP1 = 0f;
        if (frameTimes.Count > 0)
        {
            List<float> sorted = frameTimes.OrderByDescending(x => x).ToList();
            int k = Mathf.Clamp(Mathf.CeilToInt(sorted.Count * 0.01f) - 1, 0, sorted.Count - 1);
            fpsP1 = sorted[k] > 0f ? 1f / sorted[k] : 0f;   // 1% low
        }

        // 결정 지연
        List<double> lat = brain.LatenciesMs;
        double latMean = lat.Count > 0 ? lat.Average() : 0.0;
        double latSd = lat.Count > 1 ? Math.Sqrt(lat.Sum(x => (x - latMean) * (x - latMean)) / (lat.Count - 1)) : 0.0;
        double latMax = lat.Count > 0 ? lat.Max() : 0.0;
        int fallbacks = (c == Condition.ML && mlBrain != null) ? mlBrain.FallbackCount : 0;

        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.Append(runLabel).Append(',').Append(c).Append(',').Append(index).Append(',').Append(seed).Append(',');
        sb.Append(outcome).Append(',').Append(duration.ToString("F3", ci)).Append(',');
        sb.Append(npcHealth.CurrentHP).Append(',').Append(botHealth.CurrentHP).Append(',');
        sb.Append(npcExec.HitsLanded).Append(',').Append(botExec.HitsLanded).Append(',');
        sb.Append(frameTimes.Count).Append(',').Append(fpsMean.ToString("F2", ci)).Append(',').Append(fpsP1.ToString("F2", ci)).Append(',');
        sb.Append(lat.Count).Append(',').Append(latMean.ToString("F4", ci)).Append(',').Append(latSd.ToString("F4", ci)).Append(',').Append(latMax.ToString("F4", ci)).Append(',');
        sb.Append(fallbacks);
        foreach (int n in npcExec.ExecutedCounts) sb.Append(',').Append(n);
        sb.Append(',').Append(timeScale.ToString("F2", ci));
        sb.Append(',').Append(attackRange.ToString("F2", ci));
        sb.Append(',').Append(Clean(SystemInfo.processorType));
        sb.Append(',').Append(Clean(SystemInfo.graphicsDeviceName));
        sb.Append(',').Append(Application.unityVersion);
        writer.WriteLine(sb.ToString());
        writer.Flush();

        UnityEngine.Debug.Log(string.Format("[MatchRunner] ({0}/{1}) {2} #{3}: {4} ({5:F1}s)",
            doneMatches + 1, totalMatches, c, index, outcome, duration));
    }

    private void OnGUI()
    {
        if (!showProgress || totalMatches == 0) return;
        float elapsed = Time.realtimeSinceStartup - experimentStartRealtime;
        string eta = "-";
        if (doneMatches > 0 && !finished)
        {
            float remain = elapsed / doneMatches * (totalMatches - doneMatches);
            eta = FormatTime(remain);
        }
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat("[MatchRunner] {0}  {1}\n", runLabel, finished ? "완료" : "진행 중");
        sb.AppendFormat("전체: {0} / {1} 판 ({2:F0}%)\n", doneMatches, totalMatches, 100f * doneMatches / totalMatches);
        if (!finished) sb.AppendFormat("현재: {0}  {1} / {2}\n", currentCondition, currentIndex + 1, matchesPerCondition);
        sb.AppendFormat("경과: {0}   남은 예상: {1}\n", FormatTime(elapsed), eta);
        foreach (Condition c in conditions)
        {
            int[] t = tally[c];
            sb.AppendFormat("{0,-6} 승 {1}  무 {2}  패 {3}\n", c, t[0], t[1], t[2]);
        }
        GUI.Box(new Rect(Screen.width - 330, 10, 320, 40 + 18 * (4 + conditions.Length)), sb.ToString());
    }

    private static string FormatTime(float sec)
    {
        TimeSpan ts = TimeSpan.FromSeconds(sec);
        return string.Format("{0}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
    }

    private static string Clean(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace(",", " ").Trim();
    }

    private void OnApplicationQuit()
    {
        if (writer != null) { writer.Flush(); writer.Close(); }
    }
}
