using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// 사람 기준선 (리뷰어 2 Major 4, 리뷰어 3): 사람이 평가와 같은 고정 상대(ReferenceBotBrain)와 N판 대전.
// - 상대·사거리·HP·시드 순서는 MatchRunner와 같음. 명중 판정은 통일 규칙(칼이 몸에 닿을 때만, 데미지 5~10)
//   사람 → 봇: 사람 칼의 물리 트리거(트리거 콜라이더 무시) / 봇 → 사람: 봇 칼의 물리 트리거, 대상은 사람만
// - 결과: MatchLogs/matches_<runLabel>_*.csv (MatchRunner와 같은 열 이름, condition = Human)
// - 플레이어의 CombatDataLogger가 있으면 매 틱 기록(CombatLogs, opponent_type = ReferenceBotBrain)
// 사용: 빈 오브젝트에 붙이고 Player / OpponentBot / 스폰 위치 연결, MatchRunner와 Enemy는 Disable During Run에 넣기
public class HumanBaselineRunner : MonoBehaviour
{
    [Header("대전 오브젝트")]
    public GameObject player;          // PlayerController, CombatAgentState, HealthSystem, SwordController
    public GameObject opponentBot;     // ReferenceBotBrain, NpcActionExecutor
    public Transform playerSpawn;      // MatchRunner의 NpcSpawn과 같은 위치 권장
    public Transform opponentSpawn;
    [Tooltip("실행 중 꺼 둘 오브젝트 (예: MatchRunner, Enemy)")]
    public GameObject[] disableDuringRun;

    [Header("프로토콜 (MatchRunner와 같게)")]
    public int matches = 100;
    [Tooltip("중간에 멈췄다가 이어서 할 때 시작할 판 번호 (0부터)")]
    public int startMatch = 0;
    public int baseSeed = 1000;
    public float matchTimeout = 120f;
    public float attackRange = 4f;
    [Tooltip("판 사이 준비 시간(s). 이 동안 봇은 움직이지 않음")]
    public float countdown = 3f;
    public string runLabel = "human_baseline";

    [Header("기록")]
    public string participantId = "P01";
    [Tooltip("CombatDataLogger 세션 번호 (학습 데이터 S01~S07과 구분)")]
    public int sessionIndex = 90;

    private HealthSystem pHealth, bHealth;
    private CombatAgentState pState, bState;
    private SwordController pSword;
    private CharacterController pCC;
    private NpcActionExecutor bExec;
    private ReferenceBotBrain bBrain;
    private CombatDataLogger logger;
    private StreamWriter writer;
    private int playerHits, current, wins, losses, draws;
    private float countdownLeft;
    private bool finished;
    private readonly int[] counts = new int[ActionInfo.Count];

    private void Awake()
    {
        if (disableDuringRun != null)
            foreach (GameObject go in disableDuringRun) if (go != null) go.SetActive(false);
        player.SetActive(true);
        opponentBot.SetActive(true);

        logger = player.GetComponent<CombatDataLogger>();
        if (logger != null)
        {
            logger.participantId = participantId;
            logger.sessionIndex = sessionIndex;
            logger.opponentType = "ReferenceBotBrain";
            logger.opponent = opponentBot.GetComponent<CombatAgentState>();
        }
    }

    private IEnumerator Start()
    {
        pHealth = player.GetComponent<HealthSystem>();
        pState = player.GetComponent<CombatAgentState>();
        pSword = player.GetComponent<SwordController>();
        pCC = player.GetComponent<CharacterController>();
        bHealth = opponentBot.GetComponent<HealthSystem>();
        bState = opponentBot.GetComponent<CombatAgentState>();
        bExec = opponentBot.GetComponent<NpcActionExecutor>();
        bBrain = opponentBot.GetComponent<ReferenceBotBrain>();

        // 상대 지정·사거리 (MatchRunner와 같음)
        bExec.opponent = pState;
        bBrain.opponent = pState;
        bBrain.attackRange = attackRange;
        bExec.SetAttackRange(attackRange);
        // 명중 판정 통일: 봇 칼은 물리 트리거로 사람 몸만, 사람 칼은 트리거 콜라이더 무시
        bExec.SetHitMode(NpcActionExecutor.HitMode.SwordCollider, false);
        if (pSword != null)
        {
            pSword.legacyTriggerHits = false;
            pSword.allowedTarget = null;
            pSword.Hit += (target, loss) => { if (target == bHealth && loss > 0) playerHits++; };
        }
        NavMeshAgent ag = opponentBot.GetComponent<NavMeshAgent>();
        if (ag != null) ag.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

        pHealth.maxHP = bHealth.maxHP;   // 평가와 같은 HP
        pHealth.autoRegen = false; pHealth.autoRespawn = false;
        bHealth.autoRegen = false; bHealth.autoRespawn = false;
        bBrain.enabled = false;

        string dir = Path.Combine(Application.persistentDataPath, "MatchLogs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, string.Format("matches_{0}_{1:yyyyMMdd_HHmmss}.csv", runLabel, DateTime.Now));
        writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("run,condition,match,seed,outcome,duration_s,npc_hp_end,opp_hp_end,npc_hits,opp_hits," +
                         string.Join(",", Enum.GetNames(typeof(ActionClass)).Select(n => "cnt_" + n)) +
                         ",max_hp,attack_range,hit_mode,unity_version");
        writer.Flush();
        Debug.Log("[HumanBaseline] 결과 파일: " + path);

        yield return null;
        for (current = startMatch; current < matches; current++)
            yield return RunMatch(current);

        writer.Close(); writer = null;
        finished = true;
        Debug.Log(string.Format("[HumanBaseline] 완료: 승 {0} / 무 {1} / 패 {2}", wins, draws, losses));
    }

    private void ResetAgents()
    {
        bExec.StopAll();
        bHealth.ResetForMatch(opponentSpawn.position, opponentSpawn.rotation);
        NavMeshAgent ag = opponentBot.GetComponent<NavMeshAgent>();
        if (ag != null && ag.isOnNavMesh) ag.Warp(opponentSpawn.position);
        opponentBot.transform.rotation = opponentSpawn.rotation;
        bState.ResetState();
        bExec.ResetCounts();

        if (pCC != null) pCC.enabled = false;   // CharacterController가 켜져 있으면 위치 이동이 무시될 수 있음
        pHealth.ResetForMatch(playerSpawn.position, playerSpawn.rotation);
        if (pCC != null) pCC.enabled = true;
        pState.ResetState();
    }

    private IEnumerator RunMatch(int index)
    {
        int seed = baseSeed + index;
        bBrain.enabled = false;
        ResetAgents();

        countdownLeft = countdown;
        while (countdownLeft > 0f) { countdownLeft -= Time.deltaTime; yield return null; }
        bState.ResetState(); pState.ResetState();

        UnityEngine.Random.InitState(seed);
        playerHits = 0;
        Array.Clear(counts, 0, counts.Length);
        ActionClass last = pState.CurrentAction;
        bBrain.enabled = true;

        float t0 = Time.time;
        while (!pHealth.IsDead && !bHealth.IsDead && Time.time - t0 < matchTimeout)
        {
            yield return null;
            ActionClass a = pState.CurrentAction;
            if (a != last) { counts[(int)a]++; last = a; }
        }
        float duration = Time.time - t0;
        bBrain.enabled = false;
        bExec.StopAll();

        string outcome = pHealth.IsDead && !bHealth.IsDead ? "loss" : bHealth.IsDead && !pHealth.IsDead ? "win" : "draw";
        if (outcome == "win") wins++; else if (outcome == "loss") losses++; else draws++;

        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.Append(runLabel).Append(",Human,").Append(index).Append(',').Append(seed).Append(',').Append(outcome).Append(',');
        sb.Append(duration.ToString("F3", ci)).Append(',').Append(pHealth.CurrentHP).Append(',').Append(bHealth.CurrentHP).Append(',');
        sb.Append(playerHits).Append(',').Append(bExec.HitsLanded);
        foreach (int n in counts) sb.Append(',').Append(n);
        sb.Append(',').Append(pHealth.maxHP).Append(',').Append(attackRange.ToString("F2", ci));
        sb.Append(",SwordCollider,").Append(Application.unityVersion);
        writer.WriteLine(sb.ToString());
        writer.Flush();
        Debug.Log(string.Format("[HumanBaseline] {0}/{1}: {2} ({3:F1}s)", index + 1, matches, outcome, duration));
    }

    private void OnGUI()
    {
        GUIStyle st = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft };
        string text = finished
            ? string.Format("사람 기준선 완료: 승 {0} / 무 {1} / 패 {2}", wins, draws, losses)
            : string.Format("사람 기준선  {0} / {1} 판\n승 {2}  무 {3}  패 {4}\n내 HP {5}   상대 HP {6}{7}",
                current + 1, matches, wins, draws, losses,
                pHealth != null ? pHealth.CurrentHP : 0, bHealth != null ? bHealth.CurrentHP : 0,
                countdownLeft > 0f ? string.Format("\n\n시작까지 {0:F0}", Mathf.Ceil(countdownLeft)) : "");
        GUI.Box(new Rect(10, 10, 330, finished ? 40 : 130), text, st);
    }

    private void OnApplicationQuit()
    {
        if (writer != null) { writer.Flush(); writer.Close(); writer = null; }
    }
}
