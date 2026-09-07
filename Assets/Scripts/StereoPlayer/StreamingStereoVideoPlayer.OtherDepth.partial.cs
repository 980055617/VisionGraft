using System.Collections.Generic;
using UnityEngine;

// Else（剛体）オブジェクトの深度まわり。⑨ 人の骨格を基準にした深度追従と、
// ⑩ 人体への貫通の解消。
//
// 2026-09-06 に Playback.partial.cs から切り出した。配置の本筋（②⑦⑧⑨のスケール・深度）と
// 「Else を人に対してどう置くか」が同じ 2,482 行のファイルに同居していたため。
// 同じ partial クラスなので参照関係・シリアライズは変わらない。
public partial class StreamingStereoVideoPlayer
{

    // ⑩ Else が骨格モデルの内部に食い込んでいるとき、最小限だけ表面へ押し出す。
    //
    // 接触補正（Else を最寄りの部位へ引き寄せる）とは別物。**内部にあるときだけ、体から出る
    // 方向にのみ動かす**ので、空中にある Else は一切動かない。押し出す向きは meta.bin の
    // anchor_z が示す前後関係に従うため、背中に乗ったボールは奥側の表面へ出る。
    //
    // 発動条件は「画面上で重なっている」かつ「Else の中心が部位の内部にある」の両方。
    private void ApplyOtherPenetrationResolveForFrame()
    {
        if (!resolveOtherPenetration || metaFrameObjects == null)
        {
            return;
        }

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj other = metaFrameObjects[i];
            if (!IsCategoryOther(other.categoryId))
            {
                continue;
            }

            if (!trackInstances.TryGetValue(other.trackId, out GameObject otherInstance) ||
                otherInstance == null ||
                !otherInstance.activeInHierarchy)
            {
                continue;
            }

            if (!TryFindNearestSkeletonTrack(other, out MetaObj skeleton, out GameObject skeletonInstance))
            {
                continue;
            }

            if (!ResolveAnchorToScreen(other.anchorU, out Transform screen, out _, out _) ||
                !TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
                !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
            {
                continue;
            }

            Quaternion worldToCam = Quaternion.Inverse(camRotation);
            Vector3 otherCam = worldToCam * (otherInstance.transform.position - camOrigin);
            if (otherCam.z <= 0.0001f)
            {
                continue;
            }

            // Else の world 半径は投影サイズから逆算する。
            // bbox 径の平均 = (W+H)/2、その半分が半径なので (W+H)*0.25 [px]。
            // px → world は (2*px/eye_h) * (z/fy)。
            float otherRadiusPixels = (other.bboxW + other.bboxH) * 0.25f;
            float otherRadius = (2f * otherRadiusPixels / manifest.eye_h) * (otherCam.z / fy);

            if (!TryFindNearestBoneToPoint(
                    skeletonInstance, screen, camOrigin, camRotation, fx, fy,
                    other.anchorU, other.anchorV,
                    out float boneDepth, out float boneRadius, out float screenDistance))
            {
                continue;
            }

            // 画面上で重なっていないなら触らない（空中の Else はここで落ちる）。
            if (screenDistance > otherRadiusPixels + Mathf.Max(0f, penetrationOverlapMarginPixels))
            {
                continue;
            }

            float frontSurface = boneDepth - boneRadius - otherRadius;
            float backSurface = boneDepth + boneRadius + otherRadius;
            if (otherCam.z <= frontSurface || otherCam.z >= backSurface)
            {
                continue;   // 内部にいない
            }

            // 押し出す向きは既定では bundle の前後関係に従う。
            // ただし bundle が「奥」と言っていても、Else が画面上で体のシルエットの内側に
            // 深く入っているなら、奥へ出しても隠れたままで症状が直らない。
            // penetrationFrontBias を上げると、そういうフレームでは手前へ出す。
            bool wantsFront = other.anchorZ < skeleton.anchorZ;
            if (!wantsFront &&
                penetrationFrontBias > 0f &&
                screenDistance < otherRadiusPixels * penetrationFrontBias)
            {
                wantsFront = true;
            }
            float targetZ = wantsFront ? frontSurface : backSurface;

            float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
            targetZ = Mathf.Clamp(
                targetZ,
                Mathf.Max(0.001f, MinDistanceFromHeadMeters),
                screenDist - 0.0001f);

            if (Mathf.Abs(targetZ - otherCam.z) <= 0.0001f)
            {
                continue;
            }

            if (logPenetrationResolve)
            {
                Debug.Log(
                    $"[PENET] track={other.trackId} screenDist={screenDistance:F1}px " +
                    $"z={otherCam.z:F4} → {targetZ:F4} moved={(targetZ - otherCam.z) * 1000f:F1}mm " +
                    $"bone={boneDepth:F4} boneR={boneRadius * 1000f:F1}mm otherR={otherRadius * 1000f:F1}mm " +
                    $"dir={(wantsFront ? "front" : "back")}");
            }

            // 画面上の位置 (u, v) を保ったまま深度だけ変える。
            Vector3 moved = otherCam * (targetZ / otherCam.z);
            TrackPlacementWriter.Apply(
                otherInstance.transform,
                TrackPlacementCommand.PositionOnly(
                    camOrigin + camRotation * moved,
                    otherInstance.transform.rotation,
                    otherInstance.transform.localScale));
        }
    }

    // 画面上で指定 uv に最も近いボーンを探し、その深度・太さ・画面距離を返す。
    // 太さは「身長に対する比」の実測値（2026-08-19、BodyThicknessDump）から求める。
    private bool TryFindNearestBoneToPoint(
        GameObject instance,
        Transform screen,
        Vector3 camOrigin,
        Quaternion camRotation,
        float fx,
        float fy,
        float targetU,
        float targetV,
        out float boneDepth,
        out float boneRadius,
        out float screenDistance)
    {
        boneDepth = 0f;
        boneRadius = 0f;
        screenDistance = float.MaxValue;

        Animator animator = instance != null ? instance.GetComponentInChildren<Animator>(true) : null;
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return false;
        }

        if (!TryProjectBonesToEyeHeight(instance, screen, out float topV, out float bottomV, out _, out _, out _))
        {
            return false;
        }

        // 投影された骨格の高さを身長の代理として使い、太さの比率をメートルに直す。
        float projectedHeight = Mathf.Abs(bottomV - topV);
        if (projectedHeight <= 0.0001f)
        {
            return false;
        }

        bool found = false;
        Quaternion worldToCam = Quaternion.Inverse(camRotation);

        foreach (var pair in cache.bones)
        {
            Transform bone = pair.Value;
            if (bone == null)
            {
                continue;
            }

            Vector3 cam = worldToCam * (bone.position - camOrigin);
            if (cam.z <= 0.0001f)
            {
                continue;
            }

            // PinholePlacementSpace.ReconstructCamLocalFromEyePixel の逆変換。
            // x は fx、y は fy を使う（正方形ピクセルなので fx*eye_w = fy*eye_h）。
            float u = (0.5f + (cam.x / cam.z) * fx * 0.5f) * manifest.eye_w;
            float v = (0.5f - (cam.y / cam.z) * fy * 0.5f) * manifest.eye_h;

            float du = u - targetU;
            float dv = v - targetV;
            float dist = Mathf.Sqrt(du * du + dv * dv);
            if (dist >= screenDistance)
            {
                continue;
            }

            screenDistance = dist;
            boneDepth = cam.z;
            // 身長比の太さ → world 長。投影身長と深度から world 身長を復元する。
            float worldHeight = (2f * projectedHeight / manifest.eye_h) * (cam.z / fy);
            boneRadius = ResolveBoneThicknessRatio(pair.Key) * worldHeight;
            found = true;
        }

        return found;
    }

    // 部位ごとの「体表面までの距離 ÷ 身長」。2026-08-19 に boneWeights と骨軸への
    // 垂直距離で実測した値（docs/smpl-retargeting.md）。
    private static float ResolveBoneThicknessRatio(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Spine: return 0.0845f;
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest: return 0.0954f;
            case HumanBodyBones.Hips: return 0.0866f;
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.RightUpperLeg: return 0.0522f;
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.RightLowerLeg: return 0.0349f;
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.RightUpperArm: return 0.0316f;
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.RightLowerArm: return 0.0274f;
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.RightFoot:
            case HumanBodyBones.LeftToes:
            case HumanBodyBones.RightToes: return 0.0408f;
            case HumanBodyBones.Head:
            case HumanBodyBones.Neck: return 0.0554f;
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightHand: return 0.0199f;
            default: return 0.05f;
        }
    }

    // ⑨ ⑧ で骨格モデルを動かしたあと、Else を「bundle が意図する深度差」を保つ位置へ移す。
    // ⑧ は骨格を持つ track だけを動かすので、放置すると Else との差が bundle の意図から
    // 3〜5 倍に開く（2026-08-20 実測、足上げ区間で 71.4mm → 237.0mm）。
    // meta.bin の anchorZ はデコード済みの深度なので、その差が bundle の意図そのものになる。
    //
    // 基準にする骨格 track は「画面上でいちばん近いもの」。bundle_human のように
    // person 1 + other 1 の構成では自明で、Else が無い bundle では何もしない。
    private void ApplyOtherDepthFollowForFrame()
    {
        if (!followOtherDepthToRefinedSkeleton ||
            !refineDepthFromProjectedBones ||
            metaFrameObjects == null)
        {
            return;
        }

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj other = metaFrameObjects[i];
            if (!IsCategoryOther(other.categoryId))
            {
                continue;
            }

            if (!trackInstances.TryGetValue(other.trackId, out GameObject otherInstance) ||
                otherInstance == null ||
                !otherInstance.activeInHierarchy)
            {
                continue;
            }

            if (!TryFindNearestSkeletonTrack(other, out MetaObj skeleton, out GameObject skeletonInstance))
            {
                continue;
            }

            if (!ResolveAnchorToScreen(other.anchorU, out Transform screen, out _, out _) ||
                !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
            {
                continue;
            }

            // 人の深度は root ではなく体基準の点で測る。root はモデルによっては体の外にある
            // （FBX の bind pose に焼き込まれた原点オフセット。Core.cs の
            // otherDepthSkeletonReference を参照）。
            Vector3 skeletonRef = ResolveHumanDepthReferencePoint(skeletonInstance, camRotation);
            Quaternion worldToCam = Quaternion.Inverse(camRotation);
            Vector3 skeletonCam = worldToCam * (skeletonRef - camOrigin);
            Vector3 otherCam = worldToCam * (otherInstance.transform.position - camOrigin);
            if (skeletonCam.z <= 0.0001f || otherCam.z <= 0.0001f)
            {
                continue;
            }

            float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
            float targetZ;
            if (useMetricRatioForOtherDepth && TryResolveMetricDepthRatio(skeleton, other, out float ratio))
            {
                // disparity から実距離の比を復元して使う。配置深度は 1/z が実距離の 1/Z に
                // 対応するので、比をそのまま掛ければよい。
                targetZ = skeletonCam.z * ratio;
            }
            else
            {
                // フォールバック: meta.bin の深度差をそのまま再現する。
                targetZ = skeletonCam.z - (skeleton.anchorZ - other.anchorZ);
            }

            // 骨格 track と Else の深度「差」を時間平滑化する。
            // 個別の深度に掛けてはいけない: 両者は互いに打ち消し合って動いており、
            // 片方だけ平滑化すると相殺が壊れてばらつきが増える（2026-08-25 実測、
            // 人だけ固定で p10-p90 幅 79.5 → 126.5mm、球だけ固定で 101.0mm に悪化）。
            targetZ = skeletonCam.z - SmoothOtherDepthGap(other.trackId, skeletonCam.z - targetZ);

            targetZ = Mathf.Clamp(
                targetZ,
                Mathf.Max(0.001f, MinDistanceFromHeadMeters),
                screenDist - 0.0001f);

            // ⑨ の適用結果は [PLACE] には出ない（[PLACE] は各 track の ApplyMetaTarget 内で
            // 出力されるが、⑨ は全 track の処理が終わったあとに走るため）。
            // ⑨ 系を評価するときは必ずこのログを使うこと。
            if (logOtherDepthFollow &&
                (logOtherDepthFollowEveryNFrames <= 0 ||
                 (GetCurrentFrameIndex() % logOtherDepthFollowEveryNFrames) == 0))
            {
                Debug.Log(
                    $"[DEPTH9] ref={otherDepthSkeletonReference} f={GetCurrentFrameIndex()} track={other.trackId} " +
                    $"skelTrack={skeleton.trackId} skelZ={skeletonCam.z:F4} otherZ={otherCam.z:F4} " +
                    $"final={targetZ:F4} moved={(targetZ - otherCam.z) * 1000f:F1}mm " +
                    $"gapBefore={(skeletonCam.z - otherCam.z) * 1000f:F1}mm " +
                    $"gapAfter={(skeletonCam.z - targetZ) * 1000f:F1}mm " +
                    $"intended={(skeleton.anchorZ - other.anchorZ) * 1000f:F1}mm " +
                    $"bboxH={skeleton.bboxH:F0} otherBboxW={other.bboxW:F0} otherBboxH={other.bboxH:F0} " +
                    $"factor={targetZ / otherCam.z:F4} scaleIn={otherInstance.transform.localScale.x:F5} " +
                    $"matchScale={matchOtherScaleToFollowedDepth}");
            }

            if (Mathf.Abs(targetZ - otherCam.z) <= 0.0001f)
            {
                continue;
            }

            // 画面上の位置 (u, v) を保ったまま深度だけ変える。
            // 深度が変わったぶん見かけの大きさも変わるので、必要ならスケールを合わせる。
            // 配置パイプラインは「投影が bbox に一致する」前提で組まれているため、
            // 深度だけ動かすと球が bbox より小さく写る（Hips 参照で 0.772 倍、2026-08-26 実測）。
            //
            // 累積しないのは ApplyMetaTarget が毎 tick 位置とスケールの両方を貼り直すため
            // （1 メタフレーム内の otherZ / scaleIn の幅は実測 0.000）。毎 tick の
            // localScale は「anchor 深度で bbox を張る desiredScale」なので、
            // そこに depthFactor を掛けるのは代入と同じ意味になる。
            float depthFactor = targetZ / otherCam.z;
            Vector3 moved = otherCam * depthFactor;
            Vector3 scale = matchOtherScaleToFollowedDepth
                ? otherInstance.transform.localScale * depthFactor
                : otherInstance.transform.localScale;
            TrackPlacementWriter.Apply(
                otherInstance.transform,
                TrackPlacementCommand.PositionOnly(
                    camOrigin + camRotation * moved,
                    otherInstance.transform.rotation,
                    scale));
        }
    }

    // disparity から「Else の実距離 ÷ 骨格 track の実距離」を復元する。
    //
    //   disparity = a(t)/Z + b   （DepthCrafter は affine-invariant）
    //   Z_other / Z_skeleton = (disp_skeleton − b) / (disp_other − b)
    //
    // a(t) は比を取ると相殺されるので、b さえ分かれば実距離の比が求まる。
    // keypoints も実距離の逆算も要らない。
    private bool TryResolveMetricDepthRatio(MetaObj skeleton, MetaObj other, out float ratio)
    {
        ratio = 1f;
        if (!TryResolveDepthAffineB(out float b))
        {
            return false;
        }

        // anchorZ は配置深度（大きいほど奥）なので、disparity へ戻す。
        // NormalizeAnchorZ01 / Z01ToNearness と同じ向きの量を使う。
        float dSkeleton = Z01ToNearness(NormalizeAnchorZ01(Mathf.Clamp01(skeleton.anchorZ01)));
        float dOther = Z01ToNearness(NormalizeAnchorZ01(Mathf.Clamp01(other.anchorZ01)));

        float numerator = dSkeleton - b;
        float denominator = dOther - b;
        if (Mathf.Abs(denominator) < 0.0001f || numerator <= 0f || denominator <= 0f)
        {
            return false;   // b の外側に出たフレームは信用しない
        }

        ratio = numerator / denominator;

        if (logDepthAffineFit && metricRatioDiagCount < 5)
        {
            metricRatioDiagCount++;
            Debug.Log(
                $"[RATIO] b={b:F4} dSkel={dSkeleton:F4} dOther={dOther:F4} " +
                $"z01Skel={skeleton.anchorZ01:F4} z01Other={other.anchorZ01:F4} ratio={ratio:F4}");
        }

        // 極端な比は推定の破綻とみなす（実測では 0.5〜2.0 に収まる）。
        if (ratio < MinMetricDepthRatio || ratio > MaxMetricDepthRatio)
        {
            return false;
        }

        return true;
    }

    // `disparity = a/Z + b` の b。Inspector で指定されていればそれを使い、
    // 未指定なら shot 先頭で keypoints3d から実距離を逆算して最小二乗で解く。
    private bool TryResolveDepthAffineB(out float b)
    {
        b = depthAffineB;
        if (b > 0f)
        {
            return true;
        }

        if (depthAffineBResolved)
        {
            b = resolvedDepthAffineB;
            return resolvedDepthAffineB > 0f;
        }

        depthAffineBResolved = true;
        resolvedDepthAffineB = EstimateDepthAffineB();
        b = resolvedDepthAffineB;
        return resolvedDepthAffineB > 0f;
    }

    // meta.bin を間引いて読み、keypoints3d を持つ track の実距離と disparity から
    // `disparity = a/Z + b` を最小二乗で解く。b だけ使う。
    private float EstimateDepthAffineB()
    {
        if (manifest == null || manifest.eye_h <= 0 || manifest.fy_norm <= 0f)
        {
            return 0f;
        }

        float focalPixels = manifest.fy_norm * manifest.eye_h * 0.5f;
        List<MetaObj> buffer = new List<MetaObj>(16);
        List<float> invZ = new List<float>(128);
        List<float> disp = new List<float>(128);
        int total = (int)metaHeader.numFrames;
        int step = Mathf.Max(1, total / DepthAffineSampleCount);
        for (int frame = 0; frame < total; frame += step)
        {
            if (!TryReadFrameObjects(frame, buffer))
            {
                continue;
            }

            for (int i = 0; i < buffer.Count; i++)
            {
                MetaObj obj = buffer[i];
                if (!obj.hasSkeleton || obj.jointsCam == null || obj.bboxH <= 0f)
                {
                    continue;
                }

                float distance = EstimateDistanceFromJoints(obj, focalPixels);
                if (distance <= 0.1f)
                {
                    continue;
                }

                invZ.Add(1f / distance);
                disp.Add(Z01ToNearness(NormalizeAnchorZ01(Mathf.Clamp01(obj.anchorZ01))));
            }
        }

        // 較正のために読んだフレームの SMPL/SMAL は再生に使わないので捨てる。
        humanSmplPosesMetaBin.Clear();
        animalSmalPosesMetaBin.Clear();

        if (invZ.Count < 16)
        {
            return 0f;
        }

        float meanX = 0f;
        float meanY = 0f;
        for (int i = 0; i < invZ.Count; i++)
        {
            meanX += invZ[i];
            meanY += disp[i];
        }

        meanX /= invZ.Count;
        meanY /= invZ.Count;

        float sxy = 0f;
        float sxx = 0f;
        for (int i = 0; i < invZ.Count; i++)
        {
            float dx = invZ[i] - meanX;
            sxy += dx * (disp[i] - meanY);
            sxx += dx * dx;
        }

        if (sxx < 1e-9f)
        {
            return 0f;
        }

        float a = sxy / sxx;
        float b = meanY - a * meanX;
        if (logDepthAffineFit)
        {
            Debug.Log($"[AFFINE] samples={invZ.Count} a={a:F4} b={b:F4}");
        }

        return b > 0f && b < 1f ? b : 0f;
    }

    // keypoints を距離 Z で透視投影したときの投影高が bboxH に一致する Z を返す。
    // 厳密解は二分探索だが、Z >> 各点の前後差 なので一次近似で十分（実測で誤差 3% 程度、
    // かつ比を取る用途では系統誤差が相殺される）。
    private float EstimateDistanceFromJoints(MetaObj obj, float focalPixels)
    {
        Vector3[] joints = obj.jointsCam;
        if (joints == null || joints.Length == 0 || obj.bboxH <= 0f)
        {
            return 0f;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        for (int i = 0; i < joints.Length; i++)
        {
            float y = joints[i].y;
            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }
        }

        float span = maxY - minY;
        if (span <= 0.0001f)
        {
            return 0f;
        }

        return span * focalPixels / obj.bboxH;
    }

    // 骨格 track と Else の深度差を時間平滑化する。⑧ の比率平滑化と同じ形式で、
    // 係数は時定数から毎フレーム求めるためフレームレートに依存しない。
    // shot 境界では `otherDepthGapByTrack` をクリアして前 shot の値を引きずらせない。
    // ⑨ が「人がいる深度」として使う点を返す。camRotation はビュー方向を取るためだけに使う。
    private Vector3 ResolveHumanDepthReferencePoint(GameObject instance, Quaternion camRotation)
    {
        if (instance == null || otherDepthSkeletonReference == HumanDepthReferenceMode.Root)
        {
            return instance != null ? instance.transform.position : Vector3.zero;
        }

        if (otherDepthSkeletonReference == HumanDepthReferenceMode.Hips)
        {
            Animator animator = instance.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) { return hips.position; }
            }

            return instance.transform.position;
        }

        SkinnedMeshRenderer[] renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
        bool has = false;
        Bounds bounds = new Bounds();
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) { continue; }
            if (has) { bounds.Encapsulate(renderers[i].bounds); }
            else { bounds = renderers[i].bounds; has = true; }
        }

        if (!has) { return instance.transform.position; }
        if (otherDepthSkeletonReference == HumanDepthReferenceMode.MeshCenter) { return bounds.center; }

        // MeshFront: bounds のカメラ側の面。bundle の anchor_z が可視表面の depth を
        // サンプルした値なので、対応する点はここになる。
        Vector3 forward = camRotation * Vector3.forward;
        float half =
            Mathf.Abs(forward.x) * bounds.extents.x +
            Mathf.Abs(forward.y) * bounds.extents.y +
            Mathf.Abs(forward.z) * bounds.extents.z;
        return bounds.center - forward * half;
    }

    private float SmoothOtherDepthGap(uint trackId, float gap)
    {
        float tau = Mathf.Max(0f, otherDepthGapSmoothingSeconds);
        if (tau <= 0.0001f)
        {
            otherDepthGapByTrack[trackId] = gap;
            return gap;
        }

        if (!otherDepthGapByTrack.TryGetValue(trackId, out float previous))
        {
            otherDepthGapByTrack[trackId] = gap;
            return gap;
        }

        // 1 メタフレームにつき約 31 回走るが、Time.deltaTime の合計が 1 メタフレーム
        // ぶんになるので総進行量は tau どおり（2026-08-25 検証、tick ごとに +0.1mm ずつ
        // 進み 1 フレームで約 3.3mm = α 0.027 相当）。
        float deltaTime = Mathf.Max(0f, Time.deltaTime);
        float alpha = deltaTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / tau);
        float smoothed = previous + Mathf.Clamp01(alpha) * (gap - previous);
        otherDepthGapByTrack[trackId] = smoothed;
        return smoothed;
    }

    private bool TryFindNearestSkeletonTrack(MetaObj other, out MetaObj skeleton, out GameObject instance)
    {
        skeleton = default;
        instance = null;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj candidate = metaFrameObjects[i];
            if (!IsCategoryPerson(candidate.categoryId) && !IsCategoryAnimal(candidate.categoryId))
            {
                continue;
            }

            if (!trackInstances.TryGetValue(candidate.trackId, out GameObject candidateInstance) ||
                candidateInstance == null ||
                !candidateInstance.activeInHierarchy)
            {
                continue;
            }

            float du = candidate.anchorU - other.anchorU;
            float dv = candidate.anchorV - other.anchorV;
            float distSq = du * du + dv * dv;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                skeleton = candidate;
                instance = candidateInstance;
            }
        }

        return instance != null;
    }
}