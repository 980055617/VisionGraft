using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeTextWriterTests
{
    [Test]
    public void ApplyStyleSetsFontSizeAlignmentAndColorWithoutChangingText()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();
            text.text = "existing";
            Color color = new Color(0.2f, 0.4f, 0.6f, 1f);

            RuntimeTextWriter.ApplyStyle(text, null, 32, TextAnchor.MiddleRight, color);

            Assert.That(text.text, Is.EqualTo("existing"));
            Assert.That(text.fontSize, Is.EqualTo(32));
            Assert.That(text.alignment, Is.EqualTo(TextAnchor.MiddleRight));
            Assert.That(text.color, Is.EqualTo(color));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyContentSetsText()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();

            RuntimeTextWriter.ApplyContent(text, "updated");

            Assert.That(text.text, Is.EqualTo("updated"));
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }

    [Test]
    public void ApplyInteractionSetsRaycastTarget()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();

            RuntimeTextWriter.ApplyInteraction(text, false);

            Assert.That(text.raycastTarget, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(obj);
        }
    }
}
