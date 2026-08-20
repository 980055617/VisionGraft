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

        float deltaTime = Mathf.Max(0f, Time.deltaTime);
        float alpha = deltaTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / tau);
        float smoothed = previous + Mathf.Clamp01(alpha) * (ratio - previous);
        smoothedProjectedDepthRatioByTrack[trackId] = smoothed;
        return smoothed;
    }

    // ⑧ ロック済みスケールはそのままに、「投影された骨格の高さが bbox 高に一致する」深度へ動かす。
    // 投影高 = span(f) * scale * f_px / z(f) なので、z を ratio 倍すれば投影高が bbox に一致する。
    // 画面上の位置（u, v）を保ったまま深度だけ変えるため、カメラ空間で z 方向にスケールする。
    // 動かしたときだけ true を返す（呼び出し側が ⑦ の下端合わせを掛け直すため）。
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

        float ratio = projectedH / bboxH;

        // 案 A と同じガード。bbox が画面端で切れている・検出が破綻しているフレームでは動かさない。
        if (ratio < MinProjectedBoneRatioForScaleRefine || ratio > MaxProjectedBoneRatioForScaleRefine)
        {
            return false;
        }

        ratio = SmoothProjectedDepthRatio(obj.trackId, ratio);

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3 camLocal = Quaternion.Inverse(camRotation) * (instance.transform.position - camOrigin);
        if (camLocal.z <= 0.0001f)
        {
            return false;
        }

        float targetZ = camLocal.z * ratio * Mathf.Max(0.1f, projectedDepthScaleK);
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        targetZ = Mathf.Clamp(
            targetZ,
            Mathf.Max(0.001f, MinDistanceFromHeadMeters),
            screenDist - 0.0001f);

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
