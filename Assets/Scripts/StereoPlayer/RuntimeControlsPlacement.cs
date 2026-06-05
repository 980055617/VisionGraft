using UnityEngine;

public static class RuntimeControlsPlacement
{
    public readonly struct Pose
    {
        public Pose(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public readonly Vector3 position;
        public readonly Quaternion rotation;
    }

    public static Pose ResolveBarPose(
        Vector3 center,
        Vector3 basisForward,
        Vector3 basisRight,
        Vector3 basisUp,
        Quaternion basisRotation,
        bool hasHead,
        Vector3 headPosition,
        float screenHeightMeters,
        Vector2 controlsBarSizeMeters,
        float controlsBarGapMeters,
        Vector2 controlsBarOffsetMeters,
        float controlsBarForwardOffsetMeters)
    {
        Vector3 toHead = hasHead ? (headPosition - center).normalized : -basisForward;
        if (toHead == Vector3.zero)
        {
            toHead = -basisForward;
        }

        float halfScreenH = Mathf.Abs(screenHeightMeters) * 0.5f;
        float halfBarH = Mathf.Abs(controlsBarSizeMeters.y) * 0.5f;
        float downFromCenter = halfScreenH + controlsBarGapMeters + halfBarH - controlsBarOffsetMeters.y;
        Vector3 position =
            center
            + basisRight * controlsBarOffsetMeters.x
            - basisUp * downFromCenter
            + toHead * controlsBarForwardOffsetMeters;

        return new Pose(position, basisRotation);
    }

    public static Pose ResolveSettingsPose(
        Vector3 basisPosition,
        Vector3 basisForward,
        Vector3 basisRight,
        Vector3 basisUp,
        Quaternion basisRotation,
        bool hasHead,
        Vector3 headPosition,
        float basisWidthMeters,
        Vector2 settingsPanelSizeMeters,
        float settingsPanelGapMeters,
        Vector2 settingsPanelOffsetMeters,
        float settingsPanelForwardOffsetMeters)
    {
        Vector3 toHead = hasHead ? (headPosition - basisPosition).normalized : -basisForward;
        if (toHead == Vector3.zero)
        {
            toHead = -basisForward;
        }

        float halfBarW = Mathf.Abs(basisWidthMeters) * 0.5f;
        float halfPanelW = Mathf.Abs(settingsPanelSizeMeters.x) * 0.5f;
        float rightFromBar = halfBarW + settingsPanelGapMeters + halfPanelW + settingsPanelOffsetMeters.x;
        float forwardFromScreen = Mathf.Max(settingsPanelForwardOffsetMeters, 0.06f);
        Vector3 position =
            basisPosition
            + basisRight * rightFromBar
            + basisUp * settingsPanelOffsetMeters.y
            + toHead * forwardFromScreen;

        Quaternion rotation = basisRotation;
        if (hasHead)
        {
            Vector3 up = basisUp.sqrMagnitude > 0.000001f ? basisUp.normalized : Vector3.up;
            Vector3 look = headPosition - position;
            look = Vector3.ProjectOnPlane(look, up);
            if (look.sqrMagnitude > 0.000001f)
            {
                rotation = Quaternion.LookRotation(look.normalized, up);
            }

            rotation = Quaternion.AngleAxis(180f, up) * rotation;
        }

        return new Pose(position, rotation);
    }
}
