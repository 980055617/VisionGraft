using UnityEngine;

internal static class TrackedJointPoints
{
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;

    public static bool TryGet(Vector3[] jointsWorld, byte[] visibility, int index, out Vector3 point)
    {
        point = Vector3.zero;
        if (jointsWorld == null || visibility == null)
        {
            return false;
        }

        if (index < 0 || index >= jointsWorld.Length || index >= visibility.Length)
        {
            return false;
        }

        byte visibilityFlag = visibility[index];
        Vector3 candidate = jointsWorld[index];
        if (visibilityFlag == 0)
        {
            return false;
        }

        if (float.IsNaN(candidate.x) || float.IsInfinity(candidate.x) ||
            float.IsNaN(candidate.y) || float.IsInfinity(candidate.y) ||
            float.IsNaN(candidate.z) || float.IsInfinity(candidate.z))
        {
            return false;
        }

        if (candidate.sqrMagnitude <= InvalidJointSqrMagnitudeEpsilon)
        {
            return false;
        }

        point = candidate;
        return true;
    }
}
