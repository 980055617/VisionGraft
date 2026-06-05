using UnityEngine;

public static class DebugTransformWriter
{
    public static void ApplyWorldPoseAndScale(Transform target, Vector3 position, Quaternion rotation, Vector3 localScale)
    {
        if (target == null)
        {
            return;
        }

        target.SetPositionAndRotation(position, rotation);
        target.localScale = localScale;
    }

    public static void ApplyLocalTransform(Transform target, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        if (target == null)
        {
            return;
        }

        target.localPosition = localPosition;
        target.localRotation = localRotation;
        target.localScale = localScale;
    }
}
