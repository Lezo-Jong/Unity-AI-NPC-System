using UnityEngine;
using System.Collections.Generic;
using System.IO;

public class PlayerDataLogger : MonoBehaviour
{
    [System.Serializable]
    public class ActionRecord
    {
        public float time;
        public Vector3 position;
        public Vector3 velocity;
        public string animState;
    }

    [System.Serializable]
    public class ActionListWrapper
    {
        public List<ActionRecord> actions = new List<ActionRecord>();
    }

    public List<ActionRecord> actionLog = new List<ActionRecord>();
    private Animator animator;
    private CharacterController controller;

    private Vector3 lastPosition;
    private float lastTime;

    private float saveInterval = 15f; 
    private float nextSaveTime = 0f;

    private string saveDir = @"C:\Users\User\AppData\LocalLow\pvp\pvp";
    private string savePath = @"C:\Users\User\AppData\LocalLow\pvp\pvp\player_log.json";

    void Start()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();

        lastPosition = transform.position;
        lastTime = Time.time;
        nextSaveTime = Time.time + saveInterval;

        // 폴더 생성
        if (!Directory.Exists(saveDir))
            Directory.CreateDirectory(saveDir);

        // 기존 로그 불러오기 (있으면 이어서 저장)
        if (File.Exists(savePath))
        {
            try
            {
                string existingJson = File.ReadAllText(savePath);
                ActionListWrapper existingWrapper = JsonUtility.FromJson<ActionListWrapper>(existingJson);
                if (existingWrapper != null && existingWrapper.actions != null)
                {
                    actionLog.AddRange(existingWrapper.actions);
                    Debug.Log("[PlayerDataLogger] 기존 로그 불러오기 완료, 기존 기록 수: " + existingWrapper.actions.Count);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PlayerDataLogger] 기존 로그 불러오기 실패: " + e.Message);
            }
        }
    }

    void LateUpdate() // 이동 후 기록
    {
        // velocity 계산
        float deltaTime = Time.time - lastTime;
        Vector3 velocity = Vector3.zero;
        if (deltaTime > 0f)
        {
            velocity = (transform.position - lastPosition) / deltaTime;
        }

        // 현재 애니메이션 Clip 이름
        string clipName = "Unknown";
        if (animator.GetCurrentAnimatorClipInfoCount(0) > 0)
        {
            var clipInfo = animator.GetCurrentAnimatorClipInfo(0);
            clipName = clipInfo[0].clip.name;
        }

        // 로그 추가
        actionLog.Add(new ActionRecord
        {
            time = Time.time,
            position = transform.position,
            velocity = velocity,
            animState = clipName
        });

        lastPosition = transform.position;
        lastTime = Time.time;

        // 15초마다 저장
        if (Time.time >= nextSaveTime)
        {
            SaveLog();
            nextSaveTime = Time.time + saveInterval;
        }
    }

    private void OnApplicationQuit()
    {
        SaveLog();
    }

    private void SaveLog()
    {
        try
        {
            ActionListWrapper wrapper = new ActionListWrapper();
            wrapper.actions = actionLog;
            string json = JsonUtility.ToJson(wrapper, true);
            File.WriteAllText(savePath, json);
            Debug.Log("[PlayerDataLogger] 로그 저장됨, 총 기록 수: " + actionLog.Count);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[PlayerDataLogger] 로그 저장 실패: " + e.Message);
        }
    }
}
