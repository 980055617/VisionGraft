using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeGraphicWriterTests
{
    [Test]
    public void ApplyColorSetsGraphicColor()
    {
        GameObject obj = new GameObject("Image");
        try
        {
            Image image = obj.AddComponent<Image>();
            Color color = new Color(0.1f, 0.2f, 0.3f, 0.4f);

            RuntimeGraphicWriter.ApplyColor(image, color);

            Assert.That(image.color, Is.EqualTo(color));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyTargetGraphicSetsSelectableTargetGraphic()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Image image = obj.AddComponent<Image>();
            Button button = obj.AddComponent<Button>();

            RuntimeGraphicWriter.ApplyTargetGraphic(button, image);

            Assert.That(button.targetGraphic, Is.EqualTo(image));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyColorsSetsSelectableColorBlock()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Button button = obj.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.red;

            RuntimeGraphicWriter.ApplyColors(button, colors);

            Assert.That(button.colors.normalColor, Is.EqualTo(Color.red));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }
}
