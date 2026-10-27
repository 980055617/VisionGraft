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
    // mirror（2026-10-18、StreamingStereoVideoPlayer.animalGestureTurnTowardBodyFront、既定 OFF）: 左右を入れ替えて足す。体の軸で読む資産（anatomicalAxes）だけ
    // （骨の局所軸の資産は、左右の脚の局所軸が鏡映のモデルが 52 体中 15 体あり、1 つの規則で入れ替えられない）。対の点（四肢・耳・肩甲骨）は反対側の点へ移し、
    // right はそのまま、up と forward は符号を反転する（体の正中面での鏡映 M = diag(−1, 1, 1) は M·Euler(x, y, z)·M = Euler(x, −y, −z)。体の右の軸まわりは変わらず、
    // 上・前の軸まわりは逆向き）。対の点の right には元の側の骨と移した先の骨の符号（gestureSwingSign）の積を掛ける（左右の bind が対称なら 1。world の動きがちょうど鏡映になる）。
    // 入れ替えてよいか（Root の点が無い・足す骨すべてに体の軸がある）は CanMirror で先に確かめる。false なら従来と同じ計算。
    internal static void ApplyToRigCache(AnimalGesturePose clip, float normalizedTime, AnimalRigCache cache, bool onCanonicalLimbs = false, Quaternion? headRemap = null, bool mirror = false)
    {
        if (clip == null || clip.pointCurves == null || cache == null)
        {
            return;
        }

        // 資産の onRoleBones なら四肢は役の骨（解剖学的な上腕・前腕・手）、anatomicalAxes なら曲線を体の軸で読む（AnimalGesturePose の説明、2026-10-05）。
        bool canonicalLimbs = onCanonicalLimbs && !clip.onRoleBones;
        bool anatomical = clip.anatomicalAxes;
        bool mirrored = mirror && anatomical;
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
            AnimalGesturePoint point = pointCurve.point;
            if (mirrored)
            {
                point = MirrorPoint(point);
                upDeg = -upDeg;
                forwardDeg = -forwardDeg;
            }

            if (point == AnimalGesturePoint.Trunk)
            {
                ApplyTrunk(cache, rightDeg, upDeg, forwardDeg, anatomical, clip.trunkProfile);
                continue;
            }

            Transform bone = canonicalLimbs && cache.gestureCanonicalLimbs.TryGetValue(point, out Transform canonical)
                ? canonical
                : ResolveBone(point, cache);
            if (bone == null)
            {
                continue;
            }

            if (mirrored && point != pointCurve.point)
            {
                Transform source = ResolveGestureBone(pointCurve.point, cache, canonicalLimbs);
                if (source != null)
                {
                    rightDeg *= SwingSign(cache, source) * SwingSign(cache, bone);
                }
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
    // 資産の trunkProfile にキーが 2 つ以上あれば、累積はその曲線の値（x = (k + 1) / n。最大でそろえない）にする（2026-10-17、身震い v3。
    // 肩甲帯を 1 にすると、曲線の値がそのまま骨盤に対する肩甲帯の回転になる）。
    private const float TrunkGirdleShare = 0.3f;

    private static void ApplyTrunk(AnimalRigCache cache, float rightDeg, float upDeg, float forwardDeg, bool anatomical, AnimationCurve profile)
    {
        int n = cache.trunkChain.Count;
        if (n == 0)
        {
            return;
        }

        bool customProfile = profile != null && profile.length >= 2;
        float peak = 0f;
        if (!customProfile)
        {
            for (int k = 0; k < n; k++)
            {
                peak = Mathf.Max(peak, TrunkCumulative((k + 1f) / n));
            }

            if (peak <= 1e-4f)
            {
                return;
            }
        }

        float previous = 0f;
        for (int k = 0; k < n; k++)
        {
            float x = (k + 1f) / n;
            float cumulative = customProfile ? profile.Evaluate(x) : TrunkCumulative(x) / peak;
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

    // 左右の対の点を反対側の点へ（mirror）。正中の点（Root・HeadTip・TailTip・Trunk・Neck・TailBase・TailMid・Jaw）はそのまま。
    internal static AnimalGesturePoint MirrorPoint(AnimalGesturePoint point)
    {
        switch (point)
        {
            case AnimalGesturePoint.FrontLeftPaw: return AnimalGesturePoint.FrontRightPaw;
            case AnimalGesturePoint.FrontRightPaw: return AnimalGesturePoint.FrontLeftPaw;
            case AnimalGesturePoint.RearLeftPaw: return AnimalGesturePoint.RearRightPaw;
            case AnimalGesturePoint.RearRightPaw: return AnimalGesturePoint.RearLeftPaw;
            case AnimalGesturePoint.FrontLeftUpper: return AnimalGesturePoint.FrontRightUpper;
            case AnimalGesturePoint.FrontRightUpper: return AnimalGesturePoint.FrontLeftUpper;
            case AnimalGesturePoint.RearLeftUpper: return AnimalGesturePoint.RearRightUpper;
            case AnimalGesturePoint.RearRightUpper: return AnimalGesturePoint.RearLeftUpper;
            case AnimalGesturePoint.FrontLeftLower: return AnimalGesturePoint.FrontRightLower;
            case AnimalGesturePoint.FrontRightLower: return AnimalGesturePoint.FrontLeftLower;
            case AnimalGesturePoint.RearLeftLower: return AnimalGesturePoint.RearRightLower;
            case AnimalGesturePoint.RearRightLower: return AnimalGesturePoint.RearLeftLower;
            case AnimalGesturePoint.LeftEar: return AnimalGesturePoint.RightEar;
            case AnimalGesturePoint.RightEar: return AnimalGesturePoint.LeftEar;
            case AnimalGesturePoint.FrontLeftScapula: return AnimalGesturePoint.FrontRightScapula;
            case AnimalGesturePoint.FrontRightScapula: return AnimalGesturePoint.FrontLeftScapula;
            default: return point;
        }
    }

    // 骨ごとの right の符号（AnimalPoseApplier.EnsureGestureAnatomy。無ければ +1、AnatomicalOffset と同じ）。
    private static float SwingSign(AnimalRigCache cache, Transform bone)
    {
        return cache.gestureSwingSign.TryGetValue(bone, out float s) ? s : 1f;
    }

    // ApplyToRigCache と同じ骨の選び方（四肢は canonicalLimbs なら正規名の骨）。
    private static Transform ResolveGestureBone(AnimalGesturePoint point, AnimalRigCache cache, bool canonicalLimbs)
    {
        return canonicalLimbs && cache.gestureCanonicalLimbs.TryGetValue(point, out Transform canonical) ? canonical : ResolveBone(point, cache);
    }

    // 左右を入れ替えて足してよいか（mirror の前提。StreamingStereoVideoPlayer.ResolveAnimalGestureMirror が先に呼ぶ）。読むだけ。
    // 数式（対の点の入れ替え・up と forward の反転・right の符号の積）は骨ごとにちょうど鏡映になる（Python の写しで確かめた）。ただし頭を視聴者へ向ける層が首を
    // 上下に曲げている間は、首と頭がそれぞれ自分の運ぶ面で鏡映されるので、首＋頭のつながりとしては 1〜2° ずれる（lynx_left で約 1.2°。見ても分からない大きさ）。
    // 実際に撮って確かめたのは「正中の点の up だけ」と「左右で同じ曲線の耳の up」（視線をそらす）だけなので、確かめていない形は入れない（2026-10-18 のレビュー）:
    // - 曲線を体の軸で読む資産（anatomicalAxes）だけ（骨の局所軸の資産は、左右の脚の局所軸が鏡映のモデルが 52 体中 15 体あり、1 つの規則で入れ替えられない）
    // - Root の点（体の軸を持たない）・胴の点（鎖への配り方がモデルの鎖の長さで変わる）・0 でない forward・対の点の 0 でない right は入れない（untested_*）
    // - 対の点は、左右の骨が両方あるか両方無いときだけ（片方だけのモデルでは、その骨が反対側の曲線を動かしてしまう。unpaired_*）
    // - 足す骨（入れ替える前と後の両方）すべてに体の軸（gestureAxesFrame）があること
    internal static bool CanMirror(AnimalGesturePose clip, AnimalRigCache cache, bool onCanonicalLimbs, out string reason)
    {
        reason = string.Empty;
        if (clip == null || clip.pointCurves == null || cache == null)
        {
            reason = "no_clip";
            return false;
        }

        if (!clip.anatomicalAxes)
        {
            reason = "local_axes";
            return false;
        }

        bool canonicalLimbs = onCanonicalLimbs && !clip.onRoleBones;
        foreach (AnimalGesturePointCurve c in clip.pointCurves)
        {
            if (c == null || !RowIsNonZero(c))
            {
                continue;
            }

            if (c.point == AnimalGesturePoint.Root)
            {
                reason = "root";
                return false;
            }

            if (c.point == AnimalGesturePoint.Trunk)
            {
                reason = "untested_Trunk";
                return false;
            }

            if (CurveIsNonZero(c.forward))
            {
                reason = "untested_forward_" + c.point;
                return false;
            }

            AnimalGesturePoint partner = MirrorPoint(c.point);
            bool paired = partner != c.point;
            if (paired && CurveIsNonZero(c.right))
            {
                reason = "untested_right_" + c.point;
                return false;
            }

            Transform own = ResolveGestureBone(c.point, cache, canonicalLimbs);
            Transform other = paired ? ResolveGestureBone(partner, cache, canonicalLimbs) : own;
            if (paired && (own == null) != (other == null))
            {
                reason = "unpaired_" + c.point;
                return false;
            }

            if ((own != null && !cache.gestureAxesFrame.ContainsKey(own)) || (other != null && !cache.gestureAxesFrame.ContainsKey(other)))
            {
                reason = "no_frame_" + c.point;
                return false;
            }
        }

        return true;
    }

    // 身ぶりがその点を 0 でない曲線で使うか（顎の骨が無いモデルで顎の点を持つ身ぶりを選ばない、StreamingStereoVideoPlayer.ResolvePlayableAnimalStaticClips）。
    internal static bool UsesPoint(AnimalGesturePose clip, AnimalGesturePoint point)
    {
        if (clip == null || clip.pointCurves == null)
        {
            return false;
        }

        foreach (AnimalGesturePointCurve c in clip.pointCurves)
        {
            if (c != null && c.point == point && RowIsNonZero(c))
            {
                return true;
            }
        }

        return false;
    }

    // 身ぶりの頭の向きの左右（animalGestureTurnTowardBodyFront）: 頭と首の up の曲線の和 y(t)（度、+ = 体の右へ向く）の時間平均 mean と、|y| の時間平均 meanAbs。
    // 0..1 を 256 等分した区間の中点で測る。胴（Trunk）と Root の up は数えない（今の資産には無い。胴は鎖に配る割合がモデルの鎖の長さで変わる）。
    internal static void MeasureHeadYaw(AnimalGesturePose clip, out float mean, out float meanAbs)
    {
        mean = 0f;
        meanAbs = 0f;
        if (clip == null || clip.pointCurves == null)
        {
            return;
        }

        const int samples = 256;
        for (int i = 0; i < samples; i++)
        {
            float t = (i + 0.5f) / samples;
            float y = 0f;
            foreach (AnimalGesturePointCurve c in clip.pointCurves)
            {
                if (c != null && (c.point == AnimalGesturePoint.HeadTip || c.point == AnimalGesturePoint.Neck))
                {
                    y += EvaluateOrZero(c.up, t);
                }
            }

            mean += y;
            meanAbs += Mathf.Abs(y);
        }

        mean /= samples;
        meanAbs /= samples;
    }

    private const float CurveZeroDegrees = 1e-3f;
    private const int CurveZeroSamples = 64;

    private static bool RowIsNonZero(AnimalGesturePointCurve c)
    {
        return CurveIsNonZero(c.right) || CurveIsNonZero(c.up) || CurveIsNonZero(c.forward);
    }

    // 曲線が 0 でないか（キーの値か、0..1 を 64 等分した点の値のどれかが 1e-3° を超える）。
    private static bool CurveIsNonZero(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0)
        {
            return false;
        }

        for (int k = 0; k < curve.length; k++)
        {
            if (Mathf.Abs(curve[k].value) > CurveZeroDegrees)
            {
                return true;
            }
        }

        for (int i = 0; i <= CurveZeroSamples; i++)
        {
            if (Mathf.Abs(curve.Evaluate((float)i / CurveZeroSamples)) > CurveZeroDegrees)
            {
                return true;
            }
        }

        return false;
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
            case AnimalGesturePoint.Jaw: return cache.gestureJaw;
            default: return null;
        }
    }
}
