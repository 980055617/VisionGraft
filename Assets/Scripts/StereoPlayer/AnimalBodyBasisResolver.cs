using UnityEngine;

public static class AnimalBodyBasisResolver
{
    public static bool TryResolveFromJoints(
        Vector3[] jointsWorld,
        byte[] vis,
        Vector3 preferredUp,
        out Vector3 forward,
        out Vector3 up,
        out Vector3 facingHint)
    {
        forward = Vector3.zero;
        up = Vector3.zero;
        facingHint = Vector3.zero;

        bool hasPelvis = TrackedJointPoints.TryGet(jointsWorld, vis, 7, out Vector3 pelvisHub);
        bool hasWithers = TrackedJointPoints.TryGet(jointsWorld, vis, 18, out Vector3 withersHub);
        bool hasHeadRoot = TrackedJointPoints.TryGet(jointsWorld, vis, 24, out Vector3 headRoot);

        if (hasPelvis && hasWithers)
        {
            forward = (withersHub - pelvisHub).normalized;
        }

        if (forward.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        if (hasWithers && hasHeadRoot)
        {
            facingHint = (headRoot - withersHub).normalized;
            if (facingHint.sqrMagnitude > 0.000001f && Vector3.Dot(forward, facingHint) < 0f)
            {
                forward = -forward;
            }
        }

        bool hasLeftShoulder = TrackedJointPoints.TryGet(jointsWorld, vis, 12, out Vector3 leftShoulder);
        bool hasRightShoulder = TrackedJointPoints.TryGet(jointsWorld, vis, 13, out Vector3 rightShoulder);
        bool hasLeftHip = TrackedJointPoints.TryGet(jointsWorld, vis, 10, out Vector3 leftHip);
        bool hasRightHip = TrackedJointPoints.TryGet(jointsWorld, vis, 11, out Vector3 rightHip);

        Vector3 rightAxis = Vector3.zero;
        if (hasLeftShoulder && hasRightShoulder)
        {
            rightAxis += rightShoulder - leftShoulder;
        }
        if (hasLeftHip && hasRightHip)
        {
            rightAxis += rightHip - leftHip;
        }

        if (rightAxis.sqrMagnitude > 0.000001f)
        {
            rightAxis.Normalize();
            Vector3 upA = Vector3.Cross(rightAxis, forward);
            Vector3 upB = -upA;
            if (preferredUp.sqrMagnitude < 0.000001f)
            {
                preferredUp = Vector3.up;
            }

            up = Vector3.Dot(upA, preferredUp) >= Vector3.Dot(upB, preferredUp) ? upA : upB;
        }

        if (up.sqrMagnitude <= 0.000001f)
        {
            Vector3 fallbackUp = preferredUp.sqrMagnitude > 0.000001f ? preferredUp : Vector3.up;
            up = Vector3.ProjectOnPlane(fallbackUp, forward);
        }

        if (up.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        up.Normalize();
        Vector3 right = Vector3.Cross(forward, up);
        if (right.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        right.Normalize();
        up = Vector3.Cross(right, forward).normalized;
        return up.sqrMagnitude > 0.000001f;
    }

    public static bool TryResolveFromControl(
        AnimalControlWorldData control,
        out Vector3 forward,
        out Vector3 up,
        out Vector3 facingHint)
    {
        forward = Vector3.zero;
        up = Vector3.zero;
        facingHint = Vector3.zero;

        if (control.hasRoot && control.hasForwardHint)
        {
            forward = (control.forwardHintWorld - control.rootWorld).normalized;
        }
        else if (control.hasRoot && control.hasWithers)
        {
            forward = (control.withersWorld - control.rootWorld).normalized;
        }

        if (control.hasHeadRoot && control.hasHeadTip)
        {
            facingHint = (control.headTipWorld - control.headRootWorld).normalized;
            if (forward.sqrMagnitude > 0.000001f && facingHint.sqrMagnitude > 0.000001f && Vector3.Dot(forward, facingHint) < 0f)
            {
                forward = -forward;
            }
        }

        if (control.hasRoot &&
            control.hasUpHint &&
            (!control.hasWithers || Vector3.Distance(control.upHintWorld, control.withersWorld) > 0.001f))
        {
            up = (control.upHintWorld - control.rootWorld).normalized;
        }

        if (forward.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        if (up.sqrMagnitude <= 0.000001f)
        {
            up = Vector3.up;
        }

        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude <= 0.000001f)
        {
            up = Vector3.ProjectOnPlane(Vector3.up, forward);
        }
        if (up.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        up.Normalize();
        Vector3 right = Vector3.Cross(forward, up);
        if (right.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        right.Normalize();
        up = Vector3.Cross(right, forward).normalized;
        return true;
    }
}
