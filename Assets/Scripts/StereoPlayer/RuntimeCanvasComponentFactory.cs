using System;
using UnityEngine;
using UnityEngine.UI;

public static class RuntimeCanvasComponentFactory
{
    public static Canvas EnsureCanvas(GameObject root)
    {
        if (root == null)
        {
            return null;
        }

        Canvas canvas = root.GetComponent<Canvas>();
        return canvas != null ? canvas : root.AddComponent<Canvas>();
    }

    public static GraphicRaycaster EnsureGraphicRaycaster(GameObject root, bool ignoreReversedGraphics)
    {
        if (root == null)
        {
            return null;
        }

        GraphicRaycaster raycaster = root.GetComponent<GraphicRaycaster>();
        if (raycaster == null)
        {
            raycaster = root.AddComponent<GraphicRaycaster>();
        }

        raycaster.ignoreReversedGraphics = ignoreReversedGraphics;
        return raycaster;
    }

    public static Component EnsureComponent(GameObject root, Type componentType)
    {
        if (root == null || componentType == null)
        {
            return null;
        }

        Component component = root.GetComponent(componentType);
        return component != null ? component : root.AddComponent(componentType);
    }
}
