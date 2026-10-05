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
    // onCanonicalLimbs: 四肢の点を、脚の割り当ての表で付け替えた役の骨ではなく正規名の骨（cache.gestureCanonicalLimbs）に乗せる
    // （2026-10-04。資産は正規名の骨の局所軸で作ってあるので、役が 1 本上の骨へ移ると同じ回転が別の向きに効く）。
    // headRemap: 頭の点（HeadTip）の回転を、頭の骨の局所軸ではなくモデルの解剖学的な軸で掛けるための写像（頭ローカル、AnimalPoseApplier.TryGetHeadGestureRemap）。
    // null なら従来どおり局所軸。
    internal static void ApplyToRigCache(AnimalGesturePose clip, float normalizedTime, AnimalRigCache cache, bool onCanonicalLimbs = false, Quaternion? headRemap = null)
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

            Transform bone = onCanonicalLimbs && cache.gestureCanonicalLimbs.TryGetValue(pointCurve.point, out Transform canonical)
                ? canonical
                : ResolveBone(pointCurve.point, cache);
            if (bone == null)
            {
                continue;
            }

            // Curve values are degrees of local rotation around the bone's own right/up/forward
            // axes, applied on top of whatever AnimalPoseApplier just wrote.
            float rightDeg = EvaluateOrZero(pointCurve.right, normalizedTime);
            float upDeg = EvaluateOrZero(pointCurve.up, normalizedTime);
            float forwardDeg = EvaluateOrZero(pointCurve.forward, normalizedTime);
            // FK が毎 tick 書き直さない骨は bind の局所の上に足す（今の回転に掛けると積み重なって回り続ける。cache.gestureBindLocal、2026-10-04）。
            Quaternion baseLocal = cache.gestureBindLocal.TryGetValue(bone, out Quaternion bindLocal) ? bindLocal : bone.localRotation;
            Quaternion offset = Quaternion.Euler(rightDeg, upDeg, forwardDeg);
            if (headRemap.HasValue && pointCurve.point == AnimalGesturePoint.HeadTip && bone == cache.head)
            {
                offset = headRemap.Value * offset * Quaternion.Inverse(headRemap.Value);
            }

            bone.localRotation = baseLocal * offset;
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
