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
            // 2026-09-18: track が 1 つも無い frame でも、直前まで出ていたモデルを消し、慣性中の Else は進める。
            // 以前はここで何もせず return していたので、car（25% の frame に track が無い）で最後に消えた車が
            // 次の track が出るまで置き去りになり（track 3 は f1036 → 1340 の 10 秒、track 4 は動画の最後まで）、
            // 空の区間へシークしても直前のモデルがその場に残っていた（実機のユーザー指摘）。
            metaFrameObjects.Clear();
            HideUnselectedTrackInstances(NoTracks);
            ApplyElseFrameOutForFrame(metaFrameUsed);
            return;
        }

        int frame = metaFrameUsed;
        ApplyBatchSwapModelSpecForFrame(frame);
        SyncShotBoundaryForFrame(frame);
        ApplyOtherProxyBoxesForFrame(metaFrameObjects, frame);

        if (TryApplyDisplayedTracks(frame))
        {
            ApplyOtherDepthFollowForFrame();
            ApplyOtherPenetrationResolveForFrame();
            ApplyElseFrameOutForFrame(frame);
            ApplyElseChainPlacementForFrame(frame);
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
        ApplyElseFrameOutForFrame(frame);
        ApplyElseChainPlacementForFrame(frame);
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


    // track の無い frame で「全部消す」ために渡す空集合。
    private static readonly HashSet<uint> NoTracks = new HashSet<uint>();

    private void HideUnselectedTrackInstances(HashSet<uint> selectedTracks)
    {
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (selectedTracks.Contains(kv.Key) || kv.Value == null)
            {
                continue;
            }

            if (logElseChainPlacement && kv.Value.activeSelf)
            {
                Debug.Log($"[HIDE] track={kv.Key} ({kv.Value.name}) hidden: not in this frame");
            }
            SceneObjectWriter.ApplyActive(kv.Value, false);
            // このフレームに居ない track の Random モーションは片付ける。Random イベント中にシークやキー送りで
            // 対象の居ない区間へ飛ぶと、モーションが Owned のまま評価されず、動画の一時停止のカウンタが 1 のまま
            // 残って以後 Random が二度と発火しなかった（2026-09-29 の監査）。
            StopRandomInteractiveMotionForAbsentTrack(kv.Key);
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
            // モデルが「表示しない」になっている track。配置するものが無いのでここで打ち切るが、
            // **走っている最中のモーションは片付ける。** 以前は下の TryApplyOwnedInteractiveMotion に
            // 到達しないまま Random イベントが残り、動画が止まったままになった（2026-09-25 の監査 D10）。
            StopInteractiveMotionForHiddenTrack(target.trackId);
            return;
        }

        SceneObjectWriter.ApplyActive(instance, true);
        // 脚の骨長合わせはインスタンスごとに一度だけ。モデルを切り替えると
        // TrackInstanceLifecycle がインスタンスを作り直すので、新しいモデルにも掛かる。
        TryApplyHumanBoneLengthCorrection(instance, target);
        Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
        rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole);

        // prefab が持っている向きの補正を右から掛ける。**配置は root の world 回転を
        // 上書きするので、これをしないと作者の補正が消える。**
        //
        // 2026-08-28: 06_DieselLocomotive が縦に立っていたのがこれ。prefab root の
        // X 軸 -90 度（長軸のローカル Y を -Z へ倒す）が SetPositionAndRotation で
        // 消え、長軸が world 上向きのままになっていた。
        // root に回転を持つ prefab は 75 個中 2 つだけ（06_DieselLocomotive と
        // 21_Donkey1.0）。残りは恒等なので合成しても何も変わらない。
        //
        // **21_Donkey1.0 は実機で見て確認すること。** 補正が復活するぶん向きが変わる。
        rotationPinhole = ApplyModelBaseRotation(instance, rotationPinhole);

        ObserveInteractiveMotionLiveTrackedSample(target.trackId, target, screen);
        if (!interactiveScheduleAfterPlacement)
        {
            UpdateInteractiveMotionSchedule(target.trackId, target, frame);
        }
        TryStopSystemTriggerOnVisibleFrame(target.trackId);

        if (TryApplyOwnedInteractiveMotion(target.trackId, instance, screen, frame))
        {
            return;
        }

        float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
        // 手動倍率は yaw と対で動く値なので、**同じ frame** で評価する。
        float manualScale = EvaluateManualScaleForFrame(target.trackId, frame);
        ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxHAdjusted, screen, manualScale);
        LogHipStageIfEnabled("1_placed", instance, target, screen);
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
        LogHipStageIfEnabled("2_skeleton", instance, target, screen);
        AlignModelBodyToAnchorDepthIfEnabled(instance, target);
        LogHipStageIfEnabled("3_bodyalign", instance, target, screen);

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
        // 発火の判定はこの tick の配置と表示位置の記録の後（interactiveScheduleAfterPlacement、2026-10-06）。起点・倍率・凍結姿勢が
        // この tick の値になる（前に置くと、再び現れた最初の tick・shot の切れ目の最初の tick で前の出番の値から始まる）。
        if (interactiveScheduleAfterPlacement)
        {
            UpdateInteractiveMotionSchedule(target.trackId, target, frame);
        }
        ApplyInteractiveHandoffBlendIfActive(target.trackId, instance, frame);
        LogPlacementMeasurementIfEnabled(target, instance, screen, frame);
        LogHorizontalPlacementIfEnabled(target, instance, screen, frame);
        LogAnimalBoneVsKeypointIfEnabled(target, instance, screen, frame);
        LogBoneVsKeypointIfEnabled(target, instance, screen, frame);
        DumpBoneWorldIfEnabled(target, instance, screen, frame);
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
        trackInstances.TryGetValue(trackId, out GameObject before);
        GameObject instance = TrackInstanceLifecycle.GetOrCreate(
            trackId,
            prefab,
            trackInstances,
            trackPrefabSources,
            lockedModelLocalScaleByTrack,
            ref selectedManualRotationTrackId);
        // 新しいインスタンスを作ったときだけ、実験ログに「この track にこのモデル」を残す。
        if (instance != null && !ReferenceEquals(instance, before))
        {
            LogExperimentModelAssigned(trackId, categoryId, prefab);
            ApplyAnimalBoneOverride(instance, categoryId, prefab);
        }
        return instance;
    }


    // 36_LabradorDog の脚の骨を 1 本上へ直す割り当て（fixLabradorLegMapping、2026-10-03）。2026-10-02 の A/B の d・e と同じ。
    // バッチの指定と違って先頭に "prefab|" を付けない（この経路は絞り込みを外さずに分割するので、付けると最初のキーが
    // "36_LabradorDog|frontLUpper" になり必ず失敗していた。2026-10-04 の査読で発見。表が先に当たるので既定では効いていなかった）。
    private const string LabradorLegMappingFixSpec =
        "frontLUpper=LeftShoulder01;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
        "frontRUpper=RightShoulder01;frontRLower=front_r_upper;frontRPaw=front_r_lower;" +
        "rearLUpper=LeftPelvis;rearLLower=rear_l_upper;rearLPaw=rear_l_lower;rearLToe=rear_l_paw;" +
        "rearRUpper=RightPelvis;rearRLower=rear_r_upper;rearRPaw=rear_r_lower;rearRToe=rear_r_paw";

    // 新しい animal インスタンスに脚の骨の割り当ての上書き（AnimalBoneMappingOverride）を付ける。優先順位:
    //   1. バッチの指定（batchmode のみ、-animalBoneOverride "prefab|key=value;..."。filter は部分一致なので完全な prefab 名を渡す）
    //   2. fixAnimalLegMapping（2026-10-04、MAP の監査）なら AnimalLegMappingFix.Table（prefab 名から数字の接頭辞を外して完全一致）
    //   3. fixLabradorLegMapping（10/03 の Labrador だけの試作。表の LabradorDog 行と同じ）
    // **組の骨名が 1 つでも無ければ組ごと使わない**（ResolveBone は上書きがあると正規名に戻らないので、前肢上・後肢上が null になると
    // IsAnimalRigReadyForSmalFk が偽になり SMAL FK が止まって keypoint 経路に落ちる。見た目が大きく変わる）。
    // リグのキャッシュ（AnimalPoseApplier.GetOrBuildAnimalRigCache）は最初の姿勢適用で bind を採るので、
    // それより前（インスタンスを作った直後）でないと効かない。キャッシュは Animator があればその Transform、
    // 無ければインスタンスの root から GetComponentInChildren で探すので、同じ位置に付ける。
    private void ApplyAnimalBoneOverride(GameObject instance, byte categoryId, GameObject prefab)
    {
        if (instance == null || !IsCategoryAnimal(categoryId))
        {
            return;
        }

        string spec = null;
        string source = null;
        if (Application.isBatchMode && !string.IsNullOrEmpty(batchAnimalBoneOverrideSpec))
        {
            spec = batchAnimalBoneOverrideSpec;
            source = "batch";
            int bar = spec.IndexOf('|');
            if (bar >= 0)
            {
                string filter = spec.Substring(0, bar);
                spec = spec.Substring(bar + 1);
                if (prefab == null || prefab.name.IndexOf(filter, System.StringComparison.Ordinal) < 0)
                {
                    spec = null;
                }
            }
        }

        if (spec == null && fixAnimalLegMapping && prefab != null &&
            AnimalLegMappingFix.Table.TryGetValue(AnimalLegMappingFix.Key(prefab.name), out string tableSpec))
        {
            spec = tableSpec;
            source = "table";
        }

        if (spec == null && fixLabradorLegMapping && prefab != null &&
            prefab.name.IndexOf("36_LabradorDog", System.StringComparison.Ordinal) >= 0)
        {
            spec = LabradorLegMappingFixSpec;
            source = "labrador";
        }

        if (string.IsNullOrEmpty(spec))
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        GameObject host = animator != null ? animator.gameObject : instance;

        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (Transform t in host.GetComponentsInChildren<Transform>(true))
        {
            names.Add(t.name);
        }

        // フィールドへの代入は AnimalLegMappingFix.TrySetOverrideField（switch）。反射は使わない（IL2CPP のストリップで黙って効かなくなる危険）。
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string pair in spec.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = pair.Substring(0, eq).Trim();
            string value = pair.Substring(eq + 1).Trim();
            if (!AnimalLegMappingFix.IsOverrideKey(key) || !names.Contains(value))
            {
                Debug.LogWarning($"[BONEOVERRIDE] {(prefab != null ? prefab.name : "?")}: '{key}={value}' が無いので脚の割り当ての修正を使わない（source={source}）");
                return;
            }

            pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        AnimalBoneMappingOverride ov = host.GetComponentInChildren<AnimalBoneMappingOverride>();
        if (ov == null)
        {
            ov = host.AddComponent<AnimalBoneMappingOverride>();
        }

        foreach (var kv in pairs)
        {
            AnimalLegMappingFix.TrySetOverrideField(ov, kv.Key, kv.Value);
        }

        Debug.Log($"[BONEOVERRIDE] {(prefab != null ? prefab.name : "?")} host={host.name} source={source} <- {spec}");
    }


    private GameObject ResolveTrackPrefab(uint trackId, byte categoryId)
    {
        // 永続化から復元した prefab 名は、ここで初めてカテゴリが確定するので index に直す。
        ResolvePendingModelSelection(trackId, ResolvePrefabsForCategory(categoryId));

        if (IsCategoryAnimal(categoryId))
        {
            // 実験の指示（チュートリアルは犬）があれば、ここで初めて index に解決する。
            ApplyExperimentPreferredAnimalModelOnce();
            return ResolvePrefabFromSelection(trackId, animalPrefabs, selectedAnimalIndex);
        }
        if (IsCategoryOther(categoryId))
        {
            return ResolvePrefabFromSelection(trackId, elsePrefabs, selectedElseIndex);
        }
        return ResolvePrefabFromSelection(trackId, humanPrefabs, selectedHumanIndex);
    }


    // 「置かない」なら null を返す。Clamp より前に見ること（-1 は 0 に丸められてしまう）。
    private GameObject ResolvePrefabFromSelection(uint trackId, GameObject[] prefabs, int defaultIndex)
    {
        int index = ResolveSelectedModelIndex(trackId, defaultIndex);
        if (IsHiddenModelIndex(index))
        {
            return null;
        }

        if (prefabs == null || prefabs.Length == 0)
        {
            return null;
        }

        return prefabs[Mathf.Clamp(index, 0, prefabs.Length - 1)];
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


    // 「この track にはモデルを置かない」を表す選択値。
    //
    // 一覧の index は 0 以上なので、負の値なら衝突しない。ResolveTrackPrefab が null を返し、
    // TrackInstanceLifecycle が既存インスタンスを片付ける。
    // 保存は prefab 名の代わりに HiddenModelName を書く（index はモデルの増減でずれるため）。
    public const int HiddenModelIndex = -1;
    public const string HiddenModelName = "(none)";

    private static bool IsHiddenModelIndex(int index)
    {
        return index == HiddenModelIndex;
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

    private static Quaternion ApplyModelBaseRotation(GameObject instance, Quaternion placementRotation)
    {
        ReplaceableModel model = instance != null ? instance.GetComponent<ReplaceableModel>() : null;
        if (model == null)
        {
            return placementRotation;
        }

        return placementRotation * model.baseLocalRotation;
    }


    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxHAdjusted, Transform screen, float manualScale)
    {
        if (instance == null)
        {
            return;
        }

        ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
        float modelHeight = model != null ? model.GetModelHeightMeters() : 0f;
        // **真値は player 側の manualScaleKeyframesByTrack。** ReplaceableModel.userScale は
        // モデルを変えるとインスタンスごと作り直されて既定へ戻るので、そこに貯めてはいけない。
        // コンポーネント側へは毎フレーム書き戻すだけ（[PLACE] ログが読むため）。
        float userScale = manualScale;
        if (model != null)
        {
            model.userScale = userScale;
        }
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
        bool lockingNow = lockScale && !lockedModelLocalScaleByTrack.ContainsKey(obj.trackId);
        int scaleReferenceFrame = GetCurrentPlaybackFrame();
        if (lockScale && TryResolveShotStartScaleReference(obj.trackId, out MetaObj shotStartObj))
        {
            scaleBBoxH = shotStartObj.bboxH;
            scaleAnchorZ = shotStartObj.anchorZ;
            scaleTargetHeightMeters = ComputeTargetHeightMeters(scaleBBoxH, scaleAnchorZ);
            scaleReferenceFrame = shotBoundaries.GetStartFrame(lastAppliedShotIndex);
        }

        // C4-T（animalScaleFollowApproachTrend）の倍率の基準フレーム。ロックした回だけ書く。
        if (lockingNow)
        {
            RecordScaleLockReferenceFrame(obj.trackId, scaleReferenceFrame);
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
            // 手動倍率は **ロックの外側**で掛ける。GetOrLockModelLocalScale は Human / Animal の
            // スケールを shot 先頭で凍らせるので、内側で掛けるとロックした時点の倍率が焼き付き、
            // 以後スライダーを動かしても shot 境界まで反映されない。
            //
            // ResolveDesiredLocalScale はこの分岐では userScale を掛けていない
            // （掛けているのは焦点距離が取れないフォールバック分岐だけ）ので、二重にならない。
            // C4-T の倍率も手動倍率と同じくロックの外側で掛ける。⑧ は手動倍率だけを割り戻すので、
            // この倍率で伸びたぶんは ⑧ が奥へ押し戻さない（接近を scale で追う狙いどおり）。
            Vector3 lockedOrDesired = lockScale ? GetOrLockModelLocalScale(obj.trackId, desiredScale) : desiredScale;
            float trendFactor = ResolveAnimalScaleTrendFactor(obj, GetCurrentPlaybackFrame(), lockedOrDesired.x);
            TrackPlacementWriter.ApplyLocalScaleWithGroundAlignment(
                instance.transform,
                lockedOrDesired * (userScale * trendFactor),
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
    // 平滑化を最後に進めた動画フレーム。tick ではなく動画フレームで刻むため。
    private readonly Dictionary<uint, int> smoothedDepthRatioFrameByTrack = new Dictionary<uint, int>();

    private float SmoothProjectedDepthRatio(uint trackId, float ratio, bool allowFastTrack = true, float tauOverride = -1f)
    {
        // tauOverride > 0: animal だけ時定数を替える（animalDepthRefineSmoothingSeconds、2026-10-03）。
        float tau = tauOverride > 0f ? tauOverride : Mathf.Max(0f, projectedDepthSmoothingSeconds);
        if (tau <= 0.0001f)
        {
            smoothedProjectedDepthRatioByTrack[trackId] = ratio;
            return ratio;
        }

        if (!smoothedProjectedDepthRatioByTrack.TryGetValue(trackId, out float previous))
        {
            // shot 先頭・モデル変更直後は平滑化せず、その場の値から始める。
            smoothedProjectedDepthRatioByTrack[trackId] = ratio;
            smoothedDepthRatioFrameByTrack[trackId] = GetCurrentPlaybackFrame();
            if (logPlacementMeasurement)
            {
                // 記録だけ（2026-10-09）: 平滑化の最初の値と、その時点で shot の頭の倍率の測り直しが済んでいたか。
                // 済む前の値から始まると、遅れて測り直しが通ったあと τ をかけて奥行きが動く（Docs/tmp/roster_20261009/README.md の 11）。
                Debug.Log($"[DEPTH8-INIT] shot={lastAppliedShotIndex} track={trackId} frame={GetCurrentPlaybackFrame()} ratio={ratio:F3} refined={scaleRefinedByTrack.Contains(trackId)}");
            }
            return ratio;
        }

        // **平滑化を動画フレームで刻む（fps 非依存にする）。**
        //
        // 元は毎 Update に `Time.deltaTime` で進めていた。深度そのものは
        // `投影高 ∝ 1/z` が厳密なので `z × ratio` の 1 手で目標に届くが、
        // **平滑化が tick 回数に依存する**ため結果が fps で変わっていた。
        // 実測（2026-09-09）: バッチ 15.5 tick/フレームでは収束するのに、
        // 実機 72Hz の 2.4 tick/フレームでは追いつかず、`ratio` が 1.17 前後で
        // 固定される（= モデルが常に 17% 大きい）。
        // 表示レートやコマ落ちで見え方が変わるのは、被験者実験では交絡要因になる
        // （Docs/experiment-flow.md）。
        //
        // 同じ動画フレームの間は値を進めず、前回の結果をそのまま返す。
        // 深度の適用は 1 手で厳密なので、同じ値を返せば何 tick 走っても同じ位置に落ちる。
        if (smoothDepthPerVideoFrame)
        {
            int frame = GetCurrentPlaybackFrame();
            if (!smoothedDepthRatioFrameByTrack.TryGetValue(trackId, out int lastFrame))
            {
                lastFrame = frame - 1;
            }

            int advanced = frame - lastFrame;

            // **巻き戻りを必ず拾う。**ループ（2166 → 0）やシークで負になる。
            // ここを「進んでいない」と同じ扱いにすると記録が更新されず、
            // 以後ずっと負のままになって平滑化が凍結する（2026-09-10 実機で発覚）。
            // 巻き戻ったら履歴は無意味なので、その場の比をそのまま採用する。
            if (advanced < 0)
            {
                smoothedDepthRatioFrameByTrack[trackId] = frame;
                smoothedProjectedDepthRatioByTrack[trackId] = ratio;
                return ratio;
            }

            if (advanced == 0)
            {
                return previous;
            }

            smoothedDepthRatioFrameByTrack[trackId] = frame;
            float videoFps = manifest != null && manifest.fps > 0.01f ? (float)manifest.fps : 30f;
            float dt = advanced / videoFps;
            float a = 1f - Mathf.Exp(-dt / tau);
            if (allowFastTrack)
            {
                float relErr = Mathf.Abs(ratio - previous) / Mathf.Max(0.05f, Mathf.Abs(previous));
                float loF = Mathf.Max(0f, depthRefineFastTrackLow);
                float hiF = Mathf.Max(loF + 0.001f, depthRefineFastTrackHigh);
                a = Mathf.Clamp01(a + (1f - a) * Mathf.Clamp01((relErr - loF) / (hiF - loF)));
            }
            float result = previous + a * (ratio - previous);
            smoothedProjectedDepthRatioByTrack[trackId] = result;
            return result;
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
        if (allowFastTrack)
        {
            float relativeError = Mathf.Abs(ratio - previous) / Mathf.Max(0.05f, Mathf.Abs(previous));
            float lo = Mathf.Max(0f, depthRefineFastTrackLow);
            float hi = Mathf.Max(lo + 0.001f, depthRefineFastTrackHigh);
            float boost = Mathf.Clamp01((relativeError - lo) / (hi - lo));
            alpha = Mathf.Clamp01(alpha + (1f - alpha) * boost);
        }

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
    // clippedBottomOverride: 全フレームを走査する呼び出し元（AnimalScaleTrend）が自分の状態で判定した値。null なら IsBBoxBottomClipped。
    private float ResolveUnclippedTargetHeight(MetaObj obj, float bboxH, bool? clippedBottomOverride = null)
    {
        if (!extendTargetHeightForClippedBBox || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return bboxH;
        }

        // 下端も上端も切れていないなら bbox 高がそのまま被写体の高さ。
        bool clippedTop = obj.bboxY <= 0;
        bool clippedBottom = clippedBottomOverride ?? IsBBoxBottomClipped(obj);
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

    // animalPlaceClippedFromFullBody（既定 OFF、2026-10-07、新しい振る舞い）: 体全体の像の高さで ⑧ の目標と shot 先頭の倍率の基準を上げる比（1 以上）。
    // 上端か下端が切れた animal のフレームだけ max(currentTargetH, 体全体の像の高さ) ÷ currentTargetH。それ以外（見切れていない・transl が無い・
    // Human / Else・フラグ OFF）は 1。上の外挿の方が大きいフレームも 1（bundle_animal の shot 1・20 は外挿が 1.6 倍の上限で、体全体の像は 1.39 倍・1.12 倍）。
    // 今より大きくなるのは、左右が切れて外挿を諦めていた犬の顔の寄り（shot 2・11・13）と、keypoints の横の幅に尾が入って外挿が小さく出ていた猫
    // （shot 23・24・25・27）。体全体の像にも外挿と同じ上限 maxClippedHeightExtrapolation × bboxH を掛ける（bundle_animal では最大 1.545 倍で当たらない）。
    // 上端の判定にはヒステリシスが無い。上端が 0 と数 px を行き来するフレームで目標が 1 フレームで跳ねる（⑧ に効く shot で 5% を超えるのは shot 23 の
    // 5 か所・25 の 2 か所。今の外挿でも同じ shot に同じ数の跳ねがあり、最大は同じ 20.8%）。⑧ の平滑（τ 1.2 s）を通る。
    private float ResolveAnimalFullBodyTargetGain(MetaObj obj, float bboxH, float currentTargetH)
    {
        if (!animalPlaceClippedFromFullBody || bboxH <= 0f || currentTargetH <= 0f || manifest == null || !IsCategoryAnimal(obj.categoryId))
        {
            return 1f;
        }

        bool clippedTop = obj.bboxY <= 0;
        bool clippedBottom = IsBBoxBottomClipped(obj);
        if (!clippedTop && !clippedBottom)
        {
            return 1f;
        }

        // 副選択: 左右が切れて上の外挿を諦めていたフレームだけ（ResolveUnclippedTargetHeight の横の判定と同じ式）。
        if (animalPlaceClippedFromFullBodyWidthClippedOnly &&
            obj.bboxX > 0 && obj.bboxX + obj.bboxW < manifest.eye_w && obj.bboxW > 0)
        {
            return 1f;
        }

        if (!TryResolveAnimalFullBodyHeightPixels(obj, out float fullBodyH))
        {
            return 1f;
        }

        fullBodyH = Mathf.Min(fullBodyH, bboxH * Mathf.Max(1f, maxClippedHeightExtrapolation));
        return fullBodyH > currentTargetH ? fullBodyH / currentTargetH : 1f;
    }

    // 測り直し（RefineLockedScaleFromProjectedBones）が倍率に入れた gain（1 より大きいときだけ記録）。⑧ はこれがある track だけ目標を上げる。
    // scaleRefineFactorByTrack と同じ寿命: モデルの差し替え・視聴距離の変更では持ち越し、shot 境界（偽のカットを除く）で捨てる（ResetPerShotTrackState）。
    private readonly Dictionary<uint, float> animalFullBodyGainByTrack = new Dictionary<uint, float>();

    // 体全体の像の高さ（eye px）。bbox は見えている部分だけだが（bundle 側の回答、Docs/bundle-placement.md の 2026-08-26）、meta.bin の
    // keypoints3d（根 = 関節 7 と 18 の中点からの相対、x 右・y 上・z 前。見切れた関節にも当てはめの座標が入る）と SMAL block の transl は
    // 画面の外まで体全体を持っている。transl は AniMer の全画面のカメラ（f_full = 5000 px、主点は画面の中心）の値なので（Docs/bundle-shared/D-017）、
    // 関節 i の像の縦の位置は y_i ÷ (tz + z_i) に比例する。根の奥行きを tz で代用する（transl はモデルの原点で根ではない。差は中央 0.24〜0.42 単位）。
    //   高さ = AnimalFullBodyHeightPerEyeWidth × eye_w × (y_i ÷ (tz + z_i) の最大 − 最小)
    // 係数 4.666（= 5972 px ÷ 1280 ≒ 5000 × 1.19。1.19 は関節の外側のシルエットのぶん）は、見切れていない 988 フレーム（犬 564・猫 424）の
    // bbox 高 ÷ (y_i ÷ (tz + z_i) の幅) の中央値。そこでの誤差は中央 7.6%・p90 16.7%（較正に使ったフレームそのもので測った値）。犬だけ・猫だけで
    // 較正すると 5591・6160 px。**犬の誤差は距離で偏る**（遠い shot は −7〜−15%、近い shot は +8〜+16%）。犬の顔の寄り（shot 2・11・13、tz 4.6〜5.7）は
    // 較正の外（見切れていない犬で tz < 8.9 は 2 フレームだけ）で、距離の傾向を伸ばすと +20% 前後だが、切れていない上側（根から bbox 上端）の
    // 当てはまりは逆向き（実測 ÷ 予測 1.18〜1.23）。符号は決まらず ±20% 程度と見る。採否は絵で決める。検証は scratchpad の fixwork3/A・A_verify。
    // 全関節を使う（TryGetJointSpan と同じ理由。見切れた関節こそ画面外の体を表す）。
    private const float AnimalFullBodyHeightPerEyeWidth = 4.666f;

    private bool TryResolveAnimalFullBodyHeightPixels(MetaObj obj, out float heightPixels)
    {
        heightPixels = 0f;
        if (!obj.hasSkeleton || obj.jointsCam == null || manifest == null || manifest.eye_w <= 0)
        {
            return false;
        }

        // obj はこの tick の表示用のフレーム（DisplayModelTick の displayMetadataFrame）から読んだもの。SMAL block も同じフレームで引く。
        int frame = GetPlaybackFrameSnapshot().displayMetadataFrame;
        if (!TryGetAnimalSmalPoseLoaded(frame, obj.trackId, out AnimalSmalPose smal) || !smal.hasTransl || smal.transl.z <= 0.0001f)
        {
            return false;
        }

        float tz = smal.transl.z;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        int used = 0;
        for (int i = 0; i < obj.jointsCam.Length; i++)
        {
            Vector3 j = obj.jointsCam[i];
            float depth = tz + j.z;
            if (float.IsNaN(j.y) || float.IsNaN(j.z) || depth <= tz * 0.2f)
            {
                continue;
            }

            float yOverDepth = j.y / depth;
            if (yOverDepth < minY) { minY = yOverDepth; }
            if (yOverDepth > maxY) { maxY = yOverDepth; }
            used++;
        }

        if (used < 6 || maxY <= minY)
        {
            return false;
        }

        heightPixels = AnimalFullBodyHeightPerEyeWidth * manifest.eye_w * (maxY - minY);
        return true;
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

    // 奥行きの上限。既定はスクリーンのすぐ手前（3 月の設計の名残。8/20 からスクリーンは背景描画なので描画上は不要）。
    // allowDepthBehindScreen（既定 OFF、2026-10-03）なら screenDist × depthBehindScreenMaxFactor まで許す。
    // ⑧・⑨・めり込み解消・最初の配置の 4 か所が同じ値を使う（片方だけ外すと人とボールの前後がずれる）。
    private float ResolveDepthUpperLimitMeters(float screenDist)
    {
        return allowDepthBehindScreen
            ? screenDist * Mathf.Max(1f, depthBehindScreenMaxFactor)
            : screenDist - 0.0001f;
    }

    // ⑧ が深度を解くときの「体の位置」。
    //
    // Humanoid の Hips を使う。[DEPTH9]（Else を人の骨格基準で置く段）も ref=Hips なので
    // 基準を揃えている。Hips が取れないモデル・カテゴリは root をそのまま返す（従来動作）。
    private Vector3 ResolveDepthReferenceWorld(GameObject instance)
    {
        if (instance == null)
        {
            return Vector3.zero;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator != null && animator.isHuman)
        {
            HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
            if (cache != null && cache.ready &&
                cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hips) && hips != null)
            {
                return hips.position;
            }
        }
        else if (animalDepthReferenceFromBody)
        {
            // 既定 OFF（2026-10-03）: Generic リグは投影に使うボーン（excludeUnweightedProjectionBones なら補助ボーン抜き）の重心。
            var bones = ResolveProjectionBones(instance, animator);
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].Value != null)
                {
                    sum += bones[i].Value.position;
                    n++;
                }
            }

            if (n > 0)
            {
                return sum / n;
            }
        }

        return instance.transform.position;
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

        if (!TryProjectBonesToEyeHeight(instance, screen, out _, out _, out float projectedH, out _, out _, useSilhouetteProjectionExtent) ||
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
        // animalPlaceClippedFromFullBody（既定 OFF）: shot 先頭の測り直しが体全体の gain を倍率に入れた track だけ、目標も体全体の像の高さとの大きい方へ
        // 上げる（ResolveAnimalFullBodyTargetGain）。倍率に入っていない track で上げると、増えた分を全部 ⑧ が奥行きで寄せる（shot 2 で最も近い骨が目から
        // 約 0.09 m）。shot の途中で gain が先頭より上がった分は ⑧ が寄せる（bundle_animal の shot 27 で先頭の gain より最大 +17%）。
        if (animalFullBodyGainByTrack.ContainsKey(obj.trackId))
        {
            targetH *= ResolveAnimalFullBodyTargetGain(obj, bboxH, targetH);
        }

        // 手動倍率は「自動フィットからわざとずらした量」なので、補正の対象から外す。
        // 割り戻さないと、ユーザーが 2 倍にしたぶんだけモデルを奥へ押しやって打ち消す
        // （投影高は距離に反比例するため、見かけは戻るが深度が壊れる）。
        float manualScale = Mathf.Max(0.01f, EvaluateManualScaleForFrame(obj.trackId, GetCurrentPlaybackFrame()));
        float ratio = projectedH / (targetH * manualScale);

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

        // **深度は root ではなく体の代表点で解く。**
        //
        // Person では ④ AlignModelBodyToAnchorDepthIfEnabled が「hips を anchorZ へ持ってくる」
        // ために root を体の手前へ置き去りにする。置き去りの量は anchorZ × lossyScale で、
        // **どちらも screenDist に比例するので screenDist の 2 乗で増える**
        // （2026-09-05 実測: 1.0m で 0.222m、3.0m で 2.451m）。
        //
        // root を「モデルの深度」として扱うと、順序クランプが root の深度（3.0m 設定で 0.408m）
        // を Else の配置深度（2.72m）と比べて「人が手前すぎる」と誤判定し、倍率を 6.4 まで
        // 押し上げていた。生の比のガード上限は 3.0 なので、この値は投影からは出ない。
        // 結果、剛体でぶら下がっている体が 2.86m → 5.30m へ押し出され、boneRatio が
        // 0.54 まで落ちていた（2.86 / 5.30 = 0.540 と一致）。
        //
        // 体の基準点が取れないケース（Animal は Generic リグで、そもそも ④ を通らないので
        // root と体がずれない）は従来どおり root で解く。
        Vector3 bodyWorld = ResolveDepthReferenceWorld(instance);
        Vector3 camLocal = Quaternion.Inverse(camRotation) * (bodyWorld - camOrigin);
        if (camLocal.z <= 0.0001f)
        {
            return false;
        }

        // 前後関係の制約は「深度が決まった後の硬いクランプ」ではなく、平滑化の手前で
        // ratio を許容範囲に丸める形で入れる。深度側でクランプすると発動フレームで
        // 一気に 135mm 動いて跳ねる（2026-08-20 実測、1 フレーム変化 max 22mm → 206mm）。
        ratio = ClampRatioPreservingOtherOrder(obj, camLocal.z, ratio);
        // animal は姿勢で骨格の投影高が急に変わるので、速追従（1 frame で奥行きが飛ぶ）は掛けない（2026-09-18）。
        bool isAnimal = IsCategoryAnimal(obj.categoryId);
        bool allowFastTrack = depthRefineFastTrackForAnimal || !isAnimal;
        float tauOverride = isAnimal && animalDepthRefineSmoothingSeconds > 0f ? animalDepthRefineSmoothingSeconds : -1f;
        ratio = SmoothProjectedDepthRatio(obj.trackId, ratio, allowFastTrack, tauOverride);

        float beforeZ = camLocal.z;
        float ratioZ = camLocal.z * ratio * Mathf.Max(0.1f, projectedDepthScaleK);
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        // スクリーンより手前に収める制約も**体**に掛ける。root に掛けると root が
        // screenDist で止まるだけで、体はそのぶん奥（3.0m 設定で 5.45m）に残る。
        // allowDepthBehindScreen（既定 OFF）なら全カテゴリでスクリーンより奥を許す。
        // animal だけ別に上限を指定することもできる（animalDepthRefineMaxDepthMeters、既定 0 = 従来どおりスクリーン）。
        float upperZ = ResolveDepthUpperLimitMeters(screenDist);
        if (isAnimal && animalDepthRefineMaxDepthMeters > upperZ)
        {
            upperZ = animalDepthRefineMaxDepthMeters;
        }

        float targetZ = Mathf.Clamp(
            ratioZ,
            Mathf.Max(0.001f, MinDistanceFromHeadMeters),
            upperZ);

        if (logDepthRefineStages)
        {
            float rootZ = (Quaternion.Inverse(camRotation) * (instance.transform.position - camOrigin)).z;
            Debug.Log(
                $"[DEPTH8] track={obj.trackId} anchorRaw01={obj.anchorZ01:F4} " +
                $"anchorZ={obj.anchorZ:F4} before={beforeZ:F4} " +
                $"ratio={ratio:F4} afterRatio={ratioZ:F4} final={targetZ:F4} " +
                $"screenMoved={(targetZ - ratioZ) * 1000f:F1}mm " +
                $"rootZ={rootZ:F4} bodyMinusRoot={(beforeZ - rootZ) * 1000f:+0.0;-0.0}mm");
        }

        if (Mathf.Abs(targetZ - camLocal.z) <= 0.0001f)
        {
            return false;
        }

        // **位置ベクトルを倍率で伸ばすのではなく、体の移動量ぶんだけ root を平行移動する。**
        // 伸ばす形が正しいのは「その位置に実体がある」ときだけで、体から離れた root に
        // 掛けると横方向にも飛ぶ。
        Vector3 bodyTarget = camLocal * (targetZ / camLocal.z);
        Vector3 deltaWorld = camRotation * (bodyTarget - camLocal);
        TrackPlacementWriter.Apply(
            instance.transform,
            TrackPlacementCommand.PositionOnly(
                instance.transform.position + deltaWorld,
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
            NoteScaleRefineSkip(obj, "noLock", 0f, bboxH);
            return;
        }

        if (!TryProjectBonesToEyeHeight(instance, screen, out _, out _, out float projectedH, out _, out _, useSilhouetteProjectionExtent) ||
            projectedH <= 0.0001f)
        {
            NoteScaleRefineSkip(obj, "projectFail", 0f, bboxH);
            return;
        }

        // 手動倍率が掛かっている track ではこの 1 回きりの測り直しをしない。
        //
        // 割り戻して measure すれば理屈は合うが、**投影高はスケールに厳密には比例しない**
        // （モデルは眼から 0.44〜0.77m と近く、2 倍にすると各ボーンの深度も動く）。
        // 実測では ×2 のとき boneRatio が 0.977 ではなく 1.071 に出て、補正が ×0.934 と
        // 逆向きに効き、スライダーの 2.00 が実際には 1.55 倍にしかならなかった（2026-08-28）。
        //
        // そもそもこの補正の目的は「自動フィットを bbox にぴったり合わせる」ことで、
        // 手動倍率はユーザーが**わざとそこからずらす**指定なので、両立させる意味がない。
        // 倍率 1.0 の track は従来どおり補正する。
        float manualScale = Mathf.Max(0.01f, EvaluateManualScaleForFrame(obj.trackId, GetCurrentPlaybackFrame()));
        if (Mathf.Abs(manualScale - ManualScaleDefault) > 0.001f)
        {
            NoteScaleRefineSkip(obj, "manualScale", 0f, bboxH);
            return;
        }

        // B1（refineLockedScaleAgainstSmoothedTarget、animal のみ、既定 OFF）: 基準を ±W frame で平滑した目標高にする。
        // ロックしたフレームの bbox が姿勢や検出で一時的に伸び縮みしていると、その値が shot の間ずっと残るため。
        float refineTargetH = bboxH;
        if (refineLockedScaleAgainstSmoothedTarget && IsCategoryAnimal(obj.categoryId) &&
            TryGetAnimalTrendSmoothedTargetHeight(obj.trackId, GetCurrentPlaybackFrame(), out float smoothedTargetH))
        {
            refineTargetH = smoothedTargetH;
        }

        float ratio = projectedH / refineTargetH;

        // bbox が画面端で切れている、検出が破綻している等でこの範囲を外れたら補正しない。
        // 誤った基準を焼き付けると shot の間ずっと残るため、疑わしいときは何もしない方が安全。
        //
        // 既定 OFF（2026-10-09、animalScaleRefineMinRatioFromDepthRefine、既存の欠陥の直し）: animal は下限を ⑧ の下限（depthRefineMinRatio）に揃える。
        // ⑧ は同じ理由で 2026-08-27 に下限を 0.4 → 0.2 に下げてあり、ここだけ 0.4 のまま残っていた。比が 0.2〜0.4 の間だと、ここは何もせず、
        // ⑧ が小さいモデルを目の前へ引き寄せて大きさを合わせる（04_Lion の shot 15: 目から 0.26 m に骨の広がり 8 cm、51 体の中央値は 0.83 m・17 cm）。
        // ② の倍率（bind 姿勢のメッシュの AABB で合わせる）で比がいつも 0.4 近くに出るモデル（Lion・Mink・EuropeanBadger・Racoon）で起きる
        // （Docs/tmp/roster_20261009/README.md の 11）。新しい定数は足さない。
        float minRefineRatio = MinProjectedBoneRatioForScaleRefine;
        if (animalScaleRefineMinRatioFromDepthRefine && IsCategoryAnimal(obj.categoryId))
        {
            minRefineRatio = Mathf.Min(minRefineRatio, depthRefineMinRatio);
        }

        if (ratio < minRefineRatio || ratio > MaxProjectedBoneRatioForScaleRefine)
        {
            NoteScaleRefineSkip(obj, ratio < minRefineRatio ? "ratioBelowMin" : "ratioAboveMax", ratio, bboxH);
            return;
        }

        // animalPlaceClippedFromFullBody（既定 OFF）: 見切れた animal は ⑧ と同じ gain で基準を大きくし、倍率に入れる。上のガードは測った比に掛け、gain は
        // 通った後に入れる（基準を大きくしただけで「測定が疑わしい」にしない。shot 25・27 の先頭は測った比 0.46〜0.52 で、gain 1.2〜1.4 を先に割ると
        // 0.4 を割り、測り直しが毎 tick 止まる）。入れた gain は ⑧ のために覚える。gain はこの測り直しが走った 1 フレームの値（ふつうは shot の先頭。
        // コマ落ち・シークで別のフレームになると、shot 23 なら 1.035 が 1.074 に変わる）。どのフレームの値だったかを [FULLBODY] に出す。
        if (animalPlaceClippedFromFullBody && IsCategoryAnimal(obj.categoryId))
        {
            float fullBodyGain = ResolveAnimalFullBodyTargetGain(obj, bboxH, ResolveUnclippedTargetHeight(obj, bboxH));
            if (fullBodyGain > 1f)
            {
                Debug.Log($"[FULLBODY] track={obj.trackId} frame={GetPlaybackFrameSnapshot().displayMetadataFrame} gain={fullBodyGain:F4} " +
                          $"boneRatio={ratio:F4} -> {ratio / fullBodyGain:F4} lockedScale={locked.x:F5}");
                ratio /= fullBodyGain;
                animalFullBodyGainByTrack[obj.trackId] = fullBodyGain;
            }
        }

        // ratio を projectedBoneRatioTarget に合わせる（既定 1.0 = bbox ぴったり）。
        float factor = Mathf.Max(0.1f, projectedBoneRatioTarget) / ratio;
        Vector3 refined = locked * factor;
        lockedModelLocalScaleByTrack[obj.trackId] = refined;
        scaleRefinedByTrack.Add(obj.trackId);
        NoteScaleRefinePassed(obj, ratio);
        // モデルを替えて再ロックしたときに掛け直すため、倍率そのものを覚えておく。
        scaleRefineFactorByTrack[obj.trackId] = factor;
        trackPrefabSources.TryGetValue(obj.trackId, out GameObject refinedPrefab);
        scaleRefineFactorPrefabByTrack[obj.trackId] = refinedPrefab;

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


    // shot の頭の倍率の測り直しが通らなかった記録（2026-10-09、記録だけ・振る舞いは変えない。logPlacementMeasurement のときだけ）。
    // 測り直しは比が 0.4〜3.0 の外だと何もせずに戻り、次の tick でまたやり直す。shot の間ずっと外なら ② の倍率のまま残る（今の既定で 8 体・15 shot、
    // Docs/tmp/roster_20261009/README.md の 11）。[SCALEFIX] は通ったときしか出ないので、外れた比と理由をここで残す:
    //   [SCALEFIX-SKIP]  shot・track・理由の組ごとに最初の 1 回（毎 tick 出すとログが埋まる）
    //   [SCALEFIX-AT]    通ったとき毎回: フレームと、それまでに外れた tick 数・比の幅
    //   [SCALEFIX-NEVER] shot の境界で、前の shot で一度も通らなかった track
    private struct ScaleRefineSkipNote
    {
        public int shot;
        public int ticks;
        public float minRatio;
        public float maxRatio;
        public int firstFrame;
        public int lastFrame;
        public string reasons;
    }

    private readonly Dictionary<uint, ScaleRefineSkipNote> scaleRefineSkipNotes = new Dictionary<uint, ScaleRefineSkipNote>();
    private readonly HashSet<string> scaleRefineSkipLogged = new HashSet<string>();

    private void NoteScaleRefineSkip(MetaObj obj, string reason, float ratio, float bboxH)
    {
        if (!logPlacementMeasurement)
        {
            return;
        }

        int frame = GetPlaybackFrameSnapshot().displayMetadataFrame;
        if (!scaleRefineSkipNotes.TryGetValue(obj.trackId, out ScaleRefineSkipNote note) || note.shot != lastAppliedShotIndex)
        {
            note = new ScaleRefineSkipNote { shot = lastAppliedShotIndex, minRatio = float.MaxValue, maxRatio = float.MinValue, firstFrame = frame, reasons = "" };
        }

        note.ticks++;
        note.lastFrame = frame;
        if (ratio > 0f)
        {
            note.minRatio = Mathf.Min(note.minRatio, ratio);
            note.maxRatio = Mathf.Max(note.maxRatio, ratio);
        }

        if (note.reasons.IndexOf(reason, System.StringComparison.Ordinal) < 0)
        {
            note.reasons = note.reasons.Length == 0 ? reason : note.reasons + "," + reason;
        }

        scaleRefineSkipNotes[obj.trackId] = note;
        if (scaleRefineSkipLogged.Add(lastAppliedShotIndex + ":" + obj.trackId + ":" + reason))
        {
            Debug.Log($"[SCALEFIX-SKIP] shot={lastAppliedShotIndex} track={obj.trackId} frame={frame} reason={reason} boneRatio={ratio:F3} bboxH={bboxH:F0}");
        }
    }

    private void NoteScaleRefinePassed(MetaObj obj, float ratio)
    {
        if (!logPlacementMeasurement)
        {
            return;
        }

        int frame = GetPlaybackFrameSnapshot().displayMetadataFrame;
        bool skipped = scaleRefineSkipNotes.TryGetValue(obj.trackId, out ScaleRefineSkipNote note) && note.shot == lastAppliedShotIndex;
        Debug.Log($"[SCALEFIX-AT] shot={lastAppliedShotIndex} track={obj.trackId} frame={frame} boneRatio={ratio:F3} " +
                  (skipped
                      ? $"skippedTicks={note.ticks} skippedFrames={note.firstFrame}-{note.lastFrame} skippedRatio={FormatSkipRatio(note)} reasons={note.reasons}"
                      : "skippedTicks=0"));
        scaleRefineSkipNotes.Remove(obj.trackId);
    }

    // shot の境界で呼ぶ（SyncShotBoundaryForFrame）。前の shot で一度も通らなかった track を出して消す。
    private void LogScaleRefineNeverPassedAndClear(int previousShotIndex)
    {
        if (logPlacementMeasurement)
        {
            foreach (KeyValuePair<uint, ScaleRefineSkipNote> kv in scaleRefineSkipNotes)
            {
                if (kv.Value.shot == previousShotIndex && !scaleRefinedByTrack.Contains(kv.Key))
                {
                    Debug.Log($"[SCALEFIX-NEVER] shot={kv.Value.shot} track={kv.Key} ticks={kv.Value.ticks} frames={kv.Value.firstFrame}-{kv.Value.lastFrame} " +
                              $"ratio={FormatSkipRatio(kv.Value)} reasons={kv.Value.reasons}");
                }
            }
        }

        scaleRefineSkipNotes.Clear();
    }

    private static string FormatSkipRatio(ScaleRefineSkipNote note)
    {
        return note.maxRatio >= note.minRatio ? $"{note.minRatio:F3}-{note.maxRatio:F3}" : "n/a";
    }

    private Vector3 GetOrLockModelLocalScale(uint trackId, Vector3 desiredLocalScale)
    {
        if (lockedModelLocalScaleByTrack.TryGetValue(trackId, out Vector3 lockedScale))
        {
            return lockedScale;
        }

        // 補正倍率が残っていれば、それは**同じ shot の同じ track**で一度測った結果。
        // モデルを替えただけで測り直すと、差し替えた瞬間の姿勢が焼き込まれて大きさが跳ねる
        // （2026-08-31 実測: 同一モデルへの差し替えでも 15% 縮んだ）。掛け直して確定させる。
        // 2026-10-10: 持ち越すのは同じ prefab のときだけ。別のモデルに替えたときに前のモデルの倍率を掛けると、動物では
        // 大きさと奥行きが崩れた（被験者は試行中にモデルを替えられる）。別のモデルなら捨てて、下の測り直しに回す。
        if (scaleRefineFactorByTrack.TryGetValue(trackId, out float carriedFactor) &&
            (!trackPrefabSources.TryGetValue(trackId, out GameObject currentPrefab) ||
             !scaleRefineFactorPrefabByTrack.TryGetValue(trackId, out GameObject factorPrefab) || factorPrefab != currentPrefab))
        {
            scaleRefineFactorByTrack.Remove(trackId);
            scaleRefineFactorPrefabByTrack.Remove(trackId);
            if (logPlacementMeasurement)
            {
                Debug.Log($"[SCALEFIX] track={trackId} 別のモデルに替えたので補正倍率 x{carriedFactor:F3} を捨てて測り直す");
            }
        }

        if (scaleRefineFactorByTrack.TryGetValue(trackId, out carriedFactor))
        {
            Vector3 carried = desiredLocalScale * carriedFactor;
            scaleRefinedByTrack.Add(trackId);
            lockedModelLocalScaleByTrack[trackId] = carried;
            if (logPlacementMeasurement)
            {
                Debug.Log($"[SCALEFIX] track={trackId} 補正倍率 x{carriedFactor:F3} を持ち越し（測り直さない）");
            }

            return carried;
        }

        // 倍率が無い = shot 境界を越えた直後。ここで初めて FK 後の実測補正を通す。
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


    // 下端が画面の下端で切れているかの track ごとの状態（bboxBottomClipHysteresis）。shot の切れ目で消す（ResetPerShotTrackState）。
    private readonly Dictionary<uint, bool> bboxBottomClippedByTrack = new Dictionary<uint, bool>();

    // 下端が画面の下端で切れているか。⑦（FitDisplayedModelToBBox）と ⑧（ResolveUnclippedTargetHeight）がこの 1 つの判定を使う（2026-10-06）。
    // 同じ tick に何度呼んでも結果は変わらない（抜ける側の余裕を入る側以上にしているので、同じ下端なら状態が動かない）。
    private bool IsBBoxBottomClipped(MetaObj obj)
    {
        float bottom = obj.bboxY + obj.bboxH;
        if (!bboxBottomClipHysteresis)
        {
            return bottom >= manifest.eye_h;
        }

        bool wasClipped = bboxBottomClippedByTrack.TryGetValue(obj.trackId, out bool previous) && previous;
        bool clipped = ResolveBBoxBottomClipState(wasClipped, bottom);
        bboxBottomClippedByTrack[obj.trackId] = clipped;
        return clipped;
    }

    // 状態を書かない判定（全フレームを走査する AnimalScaleTrend は自分の状態を持ってこれを呼ぶ）。
    private bool ResolveBBoxBottomClipState(bool wasClipped, float bboxBottom)
    {
        if (!bboxBottomClipHysteresis)
        {
            return bboxBottom >= manifest.eye_h;
        }

        int enter = Mathf.Max(0, bboxBottomClipEnterMarginPixels);
        int leave = Mathf.Max(enter, bboxBottomClipLeaveMarginPixels);
        return bboxBottom >= manifest.eye_h - (wasClipped ? leave : enter);
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
        string bottomBoneName = null;
        if (TryProjectBonesToEyeHeight(instance, screen, out _, out float boneBottomV, out _, out _, out bottomBoneName, useSilhouetteProjectionExtent))
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
        bool clippedBottom = IsBBoxBottomClipped(obj);
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
            LogBottomFixDepthIfEnabled(instance, obj, screen, null, projectedTopV, obj.bboxY, depthMeters, true);
            AlignProjectedModelBottomToBBox(instance.transform, screen, projectedTopV, depthMeters, obj.bboxY);
            return;
        }

        // depthMeters はモデル AABB 中心の深度で、anchorZ とは 3〜4% ずれる。
        // ここは「投影した下端を bbox 下端に一致させる」処理なので、投影に使ったのと同じ
        // 深度（depthMeters）で逆算するのが正しい。anchorZ を混ぜてはいけない。
        float targetBottomV = ResolveReliableBBoxBottomVEye(obj);
        LogBottomFixDepthIfEnabled(instance, obj, screen, bottomBoneName, bottomV, targetBottomV, depthMeters, false);
        // 既定 OFF（2026-10-09、animalBottomFitUsesBoneDepth、既存の欠陥の直し）: animal は、一番下の骨で合わせるとき px → m の換算をその骨自身の深さで行う。
        // 骨はその骨の深さで投影している（v = (cy − y·fy/z·0.5)·eye_h、カメラの上方向へ動かしても z は変わらない）ので、AABB の中心の深さで割り戻すと
        // 移動量が「AABB の深さ ÷ 骨の深さ」倍にずれる。伏せた 31_GrayWolf の鼻先（目から 0.29 m、AABB の中心 0.73 m）で 2.5 倍行き過ぎ、⑧ が奥行きの
        // 上限で止まって 2 回目の ⑦ が走らない tick では、体が 0.5 m 浮いたまま残る。⑧ に目の前へ寄せられた 06_AmericanMink では唇の骨が目から 3 mm で
        // 50 倍になり、体が頭の上へ飛ぶ（Docs/tmp/roster_20261009/README.md の 11-4）。骨が取れない（AABB の下端で合わせる）ときは今のまま。
        float alignDepth = depthMeters;
        if (animalBottomFitUsesBoneDepth && IsCategoryAnimal(obj.categoryId) &&
            TryResolveProjectionBoneDepth(instance, screen, bottomBoneName, out float bottomBoneDepth))
        {
            alignDepth = bottomBoneDepth;
        }

        AlignProjectedModelBottomToBBox(instance.transform, screen, bottomV, alignDepth, targetBottomV);
    }

    // 投影に使う骨（ResolveProjectionBones）のうち、名前が一致する骨のカメラ空間の深さ。投影できる深さ（0.001 m より奥）のときだけ true。
    private bool TryResolveProjectionBoneDepth(GameObject instance, Transform screen, string boneName, out float depth)
    {
        depth = 0f;
        if (instance == null || string.IsNullOrEmpty(boneName) || !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        var bones = ResolveProjectionBones(instance, animator);
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Value != null && bones[i].Key == boneName)
            {
                depth = (Quaternion.Inverse(camRotation) * (bones[i].Value.position - camOrigin)).z;
                return depth > 0.001f;
            }
        }

        return false;
    }

    // 記録だけ（2026-10-09、logPlacementMeasurement、animal のみ）: ⑦ は縦の移動量を depthMeters（レンダラーの AABB の中心の深さ）で px → m に
    // 直すが、合わせる下端は一番下の骨の投影（骨はその骨自身の深さで投影している）。骨の深さが AABB の中心と大きく違うと、移動量がその比だけ
    // ずれる疑い（31_GrayWolf の shot 6 の浮き、Docs/tmp/roster_20261009/README.md の 11-3）。骨・その深さ・AABB の深さ・比・px の差を出す。
    // 上端合わせ（下端が切れているとき）はメッシュの投影上端なので bone=meshTop。
    private void LogBottomFixDepthIfEnabled(GameObject instance, MetaObj obj, Transform screen, string boneName, float projectedV, float targetV,
        float aabbDepth, bool topAligned)
    {
        if (!logPlacementMeasurement || !IsCategoryAnimal(obj.categoryId))
        {
            return;
        }

        float boneDepth = -1f;
        if (!topAligned && !TryResolveProjectionBoneDepth(instance, screen, boneName, out boneDepth))
        {
            boneDepth = -1f;
        }

        Debug.Log($"[BOTTOMFIX-DEPTH] f={GetPlaybackFrameSnapshot().displayMetadataFrame} track={obj.trackId} {(topAligned ? "top" : "bottom")} " +
                  $"bone={(topAligned ? "meshTop" : boneName)} boneZ={boneDepth:F3} aabbZ={aabbDepth:F3} " +
                  $"k={(boneDepth > 0.0001f ? aabbDepth / boneDepth : 0f):F2} deltaV={targetV - projectedV:F1} root={instance.transform.position.y:F3}");
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
