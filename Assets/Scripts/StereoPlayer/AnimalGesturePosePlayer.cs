using UnityEngine;

// Generic playback for AnimalGesturePose: evaluates each point's curves at the gesture's
// current normalized time and applies the result as an additive local-rotation offset on the
// matching AnimalRigCache bone, after AnimalPoseApplier has finished placing it for this frame
// (either via SMAL FK or keypoint/control-target IK - this runs identically either way, which
// is what lets one gesture asset work on any track regardless of pose source). One engine for
// every gesture asset - adding a new static animation or walk cycle means authoring a new
// AnimalGesturePose, not a new code path.
internal static class AnimalGesturePosePlayer
{
    internal static void ApplyToRigCache(AnimalGesturePose clip, float normalizedTime, AnimalRigCache cache)
    {
        if (clip == null || clip.pointCurves == null || cache == null)
        {
            return;
        }

        for (int i = 0; i < clip.pointCurves.Count; i++)
        {
            AnimalGesturePointCurve pointCurve = clip.pointCurves[i];
            if (pointCurve == null)
            {
                continue;
            }

            Transform bone = ResolveBone(pointCurve.point, cache);
            if (bone == null)
            {
                continue;
            }

            // Curve values are degrees of local rotation around the bone's own right/up/forward
            // axes, applied on top of whatever AnimalPoseApplier just wrote.
            float rightDeg = EvaluateOrZero(pointCurve.right, normalizedTime);
            float upDeg = EvaluateOrZero(pointCurve.up, normalizedTime);
            float forwardDeg = EvaluateOrZero(pointCurve.forward, normalizedTime);
            bone.localRotation = bone.localRotation * Quaternion.Euler(rightDeg, upDeg, forwardDeg);
        }
    }

    private static float EvaluateOrZero(AnimationCurve curve, float t)
    {
        return curve != null ? curve.Evaluate(t) : 0f;
    }

    private static Transform ResolveBone(AnimalGesturePoint point, AnimalRigCache cache)
    {
        switch (point)
        {
            case AnimalGesturePoint.Root: return cache.root;
            case AnimalGesturePoint.HeadTip: return cache.head;
            case AnimalGesturePoint.TailTip: return cache.tailTip;
            case AnimalGesturePoint.FrontLeftPaw: return cache.leftFrontPaw;
            case AnimalGesturePoint.FrontRightPaw: return cache.rightFrontPaw;
            case AnimalGesturePoint.RearLeftPaw: return cache.leftRearPaw;
            case AnimalGesturePoint.RearRightPaw: return cache.rightRearPaw;
            case AnimalGesturePoint.FrontLeftUpper: return cache.leftFrontUpper;
            case AnimalGesturePoint.FrontRightUpper: return cache.rightFrontUpper;
            case AnimalGesturePoint.RearLeftUpper: return cache.leftRearUpper;
            case AnimalGesturePoint.RearRightUpper: return cache.rightRearUpper;
            case AnimalGesturePoint.FrontLeftLower: return cache.leftFrontLower;
            case AnimalGesturePoint.FrontRightLower: return cache.rightFrontLower;
            case AnimalGesturePoint.RearLeftLower: return cache.leftRearLower;
            case AnimalGesturePoint.RearRightLower: return cache.rightRearLower;
            default: return null;
        }
    }
}
