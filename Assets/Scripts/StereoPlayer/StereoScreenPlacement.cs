using UnityEngine;

public static class StereoScreenPlacement
{
    public readonly struct Placement
    {
        public Placement(Vector3 center, Quaternion rotation, Vector3 leftPosition, Vector3 rightPosition)
        {
            this.center = center;
            this.rotation = rotation;
            this.leftPosition = leftPosition;
            this.rightPosition = rightPosition;
        }

        public readonly Vector3 center;
        public readonly Quaternion rotation;
        public readonly Vector3 leftPosition;
        public readonly Vector3 rightPosition;
    }

    public readonly struct ForcedPose
    {
        public ForcedPose(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public readonly Vector3 position;
        public readonly Quaternion rotation;
    }

    public static Placement ResolvePlacement(
        Vector3 headPosition,
        Quaternion headRotation,
        float screenDistanceMeters,
        Vector3 screenOffsetMeters,
        float eyeSeparationMeters)
    {
        Vector3 headForward = headRotation * Vector3.forward;
        Vector3 headUp = headRotation * Vector3.up;
        Vector3 headRight = headRotation * Vector3.right;
        Vector3 center = headPosition + headForward * screenDistanceMeters + headRotation * screenOffsetMeters;
        Vector3 toHead = (headPosition - center).normalized;
        Quaternion rotation = Quaternion.LookRotation(toHead, headUp);
        Vector3 rightOffset = headRight * eyeSeparationMeters;
        return new Placement(center, rotation, center - rightOffset, center + rightOffset);
    }

    public static void ResolveFitSize(
        float screenDistanceMeters,
        float fovxDeg,
        int eyeWidth,
        int eyeHeight,
        out float width,
        out float height)
    {
        float distance = Mathf.Max(0.0001f, screenDistanceMeters);
        float fovxRad = fovxDeg * Mathf.Deg2Rad;
        width = 2f * distance * Mathf.Tan(fovxRad * 0.5f);
        height = width * (eyeHeight / (float)eyeWidth);
    }

    public static ForcedPose ResolveForcedInFrontPose(Vector3 cameraPosition, Vector3 cameraForward, float screenDistanceMeters)
    {
        Vector3 position = cameraPosition + cameraForward * screenDistanceMeters;
        Quaternion rotation = Quaternion.LookRotation((cameraPosition - position).normalized, Vector3.up);
        return new ForcedPose(position, rotation);
    }
}
