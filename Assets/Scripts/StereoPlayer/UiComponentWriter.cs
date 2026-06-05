using UnityEngine;
using UnityEngine.UI;

public static class UiComponentWriter
{
    // ── Canvas ──────────────────────────────────────────────────────────

    public static void ApplyWorldSpaceCamera(Canvas canvas, Camera camera)
    {
        if (canvas == null) return;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;
    }

    public static void ApplyWorldCameraIfMissing(Canvas canvas, Camera camera)
    {
        if (canvas == null || canvas.worldCamera != null) return;
        canvas.worldCamera = camera;
    }

    public static void ApplyWorldCamera(Canvas canvas, Camera camera)
    {
        if (canvas == null) return;
        canvas.worldCamera = camera;
    }

    // ── Text ────────────────────────────────────────────────────────────

    public static void ApplyTextStyle(Text text, Font font, int fontSize, TextAnchor alignment, Color color)
    {
        if (text == null) return;
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
    }

    public static void ApplyTextContent(Text text, string content)
    {
        if (text == null) return;
        text.text = content;
    }

    public static void ApplyTextInteraction(Text text, bool raycastTarget)
    {
        if (text == null) return;
        text.raycastTarget = raycastTarget;
    }

    public static void ApplyTextOverflow(Text text, HorizontalWrapMode horizontal, VerticalWrapMode vertical)
    {
        if (text == null) return;
        text.horizontalOverflow = horizontal;
        text.verticalOverflow = vertical;
    }

    // ── Graphic / Selectable ────────────────────────────────────────────

    public static void ApplyGraphicColor(Graphic graphic, Color color)
    {
        if (graphic == null) return;
        graphic.color = color;
    }

    public static void ApplyTargetGraphic(Selectable selectable, Graphic targetGraphic)
    {
        if (selectable == null) return;
        selectable.targetGraphic = targetGraphic;
    }

    public static void ApplySelectableColors(Selectable selectable, ColorBlock colors)
    {
        if (selectable == null) return;
        selectable.colors = colors;
    }

    public static void ApplyInteractable(Selectable selectable, bool interactable)
    {
        if (selectable == null) return;
        selectable.interactable = interactable;
    }

    // ── Slider ──────────────────────────────────────────────────────────

    public static void ApplySliderDirection(Slider slider, Slider.Direction direction)
    {
        if (slider == null) return;
        slider.direction = direction;
    }

    public static void ApplySliderRange(Slider slider, float minValue, float maxValue)
    {
        if (slider == null) return;
        slider.minValue = minValue;
        slider.maxValue = maxValue;
    }

    public static void ApplySliderValueWithoutNotify(Slider slider, float value)
    {
        if (slider == null) return;
        slider.SetValueWithoutNotify(value);
    }

    public static void ApplySliderRects(Slider slider, RectTransform fillRect, RectTransform handleRect)
    {
        if (slider == null) return;
        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
    }
}
