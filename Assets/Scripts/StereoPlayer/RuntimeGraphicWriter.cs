using UnityEngine;
using UnityEngine.UI;

public static class RuntimeGraphicWriter
{
    public static void ApplyColor(Graphic graphic, Color color)
    {
        if (graphic == null)
        {
            return;
        }

        graphic.color = color;
    }

    public static void ApplyTargetGraphic(Selectable selectable, Graphic targetGraphic)
    {
        if (selectable == null)
        {
            return;
        }

        selectable.targetGraphic = targetGraphic;
    }

    public static void ApplyColors(Selectable selectable, ColorBlock colors)
    {
        if (selectable == null)
        {
            return;
        }

        selectable.colors = colors;
    }
}
