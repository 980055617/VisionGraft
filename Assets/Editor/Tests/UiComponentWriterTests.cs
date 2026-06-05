using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class UiComponentWriterTests
{
    // ── from RuntimeCanvasWriter ────────────────────────────────────────

    [Test]
    public void ApplyWorldSpaceCameraSetsRenderModeAndCamera()
    {
        GameObject canvasObj = new GameObject("Canvas", typeof(Canvas));
        GameObject cameraObj = new GameObject("Camera", typeof(Camera));
        try
        {
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            Camera camera = cameraObj.GetComponent<Camera>();

            UiComponentWriter.ApplyWorldSpaceCamera(canvas, camera);

            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
            Assert.That(canvas.worldCamera, Is.EqualTo(camera));
        }
        finally
        {
            Object.DestroyImmediate(canvasObj);
            Object.DestroyImmediate(cameraObj);
        }
    }

    [Test]
    public void ApplyWorldCameraIfMissingKeepsExistingCamera()
    {
        GameObject canvasObj = new GameObject("Canvas", typeof(Canvas));
        GameObject existingObj = new GameObject("ExistingCamera", typeof(Camera));
        GameObject candidateObj = new GameObject("CandidateCamera", typeof(Camera));
        try
        {
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            canvas.worldCamera = existingObj.GetComponent<Camera>();

            UiComponentWriter.ApplyWorldCameraIfMissing(canvas, candidateObj.GetComponent<Camera>());

            Assert.That(canvas.worldCamera, Is.EqualTo(existingObj.GetComponent<Camera>()));
        }
        finally
        {
            Object.DestroyImmediate(canvasObj);
            Object.DestroyImmediate(existingObj);
            Object.DestroyImmediate(candidateObj);
        }
    }

    [Test]
    public void ApplyWorldCameraReplacesExistingCamera()
    {
        GameObject canvasObj = new GameObject("Canvas", typeof(Canvas));
        GameObject existingObj = new GameObject("Existing", typeof(Camera));
        GameObject replacementObj = new GameObject("Replacement", typeof(Camera));
        try
        {
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            canvas.worldCamera = existingObj.GetComponent<Camera>();
            Camera replacement = replacementObj.GetComponent<Camera>();

            UiComponentWriter.ApplyWorldCamera(canvas, replacement);

            Assert.That(canvas.worldCamera, Is.EqualTo(replacement));
        }
        finally
        {
            Object.DestroyImmediate(canvasObj);
            Object.DestroyImmediate(existingObj);
            Object.DestroyImmediate(replacementObj);
        }
    }

    // ── from RuntimeTextWriter ──────────────────────────────────────────

    [Test]
    public void ApplyTextStyleSetsFontSizeAlignmentAndColor()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();
            text.text = "existing";
            Color color = new Color(0.2f, 0.4f, 0.6f, 1f);

            UiComponentWriter.ApplyTextStyle(text, null, 32, TextAnchor.MiddleRight, color);

            Assert.That(text.text, Is.EqualTo("existing"));
            Assert.That(text.fontSize, Is.EqualTo(32));
            Assert.That(text.alignment, Is.EqualTo(TextAnchor.MiddleRight));
            Assert.That(text.color, Is.EqualTo(color));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplyTextContentSetsText()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();

            UiComponentWriter.ApplyTextContent(text, "updated");

            Assert.That(text.text, Is.EqualTo("updated"));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplyTextInteractionSetsRaycastTarget()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();

            UiComponentWriter.ApplyTextInteraction(text, false);

            Assert.That(text.raycastTarget, Is.False);
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplyTextOverflowSetsWrapModes()
    {
        GameObject obj = new GameObject("Text");
        try
        {
            Text text = obj.AddComponent<Text>();

            UiComponentWriter.ApplyTextOverflow(text, HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate);

            Assert.That(text.horizontalOverflow, Is.EqualTo(HorizontalWrapMode.Wrap));
            Assert.That(text.verticalOverflow, Is.EqualTo(VerticalWrapMode.Truncate));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    // ── from RuntimeGraphicWriter ───────────────────────────────────────

    [Test]
    public void ApplyGraphicColorSetsColor()
    {
        GameObject obj = new GameObject("Image");
        try
        {
            Image image = obj.AddComponent<Image>();
            Color color = new Color(0.1f, 0.2f, 0.3f, 0.4f);

            UiComponentWriter.ApplyGraphicColor(image, color);

            Assert.That(image.color, Is.EqualTo(color));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplyTargetGraphicSetsSelectableTargetGraphic()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Image image = obj.AddComponent<Image>();
            Button button = obj.AddComponent<Button>();

            UiComponentWriter.ApplyTargetGraphic(button, image);

            Assert.That(button.targetGraphic, Is.EqualTo(image));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplySelectableColorsSetsColorBlock()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Button button = obj.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.red;

            UiComponentWriter.ApplySelectableColors(button, colors);

            Assert.That(button.colors.normalColor, Is.EqualTo(Color.red));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    // ── from RuntimeSliderWriter ────────────────────────────────────────

    [Test]
    public void ApplySliderRangeSetsMinAndMax()
    {
        GameObject obj = new GameObject("Slider");
        try
        {
            Slider slider = obj.AddComponent<Slider>();

            UiComponentWriter.ApplySliderRange(slider, -180f, 180f);

            Assert.That(slider.minValue, Is.EqualTo(-180f));
            Assert.That(slider.maxValue, Is.EqualTo(180f));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplySliderValueWithoutNotifySetsValue()
    {
        GameObject obj = new GameObject("Slider");
        try
        {
            Slider slider = obj.AddComponent<Slider>();

            UiComponentWriter.ApplySliderValueWithoutNotify(slider, 0.5f);

            Assert.That(slider.value, Is.EqualTo(0.5f).Within(0.0001f));
        }
        finally { Object.DestroyImmediate(obj); }
    }

    [Test]
    public void ApplySliderRectsSetsRects()
    {
        GameObject obj = new GameObject("Slider");
        GameObject fill = new GameObject("Fill");
        GameObject handle = new GameObject("Handle");
        try
        {
            Slider slider = obj.AddComponent<Slider>();
            RectTransform fillRect = fill.AddComponent<RectTransform>();
            RectTransform handleRect = handle.AddComponent<RectTransform>();

            UiComponentWriter.ApplySliderRects(slider, fillRect, handleRect);

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

    // ── from RuntimeSelectableWriter ────────────────────────────────────

    [Test]
    public void ApplyInteractableSetsSelectableState()
    {
        GameObject obj = new GameObject("Button");
        try
        {
            Button button = obj.AddComponent<Button>();

            UiComponentWriter.ApplyInteractable(button, false);

            Assert.That(button.interactable, Is.False);
        }
        finally { Object.DestroyImmediate(obj); }
    }

    // ── null safety ─────────────────────────────────────────────────────

    [Test]
    public void AllMethodsIgnoreNullInputs()
    {
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyWorldSpaceCamera(null, null));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyWorldCameraIfMissing(null, null));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyWorldCamera(null, null));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyTextStyle(null, null, 0, TextAnchor.UpperLeft, Color.white));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyTextContent(null, "x"));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyTextInteraction(null, false));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyTextOverflow(null, HorizontalWrapMode.Overflow, VerticalWrapMode.Overflow));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyGraphicColor(null, Color.white));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyTargetGraphic(null, null));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplySelectableColors(null, default));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplySliderDirection(null, Slider.Direction.LeftToRight));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplySliderRange(null, 0f, 1f));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplySliderValueWithoutNotify(null, 0f));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplySliderRects(null, null, null));
        Assert.DoesNotThrow(() => UiComponentWriter.ApplyInteractable(null, true));
    }
}
