using UnityEngine;

// Baseline (리뷰어 2·3 요청): 상태를 전혀 보지 않고 사람의 행동 빈도대로 행동을 뽑는 컨트롤러.
// "MLP가 행동 빈도 이상의 것을 배웠는가"를 확인하기 위한 비교군입니다.
// - 사거리 밖: 추적(Move) — RandomBrain과 같음
// - 한가할 때: 사람 결정 표본의 라벨 빈도대로 추첨
// - 콤보 예약 기회: 사람의 콤보 지속 비율대로 계속(Slash) 또는 종료(Idle)
// 빈도 출처: data/human 9개 파일 → train.py build_decisions(horizon 3) → 거리 < 4.5 m
public class FrequencyMatchedBrain : NpcBrain
{
    [Tooltip("한가할 때 행동 확률 (ActionClass 순서). 사람 결정 표본 n = 4,853")]
    public float[] freeProbs =
    {
        0.36122f, // Idle
        0.02411f, // Move
        0.05358f, // Slash
        0f,       // Slash2 (한가할 때는 나오지 않음)
        0f,       // Slash3
        0.09273f, // Block
        0.09850f, // CrouchBlock
        0.03462f, // Jump
        0.29116f, // Crouch
        0.04410f  // CrouchSlash
    };
    [Tooltip("Slash 중 다음 단계(Slash2)로 이어 간 비율 (사람, n = 88)")]
    public float continueAfterSlash = 0.591f;
    [Tooltip("Slash2 중 다음 단계(Slash3)로 이어 간 비율 (사람, n = 52)")]
    public float continueAfterSlash2 = 0.731f;

    protected override ActionClass Decide(float[] f)
    {
        if (f[0] > attackRange) return ActionClass.Move;

        if (executor.IsBusy)   // 콤보 예약 기회
        {
            float p = executor.CurrentAction == ActionClass.Slash2 ? continueAfterSlash2 : continueAfterSlash;
            return Random.value < p ? ActionClass.Slash : ActionClass.Idle;
        }

        float sum = 0f;
        foreach (float v in freeProbs) sum += v;
        float r = Random.value * sum, acc = 0f;
        for (int i = 0; i < freeProbs.Length; i++)
        {
            acc += freeProbs[i];
            if (r < acc) return (ActionClass)i;
        }
        return ActionClass.Idle;
    }
}
