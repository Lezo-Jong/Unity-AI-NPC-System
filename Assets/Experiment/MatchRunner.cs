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

    private NpcActionExecutor npcExec, botExec;
    private CombatAgentState npcState, botState;
    private HealthSystem npcHealth, botHealth;
    private RandomBrain randomBrain;
    private FSMBrain fsmBrain;
    private MLBrain mlBrain;
    private ReferenceBotBrain botBrain;
    private StreamWriter writer;

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
                         counts + ",time_scale,device,gpu,unity_version");
        writer.Flush();
        UnityEngine.Debug.Log("[MatchRunner] 결과 파일: " + path);

        yield return null;
        Time.timeScale = timeScale;

        foreach (Condition c in conditions)
        {
            for (int i = 0; i < matchesPerCondition; i++)
            {
                yield return RunMatch(c, i);
            }
        }

        Time.timeScale = 1f;
        writer.Close();
        writer = null;
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
        sb.Append(',').Append(Clean(SystemInfo.processorType));
        sb.Append(',').Append(Clean(SystemInfo.graphicsDeviceName));
        sb.Append(',').Append(Application.unityVersion);
        writer.WriteLine(sb.ToString());
        writer.Flush();

        UnityEngine.Debug.Log(string.Format("[MatchRunner] {0} #{1}: {2} ({3:F1}s)", c, index, outcome, duration));
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
