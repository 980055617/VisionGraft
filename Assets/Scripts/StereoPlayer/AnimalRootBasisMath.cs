using UnityEngine;

public static class AnimalRootBasisMath
{
    public static void StabilizePitchRoll(
        Vector3 worldUp,
        Vector3 forward,
        Vector3 up,
        float pitchRollBlend,
        out Vector3 stabilizedForward,
        out Vector3 stabilizedUp)
    {
        stabilizedForward = forward;
        stabilizedUp = up;

        Vector3 planarForward = Vector3.ProjectOnPlane(forward, worldUp);
        if (planarForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        planarForward.Normalize();

        float tiltBlend = Mathf.Clamp01(pitchRollBlend);
        Vector3 blendedForward = Vector3.Slerp(planarForward, forward.normalized, tiltBlend);
        if (blendedForward.sqrMagnitude <= 0.000001f)
        {
            blendedForward = planarForward;
        }
        blendedForward.Normalize();

        Vector3 blendedUp = Vector3.Slerp(worldUp, up.sqrMagnitude > 0.000001f ? up.normalized : worldUp, tiltBlend);
        blendedUp = Vector3.ProjectOnPlane(blendedUp, blendedForward);
        if (blendedUp.sqrMagnitude <= 0.000001f)
        {
            blendedUp = Vector3.ProjectOnPlane(worldUp, blendedForward);
        }
        if (blendedUp.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = blendedForward;
            stabilizedUp = worldUp;
            return;
        }

        blendedUp.Normalize();
        Vector3 right = Vector3.Cross(blendedForward, blendedUp);
        if (right.sqrMagnitude > 0.000001f)
        {
            right.Normalize();
            blendedUp = Vector3.Cross(right, blendedForward).normalized;
        }

        stabilizedForward = blendedForward;
        stabilizedUp = blendedUp;
    }
}
