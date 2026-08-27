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

        // 2026-08-28: D-007 の対応表で番号の意味が確定した。**挙動は正しかったが名前が
        // 誤っていた**ので名前だけ直す。kp18 は「き甲」ではなく **頭**、kp24 は **鼻先端**、
        // kp10 / kp11 は股関節ではなく **左膝 / 右膝**。kp12 / kp13 の左右も表と一致する。
        // なお同時期に判明したとおり AnimalPoseJointChains のほうは左右が逆だった。
        // こちらは元から正しい（両者が食い違っていたこと自体が、チェーン側が推測だった証拠）。
        bool hasPelvis = TrackedJointPoints.TryGet(jointsWorld, vis, 7, out Vector3 pelvisHub);
        bool hasHead = TrackedJointPoints.TryGet(jointsWorld, vis, 18, out Vector3 headHub);
        bool hasNoseTip = TrackedJointPoints.TryGet(jointsWorld, vis, 24, out Vector3 noseTip);

        if (hasPelvis && hasHead)
        {
            // 骨盤（尾の付け根）→ 頭。これが体軸。
            forward = (headHub - pelvisHub).normalized;
        }

        if (forward.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        if (hasHead && hasNoseTip)
        {
            // 頭 → 鼻先端。体軸の向き（前後）を確定させるのに使う。
            facingHint = (noseTip - headHub).normalized;
            if (facingHint.sqrMagnitude > 0.000001f && Vector3.Dot(forward, facingHint) < 0f)
            {
                forward = -forward;
            }
        }

        bool hasLeftShoulder = TrackedJointPoints.TryGet(jointsWorld, vis, 12, out Vector3 leftShoulder);
        bool hasRightShoulder = TrackedJointPoints.TryGet(jointsWorld, vis, 13, out Vector3 rightShoulder);
        bool hasLeftKnee = TrackedJointPoints.TryGet(jointsWorld, vis, 10, out Vector3 leftKnee);
        bool hasRightKnee = TrackedJointPoints.TryGet(jointsWorld, vis, 11, out Vector3 rightKnee);

        Vector3 rightAxis = Vector3.zero;
        if (hasLeftShoulder && hasRightShoulder)
        {
            rightAxis += rightShoulder - leftShoulder;
        }
        if (hasLeftKnee && hasRightKnee)
        {
            rightAxis += rightKnee - leftKnee;
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
