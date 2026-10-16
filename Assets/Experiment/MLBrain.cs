using System;
using Unity.InferenceEngine;
using UnityEngine;

// 제안 모델: ONNX(MLP) 추론으로 다음 행동을 선택합니다. (Unity Inference Engine, 구 Sentis)
// 입력 정규화는 ONNX 그래프 안에 포함되어 있으므로 원시 feature를 그대로 넣습니다.
// 추론 오류 시 FSM 규칙으로 대체(fallback)하고 횟수를 기록합니다.
public class MLBrain : NpcBrain
{
    public ModelAsset modelAsset;
    public BackendType backendType = BackendType.CPU;

    public int FallbackCount { get; private set; }

    private Worker worker;
    private int fallbackComboStep;

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
                    return (ActionClass)best;
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

    private void OnDestroy()
    {
        worker?.Dispose();
    }
}
