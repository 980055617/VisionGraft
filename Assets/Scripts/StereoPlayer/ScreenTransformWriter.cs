using UnityEngine;

public static class ScreenTransformWriter
{
    public static void ApplyPose(Transform screen, Vector3 position, Quaternion rotation)
    {
        if (screen == null)
        {
            return;
        }

        screen.SetPositionAndRotation(position, rotation);
    }

    public static void ApplyLocalScale(Transform screen, Vector3 localScale)
    {
        if (screen == null)
        {
            return;
        }

        screen.localScale = localScale;
    }

    public static void RotateSelf(Transform screen, float xDegrees, float yDegrees, float zDegrees)
    {
        if (screen == null)
        {
            return;
        }

        screen.Rotate(xDegrees, yDegrees, zDegrees, Space.Self);
    }
}
