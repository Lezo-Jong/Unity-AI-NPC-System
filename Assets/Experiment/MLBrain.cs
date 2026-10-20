using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;

// 제안 모델: ONNX(MLP) 추론으로 다음 행동을 선택합니다. (Unity Sentis 2.6, 패키지 com.unity.ai.inference; C# 네임스페이스는 Unity.InferenceEngine)
// 입력 정규화는 ONNX 그래프 안에 포함되어 있으므로 원시 feature를 그대로 넣습니다.
// 추론 오류 시 FSM 규칙으로 대체(fallback)하고 횟수를 기록합니다.
public class MLBrain : NpcBrain
{
    public ModelAsset modelAsset;
    public BackendType backendType = BackendType.CPU;
    [Tooltip("켜면 확률(softmax)에 따라 행동을 뽑음. 끄면 가장 높은 확률의 행동만 고름(argmax). 난수는 MatchRunner의 판별 시드를 따름")]
    public bool sampleAction = false;
    [Tooltip("진단용: 매 결정의 입력 19개·출력 확률·선택 행동을 CSV로 저장 (persistentDataPath/MLDebug)")]
    public bool logDecisions = false;

    public int FallbackCount { get; private set; }

    private Worker worker;
    private int fallbackComboStep;
    private StreamWriter debugWriter;
    private readonly float[] probs = new float[ActionInfo.Count];

    protected override void Awake()
    {
        base.Awake();
        try
        {
            Model model = ModelLoader.Load(modelAsset);
            worker = new Worker(model, backendType);

            // Warm-up: 첫 실행의 준비 비용이 결정 지연 측정에 섞이지 않도록 미리 한 번 실행
            using (Tensor<float> dummy = new Tensor<float>(new TensorShape(1, CombatFeatures.Count), new float[CombatFeatures.Count]))
            {
                worker.Schedule(dummy);
                (worker.PeekOutput() as Tensor<float>)?.DownloadToArray();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[MLBrain] 모델 로드 실패: " + e.Message);
            worker = null;
        }
    }

    public void ResetFallbackCount() { FallbackCount = 0; }

    protected override ActionClass Decide(float[] f)
    {
        if (worker != null)
        {
            try
            {
                using (Tensor<float> input = new Tensor<float>(new TensorShape(1, CombatFeatures.Count), f))
                {
                    worker.Schedule(input);
                    Tensor<float> output = worker.PeekOutput() as Tensor<float>;   // worker 소유: Dispose 금지
                    if (output == null || output.shape.length != ActionInfo.Count)
                        throw new Exception("출력 크기 " + (output == null ? 0 : output.shape.length) + " ≠ " + ActionInfo.Count);

                    float[] logits = output.DownloadToArray();
                    int best = 0;
                    for (int i = 1; i < logits.Length; i++)
                    {
                        if (logits[i] > logits[best]) best = i;
                    }
                    Softmax(logits, best);
                    int chosen = sampleAction ? Sample() : best;
                    if (logDecisions) LogDecision(f, chosen);
                    return (ActionClass)chosen;
                }
            }
            catch (Exception e)
            {
                if (FallbackCount == 0) UnityEngine.Debug.LogWarning("[MLBrain] 추론 실패, FSM으로 대체: " + e.Message);
            }
        }
        FallbackCount++;
        return FSMBrain.Rule(f, attackRange, 1.0f, ref fallbackComboStep);
    }

    private void Softmax(float[] logits, int best)
    {
        float sum = 0f;
        for (int i = 0; i < probs.Length; i++) { probs[i] = Mathf.Exp(logits[i] - logits[best]); sum += probs[i]; }
        for (int i = 0; i < probs.Length; i++) probs[i] /= sum;
    }

    private int Sample()
    {
        float r = UnityEngine.Random.value, acc = 0f;
        for (int i = 0; i < probs.Length; i++) { acc += probs[i]; if (r < acc) return i; }
        return probs.Length - 1;
    }

    private void LogDecision(float[] f, int chosen)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (debugWriter == null)
        {
            string dir = Path.Combine(Application.persistentDataPath, "MLDebug");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, string.Format("ml_decisions_{0:yyyyMMdd_HHmmss}.csv", DateTime.Now));
            debugWriter = new StreamWriter(path, false, new UTF8Encoding(false));
            StringBuilder h = new StringBuilder("time,");
            h.Append(string.Join(",", CombatFeatures.Names));
            foreach (string n in Enum.GetNames(typeof(ActionClass))) h.Append(",p_").Append(n);
            h.Append(",chosen");
            debugWriter.WriteLine(h.ToString());
            UnityEngine.Debug.Log("[MLBrain] 결정 기록: " + path);
        }
        StringBuilder sb = new StringBuilder(256);
        sb.Append(Time.time.ToString("F3", ci));
        foreach (float v in f) sb.Append(',').Append(v.ToString("0.#####", ci));
        foreach (float v in probs) sb.Append(',').Append(v.ToString("0.####", ci));
        sb.Append(',').Append((ActionClass)chosen);
        debugWriter.WriteLine(sb.ToString());
    }

    private void OnDestroy()
    {
        worker?.Dispose();
        if (debugWriter != null) { debugWriter.Flush(); debugWriter.Close(); debugWriter = null; }
    }
}
