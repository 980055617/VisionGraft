using UnityEngine;

public static class RuntimeUiTransformWriter
{
    public static void ApplyWorldPose(Transform root, Vector3 position, Quaternion rotation)
    {
        if (root == null)
        {
            return;
        }

        root.SetPositionAndRotation(position, rotation);
    }

    public static void ApplyLocalScale(Transform root, Vector3 localScale)
    {
        if (root == null)
        {
            return;
        }

        root.localScale = localScale;
    }

    public static void ApplyCenteredRect(RectTransform rect, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    public static void ApplyAnchoredRect(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 sizeDelta)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    public static void ApplySizeDelta(RectTransform rect, Vector2 sizeDelta)
    {
        if (rect == null)
        {
            return;
        }

        rect.sizeDelta = sizeDelta;
    }

    public static void ApplyAnchoredPosition(RectTransform rect, Vector2 anchoredPosition)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchoredPosition = anchoredPosition;
    }

    public static void ApplyStretchRect(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    public static void ApplyFullStretchSurfaceRect(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = Vector3.zero;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
