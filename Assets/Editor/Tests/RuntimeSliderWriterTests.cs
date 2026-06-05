using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeSliderWriterTests
{
    [Test]
    public void ApplyRangeSetsMinAndMax()
    {
        GameObject obj = new GameObject("Slider");
        try
        {
            Slider slider = obj.AddComponent<Slider>();

            RuntimeSliderWriter.ApplyRange(slider, -180f, 180f);

            Assert.That(slider.minValue, Is.EqualTo(-180f));
            Assert.That(slider.maxValue, Is.EqualTo(180f));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyValueWithoutNotifySetsValue()
    {
        GameObject obj = new GameObject("Slider");
        try
        {
            Slider slider = obj.AddComponent<Slider>();

            RuntimeSliderWriter.ApplyValueWithoutNotify(slider, 0.5f);

            Assert.That(slider.value, Is.EqualTo(0.5f).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyRectsSetsFillAndHandle()
    {
        GameObject obj = new GameObject("Slider");
        GameObject fill = new GameObject("Fill");
        GameObject handle = new GameObject("Handle");
        try
        {
            Slider slider = obj.AddComponent<Slider>();
            RectTransform fillRect = fill.AddComponent<RectTransform>();
            RectTransform handleRect = handle.AddComponent<RectTransform>();

            RuntimeSliderWriter.ApplyRects(slider, fillRect, handleRect);

            Assert.That(slider.fillRect, Is.EqualTo(fillRect));
            Assert.That(slider.handleRect, Is.EqualTo(handleRect));
        }
        finally
        {
            Object.DestroyImmediate(obj);
            Object.DestroyImmediate(fill);
            Object.DestroyImmediate(handle);
        }
    }
}
