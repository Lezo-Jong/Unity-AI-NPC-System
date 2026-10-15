using UnityEngine;
using UnityEngine.UI;

public class HealthBarUI : MonoBehaviour
{
    [Header("UI Components")]
    [SerializeField] private Slider slider;
    [SerializeField] private Image fillImage;

    [Header("Target")]
    [SerializeField] private HealthSystem healthSystem;
    [SerializeField] private bool isEnemy = false; // ✅ 적이면 true

    private void OnEnable()
    {
        if (healthSystem != null)
        {
            healthSystem.OnHealthChanged += UpdateHealthBar;
            UpdateHealthBar(healthSystem.CurrentHP, healthSystem.MaxHP);
        }
    }

    private void OnDisable()
    {
        if (healthSystem != null)
            healthSystem.OnHealthChanged -= UpdateHealthBar;
    }

    private void UpdateHealthBar(int current, int max)
    {
        if (slider != null)
        {
            slider.maxValue = max;
            slider.value = current;
        }

        if (fillImage != null)
        {
            if (isEnemy)
            {
                fillImage.color = Color.red; // 적은 무조건 빨강
            }
            else
            {
                float ratio = (float)current / max;

                // ✅ 초록 → 노랑 → 빨강 그라데이션
                if (ratio > 0.5f)
                    fillImage.color = Color.Lerp(Color.yellow, Color.green, (ratio - 0.5f) * 2f);
                else
                    fillImage.color = Color.Lerp(Color.red, Color.yellow, ratio * 2f);
            }
        }
    }
}
