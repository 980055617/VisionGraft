using UnityEngine;

public static class TrackPlacementWriter
{
    public static void Apply(Transform root, TrackPlacementCommand command)
    {
        if (root == null)
        {
            return;
        }

        root.SetPositionAndRotation(command.position, command.rotation);
        root.localScale = command.localScale;
    }

    public static void ApplyPosition(Transform root, Vector3 position)
    {
        if (root == null)
        {
            return;
        }

        root.position = position;
    }

    public static void ApplyLocalScale(Transform root, Vector3 localScale)
    {
        if (root == null)
        {
            return;
        }

        root.localScale = localScale;
    }

    public static void ApplyLocalScaleWithGroundAlignment(
        Transform root,
        Vector3 localScale,
        bool alignToGround,
        float baseBottomOffsetLocal)
    {
        if (root == null)
        {
            return;
        }

        ApplyLocalScale(root, localScale);
        if (!alignToGround)
        {
            return;
        }

        float offsetWorld = baseBottomOffsetLocal * root.lossyScale.y;
        ApplyPosition(root, root.position + root.up * offsetWorld);
    }

    public static void ApplyRotation(Transform root, Quaternion rotation)
    {
        if (root == null)
        {
            return;
        }

        root.rotation = rotation;
    }

    public static void ApplyAnchoredPose(
        Transform root,
        Vector3 targetAnchorWorld,
        Quaternion rotation,
        Vector3 localScale,
        Transform anchor)
    {
        if (root == null)
        {
            return;
        }

        Apply(root, new TrackPlacementCommand(targetAnchorWorld, rotation, localScale));
        if (anchor == null)
        {
            return;
        }

        Vector3 delta = anchor.position - root.position;
        ApplyPosition(root, targetAnchorWorld - delta);
    }

    public static void ApplyAnchorPosition(Transform root, Transform anchor, Vector3 targetAnchorWorld)
    {
        if (root == null)
        {
            return;
        }

        ApplyPosition(root, CalculateAnchorAlignedPosition(root, anchor, targetAnchorWorld));
    }

    public static Vector3 CalculateAnchorAlignedPosition(
        Transform root,
        Transform anchor,
        Vector3 targetAnchorWorld)
    {
        if (root == null || anchor == null)
        {
            return targetAnchorWorld;
        }

        return root.position + targetAnchorWorld - anchor.position;
    }

    public static void ApplyBottomAlignment(
        Transform root,
        Vector3 targetBottomWorld,
        Vector3 upAxis,
        float modelBottomOffsetWorld,
        bool verticalOnly)
    {
        if (root == null)
        {
            return;
        }

        Vector3 up = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 modelBottomWorld = root.position - up * modelBottomOffsetWorld;
        Vector3 delta = targetBottomWorld - modelBottomWorld;
        ApplyPosition(root, verticalOnly ? root.position + up * Vector3.Dot(delta, up) : root.position + delta);
    }

    public static void ApplyCameraSpaceOffset(Transform root, Quaternion cameraRotation, Vector3 cameraSpaceOffset)
    {
        if (root == null)
        {
            return;
        }

        ApplyPosition(root, root.position + cameraRotation * cameraSpaceOffset);
    }
}
