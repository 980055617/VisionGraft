using NUnit.Framework;
using UnityEngine;

public class RuntimeUiTransformWriterTests
{
    [Test]
    public void ApplyWorldPoseKeepsExistingLocalScale()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            ui.transform.localScale = Vector3.one * 0.01f;
            Vector3 position = new Vector3(0f, 1f, 2f);
            Quaternion rotation = Quaternion.Euler(0f, 25f, 0f);

            RuntimeUiTransformWriter.ApplyWorldPose(ui.transform, position, rotation);

            AssertVector(ui.transform.position, position);
            Assert.That(Quaternion.Angle(ui.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(ui.transform.localScale, Vector3.one * 0.01f);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyLocalScaleKeepsExistingWorldPose()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            ui.transform.position = Vector3.right;
            ui.transform.rotation = Quaternion.Euler(0f, 60f, 0f);
            Vector3 scale = new Vector3(0.002f, 0.003f, 0.002f);

            RuntimeUiTransformWriter.ApplyLocalScale(ui.transform, scale);

            AssertVector(ui.transform.position, Vector3.right);
            Assert.That(Quaternion.Angle(ui.transform.rotation, Quaternion.Euler(0f, 60f, 0f)), Is.LessThan(0.001f));
            AssertVector(ui.transform.localScale, scale);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyCenteredRectSetsCenterAnchorsPositionAndSize()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            Vector2 position = new Vector2(12f, -34f);
            Vector2 size = new Vector2(300f, 120f);

            RuntimeUiTransformWriter.ApplyCenteredRect(rect, position, size);

            AssertVector(rect.anchorMin, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchorMax, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, size);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyAnchoredRectSetsAnchorsPositionAndSize()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            Vector2 anchor = new Vector2(0.12f, 0.55f);
            Vector2 position = Vector2.zero;
            Vector2 size = new Vector2(280f, 90f);

            RuntimeUiTransformWriter.ApplyAnchoredRect(rect, anchor, anchor, position, size);

            AssertVector(rect.anchorMin, anchor);
            AssertVector(rect.anchorMax, anchor);
            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, size);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplySizeDeltaSetsRectSize()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            Vector2 size = new Vector2(1200f, 900f);

            RuntimeUiTransformWriter.ApplySizeDelta(rect, size);

            AssertVector(rect.sizeDelta, size);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyAnchoredPositionSetsOnlyAnchoredPosition()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(10f, 20f);
            Vector2 position = new Vector2(-180f, -62f);

            RuntimeUiTransformWriter.ApplyAnchoredPosition(rect, position);

            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, new Vector2(10f, 20f));
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyStretchRectSetsAnchorsAndOffsets()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            Vector2 offsetMin = new Vector2(14f, 0f);
            Vector2 offsetMax = new Vector2(-14f, 0f);

            RuntimeUiTransformWriter.ApplyStretchRect(rect, Vector2.zero, Vector2.one, offsetMin, offsetMax);

            AssertVector(rect.anchorMin, Vector2.zero);
            AssertVector(rect.anchorMax, Vector2.one);
            AssertVector(rect.offsetMin, offsetMin);
            AssertVector(rect.offsetMax, offsetMax);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    [Test]
    public void ApplyFullStretchSurfaceRectSetsAnchorsPivotPositionAndOffsets()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();

            RuntimeUiTransformWriter.ApplyFullStretchSurfaceRect(rect);

            AssertVector(rect.anchorMin, Vector2.zero);
            AssertVector(rect.anchorMax, Vector2.one);
            AssertVector(rect.pivot, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchoredPosition3D, Vector3.zero);
            AssertVector(rect.offsetMin, Vector2.zero);
            AssertVector(rect.offsetMax, Vector2.zero);
        }
        finally
        {
            Object.DestroyImmediate(ui);
        }
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }

    private static void AssertVector(Vector2 actual, Vector2 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
    }
}
