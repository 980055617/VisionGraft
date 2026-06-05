using UnityEngine.UI;

public static class RuntimeSliderWriter
{
    public static void ApplyDirection(Slider slider, Slider.Direction direction)
    {
        if (slider == null)
        {
            return;
        }

        slider.direction = direction;
    }

    public static void ApplyRange(Slider slider, float minValue, float maxValue)
    {
        if (slider == null)
        {
            return;
        }

        slider.minValue = minValue;
        slider.maxValue = maxValue;
    }

    public static void ApplyValueWithoutNotify(Slider slider, float value)
    {
        if (slider == null)
        {
            return;
        }

        slider.SetValueWithoutNotify(value);
    }

    public static void ApplyRects(Slider slider, UnityEngine.RectTransform fillRect, UnityEngine.RectTransform handleRect)
    {
        if (slider == null)
        {
            return;
        }

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
    }
}
