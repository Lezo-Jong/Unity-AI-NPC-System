using UnityEngine;
using Unity.Barracuda;

public class EnemyMLController : MonoBehaviour
{
    // 모델과 Worker
    public NNModel modelAsset;
    private Model runtimeModel;
    private IWorker worker;

    // 입력 배열 크기: 19
    private float[] inputFeatures = new float[]
    {
        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 
        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f
    };

    void Start()
    {
        // 모델 로드
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = WorkerFactory.CreateWorker(WorkerFactory.Type.ComputePrecompiled, runtimeModel);
    }

    void Update()
    {
        // 예시: 매 프레임 예측
        float[] output = Predict(inputFeatures);

        // 출력 확인
        Debug.Log("Predicted output: " + string.Join(",", output));
    }

    public float[] Predict(float[] features)
    {
        if (features.Length != 19)
        {
            Debug.LogError("Input feature length must be 19!");
            return null;
        }

        // 입력 Tensor 생성: 1x1x1x19 (Batch=1, Channels=19)
        Tensor inputTensor = new Tensor(1, 1, 1, 19, features);

        // 모델 실행
        worker.Execute(inputTensor);

        // 출력 Tensor 가져오기 (예시: 첫 번째 출력)
        Tensor outputTensor = worker.PeekOutput();

        // 결과 배열로 복사
        float[] outputArray = outputTensor.ToReadOnlyArray();

        // Tensor 해제
        inputTensor.Dispose();
        outputTensor.Dispose();

        return outputArray;
    }

    private void OnDestroy()
    {
        // Worker 해제
        worker?.Dispose();
    }
}
