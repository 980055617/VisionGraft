using UnityEngine;

public static class TransformWriter
{
    public static void ApplyPose(Transform target, Vector3 position, Quaternion rotation)
    {
        if (target == null) return;
        target.SetPositionAndRotation(position, rotation);
    }

    public static void ApplyLocalScale(Transform target, Vector3 localScale)
    {
        if (target == null) return;
        target.localScale = localScale;
    }

    public static void RotateSelf(Transform target, float xDegrees, float yDegrees, float zDegrees)
    {
        if (target == null) return;
        target.Rotate(xDegrees, yDegrees, zDegrees, Space.Self);
    }

    public static void ApplyWorldRotation(Transform target, Quaternion rotation)
    {
        if (target == null) return;
        target.rotation = rotation;
    }

    public static void ApplyLocalRotation(Transform target, Quaternion localRotation)
    {
        if (target == null) return;
        target.localRotation = localRotation;
    }

    public static void ApplyLocalPosition(Transform target, Vector3 localPosition)
    {
        if (target == null) return;
        target.localPosition = localPosition;
    }

    public static void ApplyLocalPose(Transform target, Vector3 localPosition, Quaternion localRotation)
    {
        if (target == null) return;
        target.localPosition = localPosition;
        target.localRotation = localRotation;
    }

    public static void ApplyWorldPoseAndScale(Transform target, Vector3 position, Quaternion rotation, Vector3 localScale)
    {
        if (target == null) return;
        target.SetPositionAndRotation(position, rotation);
        target.localScale = localScale;
    }

    public static void ApplyLocalTransform(Transform target, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        if (target == null) return;
        target.localPosition = localPosition;
        target.localRotation = localRotation;
        target.localScale = localScale;
    }

    public static void ApplyCenteredRect(RectTransform rect, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        if (rect == null) return;
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
        if (rect == null) return;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    public static void ApplySizeDelta(RectTransform rect, Vector2 sizeDelta)
    {
        if (rect == null) return;
        rect.sizeDelta = sizeDelta;
    }

    public static void ApplyAnchoredPosition(RectTransform rect, Vector2 anchoredPosition)
    {
        if (rect == null) return;
        rect.anchoredPosition = anchoredPosition;
    }

    public static void ApplyStretchRect(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax)
    {
        if (rect == null) return;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    public static void ApplyFullStretchSurfaceRect(RectTransform rect)
    {
        if (rect == null) return;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = Vector3.zero;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
