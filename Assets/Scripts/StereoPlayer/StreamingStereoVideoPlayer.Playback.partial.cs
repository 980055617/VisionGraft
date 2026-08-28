using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: meta frame cache, track instance state, manifest/screen helpers, and manual-yaw partials
    // Provides: model display pipeline, target selection, replaceable model apply, TryApplySkeleton entry

    public void DisplayModelTick()
    {
        if (!displayModel || !metaLoaded || isNormalMode)
        {
            return;
        }
        if (!HasAnyDisplayPrefabConfigured())
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        RuntimePlaybackTimeline.FrameSnapshot frameSnapshot = GetPlaybackFrameSnapshot();
        int metaFrameUsed = frameSnapshot.displayMetadataFrame;
        if (!TryReadFrameObjects(metaFrameUsed, metaFrameObjects) ||
            metaFrameObjects.Count == 0)
        {
            return;
        }

        int frame = metaFrameUsed;
        SyncShotBoundaryForFrame(frame);
        ApplyOtherProxyBoxesForFrame(metaFrameObjects, frame);

        if (TryApplyDisplayedTracks(frame))
        {
            ApplyOtherDepthFollowForFrame();
            ApplyOtherPenetrationResolveForFrame();
            ApplyHumanOtherContactCorrectionForFrame();
            LogHumanOtherGapIfEnabled(frame);
            LogBallHeadIfEnabled(frame);
            return;
        }

        MetaObj target = SelectAutoDisplayTarget(metaFrameObjects);
        int autoTrackId = (int)target.trackId;
        if (autoTrackId != lastAutoTrackId)
        {
            lastAutoTrackId = autoTrackId;
        }

        ApplyMetaTarget(target, frame);
        ApplyOtherDepthFollowForFrame();
        ApplyOtherPenetrationResolveForFrame();
        ApplyHumanOtherContactCorrectionForFrame();
        LogHumanOtherGapIfEnabled(frame);
        LogBallHeadIfEnabled(frame);
    }


    private bool HasAnyDisplayPrefabConfigured()
    {
        return (humanPrefabs != null && humanPrefabs.Length > 0) ||
               (animalPrefabs != null && animalPrefabs.Length > 0) ||
               (elsePrefabs != null && elsePrefabs.Length > 0);
    }


    private bool TryApplyDisplayedTracks(int frame)
    {
        // 空 = bundle 内の全トラックを表示
        if (displayTrackIds == null || displayTrackIds.Length == 0)
        {
            return TryApplyAllTracks(frame);
        }

        // 指定あり = そのトラック ID のみ表示
        HashSet<uint> selectedTracks = new HashSet<uint>();
        HashSet<uint> appliedTracks = new HashSet<uint>();
        for (int i = 0; i < displayTrackIds.Length; i++)
        {
            int displayTrackId = displayTrackIds[i];
            if (displayTrackId < 0)
            {
                continue;
            }

            uint trackId = (uint)displayTrackId;
            selectedTracks.Add(trackId);
            if (TryApplyTargetByTrackId(trackId, frame))
            {
                appliedTracks.Add(trackId);
            }
            else if (TryApplyInteractiveSystemTriggerTrack(trackId, frame))
            {
                appliedTracks.Add(trackId);
            }
        }

        HideUnselectedTrackInstances(appliedTracks);
        return selectedTracks.Count > 0;
    }


    private bool TryApplyAllTracks(int frame)
    {
        if (metaFrameObjects.Count == 0)
        {
            return false;
        }

        HashSet<uint> appliedTracks = new HashSet<uint>();
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            uint trackId = metaFrameObjects[i].trackId;
            if (appliedTracks.Contains(trackId))
            {
                continue;
            }

            if (TryApplyTargetByTrackId(trackId, frame))
            {
                appliedTracks.Add(trackId);
            }
            else if (TryApplyInteractiveSystemTriggerTrack(trackId, frame))
            {
                appliedTracks.Add(trackId);
            }
        }

        HideUnselectedTrackInstances(appliedTracks);
        return true;
    }


    private void HideUnselectedTrackInstances(HashSet<uint> selectedTracks)
    {
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (selectedTracks.Contains(kv.Key) || kv.Value == null)
            {
                continue;
            }

            SceneObjectWriter.ApplyActive(kv.Value, false);
        }
    }


    private bool TryApplyTargetByTrackId(uint trackId, int frame)
    {
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            if (metaFrameObjects[i].trackId != trackId)
            {
                continue;
            }

            ApplyMetaTarget(metaFrameObjects[i], frame);
            return true;
        }

        return false;
    }


    private void ApplyMetaTarget(MetaObj target, int frame)
    {
        if (!ResolveAnchorToScreen(target.anchorU, out Transform screen, out int uEye, out _))
        {
            return;
        }

        // Bundle writer stores anchor/bbox already mapped into eye pixel coordinates.
        float uEyeF = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        float vEyeF = Mathf.Clamp(target.anchorV, 0f, manifest.eye_h - 1f);

        float bboxHAdjusted = target.bboxH;

        // Else も Human/Animal と同じく meta.bin の anchor (u/v + anchorZ) だけで配置する。
        // source/other_object_proxies.json の cameraXyz / proxy3d は units="same_as_depth_npz"、
        // つまり 0=far/1=near の正規化深度でメートルではないため runtime 配置には使えない
        // （そのまま world に流すと前後関係が反転する）。sidecar は debug 可視化専用。
        Vector3 anchorWorld = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);

        GameObject instance = GetOrCreateTrackInstance(target.trackId, target.categoryId);
        if (instance == null)
        {
            return;
        }

        SceneObjectWriter.ApplyActive(instance, true);
        // 脚の骨長合わせはインスタンスごとに一度だけ。モデルを切り替えると
        // TrackInstanceLifecycle がインスタンスを作り直すので、新しいモデルにも掛かる。
        TryApplyHumanBoneLengthCorrection(instance, target);
        Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
        rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole, screen != null ? screen.up : Vector3.up);

        ObserveInteractiveMotionLiveTrackedSample(target.trackId, target, screen);
        UpdateInteractiveMotionSchedule(target.trackId, target, frame);
        TryStopSystemTriggerOnVisibleFrame(target.trackId);

        if (TryApplyOwnedInteractiveMotion(target.trackId, instance, screen, frame))
        {
            return;
        }

        float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
        ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxHAdjusted, screen);
        bool preserveRootScreenHeightAfterSkeleton =
            IsCategoryPerson(target.categoryId) &&
            ShouldPreserveRootScreenHeightAfterHumanSkeletonPlacement();
        Vector3 preSkeletonRootPosition = instance.transform.position;
        TryApplySkeleton(instance, target, screen, frame);
        if (preserveRootScreenHeightAfterSkeleton)
        {
            TrackPlacementWriter.Apply(
                instance.transform,
                TrackPlacementCommand.PositionOnly(
                    ResolveRootPositionPreservingScreenHeight(
                        instance.transform.position,
                        preSkeletonRootPosition,
                        screen != null ? screen.up : Vector3.up),
                    instance.transform.rotation,
                    instance.transform.localScale));
        }
        // モデルの原点オフセットを打ち消す。Hips を「ルートを置いた位置」へ持ってくることで、
        // ② がスケールを決めるときの前提「体が anchorZ にいる」を成立させる。
        // ⑦ の下端合わせより前に入れる（縦方向はこのあと ⑦ が合わせ直す）。
        AlignModelBodyToAnchorDepthIfEnabled(instance, target);

        // ⑦ の投影ベース下端合わせが ④ の結果をどれだけ動かすかを測るため、直前の位置を控える。
        Vector3 preBottomFitPosition = instance.transform.position;
        if (!ShouldUseHumanSmplRootPlacement(target, frame))
        {
            FitDisplayedModelToBBox(instance, target, screen, bboxHAdjusted);
        }
        LogBottomAlignmentDeltaIfEnabled(target, instance, screen, frame, preBottomFitPosition);
        RefineLockedScaleFromProjectedBones(instance, target, screen, bboxHAdjusted);
        // ⑧ 投影高が bbox に一致する深度へ動かす。スケールは変えない。
        // 深度が動くと ⑦ の下端合わせが崩れるので、動かした場合だけ ⑦ を掛け直す。
        if (RefineDepthFromProjectedBones(instance, target, screen, bboxHAdjusted) &&
            !ShouldUseHumanSmplRootPlacement(target, frame))
        {
            FitDisplayedModelToBBox(instance, target, screen, bboxHAdjusted);
        }
        ObserveInteractiveMotionDisplayedRoot(target.trackId, instance);
        ApplyInteractiveHandoffBlendIfActive(target.trackId, instance, frame);
        LogPlacementMeasurementIfEnabled(target, instance, screen, frame);
        LogHorizontalPlacementIfEnabled(target, instance, screen, frame);
        LogAnimalBoneVsKeypointIfEnabled(target, instance, screen, frame);
        LogBoneVsKeypointIfEnabled(target, instance, screen, frame);
    }


    private MetaObj SelectAutoDisplayTarget(List<MetaObj> objs)
    {
        float eyeW = manifest != null ? manifest.eye_w : 0f;
        float eyeH = manifest != null ? manifest.eye_h : 0f;
        float leftCenterU = eyeW * 0.5f;
        float rightCenterU = eyeW * 1.5f;
        float centerV = eyeH * 0.5f;
        bool hasRightCenter = metaHeader.width >= eyeW * 2f && rightScreen != null;

        MetaObj best = objs[0];
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < objs.Count; i++)
        {
            MetaObj obj = objs[i];
            float dx = obj.anchorU - leftCenterU;
            float dy = obj.anchorV - centerV;
            float distSq = dx * dx + dy * dy;
            if (hasRightCenter)
            {
                float dxR = obj.anchorU - rightCenterU;
                float distSqR = dxR * dxR + dy * dy;
                distSq = Mathf.Min(distSq, distSqR);
            }

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        return best;
    }


    private GameObject GetOrCreateTrackInstance(uint trackId, byte categoryId)
    {
        GameObject prefab = ResolveTrackPrefab(trackId, categoryId);
        return TrackInstanceLifecycle.GetOrCreate(
            trackId,
            prefab,
            trackInstances,
            trackPrefabSources,
            lockedModelLocalScaleByTrack,
            ref selectedManualRotationTrackId);
    }


    private GameObject ResolveTrackPrefab(uint trackId, byte categoryId)
    {
        // 永続化から復元した prefab 名は、ここで初めてカテゴリが確定するので index に直す。
        ResolvePendingModelSelection(trackId, ResolvePrefabsForCategory(categoryId));

        if (IsCategoryAnimal(categoryId))
        {
            if (animalPrefabs != null && animalPrefabs.Length > 0)
            {
                int idx = ResolveSelectedModelIndex(trackId, selectedAnimalIndex);
                idx = Mathf.Clamp(idx, 0, animalPrefabs.Length - 1);
                return animalPrefabs[idx];
            }
            return null;
        }
        if (IsCategoryOther(categoryId))
        {
            if (elsePrefabs != null && elsePrefabs.Length > 0)
            {
                int idx = ResolveSelectedModelIndex(trackId, selectedElseIndex);
                idx = Mathf.Clamp(idx, 0, elsePrefabs.Length - 1);
                return elsePrefabs[idx];
            }
            return null;
        }
        if (humanPrefabs != null && humanPrefabs.Length > 0)
        {
            int idx = ResolveSelectedModelIndex(trackId, selectedHumanIndex);
            idx = Mathf.Clamp(idx, 0, humanPrefabs.Length - 1);
            return humanPrefabs[idx];
        }
        return null;
    }


    private GameObject[] ResolvePrefabsForCategory(byte categoryId)
    {
        if (IsCategoryAnimal(categoryId))
        {
            return animalPrefabs;
        }

        if (IsCategoryOther(categoryId))
        {
            return elsePrefabs;
        }

        return humanPrefabs;
    }


    private int ResolveSelectedModelIndex(uint trackId, int defaultIndex)
    {
        if (selectedModelIndexByTrack.TryGetValue(trackId, out int selectedIndex))
        {
            return selectedIndex;
        }

        if (TryGetInspectorTrackModelIndex(trackId, out int inspectorIndex))
        {
            return inspectorIndex;
        }

        return defaultIndex;
    }


    private bool TryGetInspectorTrackModelIndex(uint trackId, out int modelIndex)
    {
        if (trackModelIndices != null)
        {
            for (int i = 0; i < trackModelIndices.Length; i++)
            {
                if (trackModelIndices[i].trackId == (int)trackId)
                {
                    modelIndex = trackModelIndices[i].modelIndex;
                    return true;
                }
            }
        }

        modelIndex = 0;
        return false;
    }


    private float ComputeTargetHeightMeters(float bboxH, float zMeters)
    {
        if (manifest == null)
        {
            return 0f;
        }

        if (!TryGetFocalLengths(out _, out float fy))
        {
            return 0f;
        }

        return TrackModelPlacement.ResolveTargetHeightMeters(bboxH, manifest.eye_h, zMeters, fy);
    }


    // Hips が現在の root 位置に来るようモデル全体を平行移動する。
    // 回転もスケールも変えない。詳細は Core.cs の alignModelBodyToAnchorDepth を参照。
    private void AlignModelBodyToAnchorDepthIfEnabled(GameObject instance, MetaObj obj)
    {
        if (!alignModelBodyToAnchorDepth || instance == null || !IsCategoryPerson(obj.categoryId))
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null)
        {
            return;
        }

        Vector3 shift = instance.transform.position - hips.position;
        if (shift.sqrMagnitude < 0.0000001f)
        {
            return;
        }

        TrackPlacementWriter.Apply(
            instance.transform,
            TrackPlacementCommand.PositionOnly(
                instance.transform.position + shift,
                instance.transform.rotation,
                instance.transform.localScale));

        if (logBodyAnchorAlign)
        {
            Debug.Log(
                $"[BODYALIGN] f={GetCurrentFrameIndex()} track={obj.trackId} " +
                $"shift={shift.magnitude * 1000f:F1}mm");
        }
    }

    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxHAdjusted, Transform screen)
    {
        if (instance == null)
        {
            return;
        }

        ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
        float modelHeight = model != null ? model.GetModelHeightMeters() : 0f;
        float userScale = model != null ? model.userScale : 1f;
        Vector3 baseScale = model != null ? model.baseLocalScale : Vector3.one;
        float baseHeight = model != null ? model.baseBoundsSize.y : 0f;
        bool lockScale = IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId);
        bool hasFocalLengths = TryGetFocalLengths(out _, out float fyScale);

        // スケールは shot 先頭フレームの bbox から決め、track ごとにロックする
        // （GetOrLockModelLocalScale）。ロックが外れる契機は shot 境界とモデル変更の 2 つで、
        // 基準を先頭フレームに固定しておかないと「同じ shot の同じ track なのに、モデルを
        // 変えたタイミングで大きさが変わる」ことになる（ResolveShotStartScaleReference）。
        //
        // 「立位に最も近いフレームを基準にする」案は実測で悪化した（2026-08-07）。
        // アスペクト比が最大のフレームは人物が横向きで細く写っているだけのことがあり、
        // bbox 高さで絞り直しても骨格スパンが bbox の 112%（初回基準では 86%）と過大になった。
        // あちらは shot 内から都合のよいフレームを探す話で、ここは通常再生と同じ 1 点に
        // 揃える話なので目的が違う。
        float scaleTargetHeightMeters = targetHeightMeters;
        float scaleBBoxH = bboxHAdjusted;
        float scaleAnchorZ = obj.anchorZ;
        if (lockScale && TryResolveShotStartScaleReference(obj.trackId, out MetaObj shotStartObj))
        {
            scaleBBoxH = shotStartObj.bboxH;
            scaleAnchorZ = shotStartObj.anchorZ;
            scaleTargetHeightMeters = ComputeTargetHeightMeters(scaleBBoxH, scaleAnchorZ);
        }

        Vector3 desiredScale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            baseScale,
            baseHeight,
            userScale,
            modelHeight,
            scaleTargetHeightMeters,
            scaleBBoxH,
            scaleAnchorZ,
            fyScale,
            manifest != null ? manifest.eye_h : 0,
            hasFocalLengths));

        TrackPlacementWriter.ApplyAnchoredPose(
            instance.transform,
            world,
            rotation,
            instance.transform.localScale,
            model != null ? model.anchor : null);

        if (hasFocalLengths)
        {
            TrackPlacementWriter.ApplyLocalScaleWithGroundAlignment(
                instance.transform,
                lockScale ? GetOrLockModelLocalScale(obj.trackId, desiredScale) : desiredScale,
                model != null && model.anchor == null && model.alignToGround,
                model != null ? model.baseBottomOffsetLocal : 0f);
            Vector3 lossy = instance.transform.lossyScale;

            // 姿勢を持つカテゴリ（Human / Animal）は、姿勢適用後に FitDisplayedModelToBBox が
            // 「実際のメッシュの投影下端」で合わせ直す。ここで先に合わせても上書きされるだけで、
            // しかも baseBottomOffsetLocal は Awake 時（bind pose）の値で固定されているため、
            // 座位・仰向けでは root を大きく外す。実測では後段の補正量が bboxH の最大 98%
            // （175px）に達していた（2026-08-06）。姿勢を持たない Else だけがここで合わせる。
            bool alignsBottomAfterSkeleton =
                IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId);
            if (AlignModelToBBoxBottom && model != null && !alignsBottomAfterSkeleton)
            {
                Vector3 up = screen != null ? screen.up : Vector3.up;
                float vBottom = ResolveReliableBBoxBottomVEye(obj);
                Vector3 bottomWorld = AnchorUvZToWorldPinhole(screen, uEye, vBottom, obj.anchorZ);
                bottomWorld += up * ModelBottomExtraOffsetMeters;
                float modelBottomOffset = model.baseBottomOffsetLocal * lossy.y;
                TrackPlacementWriter.ApplyBottomAlignment(
                    instance.transform,
                    bottomWorld,
                    up,
                    modelBottomOffset,
                    BottomAlignVerticalOnly);
            }

            return;
        }

        TrackPlacementWriter.ApplyLocalScaleWithGroundAlignment(
            instance.transform,
            lockScale ? GetOrLockModelLocalScale(obj.trackId, desiredScale) : desiredScale,
            model != null && model.anchor == null && model.alignToGround,
            model != null ? model.baseBottomOffsetLocal : 0f);
    }


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

        Quaternion worldToCam = Quaternion.Inverse(camRotation);

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

            Quaternion inv = Quaternion.Inverse(camRotation);
            Vector3 otherCam = inv * (otherInstance.transform.position - camOrigin);
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

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        bool found = false;
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

            Quaternion inv = Quaternion.Inverse(camRotation);
            // 人の深度は root ではなく体基準の点で測る。root はモデルによっては体の外にある
            // （FBX の bind pose に焼き込まれた原点オフセット。Core.cs の
            // otherDepthSkeletonReference を参照）。
            Vector3 skeletonRef = ResolveHumanDepthReferencePoint(skeletonInstance, camRotation);
            Vector3 skeletonCam = inv * (skeletonRef - camOrigin);
            Vector3 otherCam = inv * (otherInstance.transform.position - camOrigin);
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

    // ⑧ の補正が、同じフレームの Else との前後関係を反転させないよう ratio を丸める。
    // ⑧ は人の深度だけを bbox から決めるため、Else（anchor_z 由来のまま）との相対関係が
    // bundle の意図から外れる。meta.bin の anchorZ はデコード済みの深度なので、その大小が
    // bundle の示す正しい前後関係になる。
    //
    // 深度が決まった後にクランプすると、発動フレームで一気に 135mm 動いて跳ねる
    // （2026-08-20 実測: 発動率 17.8%、1 フレーム変化 max 22mm → 206mm、実機で悪化と判定）。
    // そのため深度ではなく ratio を制限し、この後の平滑化で角を取る。
    // 平滑化が制約を後から破るので前後関係は完全には守れないが、跳ねは生じない。
    //
    // Else が複数あって制約が矛盾する場合（下限が上限を上回る）は、どれかを必ず壊すことに
    // なるので何もしない。bundle_train のような Else のみの bundle では Person がいないので
    // そもそもここへ来ない。
    private float ClampRatioPreservingOtherOrder(MetaObj obj, float currentZ, float ratio)
    {
        float eps = Mathf.Max(0f, projectedDepthOrderEpsilonMeters);
        if (eps <= 0f || metaFrameObjects == null || currentZ <= 0.0001f)
        {
            return ratio;
        }

        float k = Mathf.Max(0.1f, projectedDepthScaleK);
        float upper = float.MaxValue;
        float lower = float.MinValue;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj other = metaFrameObjects[i];
            if (other.trackId == obj.trackId)
            {
                continue;
            }

            // 骨格を持つ track は相手も ⑧ で動くので基準にできない。Else だけを見る。
            if (IsCategoryPerson(other.categoryId) || IsCategoryAnimal(other.categoryId))
            {
                continue;
            }

            // z = currentZ * ratio * k なので、深度の許容値を ratio の許容値へ写す。
            if (other.anchorZ > obj.anchorZ)
            {
                upper = Mathf.Min(upper, (other.anchorZ - eps) / (currentZ * k));
            }
            else
            {
                lower = Mathf.Max(lower, (other.anchorZ + eps) / (currentZ * k));
            }
        }

        if (lower > upper)
        {
            return ratio;
        }

        return Mathf.Clamp(ratio, lower, upper);
    }

    // ⑧ の補正比率を時間平滑化する。bbox は検出ノイズと姿勢でフレームごとに揺れるので、
    // 素通しで深度に反映するとモデルが前後に暴れる。深度ではなく比率を平滑化することで、
    // 人の実際の移動（anchor_z 由来）は保ったままノイズだけを落とす。
    // 平滑化係数は時定数から毎フレーム求めるのでフレームレートに依存しない。
    private float SmoothProjectedDepthRatio(uint trackId, float ratio)
    {
        float tau = Mathf.Max(0f, projectedDepthSmoothingSeconds);
        if (tau <= 0.0001f)
        {
            smoothedProjectedDepthRatioByTrack[trackId] = ratio;
            return ratio;
        }

        if (!smoothedProjectedDepthRatioByTrack.TryGetValue(trackId, out float previous))
        {
            // shot 先頭・モデル変更直後は平滑化せず、その場の値から始める。
            smoothedProjectedDepthRatioByTrack[trackId] = ratio;
            return ratio;
        }

        // DisplayModelTick は毎 Update 呼ばれるので、この関数は 1 メタフレームにつき
        // 約 31 回走る。ただし Time.deltaTime の合計が 1 メタフレームぶんになるため、
        // 平滑化の総進行量は tau どおりになる（2026-08-25 検証）。
        // なお ratio は毎 tick 現在の姿勢から再計算されるので、この反復は
        // 「投影骨高 == bbox 高」への不動点反復も兼ねている。フレーム単位に
        // 間引くと収束が失われるので間引かないこと。
        float deltaTime = Mathf.Max(0f, Time.deltaTime);
        float alpha = deltaTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / tau);

        // 誤差が大きいときは追従を速める。
        //
        // 一次遅れフィルタはノイズには強いがランプ状の変化に遅れる。shot 内で被写体の
        // 見かけが 1 秒で 3 倍になる場面（bundle_animal 29.9〜32.7s）や 0.7 秒しかない
        // shot では、tau=1.2s では追いつかない（実測 sizeRatio 0.64、期待 1.04）。
        // かといって tau を一律に short にすると正常フレームが行き過ぎ（1.041→1.171）、
        // 深度の揺れも 8→18mm に増える（2026-08-27 実測）。
        //
        // そこで「小さいズレ = ノイズなので鈍く、大きいズレ = 実際の変化なので速く」に
        // する。相対誤差が fastLo を超えたぶんだけ alpha を 1 に寄せる。
        float relativeError = Mathf.Abs(ratio - previous) / Mathf.Max(0.05f, Mathf.Abs(previous));
        float lo = Mathf.Max(0f, depthRefineFastTrackLow);
        float hi = Mathf.Max(lo + 0.001f, depthRefineFastTrackHigh);
        float boost = Mathf.Clamp01((relativeError - lo) / (hi - lo));
        alpha = Mathf.Clamp01(alpha + (1f - alpha) * boost);

        float smoothed = previous + alpha * (ratio - previous);
        smoothedProjectedDepthRatioByTrack[trackId] = smoothed;
        return smoothed;
    }

    // ⑧ ロック済みスケールはそのままに、「投影された骨格の高さが bbox 高に一致する」深度へ動かす。
    // 投影高 = span(f) * scale * f_px / z(f) なので、z を ratio 倍すれば投影高が bbox に一致する。
    // 画面上の位置（u, v）を保ったまま深度だけ変えるため、カメラ空間で z 方向にスケールする。
    // 動かしたときだけ true を返す（呼び出し側が ⑦ の下端合わせを掛け直すため）。
    // 見切れを補った「本来の被写体の高さ」を px で返す。推定できなければ bboxH をそのまま返す。
    //
    // 手法: 切れていない辺（横）で px/m を較正し、keypoints の縦スパンに掛ける。
    //   推定全高 = 縦スパン × (bbox幅 ÷ 横スパン) × UnclippedHeightCalibration
    // 較正係数は見切れなしフレーム 416 枚で測った高さ係数 ÷ 幅係数（317.6 / 307.2）。
    // 検証: 見切れなしフレームでの誤差は median 1.6%、p90 14.5%（2026-08-26）。
    //
    // 左右のどちらかが切れていると横で較正できないので、その場合は諦めて bboxH を返す。
    private float ResolveUnclippedTargetHeight(MetaObj obj, float bboxH)
    {
        if (!extendTargetHeightForClippedBBox || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return bboxH;
        }

        // 下端も上端も切れていないなら bbox 高がそのまま被写体の高さ。
        bool clippedTop = obj.bboxY <= 0;
        bool clippedBottom = obj.bboxY + obj.bboxH >= manifest.eye_h;
        if (!clippedTop && !clippedBottom)
        {
            return bboxH;
        }

        // 横が切れていると px/m を較正できない。
        if (obj.bboxX <= 0 || obj.bboxX + obj.bboxW >= manifest.eye_w || obj.bboxW <= 0)
        {
            return bboxH;
        }

        if (!TryGetJointSpan(obj, out float spanX, out float spanY) || spanX <= 0.0001f || spanY <= 0.0001f)
        {
            return bboxH;
        }

        float pixelsPerMeter = obj.bboxW / spanX;
        float estimated = spanY * pixelsPerMeter * UnclippedHeightCalibration;

        // 推定が bbox より小さくなるのは較正誤差。切れている以上は bbox 以上のはず。
        if (estimated <= bboxH)
        {
            return bboxH;
        }

        // 外挿を無制限に許すと 1 フレームの推定ミスで極端な値になる。
        float maxH = bboxH * Mathf.Max(1f, maxClippedHeightExtrapolation);
        return Mathf.Min(estimated, maxH);
    }

    // keypoints の x / y スパン（メートル）。root 相対座標なのでそのまま幅・高さになる。
    //
    // **可視フラグで絞ってはいけない。** 見切れたぶんのジョイントにはまさに不可視フラグが
    // 立つので、可視だけで測ると「見えている範囲」しか出ず外挿にならない。実測でも
    // shot 20（38.2〜44.9s、欠損率 43.6%）で倍率が 1.14 にしかならず、期待の 1.77 に
    // 届かなかった（2026-08-27）。bundle は不可視ジョイントにも SMAL/SMPL フィット由来の
    // 座標を格納しているので、全ジョイントを使う。
    //
    // 較正係数 1.034 もこの「全ジョイント」前提で求めたもの（見切れなしフレーム 416 枚で
    // 誤差 median 1.6%）。片方だけ変えると前提がずれる。
    private bool TryGetJointSpan(MetaObj obj, out float spanX, out float spanY)
    {
        spanX = 0f;
        spanY = 0f;
        if (!obj.hasSkeleton || obj.jointsCam == null)
        {
            return false;
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        int n = obj.jointsCam.Length;
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 j = obj.jointsCam[i];
            if (float.IsNaN(j.x) || float.IsNaN(j.y))
            {
                continue;
            }

            if (j.x < minX) { minX = j.x; }
            if (j.x > maxX) { maxX = j.x; }
            if (j.y < minY) { minY = j.y; }
            if (j.y > maxY) { maxY = j.y; }
            used++;
        }

        if (used < 6)
        {
            return false;
        }

        spanX = maxX - minX;
        spanY = maxY - minY;
        return true;
    }

    private bool RefineDepthFromProjectedBones(
        GameObject instance,
        MetaObj obj,
        Transform screen,
        float bboxH)
    {
        if (!refineDepthFromProjectedBones || instance == null || bboxH <= 0f)
        {
            return false;
        }

        // 骨格を持つカテゴリだけ。Else は投影が既に bbox と一致している（実測 sizeRatio = 1.000）。
        if (!IsCategoryPerson(obj.categoryId) && !IsCategoryAnimal(obj.categoryId))
        {
            return false;
        }

        if (!TryProjectBonesToEyeHeight(instance, screen, out _, out _, out float projectedH, out _, out _) ||
            projectedH <= 0.0001f)
        {
            return false;
        }

        // 合わせる相手は bbox 高ではなく「見切れを補った推定全高」。
        //
        // bbox は可視部分だけの外接矩形（bundle 側が 2026-08-26 に確定）。被写体が画面から
        // はみ出しているフレームで bbox 高に合わせると、動物全体を切れた bbox に押し込む
        // ことになりモデルが不当に小さくなる。実測では欠損率 49.9% の shot で本来 2.00 倍に
        // 写るべきところが 0.995 倍（＝半分）だった（2026-08-27、bundle_animal 1.6〜5.0s）。
        //
        // 見切れていないフレームでは推定全高 ≒ bbox 高になるので、正常なフレームの挙動は
        // 変わらない。推定できないフレーム（左右も切れている等）は bbox 高のまま。
        float targetH = ResolveUnclippedTargetHeight(obj, bboxH);

        float ratio = projectedH / targetH;

        // 検出が破綻しているフレームでは動かさないためのガード。
        //
        // ただし下限 0.4 は「モデルが bbox の 4 割以下にしか写っていない」ケースを全部
        // 弾いてしまう。shot 内で被写体の見かけが 3 倍以上変わる場面（bundle_animal の
        // 29.9〜32.7s、bboxH が 110→336px）では、スケールが shot 先頭でロックされている
        // ぶん ratio が 0.4 を割り、**最も補正が要るフレームで ⑧ が何もしなくなる**。
        // 実測でも 32 秒台の sizeRatio が 0.416 とガードに張り付いていた（2026-08-27）。
        if (ratio < depthRefineMinRatio || ratio > MaxProjectedBoneRatioForScaleRefine)
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3 camLocal = Quaternion.Inverse(camRotation) * (instance.transform.position - camOrigin);
        if (camLocal.z <= 0.0001f)
        {
            return false;
        }

        // 前後関係の制約は「深度が決まった後の硬いクランプ」ではなく、平滑化の手前で
        // ratio を許容範囲に丸める形で入れる。深度側でクランプすると発動フレームで
        // 一気に 135mm 動いて跳ねる（2026-08-20 実測、1 フレーム変化 max 22mm → 206mm）。
        ratio = ClampRatioPreservingOtherOrder(obj, camLocal.z, ratio);
        ratio = SmoothProjectedDepthRatio(obj.trackId, ratio);

        float beforeZ = camLocal.z;
        float ratioZ = camLocal.z * ratio * Mathf.Max(0.1f, projectedDepthScaleK);
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        float targetZ = Mathf.Clamp(
            ratioZ,
            Mathf.Max(0.001f, MinDistanceFromHeadMeters),
            screenDist - 0.0001f);

        if (logDepthRefineStages)
        {
            Debug.Log(
                $"[DEPTH8] track={obj.trackId} anchorZ={obj.anchorZ:F4} before={beforeZ:F4} " +
                $"ratio={ratio:F4} afterRatio={ratioZ:F4} final={targetZ:F4} " +
                $"screenMoved={(targetZ - ratioZ) * 1000f:F1}mm");
        }

        if (Mathf.Abs(targetZ - camLocal.z) <= 0.0001f)
        {
            return false;
        }

        Vector3 moved = camLocal * (targetZ / camLocal.z);
        TrackPlacementWriter.Apply(
            instance.transform,
            TrackPlacementCommand.PositionOnly(
                camOrigin + camRotation * moved,
                instance.transform.rotation,
                instance.transform.localScale));
        return true;
    }

    private void RefineLockedScaleFromProjectedBones(
        GameObject instance,
        MetaObj obj,
        Transform screen,
        float bboxH)
    {
        if (!refineScaleFromProjectedBones ||
            instance == null ||
            bboxH <= 0f ||
            scaleRefinedByTrack.Contains(obj.trackId))
        {
            return;
        }

        // 姿勢を持つカテゴリだけ。Else は前後の広がりが無視できるので元の式で正確
        // （実測で sizeRatio = 1.000）。
        if (!IsCategoryPerson(obj.categoryId) && !IsCategoryAnimal(obj.categoryId))
        {
            return;
        }

        if (!lockedModelLocalScaleByTrack.TryGetValue(obj.trackId, out Vector3 locked))
        {
            return;
        }

        if (!TryProjectBonesToEyeHeight(instance, screen, out _, out _, out float projectedH, out _, out _) ||
            projectedH <= 0.0001f)
        {
            return;
        }

        float ratio = projectedH / bboxH;

        // bbox が画面端で切れている、検出が破綻している等でこの範囲を外れたら補正しない。
        // 誤った基準を焼き付けると shot の間ずっと残るため、疑わしいときは何もしない方が安全。
        if (ratio < MinProjectedBoneRatioForScaleRefine || ratio > MaxProjectedBoneRatioForScaleRefine)
        {
            return;
        }

        // ratio を projectedBoneRatioTarget に合わせる（既定 1.0 = bbox ぴったり）。
        Vector3 refined = locked * (Mathf.Max(0.1f, projectedBoneRatioTarget) / ratio);
        lockedModelLocalScaleByTrack[obj.trackId] = refined;
        scaleRefinedByTrack.Add(obj.trackId);

        TrackPlacementWriter.ApplyLocalScale(instance.transform, refined);

        // スケールを変えた分だけ下端がずれるので合わせ直す。
        if (!ShouldUseHumanSmplRootPlacement(obj, GetCurrentPlaybackFrame()))
        {
            FitDisplayedModelToBBox(instance, obj, screen, bboxH);
        }

        if (logPlacementMeasurement)
        {
            Debug.Log(
                $"[SCALEFIX] track={obj.trackId} boneRatio={ratio:F3} target={projectedBoneRatioTarget:F3} " +
                $"scale {locked.x:F4} → {refined.x:F4} (×{projectedBoneRatioTarget / ratio:F3}) bboxH={bboxH:F0}");
        }
    }


    private Vector3 GetOrLockModelLocalScale(uint trackId, Vector3 desiredLocalScale)
    {
        if (lockedModelLocalScaleByTrack.TryGetValue(trackId, out Vector3 lockedScale))
        {
            return lockedScale;
        }

        // 新しくロックした = まだ FK 後の実測補正を通していない。shot 境界・モデル変更・
        // インスタンス再生成のいずれでロックが消えても、ここで必ず補正がやり直される。
        scaleRefinedByTrack.Remove(trackId);
        lockedModelLocalScaleByTrack[trackId] = desiredLocalScale;
        return desiredLocalScale;
    }


    // ロックが外れている track について、いま表示している shot の先頭フレームの bbox を返す。
    // ロック済みなら desiredScale は捨てられるので読みに行かない（毎フレーム meta.bin を
    // 引かないための早期 return でもある）。
    //
    // shot 途中から登場する track は先頭フレームに存在しないので false を返し、呼び出し元は
    // 従来どおり現在フレームの bbox でロックする。bbox が潰れているフレーム（画面端で切れて
    // いる等）を基準にしないよう、幅・高さが 0 のものも採用しない。
    private readonly List<MetaObj> shotStartFrameObjects = new List<MetaObj>();

    private bool TryResolveShotStartScaleReference(uint trackId, out MetaObj result)
    {
        result = default;
        if (lockedModelLocalScaleByTrack.ContainsKey(trackId))
        {
            return false;
        }

        if (lastAppliedShotIndex < 0)
        {
            return false;
        }

        int startFrame = shotBoundaries.GetStartFrame(lastAppliedShotIndex);
        if (startFrame == GetCurrentPlaybackFrame())
        {
            return false;
        }

        if (!TryReadFrameObjects(startFrame, shotStartFrameObjects))
        {
            return false;
        }

        for (int i = 0; i < shotStartFrameObjects.Count; i++)
        {
            MetaObj candidate = shotStartFrameObjects[i];
            if (candidate.trackId != trackId)
            {
                continue;
            }

            if (candidate.bboxW <= 0 || candidate.bboxH <= 0)
            {
                return false;
            }

            result = candidate;
            return true;
        }

        return false;
    }


    // 縦位置の基準。anchor_v ではなく bbox 下端を使うのは意図的で、anchor_v は
    // depth をサンプルした点（体の中心付近）であって接地点ではないため。
    // その結果 anchor_v は初期配置にしか効かず、最終的な縦位置は bbox 下端だけで決まる。
    // Human / Animal / Else すべてこの基準に揃えている。
    private float ResolveBBoxBottomVEye(MetaObj obj)
    {
        if (manifest == null || manifest.eye_h <= 0)
        {
            return obj.anchorV;
        }

        float vBottom = obj.bboxY + obj.bboxH;
        if (obj.bboxH <= 0)
        {
            vBottom = obj.anchorV;
        }

        return Mathf.Clamp(vBottom, 0f, manifest.eye_h - 1f);
    }

    // A track's bbox routinely collapses while it is being clipped by the frame edge (the
    // detector only sees a shrinking sliver), which moves the bbox's bottom edge away from the
    // subject's true feet position and pops the model's bottom-aligned height. Freeze the
    // bottom-alignment target at the last reliable bbox once that happens, instead of chasing
    // a bbox that no longer represents the subject's full extent.
    private const float BBoxBottomAlignMinAreaRatio = 0.5f;
    private readonly Dictionary<uint, float> lastGoodBottomAlignArea = new Dictionary<uint, float>();
    private readonly Dictionary<uint, float> lastGoodBottomAlignVEye = new Dictionary<uint, float>();

    private float ResolveReliableBBoxBottomVEye(MetaObj obj)
    {
        float vBottom = ResolveBBoxBottomVEye(obj);
        float area = (float)obj.bboxW * obj.bboxH;
        bool touchesFrameEdge = manifest != null &&
            (obj.bboxX <= 0 || obj.bboxY <= 0 ||
             obj.bboxX + obj.bboxW >= manifest.eye_w - 1 || obj.bboxY + obj.bboxH >= manifest.eye_h - 1);

        if (touchesFrameEdge &&
            lastGoodBottomAlignArea.TryGetValue(obj.trackId, out float lastGoodArea) &&
            lastGoodArea > 0f &&
            area < lastGoodArea * BBoxBottomAlignMinAreaRatio &&
            lastGoodBottomAlignVEye.TryGetValue(obj.trackId, out float frozenVBottom))
        {
            return frozenVBottom;
        }

        lastGoodBottomAlignArea[obj.trackId] = area;
        lastGoodBottomAlignVEye[obj.trackId] = vBottom;
        return vBottom;
    }


    private void FitDisplayedModelToBBox(GameObject instance, MetaObj obj, Transform screen, float bboxH)
    {
        if (instance == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        // 姿勢を持つカテゴリ専用。Else は姿勢で形が変わらないので ApplyReplaceableModelTransform
        // 側の bind pose ベースの下端合わせで足り、ここは通らない。
        if (!IsCategoryPerson(obj.categoryId) && !IsCategoryAnimal(obj.categoryId))
        {
            return;
        }

        if (bboxH <= 0f)
        {
            return;
        }

        if (!TryProjectRendererBoundsToEyeHeight(instance, screen, out float projectedTopV, out float projectedBottomV, out float projectedHeight, out float depthMeters))
        {
            return;
        }

        // 位置合わせの基準はスケールの基準と揃える。スケールは骨格（ReplaceableModel の
        // baseSkeletonHeightMeters）を bbox に合わせているので、下端も骨格の最下点で合わせる。
        // AABB 下端（靴底・服の裾）を使うと、スケール拡大に伴ってメッシュ余白も拡大し、
        // 足首が bbox 下端から 15% 浮く（2026-08-07 実測）。
        // ボーンが取れないモデルでは従来どおり AABB 下端にフォールバックする。
        float bottomV = projectedBottomV;
        if (TryProjectBonesToEyeHeight(instance, screen, out _, out float boneBottomV, out _, out _, out _))
        {
            bottomV = boneBottomV;
        }

        // 下端が画面外に切れているフレームでは、bbox の下端は「被写体の下端」ではなく
        // 「画面の端」でしかない。そこに合わせると、本来画面外にあるはずの下半身を
        // 画面の中へ持ち上げてしまう（bundle_animal の 5.0〜8.6s / 26.7〜29.9s で顕著。
        // 実測で animal の 64.6% のフレームが「下端切れ・上端有効」に該当）。
        //
        // 上端が切れていなければ、そちらは被写体の実際の上端なので基準にできる。
        // 上端で合わせれば下半身は自然に画面外へ出る。はみ出した部分は passthrough の
        // 現実映像に重なるが、それは許容する方針（2026-08-27 ユーザー確認）。
        //
        // 上下とも切れているフレーム（animal で 8.9%）はどちらも基準にできないので
        // 従来どおり下端合わせにフォールバックする。
        bool clippedBottom = obj.bboxY + obj.bboxH >= manifest.eye_h;
        bool clippedTop = obj.bboxY <= 0;
        if (alignTopWhenBottomClipped && clippedBottom && !clippedTop)
        {
            // 上端は**メッシュの投影上端**（projectedTopV）で合わせる。ボーンの最上点を
            // 使ってはいけない。`SkinnedMeshRenderer.bones` には armature の根など
            // メッシュから離れたノードが含まれ、実測では 39_Lynx で最上ボーンが V=-721
            // （画面のはるか上）に出て、bbox 上端 2 に合わせた結果モデルが 723px 下へ
            // 飛んだ（2026-08-27、41〜43 秒の猫が完全にフレームアウト）。
            //
            // 下端合わせがボーン最下点を使えているのは、armature の根がたまたま足元に
            // あるため。上端には同じ前提が成り立たない。
            // そもそも bbox の上端は被写体の見た目の上端（毛・耳）なので、対応するのは
            // 骨ではなくメッシュ。
            AlignProjectedModelBottomToBBox(instance.transform, screen, projectedTopV, depthMeters, obj.bboxY);
            return;
        }

        // depthMeters はモデル AABB 中心の深度で、anchorZ とは 3〜4% ずれる。
        // ここは「投影した下端を bbox 下端に一致させる」処理なので、投影に使ったのと同じ
        // 深度（depthMeters）で逆算するのが正しい。anchorZ を混ぜてはいけない。
        AlignProjectedModelBottomToBBox(instance.transform, screen, bottomV, depthMeters, ResolveReliableBBoxBottomVEye(obj));
    }

    // SMPL の transl で root を置く経路は無効化されている
    // （ShouldUseHumanSmplRootPlacementPolicy が引数によらず常に false を返す）。
    // 以前はここで TryGetHumanSmplPose を呼んで hasTransl を調べていたが、その結果は
    // ポリシー側で捨てられるため、毎フレームの辞書引きが完全に無駄になっていた。
    // ポリシー関数自体は将来の切り替え点として残す。
    private bool ShouldUseHumanSmplRootPlacement(MetaObj obj, int frame)
    {
        return IsCategoryPerson(obj.categoryId) &&
               ShouldUseHumanSmplRootPlacementPolicy(true, false);
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

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        float minU = float.MaxValue;
        float maxU = float.MinValue;
        Vector3 e = bounds.extents;
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

    private bool TryProjectRendererBoundsToEyeHeight(GameObject instance, Transform screen, out float topV, out float bottomV, out float heightPixels, out float depthMeters)
    {
        topV = 0f;
        bottomV = 0f;
        heightPixels = 0f;
        depthMeters = 0f;
        if (instance == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        if (!TryGetProjectionIntrinsics(out _, out float fy, out _, out float cyPixels))
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Bounds bounds;
        if (!TryGetRendererWorldBounds(instance, out bounds))
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        Vector3 centerCam = worldToCam * (bounds.center - camOrigin);
        depthMeters = Mathf.Max(0.001f, centerCam.z);

        Vector3 camUp = camRotation * Vector3.up;
        Vector3 extents = bounds.extents;
        float verticalExtent =
            Mathf.Abs(Vector3.Dot(new Vector3(extents.x, 0f, 0f), camUp)) +
            Mathf.Abs(Vector3.Dot(new Vector3(0f, extents.y, 0f), camUp)) +
            Mathf.Abs(Vector3.Dot(new Vector3(0f, 0f, extents.z), camUp));
        if (verticalExtent <= 0.000001f)
        {
            return false;
        }

        Vector3 topCam = worldToCam * ((bounds.center + camUp * verticalExtent) - camOrigin);
        Vector3 bottomCam = worldToCam * ((bounds.center - camUp * verticalExtent) - camOrigin);
        if (topCam.z <= 0.001f || bottomCam.z <= 0.001f)
        {
            return false;
        }

        topV = ((cyPixels / manifest.eye_h) - (topCam.y * fy / topCam.z) * 0.5f) * manifest.eye_h;
        bottomV = ((cyPixels / manifest.eye_h) - (bottomCam.y * fy / bottomCam.z) * 0.5f) * manifest.eye_h;
        if (bottomV < topV)
        {
            float tmp = topV;
            topV = bottomV;
            bottomV = tmp;
        }

        heightPixels = bottomV - topV;
        return heightPixels > 0.0001f;
    }


    private static bool TryGetRendererWorldBounds(GameObject instance, out Bounds bounds)
    {
        bounds = default(Bounds);
        if (instance == null)
        {
            return false;
        }

        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        bool hasAny = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled)
            {
                continue;
            }

            if (!hasAny)
            {
                bounds = renderer.bounds;
                hasAny = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasAny;
    }


    private void AlignProjectedModelBottomToBBox(Transform root, Transform screen, float projectedBottomV, float depthMeters, float targetBottomV)
    {
        if (root == null || manifest == null || manifest.eye_h <= 0)
        {
            return;
        }

        if (!TryGetProjectionIntrinsics(out _, out float fy, out _, out _))
        {
            return;
        }

        if (!TryGetPinholeBasis(screen, out _, out Quaternion camRotation))
        {
            return;
        }

        float deltaV = targetBottomV - projectedBottomV;
        float deltaCamY = -(deltaV * 2f / manifest.eye_h) * (depthMeters / fy);
        TrackPlacementWriter.ApplyCameraSpaceOffset(root, camRotation, new Vector3(0f, deltaCamY, 0f));
    }


    private void TryApplySkeleton(GameObject instance, MetaObj obj, Transform screen, int frame)
    {
        if (instance == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        if (IsCategoryAnimal(obj.categoryId))
        {
            TryApplyAnimalPosePipeline(instance, obj, screen, frame);
            return;
        }

        if (!IsCategoryOther(obj.categoryId))
        {
            TryApplyPersonPosePipeline(instance, obj, screen, frame);
        }
    }

}
