using UnityEngine;
using UnityEngine.UI;

public static class RuntimeTextWriter
{
    public static void ApplyStyle(Text text, Font font, int fontSize, TextAnchor alignment, Color color)
    {
        if (text == null)
        {
            return;
        }

        text.font = font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
    }

    public static void ApplyContent(Text text, string content)
    {
        if (text == null)
        {
            return;
        }

        text.text = content;
    }

    public static void ApplyInteraction(Text text, bool raycastTarget)
    {
        if (text == null)
        {
            return;
        }

        text.raycastTarget = raycastTarget;
    }

    public static void ApplyOverflow(Text text, HorizontalWrapMode horizontal, VerticalWrapMode vertical)
    {
        if (text == null)
        {
            return;
        }

        text.horizontalOverflow = horizontal;
        text.verticalOverflow = vertical;
    }
}
