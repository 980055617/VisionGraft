using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeCanvasComponentFactoryTests
{
    [Test]
    public void EnsureCanvasAddsCanvasWhenMissing()
    {
        GameObject root = new GameObject("Root");
        try
        {
            Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);

            Assert.That(canvas, Is.Not.Null);
            Assert.That(canvas.gameObject, Is.EqualTo(root));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void EnsureCanvasReusesExistingCanvas()
    {
        GameObject root = new GameObject("Root");
        Canvas existing = root.AddComponent<Canvas>();
        try
        {
            Canvas canvas = RuntimeCanvasComponentFactory.EnsureCanvas(root);

            Assert.That(canvas, Is.EqualTo(existing));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void EnsureGraphicRaycasterAddsRaycasterAndAppliesReversedGraphicsSetting()
    {
        GameObject root = new GameObject("Root");
        try
        {
            GraphicRaycaster raycaster = RuntimeCanvasComponentFactory.EnsureGraphicRaycaster(root, false);

            Assert.That(raycaster, Is.Not.Null);
            Assert.That(raycaster.gameObject, Is.EqualTo(root));
            Assert.That(raycaster.ignoreReversedGraphics, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }
}
