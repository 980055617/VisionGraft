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

        // 資産の onRoleBones なら四肢は役の骨（解剖学的な上腕・前腕・手）、anatomicalAxes なら曲線を体の軸で読む（AnimalGesturePose の説明、2026-10-05）。
        bool canonicalLimbs = onCanonicalLimbs && !clip.onRoleBones;
        bool anatomical = clip.anatomicalAxes;
        for (int i = 0; i < clip.pointCurves.Count; i++)
        {
            AnimalGesturePointCurve pointCurve = clip.pointCurves[i];
            if (pointCurve == null)
            {
                continue;
            }

            // Curve values are degrees of local rotation around the bone's own right/up/forward
            // axes, applied on top of whatever AnimalPoseApplier just wrote.
            float rightDeg = EvaluateOrZero(pointCurve.right, normalizedTime);
            float upDeg = EvaluateOrZero(pointCurve.up, normalizedTime);
            float forwardDeg = EvaluateOrZero(pointCurve.forward, normalizedTime);
            if (pointCurve.point == AnimalGesturePoint.Trunk)
            {
                ApplyTrunk(cache, rightDeg, upDeg, forwardDeg, anatomical);
                continue;
            }

            Transform bone = canonicalLimbs && cache.gestureCanonicalLimbs.TryGetValue(pointCurve.point, out Transform canonical)
                ? canonical
                : ResolveBone(pointCurve.point, cache);
            if (bone == null)
            {
                continue;
            }

            // FK が毎 tick 書き直さない骨は bind の局所の上に足す（今の回転に掛けると積み重なって回り続ける。cache.gestureBindLocal、2026-10-04）。
            Quaternion baseLocal = cache.gestureBindLocal.TryGetValue(bone, out Quaternion bindLocal) ? bindLocal : bone.localRotation;
            Quaternion offset;
            if (anatomical)
            {
                offset = AnatomicalOffset(cache, bone, rightDeg, upDeg, forwardDeg);
            }
            else
            {
                offset = Quaternion.Euler(rightDeg, upDeg, forwardDeg);
                if (headRemap.HasValue && pointCurve.point == AnimalGesturePoint.HeadTip && bone == cache.head)
                {
                    offset = headRemap.Value * offset * Quaternion.Inverse(headRemap.Value);
                }
            }

            bone.localRotation = baseLocal * offset;
        }
    }

    // 体の軸で読んだ回転（骨ローカル）。軸は AnimalPoseApplier.EnsureGestureAnatomy が bind で求めた骨ローカルの体の右・上・前（頭は前 = 鼻）。
    // right の値には骨ごとの符号を掛ける（+ で四肢は先が前、ほかは先が上）。軸の無い骨は骨の局所軸のまま。
    private static Quaternion AnatomicalOffset(AnimalRigCache cache, Transform bone, float rightDeg, float upDeg, float forwardDeg)
    {
        if (!cache.gestureAxesFrame.TryGetValue(bone, out Quaternion frame))
        {
            return Quaternion.Euler(rightDeg, upDeg, forwardDeg);
        }

        float sign = cache.gestureSwingSign.TryGetValue(bone, out float s) ? s : 1f;
        return frame * Quaternion.Euler(sign * rightDeg, upDeg, forwardDeg) * Quaternion.Inverse(frame);
    }

    // 胴の点: 回転を胴の鎖（spine の子から肩甲帯まで、cache.trunkChain）に配る。鎖に沿った累積が sin(πx) + 0.3x（x = 鎖の中の位置 0..1）を
    // 最大 1 にそろえた形になるように各骨へ差分を掛ける（胴の中ほどが最大、肩甲帯は 3 割を残す）。肩甲帯を 0 に戻すと正面（FaceViewer で体は視聴者を向く）からは
    // 胸と前脚の陰で見えず頭のロールだけになるので、肩も少し動かす（反論役の指摘、2026-10-05）。鎖が無いモデル（00_Dog）では何もしない。
    private const float TrunkGirdleShare = 0.3f;

    private static void ApplyTrunk(AnimalRigCache cache, float rightDeg, float upDeg, float forwardDeg, bool anatomical)
    {
        int n = cache.trunkChain.Count;
        if (n == 0)
        {
            return;
        }

        float peak = 0f;
        for (int k = 0; k < n; k++)
        {
            peak = Mathf.Max(peak, TrunkCumulative((k + 1f) / n));
        }

        if (peak <= 1e-4f)
        {
            return;
        }

        float previous = 0f;
        for (int k = 0; k < n; k++)
        {
            float cumulative = TrunkCumulative((k + 1f) / n) / peak;
            float w = cumulative - previous;
            previous = cumulative;
            Transform bone = cache.trunkChain[k];
            if (bone == null)
            {
                continue;
            }

            Quaternion baseLocal = cache.gestureBindLocal.TryGetValue(bone, out Quaternion bindLocal) ? bindLocal : bone.localRotation;
            Quaternion offset = anatomical
                ? AnatomicalOffset(cache, bone, rightDeg * w, upDeg * w, forwardDeg * w)
                : Quaternion.Euler(rightDeg * w, upDeg * w, forwardDeg * w);
            bone.localRotation = baseLocal * offset;
        }
    }

    private static float TrunkCumulative(float x)
    {
        return Mathf.Sin(Mathf.PI * x) + TrunkGirdleShare * x;
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
            case AnimalGesturePoint.Neck: return cache.neck;
            case AnimalGesturePoint.TailBase: return cache.tailBase;
            case AnimalGesturePoint.TailMid: return cache.tailMid;
            case AnimalGesturePoint.LeftEar: return cache.gestureEarLeft;
            case AnimalGesturePoint.RightEar: return cache.gestureEarRight;
            case AnimalGesturePoint.FrontLeftScapula: return cache.gestureScapulaLeft;
            case AnimalGesturePoint.FrontRightScapula: return cache.gestureScapulaRight;
            default: return null;
        }
    }
}
