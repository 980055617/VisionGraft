using UnityEngine;

public static class PoseTransformWriter
{
    public static void ApplyWorldRotation(Transform target, Quaternion rotation)
    {
        if (target == null)
        {
            return;
        }

        target.rotation = rotation;
    }

    public static void ApplyLocalRotation(Transform target, Quaternion localRotation)
    {
        if (target == null)
        {
            return;
        }

        target.localRotation = localRotation;
    }

    public static void ApplyLocalPosition(Transform target, Vector3 localPosition)
    {
        if (target == null)
        {
            return;
        }

        target.localPosition = localPosition;
    }

    public static void ApplyLocalPose(Transform target, Vector3 localPosition, Quaternion localRotation)
    {
        if (target == null)
        {
            return;
        }

        target.localPosition = localPosition;
        target.localRotation = localRotation;
    }
}
