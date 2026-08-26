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

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
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
        foreach (var pair in cache.bones)
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
                topBoneName = pair.Key.ToString();
            }

            if (pixel.y > maxV)
            {
                maxV = pixel.y;
                bottomBoneName = pair.Key.ToString();
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
}
