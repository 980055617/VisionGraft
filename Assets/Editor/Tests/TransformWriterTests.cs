using NUnit.Framework;
using UnityEngine;

public class TransformWriterTests
{
    // ── from ScreenTransformWriter ──────────────────────────────────────

    [Test]
    public void ApplyPoseKeepsExistingScale()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            screen.transform.localScale = new Vector3(1.2f, 0.8f, 1f);
            Vector3 position = new Vector3(0.1f, 1.5f, 2.0f);
            Quaternion rotation = Quaternion.Euler(0f, 180f, 0f);

            TransformWriter.ApplyPose(screen.transform, position, rotation);

            AssertVector(screen.transform.position, position);
            Assert.That(Quaternion.Angle(screen.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(screen.transform.localScale, new Vector3(1.2f, 0.8f, 1f));
        }
        finally { Object.DestroyImmediate(screen); }
    }

    [Test]
    public void ApplyLocalScaleKeepsExistingPose()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            screen.transform.position = Vector3.forward;
            screen.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            Vector3 scale = new Vector3(2f, 1f, 1f);

            TransformWriter.ApplyLocalScale(screen.transform, scale);

            AssertVector(screen.transform.position, Vector3.forward);
            Assert.That(Quaternion.Angle(screen.transform.rotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(0.001f));
            AssertVector(screen.transform.localScale, scale);
        }
        finally { Object.DestroyImmediate(screen); }
    }

    [Test]
    public void RotateSelfTurnsAroundLocalAxis()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            TransformWriter.RotateSelf(screen.transform, 0f, 180f, 0f);

            Assert.That(Vector3.Dot(screen.transform.forward, Vector3.back), Is.GreaterThan(0.999f));
        }
        finally { Object.DestroyImmediate(screen); }
    }

    // ── from PoseTransformWriter ────────────────────────────────────────

    [Test]
    public void ApplyWorldRotationKeepsPositionAndScale()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            bone.transform.position = new Vector3(1f, 2f, 3f);
            bone.transform.localScale = Vector3.one * 2f;
            Quaternion rotation = Quaternion.Euler(20f, 30f, 40f);

            TransformWriter.ApplyWorldRotation(bone.transform, rotation);

            AssertVector(bone.transform.position, new Vector3(1f, 2f, 3f));
            Assert.That(Quaternion.Angle(bone.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(bone.transform.localScale, Vector3.one * 2f);
        }
        finally { Object.DestroyImmediate(bone); }
    }

    [Test]
    public void ApplyLocalPoseWritesLocalPositionAndRotation()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            Vector3 localPosition = new Vector3(0.1f, 0.2f, 0.3f);
            Quaternion localRotation = Quaternion.Euler(0f, 90f, 0f);

            TransformWriter.ApplyLocalPose(bone.transform, localPosition, localRotation);

            AssertVector(bone.transform.localPosition, localPosition);
            Assert.That(Quaternion.Angle(bone.transform.localRotation, localRotation), Is.LessThan(0.001f));
        }
        finally { Object.DestroyImmediate(bone); }
    }

    [Test]
    public void ApplyLocalRotationSetsLocalRotation()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            Quaternion localRotation = Quaternion.Euler(0f, 45f, 0f);

            TransformWriter.ApplyLocalRotation(bone.transform, localRotation);

            Assert.That(Quaternion.Angle(bone.transform.localRotation, localRotation), Is.LessThan(0.001f));
        }
        finally { Object.DestroyImmediate(bone); }
    }

    [Test]
    public void ApplyLocalPositionSetsLocalPosition()
    {
        GameObject bone = new GameObject("Bone");
        try
        {
            Vector3 pos = new Vector3(1f, 2f, 3f);

            TransformWriter.ApplyLocalPosition(bone.transform, pos);

            AssertVector(bone.transform.localPosition, pos);
        }
        finally { Object.DestroyImmediate(bone); }
    }

    // ── from DebugTransformWriter ───────────────────────────────────────

    [Test]
    public void ApplyWorldPoseAndScaleWritesFullTransform()
    {
        GameObject debugObject = new GameObject("DebugObject");
        try
        {
            Vector3 position = new Vector3(1f, 2f, 3f);
            Quaternion rotation = Quaternion.Euler(10f, 20f, 30f);
            Vector3 scale = new Vector3(0.5f, 0.25f, 0.75f);

            TransformWriter.ApplyWorldPoseAndScale(debugObject.transform, position, rotation, scale);

            AssertVector(debugObject.transform.position, position);
            Assert.That(Quaternion.Angle(debugObject.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(debugObject.transform.localScale, scale);
        }
        finally { Object.DestroyImmediate(debugObject); }
    }

    [Test]
    public void ApplyLocalTransformWritesLocalPositionRotationAndScale()
    {
        GameObject debugObject = new GameObject("DebugChild");
        try
        {
            Vector3 position = new Vector3(0f, 0.2f, 0.4f);
            Quaternion rotation = Quaternion.Euler(0f, 45f, 0f);
            Vector3 scale = Vector3.one * 0.1f;

            TransformWriter.ApplyLocalTransform(debugObject.transform, position, rotation, scale);

            AssertVector(debugObject.transform.localPosition, position);
            Assert.That(Quaternion.Angle(debugObject.transform.localRotation, rotation), Is.LessThan(0.001f));
            AssertVector(debugObject.transform.localScale, scale);
        }
        finally { Object.DestroyImmediate(debugObject); }
    }

    // ── from RuntimeUiTransformWriter ──────────────────────────────────

    [Test]
    public void ApplyPoseForUiKeepsExistingLocalScale()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            ui.transform.localScale = Vector3.one * 0.01f;
            Vector3 position = new Vector3(0f, 1f, 2f);
            Quaternion rotation = Quaternion.Euler(0f, 25f, 0f);

            TransformWriter.ApplyPose(ui.transform, position, rotation);

            AssertVector(ui.transform.position, position);
            Assert.That(Quaternion.Angle(ui.transform.rotation, rotation), Is.LessThan(0.001f));
            AssertVector(ui.transform.localScale, Vector3.one * 0.01f);
        }
        finally { Object.DestroyImmediate(ui); }
    }

    [Test]
    public void ApplyLocalScaleForUiKeepsExistingWorldPose()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            ui.transform.position = Vector3.right;
            ui.transform.rotation = Quaternion.Euler(0f, 60f, 0f);
            Vector3 scale = new Vector3(0.002f, 0.003f, 0.002f);

            TransformWriter.ApplyLocalScale(ui.transform, scale);

            AssertVector(ui.transform.position, Vector3.right);
            Assert.That(Quaternion.Angle(ui.transform.rotation, Quaternion.Euler(0f, 60f, 0f)), Is.LessThan(0.001f));
            AssertVector(ui.transform.localScale, scale);
        }
        finally { Object.DestroyImmediate(ui); }
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

            TransformWriter.ApplyCenteredRect(rect, position, size);

            AssertVector(rect.anchorMin, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchorMax, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, size);
        }
        finally { Object.DestroyImmediate(ui); }
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

            TransformWriter.ApplyAnchoredRect(rect, anchor, anchor, position, size);

            AssertVector(rect.anchorMin, anchor);
            AssertVector(rect.anchorMax, anchor);
            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, size);
        }
        finally { Object.DestroyImmediate(ui); }
    }

    [Test]
    public void ApplySizeDeltaSetsRectSize()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();
            Vector2 size = new Vector2(1200f, 900f);

            TransformWriter.ApplySizeDelta(rect, size);

            AssertVector(rect.sizeDelta, size);
        }
        finally { Object.DestroyImmediate(ui); }
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

            TransformWriter.ApplyAnchoredPosition(rect, position);

            AssertVector(rect.anchoredPosition, position);
            AssertVector(rect.sizeDelta, new Vector2(10f, 20f));
        }
        finally { Object.DestroyImmediate(ui); }
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

            TransformWriter.ApplyStretchRect(rect, Vector2.zero, Vector2.one, offsetMin, offsetMax);

            AssertVector(rect.anchorMin, Vector2.zero);
            AssertVector(rect.anchorMax, Vector2.one);
            AssertVector(rect.offsetMin, offsetMin);
            AssertVector(rect.offsetMax, offsetMax);
        }
        finally { Object.DestroyImmediate(ui); }
    }

    [Test]
    public void ApplyFullStretchSurfaceRectSetsAnchorsPivotPositionAndOffsets()
    {
        GameObject ui = new GameObject("RuntimeUi");
        try
        {
            RectTransform rect = ui.AddComponent<RectTransform>();

            TransformWriter.ApplyFullStretchSurfaceRect(rect);

            AssertVector(rect.anchorMin, Vector2.zero);
            AssertVector(rect.anchorMax, Vector2.one);
            AssertVector(rect.pivot, new Vector2(0.5f, 0.5f));
            AssertVector(rect.anchoredPosition3D, Vector3.zero);
            AssertVector(rect.offsetMin, Vector2.zero);
            AssertVector(rect.offsetMax, Vector2.zero);
        }
        finally { Object.DestroyImmediate(ui); }
    }

    // ── null safety ─────────────────────────────────────────────────────

    [Test]
    public void AllMethodsIgnoreNullTransform()
    {
        Assert.DoesNotThrow(() => TransformWriter.ApplyPose(null, Vector3.zero, Quaternion.identity));
        Assert.DoesNotThrow(() => TransformWriter.ApplyLocalScale(null, Vector3.one));
        Assert.DoesNotThrow(() => TransformWriter.RotateSelf(null, 0f, 0f, 0f));
        Assert.DoesNotThrow(() => TransformWriter.ApplyWorldRotation(null, Quaternion.identity));
        Assert.DoesNotThrow(() => TransformWriter.ApplyLocalRotation(null, Quaternion.identity));
        Assert.DoesNotThrow(() => TransformWriter.ApplyLocalPosition(null, Vector3.zero));
        Assert.DoesNotThrow(() => TransformWriter.ApplyLocalPose(null, Vector3.zero, Quaternion.identity));
        Assert.DoesNotThrow(() => TransformWriter.ApplyWorldPoseAndScale(null, Vector3.zero, Quaternion.identity, Vector3.one));
        Assert.DoesNotThrow(() => TransformWriter.ApplyLocalTransform(null, Vector3.zero, Quaternion.identity, Vector3.one));
        Assert.DoesNotThrow(() => TransformWriter.ApplyCenteredRect(null, Vector2.zero, Vector2.one));
        Assert.DoesNotThrow(() => TransformWriter.ApplyAnchoredRect(null, Vector2.zero, Vector2.one, Vector2.zero, Vector2.one));
        Assert.DoesNotThrow(() => TransformWriter.ApplySizeDelta(null, Vector2.one));
        Assert.DoesNotThrow(() => TransformWriter.ApplyAnchoredPosition(null, Vector2.zero));
        Assert.DoesNotThrow(() => TransformWriter.ApplyStretchRect(null, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero));
        Assert.DoesNotThrow(() => TransformWriter.ApplyFullStretchSurfaceRect(null));
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
