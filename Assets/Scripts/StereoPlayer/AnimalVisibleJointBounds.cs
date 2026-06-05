using UnityEngine;

public static class AnimalVisibleJointBounds
{
    public static bool TryResolve(Vector3[] jointsWorld, byte[] visibility, int jointCount, out Bounds bounds)
    {
        bounds = default(Bounds);
        if (jointsWorld == null || visibility == null || jointCount <= 0)
        {
            return false;
        }

        bool hasAny = false;
        int count = Mathf.Min(jointCount, Mathf.Min(jointsWorld.Length, visibility.Length));
        for (int i = 0; i < count; i++)
        {
            if (visibility[i] == 0)
            {
                continue;
            }

            Vector3 point = jointsWorld[i];
            if (float.IsNaN(point.x) || float.IsInfinity(point.x) ||
                float.IsNaN(point.y) || float.IsInfinity(point.y) ||
                float.IsNaN(point.z) || float.IsInfinity(point.z))
            {
                continue;
            }

            if (!hasAny)
            {
                bounds = new Bounds(point, Vector3.zero);
                hasAny = true;
            }
            else
            {
                bounds.Encapsulate(point);
            }
        }

        return hasAny;
    }
}
