using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private Camera[] GetActiveCameras()
    {
#if UNITY_2023_1_OR_NEWER
        return FindObjectsByType<Camera>(FindObjectsSortMode.None);
#else
        return FindObjectsOfType<Camera>();
#endif
    }


    private Camera GetViewCamera()
    {
        if (ViewCameraSelection.IsUsable(cachedViewCamera))
        {
            return cachedViewCamera;
        }

        cachedViewCamera = ViewCameraSelection.Select(GetActiveCameras());
        return cachedViewCamera;
    }


    private Transform GetHeadTransform()
    {
        if (headTransform != null)
        {
            return headTransform;
        }

        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        return transform;
    }


    private Transform GetViewOrHeadTransform()
    {
        Camera viewCam = GetViewCamera();
        return viewCam != null ? viewCam.transform : GetHeadTransform();
    }


    // ---- 計測: 配置したモデルを再投影して meta.bin の bbox と比較する ----
    // sizeRatio   = 投影高さ ÷ bbox 高さ。1.0 なら映像どおりの大きさで置けている
    // topDelta    = 投影上端 - bbox 上端。正なら映像より下にずれている
    // bottomDelta = 投影下端 - bbox 下端。正なら映像より下にずれている
    // renderer.bounds を使うので updateWhenOffscreen=true により実際の姿勢が反映される。
    private void LogPlacementMeasurementIfEnabled(
        MetaObj obj,
        GameObject instance,
        Transform screen,
        int frame)
    {
        LogHumanPoseErrorIfEnabled(obj, instance, screen, frame);
        LogBoneBBoxRelativeIfEnabled(obj, instance, screen, frame);

        if (!logPlacementMeasurement ||
            instance == null ||
            obj.bboxH <= 0 ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        LogBoneLengthsOnce(instance);
        LogJointAnglesIfEnabled(obj, instance, frame);

        if (!TryProjectRendererBoundsToEyeHeight(
                instance,
                screen,
                out float topV,
                out float bottomV,
                out float heightPixels,
                out float depthMeters))
        {
            return;
        }

        float bboxTop = obj.bboxY;
        float bboxBottom = obj.bboxY + obj.bboxH;
        string category = IsCategoryPerson(obj.categoryId)
            ? "Person"
            : (IsCategoryAnimal(obj.categoryId) ? "Animal" : "Other");
        Vector3 localScale = instance.transform.localScale;

        // AABB は world 軸平行なので姿勢が傾くと過大に出る。ボーン位置ベースでも測って比較する。
        string boneInfo = string.Empty;
        if (TryProjectBonesToEyeHeight(
                instance,
                screen,
                out float boneTopV,
                out float boneBottomV,
                out float boneHeightPixels,
                out string topBoneName,
                out string bottomBoneName))
        {
            boneInfo =
                $" boneRatio={boneHeightPixels / obj.bboxH:F3} " +
                $"boneTopDelta={boneTopV - bboxTop:F1} " +
                $"boneBottomDelta={boneBottomV - bboxBottom:F1} " +
                $"topBone={topBoneName} bottomBone={bottomBoneName}";
        }

        // スケールの内訳。骨格が bbox に対して小さくなる原因を切り分けるため、
        // modelHeight（Awake 時の Renderer bounds 由来）と実際の骨格高さを並べて出す。
        // modelHeight が骨格より大きいほど desiredScale が小さくなり、モデルが縮む。
        string scaleInfo = string.Empty;
        ReplaceableModel rm = instance.GetComponent<ReplaceableModel>();
        if (rm != null)
        {
            float modelH = rm.GetModelHeightMeters();
            float target = ComputeTargetHeightMeters(obj.bboxH, obj.anchorZ);
            float skeletonLocal = 0f;
            Animator anim = instance.GetComponentInChildren<Animator>(true);
            if (anim != null && anim.isHuman)
            {
                HumanoidRigCache c = GetOrBuildHumanoidCache(anim);
                if (c != null && c.ready &&
                    c.bones.TryGetValue(HumanBodyBones.Head, out Transform hd) && hd != null &&
                    c.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform ft) && ft != null)
                {
                    float lossy = Mathf.Abs(instance.transform.lossyScale.y);
                    skeletonLocal = lossy > 0.0001f
                        ? Vector3.Distance(hd.position, ft.position) / lossy
                        : 0f;
                }
            }

            scaleInfo =
                $" modelH={modelH:F4} aabbH={rm.baseHeightMeters:F4} " +
                $"skelH={rm.baseSkeletonHeightMeters:F4} " +
                $"userScale={rm.userScale:F4} target={target:F4} " +
                $"skeletonLocal={skeletonLocal:F4} " +
                $"modelH/skeleton={(skeletonLocal > 0.0001f ? modelH / skeletonLocal : 0f):F3}";
        }

        Debug.Log(
            $"[PLACE] f={frame} track={obj.trackId} {category} " +
            $"sizeRatio={heightPixels / obj.bboxH:F3} " +
            $"topDelta={topV - bboxTop:F1} bottomDelta={bottomV - bboxBottom:F1} " +
            $"proj[top={topV:F1} bot={bottomV:F1} h={heightPixels:F1}] " +
            $"bbox[top={bboxTop:F0} bot={bboxBottom:F0} h={obj.bboxH}] " +
            $"anchorV={obj.anchorV} depth={depthMeters:F3} scale={localScale.x:F4}" +
            scaleInfo +
            boneInfo);
    }


    // ---- 計測: 投影ベースの下端合わせ（⑦）が world ベース（④）から動かした量 ----
    // 人は ④ ApplyBottomAlignment(anchorZ 基準) の後に ⑦ AlignProjectedModelBottomToBBox
    // (モデル AABB 中心の depthMeters 基準) を通るが、Else は ⑦ を通らない。
    // 同じ「bbox 下端に合わせる」処理が対象によって別の式になっているため、
    // その差が実害としてどれだけ出ているかを測る。
    private void LogBottomAlignmentDeltaIfEnabled(
        MetaObj obj,
        GameObject instance,
        Transform screen,
        int frame,
        Vector3 preFitPosition)
    {
        if (!logPlacementMeasurement ||
            instance == null ||
            obj.bboxH <= 0 ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        Vector3 delta = instance.transform.position - preFitPosition;
        string category = IsCategoryPerson(obj.categoryId)
            ? "Person"
            : (IsCategoryAnimal(obj.categoryId) ? "Animal" : "Other");

        // world の移動量を「画面上で何 px 動いたか」に直す。
        float deltaPixels = 0f;
        if (TryGetProjectionIntrinsics(out _, out float fy, out _, out _) &&
            TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation) &&
            manifest != null && manifest.eye_h > 0)
        {
            Vector3 camDelta = Quaternion.Inverse(camRotation) * delta;
            Vector3 camPos = Quaternion.Inverse(camRotation) * (instance.transform.position - camOrigin);
            float depth = Mathf.Max(0.001f, camPos.z);
            deltaPixels = -(camDelta.y * fy / depth) * 0.5f * manifest.eye_h;
        }

        Debug.Log(
            $"[BOTTOMFIX] f={frame} track={obj.trackId} {category} " +
            $"moved={delta.magnitude:F5}m deltaPixels={deltaPixels:+0.0;-0.0} " +
            $"bboxH={obj.bboxH} ratio={(obj.bboxH > 0 ? 100f * Mathf.Abs(deltaPixels) / obj.bboxH : 0f):F1}%");
    }


    // ---- 計測: 姿勢再現の誤差（meta.bin の keypoints3d vs 表示モデルのボーン）----
    // source（meta.bin の keypoints3d を bbox の見た目サイズに合わせて投影したもの）と
    // displayed（実際に表示しているモデルのボーンを投影したもの）を同じ eye pixel 空間で比べる。
    // 対応は HumanOtherSourceSegments と同じ hmr2_openpose25_extra19 のインデックス。
    private struct HumanPoseErrorProbe
    {
        public readonly int sourceIndex;
        public readonly HumanBodyBones bone;
        public readonly string label;

        public HumanPoseErrorProbe(int sourceIndex, HumanBodyBones bone, string label)
        {
            this.sourceIndex = sourceIndex;
            this.bone = bone;
            this.label = label;
        }
    }

    private static readonly HumanPoseErrorProbe[] HumanPoseErrorProbes =
    {
        new HumanPoseErrorProbe(8, HumanBodyBones.Hips, "Hips"),
        new HumanPoseErrorProbe(1, HumanBodyBones.Neck, "Neck"),
        new HumanPoseErrorProbe(9, HumanBodyBones.RightUpperLeg, "RUpLeg"),
        new HumanPoseErrorProbe(10, HumanBodyBones.RightLowerLeg, "RLowLeg"),
        new HumanPoseErrorProbe(11, HumanBodyBones.RightFoot, "RFoot"),
        new HumanPoseErrorProbe(12, HumanBodyBones.LeftUpperLeg, "LUpLeg"),
        new HumanPoseErrorProbe(13, HumanBodyBones.LeftLowerLeg, "LLowLeg"),
        new HumanPoseErrorProbe(14, HumanBodyBones.LeftFoot, "LFoot"),
        new HumanPoseErrorProbe(2, HumanBodyBones.RightUpperArm, "RUpArm"),
        new HumanPoseErrorProbe(3, HumanBodyBones.RightLowerArm, "RLowArm"),
        new HumanPoseErrorProbe(4, HumanBodyBones.RightHand, "RHand"),
        new HumanPoseErrorProbe(5, HumanBodyBones.LeftUpperArm, "LUpArm"),
        new HumanPoseErrorProbe(6, HumanBodyBones.LeftLowerArm, "LLowArm"),
        new HumanPoseErrorProbe(7, HumanBodyBones.LeftHand, "LHand")
    };

    private void LogHumanPoseErrorIfEnabled(
        MetaObj obj,
        GameObject instance,
        Transform screen,
        int frame)
    {
        if (!logHumanPoseError ||
            instance == null ||
            obj.bboxH <= 0 ||
            !IsCategoryPerson(obj.categoryId) ||
            frame % Mathf.Max(1, logHumanPoseErrorEveryNFrames) != 0)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryBuildHumanSourceContactPose(obj, fx, fy, out HumanSourcePose2D sourcePose) ||
            sourcePose == null ||
            sourcePose.keypoints == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        System.Text.StringBuilder detail = new System.Text.StringBuilder();
        float sum = 0f;
        int count = 0;
        float worst = -1f;
        string worstLabel = string.Empty;

        for (int i = 0; i < HumanPoseErrorProbes.Length; i++)
        {
            HumanPoseErrorProbe probe = HumanPoseErrorProbes[i];
            if (probe.sourceIndex < 0 || probe.sourceIndex >= sourcePose.keypoints.Length)
            {
                continue;
            }

            Transform boneTransform = animator.GetBoneTransform(probe.bone);
            if (boneTransform == null)
            {
                continue;
            }

            Vector3 boneCam = worldToCam * (boneTransform.position - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    boneCam,
                    fx,
                    fy,
                    out Vector2 displayedPixel))
            {
                continue;
            }

            // delta = 表示モデル - 元映像。uv_origin=top_left なので
            // dx>0 = モデルが右、dy>0 = モデルが下にずれている。
            Vector2 delta = displayedPixel - sourcePose.keypoints[probe.sourceIndex];
            float error = delta.magnitude;
            sum += error;
            count++;
            if (error > worst)
            {
                worst = error;
                worstLabel = probe.label;
            }

            detail.Append(' ').Append(probe.label).Append('=')
                  .Append(delta.x.ToString("F1")).Append(',')
                  .Append(delta.y.ToString("F1"));
        }

        if (count == 0)
        {
            return;
        }

        float mean = sum / count;
        Debug.Log(
            $"[POSE] f={frame} track={obj.trackId} bboxH={obj.bboxH} n={count} " +
            $"mean={mean:F1}px({100f * mean / obj.bboxH:F1}%) " +
            $"max={worst:F1}px({100f * worst / obj.bboxH:F1}%) worst={worstLabel}" +
            detail);
    }


    // ---- 計測: 主要ボーンが bbox のどの高さにあるか ----
    // 「頭が低い」原因が全体スケールか、胴の短さか、頭の小ささかを切り分ける。
    // bbox（検出器の出力＝映像の真値）を基準にするので、meta.bin の keypoints の
    // 体型比が怪しい件（脚が全身の 36.5% と出た）の影響を受けない。
    //
    // 人体計測の期待値（頭頂を 0、足裏を 1 とした相対位置）:
    //   頭の中心 0.065 / 首 0.13 / 骨盤 0.53 / 膝 0.75 / 足首 0.955
    private struct BBoxRelativeProbe
    {
        public readonly HumanBodyBones bone;
        public readonly string label;
        public readonly float expected;

        public BBoxRelativeProbe(HumanBodyBones bone, string label, float expected)
        {
            this.bone = bone;
            this.label = label;
            this.expected = expected;
        }
    }

    private static readonly BBoxRelativeProbe[] BBoxRelativeProbes =
    {
        new BBoxRelativeProbe(HumanBodyBones.Head, "Head", 0.065f),
        new BBoxRelativeProbe(HumanBodyBones.Neck, "Neck", 0.13f),
        new BBoxRelativeProbe(HumanBodyBones.Hips, "Hips", 0.53f),
        new BBoxRelativeProbe(HumanBodyBones.LeftLowerLeg, "LKnee", 0.75f),
        new BBoxRelativeProbe(HumanBodyBones.LeftFoot, "LAnkle", 0.955f)
    };

    private void LogBoneBBoxRelativeIfEnabled(
        MetaObj obj,
        GameObject instance,
        Transform screen,
        int frame)
    {
        if (!logBoneBBoxRelative ||
            instance == null ||
            obj.bboxH <= 0 ||
            !IsCategoryPerson(obj.categoryId) ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < BBoxRelativeProbes.Length; i++)
        {
            BBoxRelativeProbe probe = BBoxRelativeProbes[i];
            Transform bone = animator.GetBoneTransform(probe.bone);
            if (bone == null)
            {
                continue;
            }

            Vector3 boneCam = worldToCam * (bone.position - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest, boneCam, fx, fy, out Vector2 pixel))
            {
                continue;
            }

            float rel = (pixel.y - obj.bboxY) / obj.bboxH;
            sb.Append(' ').Append(probe.label).Append('=')
              .Append(rel.ToString("F3")).Append('/')
              .Append(probe.expected.ToString("F3"))
              .Append('(').Append((rel - probe.expected).ToString("+0.000;-0.000")).Append(')');
        }

        Debug.Log($"[BONEREL] f={frame} bboxH={obj.bboxH} 実測/期待(差){sb}");
    }


    // ---- 計測: ボールと頭の高さ関係 ----
    // 「深度を合わせてもボールが頭の上に浮く」という症状を数値で確認する。
    // 画面上（投影 v）と 3D 空間（screen.up 方向）の両方で測り、
    // 投影は合っているのに 3D でずれているのか、投影から既にずれているのかを分ける。
    private void LogBallHeadIfEnabled(int frame)
    {
        if (!logBallHead ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0 ||
            metaFrameObjects == null)
        {
            return;
        }

        if (!TryResolveGapMeasurementTargets(
                out GameObject humanInstance,
                out GameObject otherInstance,
                out uint humanTrackId,
                out uint otherTrackId))
        {
            return;
        }

        Animator animator = humanInstance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        Renderer ballRenderer = otherInstance.GetComponentInChildren<Renderer>();
        if (head == null || ballRenderer == null)
        {
            return;
        }

        Vector3 ballCenter = ballRenderer.bounds.center;
        float ballRadius = ballRenderer.bounds.extents.magnitude / Mathf.Sqrt(3f);

        // 人物側のメッシュ上端（頭頂）。Head ボーンは頭の中心付近なので別途取る。
        float headTopY = head.position.y;
        Renderer[] humanRenderers = humanInstance.GetComponentsInChildren<Renderer>(true);
        if (humanRenderers.Length > 0)
        {
            Bounds hb = humanRenderers[0].bounds;
            for (int i = 1; i < humanRenderers.Length; i++)
            {
                hb.Encapsulate(humanRenderers[i].bounds);
            }

            headTopY = hb.max.y;
        }

        Transform view = GetViewOrHeadTransform();
        Vector3 up = view != null ? view.up : Vector3.up;
        float ballUp = Vector3.Dot(ballCenter, up);
        float headBoneUp = Vector3.Dot(head.position, up);
        float meshTopUp = headTopY;

        // 画面上でも比べる
        string projInfo = string.Empty;
        if (TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) &&
            TryGetPinholeBasis(null, out Vector3 camOrigin, out Quaternion camRotation) &&
            manifest != null && manifest.eye_h > 0)
        {
            Quaternion toCam = Quaternion.Inverse(camRotation);
            Vector3 ballCam = toCam * (ballCenter - camOrigin);
            Vector3 headCam = toCam * (head.position - camOrigin);
            if (PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, ballCam, fx, fy, out Vector2 bp) &&
                PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, headCam, fx, fy, out Vector2 hp))
            {
                projInfo = $" projBallV={bp.y:F1} projHeadV={hp.y:F1} projDeltaV={bp.y - hp.y:+0.0;-0.0}";
            }
        }

        Debug.Log(
            $"[BALLHEAD] f={frame} ballUp={ballUp:F4} headBone={headBoneUp:F4} " +
            $"meshTop={meshTopUp:F4} ballAboveBone={ballUp - headBoneUp:+0.0000;-0.0000} " +
            $"ballAboveMeshTop={ballUp - meshTopUp:+0.0000;-0.0000} radius={ballRadius:F4}" +
            projInfo);
    }


    // ---- 計測: Human と Other の位置関係を成分に分解する ----
    // 「ボールが足に埋もれる」原因の切り分け専用。配置は一切変えない。
    //   depthGap   = 視線方向の差。正なら Other が Human の最近接部位より奥
    //   lateralGap = 画面平行方向の差（視線に垂直な成分の大きさ）
    //   dist       = 3D 距離。radius はボール側の半径
    // depthGap が支配的なら深度レンジの問題、lateralGap が支配的なら anchor か姿勢の問題。
    private void LogHumanOtherGapIfEnabled(int frame)
    {
        if (!logHumanOtherGap ||
            frame % Mathf.Max(1, logHumanOtherGapEveryNFrames) != 0 ||
            metaFrameObjects == null)
        {
            return;
        }

        if (!TryResolveGapMeasurementTargets(
                out GameObject humanInstance,
                out GameObject otherInstance,
                out uint humanTrackId,
                out uint otherTrackId))
        {
            return;
        }

        Renderer otherRenderer = otherInstance.GetComponentInChildren<Renderer>();
        if (otherRenderer == null)
        {
            return;
        }

        Vector3 otherCenter = otherRenderer.bounds.center;
        Vector3 otherExtents = otherRenderer.bounds.extents;
        float otherRadius = Mathf.Max(otherExtents.x, Mathf.Max(otherExtents.y, otherExtents.z));

        if (!TryResolveNearestHumanBone(humanInstance, otherCenter, out Vector3 nearest, out string boneName))
        {
            return;
        }

        Transform view = GetViewOrHeadTransform();
        Vector3 forward = view != null ? view.forward : Vector3.forward;
        Vector3 delta = otherCenter - nearest;
        float depthGap = Vector3.Dot(delta, forward);
        float lateralGap = (delta - forward * depthGap).magnitude;

        // lateralGap は画面平面内の距離なので、縦・横のどちらがずれているか分からない。
        // モデルが bbox より大きいと下端合わせのぶん上側へずれるため、縦成分が大きくなる
        // はず（2026-08-19 の切り分け）。up / right に分解して確かめる。
        Vector3 up = view != null ? view.up : Vector3.up;
        Vector3 right = view != null ? view.right : Vector3.right;
        float upGap = Vector3.Dot(delta, up);
        float rightGap = Vector3.Dot(delta, right);

        // ルートとボーン群の位置関係。⑨ は「人ルートの深度」から bundle の深度差を引いて
        // 球を置くが、bundle の anchor_z は depth map を可視表面でサンプルした値なので、
        // ルートが体のどこにあるかで基準点がずれる。2026-08-25 に「最近傍ボーンがルートより
        // 93.8mm 奥」と出たので、その内訳を確定させるための診断。
        {
            float rootDepth = Vector3.Dot(humanInstance.transform.position - (view != null ? view.position : Vector3.zero), forward);
            float minD = float.MaxValue, maxD = float.MinValue, sumD = 0f;
            int nb = 0;
            string minName = "", maxName = "";
            SkinnedMeshRenderer[] rs = humanInstance.GetComponentsInChildren<SkinnedMeshRenderer>();
            Bounds? mb = null;
            for (int r = 0; r < rs.Length; r++)
            {
                if (mb.HasValue) { Bounds tmp = mb.Value; tmp.Encapsulate(rs[r].bounds); mb = tmp; }
                else { mb = rs[r].bounds; }
                Transform[] bs = rs[r].bones;
                if (bs == null) { continue; }
                for (int b = 0; b < bs.Length; b++)
                {
                    if (bs[b] == null) { continue; }
                    float dd = Vector3.Dot(bs[b].position - (view != null ? view.position : Vector3.zero), forward);
                    if (dd < minD) { minD = dd; minName = bs[b].name; }
                    if (dd > maxD) { maxD = dd; maxName = bs[b].name; }
                    sumD += dd; nb++;
                }
            }
            if (nb > 0)
            {
                float meshC = mb.HasValue ? Vector3.Dot(mb.Value.center - (view != null ? view.position : Vector3.zero), forward) : 0f;
                float meshE = mb.HasValue ? Vector3.Dot(mb.Value.extents, new Vector3(Mathf.Abs(forward.x), Mathf.Abs(forward.y), Mathf.Abs(forward.z))) : 0f;
                Debug.Log(
                    $"[ROOTDIAG] f={frame} rootZ={rootDepth:F4} boneMin={minD:F4} boneMax={maxD:F4} " +
                    $"boneMean={(sumD / nb):F4} nBones={nb} " +
                    $"meanMinusRoot={((sumD / nb) - rootDepth) * 1000f:+0.0;-0.0}mm " +
                    $"minMinusRoot={(minD - rootDepth) * 1000f:+0.0;-0.0}mm " +
                    $"meshCenterZ={meshC:F4} meshHalfDepth={meshE * 1000f:F1}mm " +
                    $"ballZ={Vector3.Dot(otherCenter - (view != null ? view.position : Vector3.zero), forward):F4} " +
                    $"frontBone={minName} backBone={maxName} scale={humanInstance.transform.lossyScale.y:F4} " +
                    // ローカル空間での位置。プレハブ由来の固定オフセットならフレーム間で
                    // ほぼ一定、FK / transl 由来なら姿勢に応じて動く。ここで切り分ける。
                    $"localMeshC={(mb.HasValue ? humanInstance.transform.InverseTransformPoint(mb.Value.center) : Vector3.zero)} " +
                    $"localHips={ResolveHipsLocalForDiag(humanInstance)}");
            }
        }

        Debug.Log(
            $"[GAP] f={frame} human={humanTrackId} other={otherTrackId} " +
            $"dist={delta.magnitude:F4} depthGap={depthGap:+0.0000;-0.0000} " +
            $"lateralGap={lateralGap:F4} upGap={upGap:+0.0000;-0.0000} " +
            $"rightGap={rightGap:+0.0000;-0.0000} radius={otherRadius:F4} " +
            $"overlap={(delta.magnitude < otherRadius ? 1 : 0)} nearest={boneName}");
    }


    // 表示中のフレームから Human と Other を 1 体ずつ拾う。どちらかが欠けていれば計測しない。
    private bool TryResolveGapMeasurementTargets(
        out GameObject humanInstance,
        out GameObject otherInstance,
        out uint humanTrackId,
        out uint otherTrackId)
    {
        humanInstance = null;
        otherInstance = null;
        humanTrackId = 0;
        otherTrackId = 0;

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj o = metaFrameObjects[i];
            if (!trackInstances.TryGetValue(o.trackId, out GameObject inst) ||
                inst == null ||
                !inst.activeInHierarchy)
            {
                continue;
            }

            if (humanInstance == null && IsCategoryPerson(o.categoryId))
            {
                humanInstance = inst;
                humanTrackId = o.trackId;
            }
            else if (otherInstance == null &&
                     !IsCategoryPerson(o.categoryId) &&
                     !IsCategoryAnimal(o.categoryId))
            {
                otherInstance = inst;
                otherTrackId = o.trackId;
            }
        }

        return humanInstance != null && otherInstance != null;
    }


    // SkinnedMeshRenderer の bones を総当たりして最近接ボーンを返す。
    // Humanoid / Generic どちらのリグでも同じように測れるようにするため、
    // HumanBodyBones ではなくボーン配列そのものを見る。
    // [ROOTDIAG] 用。Hips ボーンのモデルローカル位置。root と体の関係を切り分けるためだけに使う。
    private static Vector3 ResolveHipsLocalForDiag(GameObject instance)
    {
        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman) { return Vector3.negativeInfinity; }
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null) { return Vector3.negativeInfinity; }
        return instance.transform.InverseTransformPoint(hips.position);
    }

    private bool TryResolveNearestHumanBone(
        GameObject instance,
        Vector3 point,
        out Vector3 nearest,
        out string boneName)
    {
        nearest = Vector3.zero;
        boneName = string.Empty;
        float best = float.MaxValue;

        SkinnedMeshRenderer[] renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int r = 0; r < renderers.Length; r++)
        {
            Transform[] bones = renderers[r].bones;
            if (bones == null)
            {
                continue;
            }

            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null)
                {
                    continue;
                }

                float d = (bones[b].position - point).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = bones[b].position;
                    boneName = bones[b].name;
                }
            }
        }

        return best < float.MaxValue;
    }


    // 表示モデルの関節角度を出す。角度は座標系・スケールに依存しないので、
    // meta.bin の keypoints3d から測った同じ角度と直接比較できる。
    // 180° = まっすぐ伸びた状態、小さいほど深く曲げている。
    private void LogJointAnglesIfEnabled(MetaObj obj, GameObject instance, int frame)
    {
        if (!logPlacementMeasurement ||
            instance == null ||
            !IsCategoryPerson(obj.categoryId) ||
            frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        float Angle(HumanBodyBones a, HumanBodyBones pivot, HumanBodyBones b)
        {
            if (!cache.bones.TryGetValue(a, out Transform ta) || ta == null ||
                !cache.bones.TryGetValue(pivot, out Transform tp) || tp == null ||
                !cache.bones.TryGetValue(b, out Transform tb) || tb == null)
            {
                return -1f;
            }

            Vector3 v1 = ta.position - tp.position;
            Vector3 v2 = tb.position - tp.position;
            if (v1.sqrMagnitude < 1e-10f || v2.sqrMagnitude < 1e-10f)
            {
                return -1f;
            }

            return Vector3.Angle(v1, v2);
        }

        float lKnee = Angle(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        float rKnee = Angle(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
        float lHip = Angle(HumanBodyBones.Neck, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        float rHip = Angle(HumanBodyBones.Neck, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg);
        float lElbow = Angle(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        float rElbow = Angle(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);

        // 股のずれが「胴側」か「大腿側」かを切り分けるための追加測定。
        // neck = 胴の曲がり（Hips-Neck-Head）、shoulder = 肩と胴（Hips-Neck-UpperArm）
        // spread = 脚の開き（UpperLeg-Hips-UpperLeg）
        float neckAngle = Angle(HumanBodyBones.Hips, HumanBodyBones.Neck, HumanBodyBones.Head);
        float shoulderAngle = Angle(HumanBodyBones.Hips, HumanBodyBones.Neck, HumanBodyBones.LeftUpperArm);
        float legSpread = Angle(HumanBodyBones.LeftUpperLeg, HumanBodyBones.Hips, HumanBodyBones.RightUpperLeg);

        Debug.Log(
            $"[ANGLE] f={frame} lKnee={lKnee:F1} rKnee={rKnee:F1} " +
            $"lHip={lHip:F1} rHip={rHip:F1} lElbow={lElbow:F1} rElbow={rElbow:F1} " +
            $"neck={neckAngle:F1} shoulder={shoulderAngle:F1} spread={legSpread:F1}");
    }


    private bool loggedBoneLengths;

    // 表示モデルの骨長を 1 回だけ出す。meta.bin の keypoints3d から測った骨長と比べて
    // 体型差（脚と胴の比率）がどれだけあるかを確認するための計測。
    private void LogBoneLengthsOnce(GameObject instance)
    {
        if (loggedBoneLengths || !logPlacementMeasurement || instance == null)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        float Len(HumanBodyBones a, HumanBodyBones b)
        {
            if (!cache.bones.TryGetValue(a, out Transform ta) || ta == null ||
                !cache.bones.TryGetValue(b, out Transform tb) || tb == null)
            {
                return 0f;
            }

            return Vector3.Distance(ta.position, tb.position);
        }

        float thigh = Len(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        float shin = Len(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        float torso = Len(HumanBodyBones.Hips, HumanBodyBones.Neck);
        float upperArm = Len(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        float foreArm = Len(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        float headTop = Len(HumanBodyBones.Neck, HumanBodyBones.Head);
        if (torso <= 0.0001f)
        {
            return;
        }

        loggedBoneLengths = true;
        Debug.Log(
            $"[BONELEN] thigh={thigh:F4} shin={shin:F4} torso={torso:F4} " +
            $"upperArm={upperArm:F4} foreArm={foreArm:F4} neckToHead={headTop:F4} " +
            $"| 胴で正規化: thigh={thigh / torso:F3} shin={shin / torso:F3} " +
            $"leg={(thigh + shin) / torso:F3} upperArm={upperArm / torso:F3} " +
            $"foreArm={foreArm / torso:F3} " +
            $"| scale={instance.transform.localScale.x:F4}");
    }


    // Humanoid のボーン world 位置を eye pixel に投影して縦の広がりを測る。
    // renderer.bounds（world 軸平行 AABB）と違い、姿勢が傾いても過大評価しない。
    // ⑧・スケール再ロック・下端合わせが投影に使うボーン一覧。
    // Humanoid は HumanBodyBones の対応表、Generic（Animal）は SkinnedMeshRenderer の
    // ボーン配列。詳細は Core.cs の projectGenericRigBones を参照。
    private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Transform>>
        projectionBoneBuffer = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Transform>>(128);

    private System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Transform>>
        ResolveProjectionBones(GameObject instance, Animator animator)
    {
        projectionBoneBuffer.Clear();
        if (animator != null && animator.isHuman)
        {
            HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
            if (cache != null && cache.ready)
            {
                foreach (var pair in cache.bones)
                {
                    if (pair.Value != null)
                    {
                        projectionBoneBuffer.Add(
                            new System.Collections.Generic.KeyValuePair<string, Transform>(
                                pair.Key.ToString(), pair.Value));
                    }
                }
            }

            return projectionBoneBuffer;
        }

        if (!projectGenericRigBones || instance == null)
        {
            return projectionBoneBuffer;
        }

        SkinnedMeshRenderer[] skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int r = 0; r < skinned.Length; r++)
        {
            Transform[] bones = skinned[r] != null ? skinned[r].bones : null;
            if (bones == null)
            {
                continue;
            }

            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] != null)
                {
                    projectionBoneBuffer.Add(
                        new System.Collections.Generic.KeyValuePair<string, Transform>(bones[b].name, bones[b]));
                }
            }
        }

        if (projectionBoneBuffer.Count > 0)
        {
            return projectionBoneBuffer;
        }

        // SkinnedMeshRenderer を持たない剛体パーツ構成のモデル（例: 00_Dog は Animator と
        // 54 個の Transform 階層を持つが、メッシュは MeshRenderer 22 個に分かれている）。
        // skinning は投影高を測るのに必要ではないので、メッシュを持つ Transform の位置を
        // ボーンの代わりに使う。姿勢適用（⑤）はボーン階層で動くのでモデル自体は動いている。
        Renderer[] all = instance.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < all.Length; r++)
        {
            if (all[r] != null && all[r].transform != null)
            {
                projectionBoneBuffer.Add(
                    new System.Collections.Generic.KeyValuePair<string, Transform>(
                        all[r].transform.name, all[r].transform));
            }
        }

        return projectionBoneBuffer;
    }

    private bool TryProjectBonesToEyeHeight(
        GameObject instance,
        Transform screen,
        out float topV,
        out float bottomV,
        out float heightPixels,
        out string topBoneName,
        out string bottomBoneName)
    {
        topV = 0f;
        bottomV = 0f;
        heightPixels = 0f;
        topBoneName = null;
        bottomBoneName = null;
        if (instance == null || manifest == null || manifest.eye_h <= 0)
        {
            return false;
        }

        // Humanoid なら HumanBodyBones の対応表を、そうでなければ SkinnedMeshRenderer の
        // ボーン配列を総当たりする。
        //
        // 以前は `!animator.isHuman` で即 false を返しており、**Animal は Generic リグなので
        // ⑧ もスケール再ロックも一度も動いていなかった**（2026-08-26 実測、animal で
        // [DEPTH8] が 0 件。human では 2000 件超）。⑧ のカテゴリ条件は Animal を通していた
        // ので、宣言と実態が食い違っていた。症状は「モデルが映像より小さく配置される」。
        //
        // 総当たりは TryResolveNearestHumanBone が既に採っている方式。
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Transform>> boneList =
            ResolveProjectionBones(instance, animator);
        if (boneList.Count == 0)
        {
            return false;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        bool hasAny = false;
        foreach (var pair in boneList)
        {
            Transform bone = pair.Value;
            if (bone == null)
            {
                continue;
            }

            Vector3 cam = worldToCam * (bone.position - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    cam,
                    fx,
                    fy,
                    out Vector2 pixel))
            {
                continue;
            }

            if (pixel.y < minV)
            {
                minV = pixel.y;
                topBoneName = pair.Key;
            }

            if (pixel.y > maxV)
            {
                maxV = pixel.y;
                bottomBoneName = pair.Key;
            }

            hasAny = true;
        }

        if (!hasAny)
        {
            return false;
        }

        topV = minV;
        bottomV = maxV;
        heightPixels = maxV - minV;
        return heightPixels > 0.0001f;
    }


    // ---- 以下は Playback.partial.cs から移設（2026-09-06）----
    // 配置の本筋と診断が同じファイルに混ざって 2,482 行になっていた。
    // 同じ partial クラスなので参照関係・シリアライズは変わらない。

    // FK 適用後の骨格投影が bbox 高さに一致するよう、ロック済みスケールを一度だけ測り直す。
    //
    // ② ResolveDesiredLocalScale が使う bboxWorldH は「被写体が anchorZ という 1 枚の面に
    // ある」前提の式で、前後に広がった姿勢では必ず過小評価になる（2026-08-18 実測: 立位 7% /
    // 深い前傾 76% 過大。keypoints3d を同じスケールで投影しても同じ比なので式の前提の問題）。
    // ここで FK 適用後の実測値から逆算し、基準フレームでの誤差を消す。
    //
    // 投影高さは scale に厳密には比例しない（scale を変えると各ボーンの深度も動く）が、
    // root 深度 0.75 m に対して体の前後の広がりは 0.1 m 程度なので誤差は 2 次に留まる。
    // 1 回の補正で十分収束するため反復はしない。
    //
    // 呼ぶのは ⑦ FitDisplayedModelToBBox の後。スケールを変えると下端が動くので、
    // 補正後に下端合わせをやり直す。
    // 診断: モデルの実ボーンの投影位置と、meta.bin の keypoints3d の投影位置を突き合わせる。
    // 「試算（keypoints ベース）は合うのに実装（実ボーン）は効かない」原因の切り分け用。
    private void LogBoneVsKeypointIfEnabled(MetaObj obj, GameObject instance, Transform screen, int frame)
    {
        if (!logBoneVsKeypoint || instance == null || !obj.hasSkeleton || obj.jointsCam == null)
        {
            return;
        }

        if (logBoneVsKeypointEveryNFrames > 0 && (frame % logBoneVsKeypointEveryNFrames) != 0)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }


        // keypoints を bbox 高さに合わせて投影する（試算と同じ手順）。
        Vector3[] joints = obj.jointsCam;
        const int PelvisIndex = 39;
        if (joints.Length <= PelvisIndex || obj.bboxH <= 0f)
        {
            return;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        for (int i = 0; i < joints.Length; i++)
        {
            float y = joints[i].y - joints[PelvisIndex].y;
            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }
        }

        float span = maxY - minY;
        if (span <= 0.0001f)
        {
            return;
        }

        float pixelsPerMeter = obj.bboxH / span;

        // 比べる部位: OpenPose25 の index → Humanoid ボーン
        (int kp, HumanBodyBones bone, string label)[] pairs =
        {
            (1, HumanBodyBones.Neck, "Neck"),
            (2, HumanBodyBones.RightUpperArm, "RSho"),
            (3, HumanBodyBones.RightLowerArm, "RElb"),
            (4, HumanBodyBones.RightHand, "RWri"),
            (5, HumanBodyBones.LeftUpperArm, "LSho"),
            (6, HumanBodyBones.LeftLowerArm, "LElb"),
            (7, HumanBodyBones.LeftHand, "LWri"),
            (9, HumanBodyBones.RightUpperLeg, "RHip"),
            (10, HumanBodyBones.RightLowerLeg, "RKnee"),
            (24, HumanBodyBones.RightFoot, "RFoot"),
            (12, HumanBodyBones.LeftUpperLeg, "LHip"),
            (13, HumanBodyBones.LeftLowerLeg, "LKnee"),
            (21, HumanBodyBones.LeftFoot, "LFoot"),
            // 足首の基準点ずれを切り分けるための追加ペア。
            // Foot ボーンが Ankle と Toe のどちらに近いか、Toes ボーンが BigToe と合うかを見る。
            (22, HumanBodyBones.RightFoot, "RFoot_vsToe"),
            (19, HumanBodyBones.LeftFoot, "LFoot_vsToe"),
            (22, HumanBodyBones.RightToes, "RToes"),
            (19, HumanBodyBones.LeftToes, "LToes"),
            (24, HumanBodyBones.RightFoot, "RFoot_vsHeel"),
            (21, HumanBodyBones.LeftFoot, "LFoot_vsHeel"),
        };

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append($"[BONEKP] f={frame} track={obj.trackId} bboxH={obj.bboxH:F0}");
        Quaternion worldToCam = Quaternion.Inverse(camRotation);

        for (int i = 0; i < pairs.Length; i++)
        {
            (int kpIndex, HumanBodyBones boneId, string label) = pairs[i];
            if (kpIndex >= joints.Length || !cache.bones.TryGetValue(boneId, out Transform bone) || bone == null)
            {
                continue;
            }

            // keypoint の投影位置（骨盤 anchor 基準）
            float ku = obj.anchorU + (joints[kpIndex].x - joints[PelvisIndex].x) * pixelsPerMeter;
            float kv = obj.anchorV - (joints[kpIndex].y - joints[PelvisIndex].y) * pixelsPerMeter;

            // 実ボーンの投影位置
            Vector3 cam = worldToCam * (bone.position - camOrigin);
            if (cam.z <= 0.0001f)
            {
                continue;
            }

            float bu = (0.5f + (cam.x / cam.z) * fx * 0.5f) * manifest.eye_w;
            float bv = (0.5f - (cam.y / cam.z) * fy * 0.5f) * manifest.eye_h;

            sb.Append($" {label}=({bu - ku:F0},{bv - kv:F0})");
        }

        Debug.Log(sb.ToString());
    }

    // root と体（Hips）の深度を段ごとに出す。**root が体からいつ離れるか**を特定するため。
    // 2026-09-05 時点では ⑧ に入る時点で 3.0m 設定で 2.451m 離れており、その発生段が
    // 未特定（localHips.z が anchorZ のメートル値と一致するところまでは判っている）。
    private void LogHipStageIfEnabled(string stage, GameObject instance, MetaObj obj, Transform screen)
    {
        if (!logDepthRefineStages || instance == null || !IsCategoryPerson(obj.categoryId))
        {
            return;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        float rootZ = (worldToCam * (instance.transform.position - camOrigin)).z;
        Vector3 body = ResolveDepthReferenceWorld(instance);
        float bodyZ = (worldToCam * (body - camOrigin)).z;
        Debug.Log(
            $"[HIPSTAGE] f={GetCurrentPlaybackFrame()} track={obj.trackId} stage={stage} " +
            $"rootZ={rootZ:F4} bodyZ={bodyZ:F4} gap={(bodyZ - rootZ) * 1000f:+0.0;-0.0}mm " +
            $"anchorZ={obj.anchorZ:F4} scale={instance.transform.lossyScale.y:F4}");
    }

    // Animal 版の [BONEKP]。実ボーンと meta.bin の keypoints3d の投影位置の差を測る。
    //
    // human の LogBoneVsKeypointIfEnabled と同じ狙い: 「姿勢が正しく適用されているか」を
    // 数値で見る。human は Humanoid リグなので Unity が対応を保証するが、**Animal は
    // Generic リグでモデルごとにボーン名が違い、AnimalRigCache が名前で解決している**。
    // 対応が外れていても静かに動き続けるので、измерение が無いと気付けない。
    //
    // ボーンと keypoint の対応は AnimalPoseJointChains と ApplyAnimalHeadPose の実装に
    // 合わせている。ここを実装と食い違わせると、また「試算と実装の前提ずれ」を起こす。
    private void LogAnimalBoneVsKeypointIfEnabled(MetaObj obj, GameObject instance, Transform screen, int frame)
    {
        if (!logAnimalBoneVsKeypoint || instance == null || manifest == null || manifest.eye_h <= 0)
        {
            return;
        }

        if (!IsCategoryAnimal(obj.categoryId) ||
            frame % Mathf.Max(1, logBoneVsKeypointEveryNFrames) != 0)
        {
            return;
        }

        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        AnimalRigCache cache = animalPoseApplier.PeekAnimalRigCache(instance);
        if (cache == null || !cache.ready)
        {
            return;
        }

        // 適用側は **回転だけ** を書いている（ApplyAnimalBoneFromPoints は
        // TransformWriter.ApplyWorldRotation のみで、位置は動かさない）。したがって
        // 「ボーンの位置と keypoint の位置の差」を測っても意味がない。最初それをやって
        // Neck 378% という数字を出したが、測っている対象が違った（2026-08-27）。
        //
        // 正しくは **方向（角度）の差**。ボーンが向いている向きと、keypoint のペアが
        // 示す向きの角度差を測る。四肢は AnimalPoseJointChains そのままで、
        // upper は chain[0]→chain[1]、lower は chain[1]→chain[2]、paw は chain[2]→chain[3]。
        //
        // ボーン側は現行 bundle では **SMAL FK が出した姿勢**（AnimalSmalFkApplier）。
        // つまりこの指標は「SMAL FK の結果 対 AniMer keypoints3d」という**別ソース同士の
        // 比較**で、「適用がターゲットに収束しているか」ではない。最優先目標が
        // keypoints3d への一致なので指標としては有効だが、読み違えないこと。
        // paw / toe / head は SMAL 側で body_pose を受け取らず親追従なので、
        // 値が小さくても「合っている」ではない（Docs/smpl-retargeting.md の駆動範囲の表）。
        // from / to が両方 non-null のときは **2 点間の向き**（to.position - from.position）を
        // 測る。null のときは従来どおりボーン自身の向き（aim child への方向）。
        //
        // 後肢 Upper で 2 点間版が要る理由:
        //   ボーン方向は「股関節 → 膝」だが、目標の kp7 は Tail1（尾の付け根）であって
        //   股関節ではない。この起点の違いだけで **22 度の下駄**が乗る（実測。前肢は
        //   kp12/13 が LLeg1/RLeg1 そのものなので下駄はちょうど 0.0 度）。
        //   Unity リグには tail_base があるので、両辺を「尾の付け根 → 膝」に揃えられる。
        // 回転ベース（LRUp）と点間ベース（LRUpTB）を両方出して差を見る。
        // **意味が違うので平均に混ぜないこと。**
        (Transform bone, Transform from, Transform to, int kpA, int kpB, string label)[] pairs =
        {
            // 2026-08-28: D-007 の対応表で全面的に訂正した。旧ペアは前肢の起点が
            // kp18（「き甲」だと思っていたが実際は**頭**）で、しかも**前肢・後肢とも
            // 左右が逆**だった。ここで測った角度を 3 セッション読んでいたが、
            // 対応づけ自体が誤っていたので過去の数値とは比較しないこと。
            //
            // 首は 26 関節に対応する点が無いので、Neck は診断から外す。
            // 代わりに head を「頭→鼻先端」で測る。
            (cache.head, null, null, AnimalHeadKeypoints.Head, AnimalHeadKeypoints.Nose, "Head"),
            (cache.leftFrontUpper,  null, null, 12,  8, "LFUp"),
            (cache.leftFrontLower,  null, null,  8, 14, "LFLo"),
            (cache.leftFrontPaw,    null, null, 14,  3, "LFPaw"),
            (cache.rightFrontUpper, null, null, 13,  9, "RFUp"),
            (cache.rightFrontLower, null, null,  9, 15, "RFLo"),
            (cache.rightFrontPaw,   null, null, 15,  4, "RFPaw"),
            (cache.leftRearUpper,   null, null,  7, 10, "LRUp"),
            (cache.leftRearLower,   null, null, 10, 16, "LRLo"),
            (cache.leftRearPaw,     null, null, 16,  5, "LRPaw"),
            (cache.rightRearUpper,  null, null,  7, 11, "RRUp"),
            (cache.rightRearLower,  null, null, 11, 17, "RRLo"),
            (cache.rightRearPaw,    null, null, 17,  6, "RRPaw"),

            // 下駄を除いた後肢 Upper。両辺とも「尾の付け根 → 膝」。
            (cache.leftRearLower,  cache.tailBase, cache.leftRearLower,   7, 10, "LRUpTB"),
            (cache.rightRearLower, cache.tailBase, cache.rightRearLower,  7, 11, "RRUpTB"),
        };

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append($"[ANIMALKP] f={frame} track={obj.trackId}");
        int resolved = 0;
        for (int i = 0; i < pairs.Length; i++)
        {
            (Transform bone, Transform from, Transform to, int kpA, int kpB, string label) = pairs[i];
            bool usePoints = from != null && to != null;
            if (bone == null || (usePoints && (from == null || to == null)))
            {
                sb.Append($" {label}=null");
                continue;
            }

            resolved++;
            if (kpA >= obj.jointsVis.Length || kpB >= obj.jointsVis.Length ||
                obj.jointsVis[kpA] == 0 || obj.jointsVis[kpB] == 0)
            {
                sb.Append($" {label}=novis");
                continue;
            }

            // jointsCam は anchor 基準の相対座標。差を取るので anchor は打ち消えるが、
            // camRotation で world 系に合わせる必要がある。
            Vector3 targetDir = camRotation * (obj.jointsCam[kpB] - obj.jointsCam[kpA]);
            Vector3 boneDir;
            if (usePoints)
            {
                boneDir = to.position - from.position;
                if (boneDir.sqrMagnitude < 0.000001f)
                {
                    sb.Append($" {label}=nodir");
                    continue;
                }

                boneDir.Normalize();
            }
            else if (!animalPoseApplier.TryGetBoneDirectionForDiag(cache, bone, out boneDir))
            {
                sb.Append($" {label}=nodir");
                continue;
            }

            if (targetDir.sqrMagnitude < 0.000001f)
            {
                sb.Append($" {label}=nodir");
                continue;
            }

            sb.Append($" {label}={Vector3.Angle(boneDir, targetDir.normalized):F0}");
        }

        sb.Append($" resolvedBones={resolved}/{pairs.Length}");

        // リグの関節内角（肘・膝の曲がり角）。**keypoint とは無関係**で、
        // 「SMAL の body_pose が Unity のボーンをどれだけ曲げたか」だけを測る。
        //
        // 測定 B（曲げ有無）で [ANIMALKP] がほとんど変わらなかったので、
        //   transport が曲げを失っているのか / SMAL の姿勢が元々 rest に近いのか
        // を分けるために入れた（2026-08-28）。
        //
        // SMAL 側の同じ内角は rest から次のぶん動いている（meta.bin から実測済み）:
        //   肘 rest 5.4° → 犬 24.3 / 18.1°（+18.9 / +12.7）
        //   膝 rest 32.6° → 犬 54.1 / 46.9°（+21.5 / +14.4）
        // Unity 側も同程度動けば transport の**大きさ**は合っている（残差は向き＝ロール）。
        // ほとんど動かなければ transport が曲げを失っている。
        System.Text.StringBuilder ab = new System.Text.StringBuilder();
        ab.Append($"[ANIMALANG] f={frame} track={obj.trackId}");
        foreach ((Transform up, Transform lo, Transform paw, string label) in new[]
        {
            (cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, "LFel"),
            (cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, "RFel"),
            (cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, "LRkn"),
            (cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, "RRkn"),
        })
        {
            if (up == null || lo == null || paw == null)
            {
                ab.Append($" {label}=null");
                continue;
            }

            Vector3 a = lo.position - up.position;
            Vector3 b = paw.position - lo.position;
            if (a.sqrMagnitude < 0.000001f || b.sqrMagnitude < 0.000001f)
            {
                ab.Append($" {label}=deg");
                continue;
            }

            ab.Append($" {label}={Vector3.Angle(a, b):F0}");
        }

        Debug.Log(ab.ToString());

        if (!loggedAnimalRigBoneNames)
        {
            loggedAnimalRigBoneNames = true;
            System.Text.StringBuilder nb = new System.Text.StringBuilder();
            nb.Append($"[ANIMALRIG] track={obj.trackId} instance={instance.name}");
            for (int i = 0; i < pairs.Length; i++)
            {
                (Transform bone, Transform _from, Transform _to, int kpA, int kpB, string label) = pairs[i];
                if (bone == null)
                {
                    nb.Append($" {label}=null");
                    continue;
                }

                // 子 Transform の数と最初の子の名前。head が本当に末端かを確かめる。
                string firstChild = bone.childCount > 0 ? bone.GetChild(0).name : "-";
                bool hasDir = animalPoseApplier.TryGetBoneDirectionForDiag(cache, bone, out _);
                nb.Append($" {label}={bone.name}(children={bone.childCount},first={firstChild},dir={(hasDir ? 1 : 0)})");
            }

            // 末端ボーンが他にもあるか。tailTip / toe も同じ状態のはず。
            foreach ((Transform t, string n) in new[]
            {
                (cache.spine, "spine"), (cache.tailBase, "tailBase"),
                (cache.tailMid, "tailMid"), (cache.tailTip, "tailTip"),
                (cache.leftRearToe, "lRearToe"), (cache.rightRearToe, "rRearToe"),
            })
            {
                if (t == null)
                {
                    nb.Append($" {n}=null");
                    continue;
                }

                bool hasDir = animalPoseApplier.TryGetBoneDirectionForDiag(cache, t, out _);
                nb.Append($" {n}={t.name}(children={t.childCount},dir={(hasDir ? 1 : 0)})");
            }

            Debug.Log(nb.ToString());
        }

        Debug.Log(sb.ToString());
    }

    // 横方向の実測用。メッシュの投影 U 範囲と bbox の U 範囲を出す。
    // ⑦ は縦しか動かしていない（AlignProjectedModelBottomToBBox は camY のみ）ので、
    // 横位置は ① の anchorU で決まる。ずれているかどうかを測るためだけの診断。
    private void LogHorizontalPlacementIfEnabled(MetaObj obj, GameObject instance, Transform screen, int frame)
    {
        if (!logHorizontalPlacement || instance == null || manifest == null || manifest.eye_w <= 0)
        {
            return;
        }

        if (frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) != 0)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _) ||
            !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation) ||
            !TryGetRendererWorldBounds(instance, out Bounds bounds))
        {
            return;
        }

        float minU = float.MaxValue;
        float maxU = float.MinValue;
        Vector3 e = bounds.extents;
        Quaternion worldToCam = Quaternion.Inverse(camRotation);

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = bounds.center + new Vector3(
                ((i & 1) == 0 ? -e.x : e.x),
                ((i & 2) == 0 ? -e.y : e.y),
                ((i & 4) == 0 ? -e.z : e.z));
            Vector3 cam = worldToCam * (corner - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, cam, fx, fy, out Vector2 px))
            {
                continue;
            }

            if (px.x < minU) { minU = px.x; }
            if (px.x > maxU) { maxU = px.x; }
        }

        if (minU > maxU)
        {
            return;
        }

        float bl = obj.bboxX;
        float br = obj.bboxX + obj.bboxW;
        Debug.Log(
            $"[HPOS] f={frame} track={obj.trackId} projL={minU:F1} projR={maxU:F1} projC={(minU + maxU) * 0.5f:F1} " +
            $"bboxL={bl:F0} bboxR={br:F0} bboxC={(bl + br) * 0.5f:F1} anchorU={obj.anchorU} " +
            $"dL={(minU - bl):F1} dR={(maxU - br):F1} dC={((minU + maxU) * 0.5f - (bl + br) * 0.5f):F1} " +
            $"clipL={(obj.bboxX <= 0 ? 1 : 0)} clipR={(obj.bboxX + obj.bboxW >= manifest.eye_w ? 1 : 0)}");
    }
}