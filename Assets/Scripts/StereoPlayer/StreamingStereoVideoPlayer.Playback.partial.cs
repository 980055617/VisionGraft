using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: meta frame cache, track instance state, manifest/screen helpers, manual-yaw/diagnostics partials
    // Provides: FollowTick pipeline, target selection, replaceable model apply, TryApplySkeleton entry

    private void PlaceOrMoveTestModel(PickResult pick)
    {
        TrySpawnOrMoveTestModel(pick);
    }


    public void FollowTick()
    {
        if (useMetaFollow && metaLoaded)
        {
            FollowTickMeta();
            return;
        }

        if (!enableFollow || !hasPickedPixel || spawnedTestModel == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        Transform screen = leftScreen;
        if (screen == null)
        {
            return;
        }

        float t = vp != null ? (float)vp.time : Time.time;
        int du = Mathf.RoundToInt(Mathf.Sin(t * followSpeed) * followAmplitudePixels);
        int dv = Mathf.RoundToInt(Mathf.Cos(t * followSpeed) * followAmplitudePixels);
        int u2 = Mathf.Clamp(pickedPixel.x + du, 0, manifest.eye_w - 1);
        int v2 = Mathf.Clamp(pickedPixel.y + dv, 0, manifest.eye_h - 1);

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u2, v2, screen, manifest.eye_w, manifest.eye_h, 0f);
        Vector3 world = worldOnPlane + screen.forward * markerOffset;
        Quaternion rotation = screen.rotation;

        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, world, rotation);

        LogFollow($"FollowTick: base=({pickedPixel.x},{pickedPixel.y}) offset=({du},{dv}) pixel=({u2},{v2}) world={world}");
    }


    private void FollowTickMeta()
    {
        if (!HasAnyReplacePrefabConfigured() && spawnedTestModel == null)
        {
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (debugDrawJoints || debugDrawAnchor || debugDrawSkeletonLines3D || debugDrawBoneAxisCompare)
        {
            debugDrawStateByTrack.Clear();
        }
        if (debugDrawMeta2D)
        {
            meta2DOverlayItems.Clear();
        }
        if (debugDrawJoints2D)
        {
            joints2DOverlayPoints.Clear();
        }

        LogResolvedManifestOnce();
        int displayedFrame = lastFrameReadyFrame;
        int frame = GetCurrentFrameIndex();
        int metaFrameUsed = useFrameReadySync ? displayedFrame : frame;
        if (ShouldEmitRigDiag(metaFrameUsed, 0u) && TryConsumeDiagBudget(metaFrameUsed))
        {
            Debug.Log(
                $"FRAME_SYNC frame={metaFrameUsed} videoFrame={frame} metaFrame={metaFrameUsed} cloudFrame={metaFrameUsed} modelFrame={metaFrameUsed}");
        }

        if (!TryReadFrameObjects(metaFrameUsed, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        frame = metaFrameUsed;
        UpdateMetaRange(frame);
        LogMeta2DFrameSummaryOnce(frame);

        if (TryApplyConfiguredTrackPrefabs(frame))
        {
            BuildJoints2DOverlayAndLog(frame);
            return;
        }

        // Intentional: per-frame meta summary suppressed in category-only logger.

        MetaObj target = metaFrameObjects[0];
        if (followTrackId >= 0)
        {
            bool found = false;
            for (int i = 0; i < metaFrameObjects.Count; i++)
            {
                if (metaFrameObjects[i].trackId == (uint)followTrackId)
                {
                    target = metaFrameObjects[i];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }
        }
        else
        {
            target = SelectAutoFollowTarget(metaFrameObjects);
            followTrackId = (int)target.trackId;
            hasPickedPixel = true;
            pickedPixel = new Vector2Int(target.anchorU, target.anchorV);

            if (followTrackId != lastAutoTrackId)
            {
                lastAutoTrackId = followTrackId;
            }
        }

        ApplyMetaTarget(target, frame);
        BuildJoints2DOverlayAndLog(frame);
    }


    private bool HasAnyReplacePrefabConfigured()
    {
        if (replacePrefab != null)
        {
            return true;
        }

        if (!useTrackPrefabOverrides)
        {
            return false;
        }

        return track0Prefab != null || track1Prefab != null;
    }


    private bool TryApplyConfiguredTrackPrefabs(int frame)
    {
        if (!useTrackPrefabOverrides || (!HasAnyReplacePrefabConfigured()))
        {
            return false;
        }

        bool foundTrack0 = TryApplyTargetByTrackId(0u, frame);
        bool foundTrack1 = TryApplyTargetByTrackId(1u, frame);
        bool appliedAny = foundTrack0 || foundTrack1;
        if (!appliedAny)
        {
            return false;
        }

        if (!foundTrack0 && trackInstances.TryGetValue(0u, out GameObject track0Instance) && track0Instance != null)
        {
            track0Instance.SetActive(false);
        }

        if (!foundTrack1 && trackInstances.TryGetValue(1u, out GameObject track1Instance) && track1Instance != null)
        {
            track1Instance.SetActive(false);
        }

        return true;
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
        if (!ResolveAnchorToScreen(target.anchorU, out Transform screen, out int uEye, out bool isRightEye))
        {
            return;
        }
        pickedScreen = screen;
        CaptureMeta2DOverlay(target, screen, isRightEye, uEye);

        float uMeta = uEye;
        float vMeta = target.anchorV;
        float uEyeF = uMeta;
        float vEyeF = vMeta;

        int cropW = GetCropW();
        int cropH = GetCropH();
        bool hasCrop = cropW > 0 && cropH > 0;
        bool outOfCrop = false;
        if (hasCrop)
        {
            int cropX = GetCropX();
            int cropY = GetCropY();
            uEyeF = uMeta - cropX;
            vEyeF = vMeta - cropY;
            if (uEyeF < 0f || vEyeF < 0f || uEyeF >= cropW || vEyeF >= cropH)
            {
                outOfCrop = true;
            }
            uEyeF = Mathf.Clamp(uEyeF, 0f, cropW - 1f);
            vEyeF = Mathf.Clamp(vEyeF, 0f, cropH - 1f);
        }
        else
        {
            uEyeF = Mathf.Clamp(uEyeF, 0f, manifest.eye_w - 1f);
            vEyeF = Mathf.Clamp(vEyeF, 0f, manifest.eye_h - 1f);
        }

        if (outOfCrop)
        {
            if (!outOfCropLoggedTracks.Contains(target.trackId))
            {
                outOfCropLoggedTracks.Add(target.trackId);
                Log(LogCategory.META_RANGE,
                    $"CROP_SKIP f={frame} t={target.trackId} uMeta={uMeta:F1} vMeta={vMeta:F1} crop_y0={GetCropY()} crop_h={cropH}",
                    frame, (int)target.trackId);
            }
            return;
        }

        float bboxWAdjusted = target.bboxW;
        float bboxHAdjusted = target.bboxH;

        if (ShouldLogBoneDetails(frame, (int)target.trackId))
        {
            LogBoneDetails(target, frame);
        }

        DebugScreenPinholeConsistency(screen, uEyeF, vEyeF, frame, (int)target.trackId);
        Vector3 anchorWorld = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);
        if (debugDrawAnchor)
        {
            DebugDrawTrackState state = GetOrCreateDebugDrawTrackState(target.trackId);
            state.hasAnchor = true;
            state.anchorWorld = anchorWorld;
        }

        GameObject instance = GetOrCreateTrackInstance(target.trackId, target.categoryId);
        if (instance != null)
        {
            instance.SetActive(true);
            Camera viewCam = GetViewCamera() ?? Camera.main;
            Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
            rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole, screen != null ? screen.up : Vector3.up);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, screen, frame);
            TryApplySkeleton(instance, target, instance.transform.position, screen, frame);
            float bboxWorldH = 0f;
            if (TryGetFocalLengths(out _, out float fy))
            {
                bboxWorldH = (2f * bboxHAdjusted / manifest.eye_h) * (target.anchorZ / fy);
            }
            UpdateAnchorDebugCubes(screen, uEyeF, vEyeF, anchorWorld, viewCam, bboxWorldH);
            LogReprojectionError(target.trackId, uEyeF, vEyeF, target.anchorZ, anchorWorld, viewCam, frame);

            Log(LogCategory.FOLLOW,
                $"f={frame} t={target.trackId} anchor=({target.anchorU},{target.anchorV}) uEye={uEyeF:F2} vEye={vEyeF:F2} screen={(isRightEye ? "R" : "L")} z={target.anchorZ:F3} pos=({anchorWorld.x:F3},{anchorWorld.y:F3},{anchorWorld.z:F3})",
                frame, (int)target.trackId);
            return;
        }

        if (spawnedTestModel == null)
        {
            LogJointDebugSkip("no_track_instance_and_no_test_model", frame, target.trackId);
            return;
        }

        LogJointDebugSkip("no_track_instance_use_test_model_path", frame, target.trackId);

        Camera viewCamFallback = GetViewCamera() ?? Camera.main;
        Quaternion rotation = GetPinholeBasisRotation(screen);

        spawnedTestModel.transform.SetPositionAndRotation(anchorWorld, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, anchorWorld, rotation);
        float bboxWorldHTest = 0f;
        if (TryGetFocalLengths(out _, out float fyTest))
        {
            bboxWorldHTest = (2f * bboxHAdjusted / manifest.eye_h) * (target.anchorZ / fyTest);
        }
        UpdateAnchorDebugCubes(screen, uEyeF, vEyeF, anchorWorld, viewCamFallback, bboxWorldHTest);
        LogReprojectionError(target.trackId, uEyeF, vEyeF, target.anchorZ, anchorWorld, viewCamFallback, frame);

        Log(LogCategory.FOLLOW,
            $"f={frame} t={target.trackId} anchor=({target.anchorU},{target.anchorV}) uEye={uEyeF:F2} vEye={vEyeF:F2} screen={(isRightEye ? "R" : "L")} z={target.anchorZ:F3} pos=({anchorWorld.x:F3},{anchorWorld.y:F3},{anchorWorld.z:F3})",
            frame, (int)target.trackId);
    }


    private MetaObj SelectAutoFollowTarget(List<MetaObj> objs)
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

        if (verboseLog)
        {
            LogMeta($"Meta auto-select: track={best.trackId} anchor=({best.anchorU},{best.anchorV}) distSq={bestDistSq:F1} leftCenter={leftCenterU:F1} rightCenter={rightCenterU:F1}");
        }

        return best;
    }


    private GameObject GetOrCreateTrackInstance(uint trackId, byte categoryId)
    {
        GameObject prefab = ResolveTrackPrefab(trackId, categoryId);
        if (prefab == null)
        {
            return null;
        }

        if (trackInstances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            if (trackPrefabSources.TryGetValue(trackId, out GameObject source) && source != prefab)
            {
                Destroy(existing);
                trackInstances.Remove(trackId);
                trackPrefabSources.Remove(trackId);
            }
            else
            {
                if (selectedManualRotationTrackId < 0)
                {
                    selectedManualRotationTrackId = (int)trackId;
                }
                return existing;
            }
        }

        GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity);
        instance.name = $"Track_{trackId}";
        if (instance.GetComponent<ReplaceableModel>() == null)
        {
            instance.AddComponent<ReplaceableModel>();
        }

        trackInstances[trackId] = instance;
        trackPrefabSources[trackId] = prefab;
        if (selectedManualRotationTrackId < 0)
        {
            selectedManualRotationTrackId = (int)trackId;
        }
        return instance;
    }


    private GameObject ResolveTrackPrefab(uint trackId, byte categoryId)
    {
        if (useTrackPrefabOverrides)
        {
            if (trackId == 0u && track0Prefab != null)
            {
                return track0Prefab;
            }

            if (trackId == 1u && track1Prefab != null)
            {
                return track1Prefab;
            }
        }

        if (replacePrefab != null)
        {
            return replacePrefab;
        }

        return null;
    }


    private float ComputeTargetHeightMeters(float bboxH, float zMeters)
    {
        if (manifest == null || manifest.eye_h <= 0 || bboxH == 0)
        {
            return 0f;
        }

        if (!TryGetFocalLengths(out _, out float fy))
        {
            return 0f;
        }

        return (2f * bboxH / (float)manifest.eye_h) * (zMeters / fy);
    }


    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxWAdjusted, float bboxHAdjusted, Transform screen, int frame)
    {
        if (instance == null)
        {
            return;
        }

        ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
        float modelHeight = model != null ? model.GetModelHeightMeters() : 0f;
        float userScale = model != null ? model.userScale : 1f;
        float targetUniform = modelHeight > 0f && targetHeightMeters > 0f
            ? (targetHeightMeters / modelHeight) * userScale
            : userScale;
        Vector3 baseScale = model != null ? model.baseLocalScale : Vector3.one;

        instance.transform.SetPositionAndRotation(world, rotation);

        Vector3 pivotOffset = Vector3.zero;
        if (model != null && model.anchor != null)
        {
            Vector3 anchorWorld = model.anchor.position;
            Vector3 rootWorld = instance.transform.position;
            Vector3 delta = anchorWorld - rootWorld;
            pivotOffset = delta;
            instance.transform.position = world - delta;
        }

        if (verboseLog)
        {
            if (TryGetFocalLengths(out float fx, out float fy))
            {
                float xNdc = (uEye / (float)manifest.eye_w - 0.5f) * 2f;
                float yNdc = (0.5f - vEye / (float)manifest.eye_h) * 2f;
                float x = xNdc * obj.anchorZ / fx;
                float y = yNdc * obj.anchorZ / fy;
                // Intentional: pinhole detail logs suppressed in category-only logger.
            }

            Debug.DrawLine(world, world + rotation * Vector3.forward * 0.2f, Color.cyan, 0.05f);
        }

        if (TryGetFocalLengths(out float fxScale, out float fyScale))
        {
            float bboxWorldW = (2f * bboxWAdjusted / manifest.eye_w) * (obj.anchorZ / fxScale);
            float bboxWorldH = (2f * bboxHAdjusted / manifest.eye_h) * (obj.anchorZ / fyScale);
            Vector2 baseBounds = model != null ? model.baseBoundsSize : Vector2.zero;
            float scaleW = baseBounds.x > 0f ? bboxWorldW / baseBounds.x : targetUniform;
            float scaleH = baseBounds.y > 0f ? bboxWorldH / baseBounds.y : targetUniform;
            float uniformScale = obj.categoryId == 2 ? Mathf.Min(scaleW, scaleH) : scaleH;
            instance.transform.localScale = baseScale * uniformScale;
            Vector3 lossy = instance.transform.lossyScale;
            string pivotInfo = model != null && model.anchor != null ? $" pivotOffset={pivotOffset}" : string.Empty;
            if (model != null && model.anchor == null && model.alignToGround)
            {
                float offsetWorld = model.baseBottomOffsetLocal * lossy.y;
                instance.transform.position += instance.transform.up * offsetWorld;
                pivotInfo += $" groundOffset={offsetWorld:F3}";
            }

            if (alignModelToBBoxBottom && model != null)
            {
                Vector3 up = screen != null ? screen.up : Vector3.up;
                float vBottom = vEye + bboxHAdjusted * bboxAnchorVToBottom;
                vBottom = Mathf.Clamp(vBottom, 0f, manifest.eye_h - 1f);
                Vector3 bottomWorld = AnchorUvZToWorldPinhole(screen, uEye, vBottom, obj.anchorZ);
                bottomWorld += up * modelBottomExtraOffsetMeters;
                float modelBottomOffset = model.baseBottomOffsetLocal * lossy.y;
                Vector3 modelBottomWorld = instance.transform.position - up * modelBottomOffset;
                Vector3 delta = bottomWorld - modelBottomWorld;
                if (bottomAlignVerticalOnly)
                {
                    float d = Vector3.Dot(delta, up);
                    instance.transform.position += up * d;
                }
                else
                {
                    instance.transform.position += delta;
                }
            }

            if (enableHeadHeightScaleCorrection && model != null && obj.categoryId != 2)
            {
                TryApplyHumanoidHeadHeightScaleCorrection(instance.transform, model, obj, rootWorld: instance.transform.position, screen: screen, baseScale: baseScale, uniformScale: uniformScale);
            }

            Log(LogCategory.SCALE,
                $"f={frame} t={obj.trackId} bboxPx=({bboxWAdjusted:F1},{bboxHAdjusted:F1}) bboxWorld=({bboxWorldW:F3},{bboxWorldH:F3}) " +
                $"baseBounds=({baseBounds.x:F3},{baseBounds.y:F3}) appliedScale=({instance.transform.localScale.x:F3},{instance.transform.localScale.y:F3},{instance.transform.localScale.z:F3}){pivotInfo}",
                frame, (int)obj.trackId);
            return;
        }

        instance.transform.localScale = baseScale * targetUniform;
        if (model != null && model.anchor == null && model.alignToGround)
        {
            float offsetWorld = model.baseBottomOffsetLocal * instance.transform.lossyScale.y;
            instance.transform.position += instance.transform.up * offsetWorld;
        }

        if (enableHeadHeightScaleCorrection && model != null && obj.categoryId != 2)
        {
            TryApplyHumanoidHeadHeightScaleCorrection(instance.transform, model, obj, rootWorld: instance.transform.position, screen: screen, baseScale: baseScale, uniformScale: targetUniform);
        }
    }


    private void TryApplyHumanoidHeadHeightScaleCorrection(Transform root, ReplaceableModel model, MetaObj obj, Vector3 rootWorld, Transform screen, Vector3 baseScale, float uniformScale)
    {
        if (root == null || model == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        if (!TryGetHeadTargetWorld(obj, rootWorld, screen, out Vector3 headTargetWorld))
        {
            return;
        }

        Vector3 up = screen != null ? screen.up : root.up;
        if (up.sqrMagnitude < 0.000001f)
        {
            up = Vector3.up;
        }
        up.Normalize();

        float bottomOffsetWorld = model.baseBottomOffsetLocal * root.lossyScale.y;
        Vector3 footWorld = root.position - up * bottomOffsetWorld;
        float currentHeadFromFoot = (model.baseHeightMeters * root.lossyScale.y);
        float targetHeadFromFoot = Vector3.Dot(headTargetWorld - footWorld, up);
        if (currentHeadFromFoot <= 0.0001f || targetHeadFromFoot <= 0.0001f)
        {
            return;
        }

        float ratio = targetHeadFromFoot / currentHeadFromFoot;
        float clampedRatio = Mathf.Clamp(ratio, headHeightScaleMin, headHeightScaleMax);
        float blendedRatio = Mathf.Lerp(1f, clampedRatio, Mathf.Clamp01(headHeightScaleAlpha));
        float correctedUniformScale = uniformScale * blendedRatio;
        root.localScale = baseScale * correctedUniformScale;

        // Keep feet fixed while applying height correction.
        float correctedBottomOffsetWorld = model.baseBottomOffsetLocal * root.lossyScale.y;
        root.position = footWorld + up * correctedBottomOffsetWorld;
    }


    private bool TryGetHeadTargetWorld(MetaObj obj, Vector3 rootWorld, Transform screen, out Vector3 headTargetWorld)
    {
        headTargetWorld = Vector3.zero;
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return false;
        }

        int jointCount = obj.skeletonKpCount;
        if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
        {
            return false;
        }

        SkeletonIndices idx = ResolveSkeletonIndices(jointCount);
        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3 hipMid = Vector3.zero;
        if (idx.leftHip >= 0 && idx.rightHip >= 0 &&
            idx.leftHip < obj.jointsCam.Length && idx.rightHip < obj.jointsCam.Length)
        {
            hipMid = (obj.jointsCam[idx.leftHip] + obj.jointsCam[idx.rightHip]) * 0.5f;
        }
        bool rootRel = !IsEffectiveJointsSpaceAbsolute() && hipMid.magnitude < boneRootRelThreshold;

        Vector3[] jointsWorld = new Vector3[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            Vector3 joint = obj.jointsCam[i];
            joint = new Vector3(joint.x * boneAxisSign.x, joint.y * boneAxisSign.y, joint.z * boneAxisSign.z);
            jointsWorld[i] = rootRel
                ? rootWorld + (camRotation * joint)
                : camOrigin + (camRotation * joint);
        }

        if (idx.leftShoulder < 0 || idx.rightShoulder < 0 ||
            idx.leftShoulder >= jointCount || idx.rightShoulder >= jointCount ||
            idx.leftShoulder >= obj.jointsVis.Length || idx.rightShoulder >= obj.jointsVis.Length ||
            obj.jointsVis[idx.leftShoulder] == 0 || obj.jointsVis[idx.rightShoulder] == 0)
        {
            return false;
        }

        Vector3 shouldersMid = (jointsWorld[idx.leftShoulder] + jointsWorld[idx.rightShoulder]) * 0.5f;
        if (!TryGetHeadTarget(jointsWorld, obj.jointsVis, shouldersMid, idx, out Vector3 head))
        {
            return false;
        }

        headTargetWorld = head;
        return true;
    }


    private void TryApplySkeleton(GameObject instance, MetaObj obj, Vector3 rootWorld, Transform screen, int frame)
    {
        Vector3 modelPosBefore = instance != null ? instance.transform.position : Vector3.zero;
        int kpCountForSummary = obj.skeletonKpCount;
        int visCountForSummary = 0;
        int invalidCountForSummary = 0;
        string jointsSpaceForSummary = "n/a";

        if (instance == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            LogJointDebugSkip("missing_instance_or_skeleton_buffers", frame, obj.trackId);
            TryLogFrameApplySummary(
                frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                jointsSpaceForSummary, false, rootWorld, rootWorld, modelPosBefore, modelPosBefore, "missing_instance_or_skeleton_buffers");
            return;
        }

        try
        {
            debugJointContextFrame = frame;
            debugJointContextTrackId = obj.trackId;
            int jointCount = obj.skeletonKpCount;
            kpCountForSummary = jointCount;
            if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
            {
                LogJointDebugSkip("invalid_kpCount_or_buffer_length", frame, obj.trackId);
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "invalid_kpCount_or_buffer_length");
                return;
            }

            SkeletonIndices idx = ResolveSkeletonIndices(jointCount);

            if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
            {
                LogJointDebugSkip("pinhole_basis_unavailable", frame, obj.trackId);
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "pinhole_basis_unavailable");
                return;
            }

            // Root-relative蛻､螳壹・蜿ｯ隕匁ｧ縺ｫ萓晏ｭ倥＆縺帙★縲∝・繝・・繧ｿ蠎ｧ讓吶〒隧穂ｾ｡縺吶ｋ縲・
            Vector3 hipMid = Vector3.zero;
            if (idx.leftHip >= 0 && idx.rightHip >= 0 &&
                idx.leftHip < obj.jointsCam.Length && idx.rightHip < obj.jointsCam.Length)
            {
                hipMid = (obj.jointsCam[idx.leftHip] + obj.jointsCam[idx.rightHip]) * 0.5f;
            }
            bool rootRel = !IsEffectiveJointsSpaceAbsolute() && hipMid.magnitude < boneRootRelThreshold;
            jointsSpaceForSummary = rootRel ? "RootRel" : "CamSpace";
            int visOk = 0;
            Vector3[] jointsWorld = new Vector3[jointCount];
            float[] dogCamZForSkip = obj.categoryId == 2 ? new float[jointCount] : null;
            for (int i = 0; i < jointCount; i++)
            {
                if (obj.jointsVis[i] > 0)
                {
                    visOk++;
                }

                Vector3 raw = obj.jointsCam[i];
                if (dogCamZForSkip != null)
                {
                    dogCamZForSkip[i] = raw.z;
                }
                bool nonFinite = float.IsNaN(raw.x) || float.IsInfinity(raw.x) ||
                    float.IsNaN(raw.y) || float.IsInfinity(raw.y) ||
                    float.IsNaN(raw.z) || float.IsInfinity(raw.z);
                bool nearZero = raw.sqrMagnitude <= InvalidJointSqrMagnitudeEpsilon;
                bool zNonPositive = raw.z <= 0f;
                if (nonFinite || nearZero || zNonPositive)
                {
                    invalidCountForSummary++;
                }

                Vector3 joint = obj.jointsCam[i];
                joint = new Vector3(joint.x * boneAxisSign.x, joint.y * boneAxisSign.y, joint.z * boneAxisSign.z);
                jointsWorld[i] = rootRel
                    ? rootWorld + (camRotation * joint)
                    : camOrigin + (camRotation * joint);
            }
            visCountForSummary = visOk;
            TryLogSpaceCheck(frame, obj, rootRel, rootWorld, screen, jointsWorld);
            int dogSkipSegmentsForFrame = 0;
            bool freezeDogDistal = false;
            if (obj.categoryId == 2 && dogCamZForSkip != null)
            {
                dogSkipSegmentsForFrame = CountSkeletonLineSkipSegments(obj.categoryId, jointCount, obj.jointsVis, dogCamZForSkip);
                freezeDogDistal = enableDogDistalFreezeOnHighSkip && dogSkipSegmentsForFrame >= dogDistalFreezeSkipThreshold;
                if (freezeDogDistal && debugLogAxisCompare && TryConsumeDiagBudget(frame))
                {
                    Debug.Log(
                        $"DOG_DISTAL_FREEZE frame={frame} trackId={obj.trackId} skipSegments={dogSkipSegmentsForFrame} threshold={dogDistalFreezeSkipThreshold}");
                }
            }

            bool needsProcessedDebug = debugLogJointsProcessed || (debugDrawJoints2D && joints2DMode == Joints2DMode.ProjectXYZ && !debugProjectXYZUseRaw);
            if (needsProcessedDebug)
            {
                Quaternion invCamRotation = Quaternion.Inverse(camRotation);
                Vector3[] processedCam = new Vector3[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    processedCam[i] = invCamRotation * (jointsWorld[i] - camOrigin);
                }

                DebugProcessedJointState procState;
                if (!debugProcessedJointsByTrack.TryGetValue(obj.trackId, out procState) || procState == null)
                {
                    procState = new DebugProcessedJointState();
                    debugProcessedJointsByTrack[obj.trackId] = procState;
                }
                procState.frame = frame;
                procState.jointsCamProcessed = processedCam;
                procState.jointsVis = new byte[jointCount];
                System.Array.Copy(obj.jointsVis, procState.jointsVis, jointCount);

                if (debugLogJointsProcessed)
                {
                    float minX = float.MaxValue;
                    float maxX = float.MinValue;
                    float minY = float.MaxValue;
                    float maxY = float.MinValue;
                    float minZ = float.MaxValue;
                    float maxZ = float.MinValue;
                    int zLe0Count = 0;
                    int zEq0Count = 0;
                    for (int i = 0; i < jointCount; i++)
                    {
                        Vector3 pj = processedCam[i];
                        minX = Mathf.Min(minX, pj.x);
                        maxX = Mathf.Max(maxX, pj.x);
                        minY = Mathf.Min(minY, pj.y);
                        maxY = Mathf.Max(maxY, pj.y);
                        minZ = Mathf.Min(minZ, pj.z);
                        maxZ = Mathf.Max(maxZ, pj.z);
                        if (pj.z <= 0f)
                        {
                            zLe0Count++;
                        }
                        if (Mathf.Approximately(pj.z, 0f))
                        {
                            zEq0Count++;
                        }
                    }

                    Debug.Log(
                        $"[joints_proc] frame={frame} trackId={obj.trackId} space={GetEffectiveJointsSpaceTag()} " +
                        $"x({minX:F4},{maxX:F4}) y({minY:F4},{maxY:F4}) z({minZ:F4},{maxZ:F4}) " +
                        $"zLe0Count={zLe0Count} zEq0Count={zEq0Count}");
                }
            }

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, jointsWorld, obj.jointsVis);
            }

            ApplyManualYawToJoints(obj.trackId, frame, jointsWorld, obj.jointsVis, instance.transform.position, instance.transform.up);

            if (enableYawDepthDisambiguation)
            {
                ApplyYawDepthDisambiguation(jointsWorld, obj.jointsVis, idx, instance.transform, camOrigin);
            }

            if (debugDrawJoints || debugDrawSkeletonLines3D || debugDrawBoneAxisCompare)
            {
                DebugDrawTrackState state = GetOrCreateDebugDrawTrackState(obj.trackId);
                state.jointCount = jointCount;
                state.categoryId = obj.categoryId;
                state.jointsWorld = new Vector3[jointCount];
                System.Array.Copy(jointsWorld, state.jointsWorld, jointCount);
                state.jointsVis = new byte[jointCount];
                System.Array.Copy(obj.jointsVis, state.jointsVis, jointCount);
                state.jointsCamZ = new float[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    state.jointsCamZ[i] = obj.jointsCam[i].z;
                }
                state.skeletonSkipCount = CountSkeletonLineSkipSegments(obj.categoryId, jointCount, obj.jointsVis, state.jointsCamZ);
                if (debugLogAxisCompare)
                {
                    Debug.Log($"SKELETON_LINE_SKIP frame={frame} trackId={obj.trackId} categoryId={obj.categoryId} skipSegments={state.skeletonSkipCount}");
                }

                state.hasAxisCompare = false;
                state.axisBoneName = string.Empty;
                state.axisIdxA = -1;
                state.axisIdxB = -1;
                if (debugDrawBoneAxisCompare)
                {
                    if (obj.categoryId == 2)
                    {
                        AnimalRigCache animalCache = GetOrBuildAnimalRigCache(instance.transform);
                        if (TrySelectDogAxisComparePair(
                            animalCache,
                            jointCount,
                            obj.jointsVis,
                            state.jointsCamZ,
                            out Transform dogBone,
                            out int idxA,
                            out int idxB,
                            out int skipMissingBone,
                            out int skipZEq0,
                            out int skipVis0,
                            out int skipOutOfRange,
                            out int totalSegments))
                        {
                            Vector3 targetDir = jointsWorld[idxB] - jointsWorld[idxA];
                            if (targetDir.sqrMagnitude > 0.000001f)
                            {
                                if (debugAutoBoneAxisApplyToRig)
                                {
                                    TryApplyAutoBoneAxis(dogBone, targetDir, frame, obj.trackId, dogBone.name);
                                }
                                state.hasAxisCompare = true;
                                state.axisBoneName = dogBone.name;
                                state.axisIdxA = idxA;
                                state.axisIdxB = idxB;
                                state.axisBase = jointsWorld[idxA];
                                state.axisTargetDir = targetDir.normalized;
                                state.axisBoneDir = dogBone.forward.normalized;
                                state.axisAngleDeg = Vector3.Angle(state.axisTargetDir, state.axisBoneDir);
                            }
                        }
                        else if (debugLogAxisCompare)
                        {
                            Debug.Log(
                                $"DOG_AXIS_SKIP frame={frame} trackId={obj.trackId} " +
                                $"skip_missingBone={skipMissingBone} skip_zEq0={skipZEq0} skip_vis0={skipVis0} skip_outOfRange={skipOutOfRange} total={totalSegments}");
                        }
                    }
                    else
                    {
                        Animator axisAnimator = instance.GetComponentInChildren<Animator>();
                        Transform leftUpperArm = axisAnimator != null ? axisAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm) : null;
                        int idxA = 5;
                        int idxB = 7;
                        if (leftUpperArm != null &&
                            IsDebugJointPairValid(idxA, idxB, jointCount, obj.jointsVis, state.jointsCamZ))
                        {
                            Vector3 targetDir = jointsWorld[idxB] - jointsWorld[idxA];
                            if (targetDir.sqrMagnitude > 0.000001f)
                            {
                                if (debugAutoBoneAxisApplyToRig)
                                {
                                    TryApplyAutoBoneAxis(leftUpperArm, targetDir, frame, obj.trackId, "LeftUpperArm");
                                }
                                state.hasAxisCompare = true;
                                state.axisBoneName = "LeftUpperArm";
                                state.axisIdxA = idxA;
                                state.axisIdxB = idxB;
                                state.axisBase = jointsWorld[idxA];
                                state.axisTargetDir = targetDir.normalized;
                                state.axisBoneDir = leftUpperArm.forward.normalized;
                                state.axisAngleDeg = Vector3.Angle(state.axisTargetDir, state.axisBoneDir);
                            }
                        }
                    }

                    if (debugLogAxisCompare)
                    {
                        if (state.hasAxisCompare)
                        {
                            Debug.Log(
                                $"AXIS_COMPARE frame={frame} trackId={obj.trackId} bone={state.axisBoneName} idxA={state.axisIdxA} idxB={state.axisIdxB} " +
                                $"angleDeg={state.axisAngleDeg:F2} skipSegments={state.skeletonSkipCount}");
                        }
                        else
                        {
                            Debug.Log(
                                $"AXIS_COMPARE frame={frame} trackId={obj.trackId} bone=n/a idxA=-1 idxB=-1 " +
                                $"angleDeg=n/a skipSegments={state.skeletonSkipCount}");
                        }
                    }
                }
            }

            if (debugDrawJoints || debugDrawAnchor || debugDisableRigApply)
            {
                Vector3 firstJoint = jointCount > 0 ? jointsWorld[0] : Vector3.zero;
                string anchorText = "n/a";
                if (debugDrawStateByTrack.TryGetValue(obj.trackId, out DebugDrawTrackState stateForLog) && stateForLog.hasAnchor)
                {
                    anchorText = $"({stateForLog.anchorWorld.x:F3},{stateForLog.anchorWorld.y:F3},{stateForLog.anchorWorld.z:F3})";
                }
                Vector3 firstCam = jointCount > 0 ? obj.jointsCam[0] : Vector3.zero;
                float camMag = firstCam.magnitude;
                float worldFromRootMag = (jointCount > 0 ? (firstJoint - rootWorld).magnitude : 0f);
                float scaleRatio = camMag > 0.000001f ? worldFromRootMag / camMag : 0f;
                Debug.Log(
                    $"JOINT_DEBUG frame={frame} trackId={obj.trackId} kpCount={jointCount} visCount={visOk} " +
                    $"j0cam=({firstCam.x:F3},{firstCam.y:F3},{firstCam.z:F3}) j0world=({firstJoint.x:F3},{firstJoint.y:F3},{firstJoint.z:F3}) " +
                    $"j0worldFromRootMag={worldFromRootMag:F3} j0camMag={camMag:F3} ratio={scaleRatio:F3} mode={(rootRel ? "RootRel" : "CamSpace")} " +
                    $"anchorWorld={anchorText} rigApplySkipped={(debugDisableRigApply ? 1 : 0)}");
            }

            Log(LogCategory.BONE,
                $"f={frame} t={obj.trackId} J={jointCount} hipMid=({hipMid.x:F3},{hipMid.y:F3},{hipMid.z:F3}) mode={(rootRel ? "RootRel" : "CamSpace")} visOk={visOk}/{jointCount}",
                frame, (int)obj.trackId);

            if (ShouldLog(LogCategory.BONE, frame, (int)obj.trackId))
            {
                DrawSkeleton(jointsWorld, obj.jointsVis, obj.categoryId);
            }

            if (!enableBoneApply)
            {
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "enableBoneApply_false");
                return;
            }

            if (debugDisableRigApply)
            {
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "debugDisableRigApply");
                return;
            }

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (animator != null && animator.enabled && useMetaFollow && debugForceDisableAnimatorForMeta)
            {
                animator.enabled = false;
                int animId = animator.GetInstanceID();
                if (debugLogAxisCompare && !animatorMetaLockLogged.Contains(animId) && TryConsumeDiagBudget(frame))
                {
                    animatorMetaLockLogged.Add(animId);
                    Debug.Log($"ANIMATOR_META_LOCK frame={frame} trackId={obj.trackId} enabledBefore=1 enabledAfter=0");
                }
            }
            if (obj.categoryId == 2)
            {
                AnimalRigCache dogCacheForErr = null;
                Transform dogCheckBone = null;
                Vector3 dogCheckBefore = Vector3.zero;
                if (ShouldEmitRigDiag(frame, obj.trackId))
                {
                    dogCacheForErr = GetOrBuildAnimalRigCache(animator != null ? animator.transform : instance.transform);
                    if (dogCacheForErr != null)
                    {
                        dogCheckBone = dogCacheForErr.head != null ? dogCacheForErr.head : instance.transform;
                        dogCheckBefore = dogCheckBone.position;
                    }
                }
                ApplyAnimalSkeleton(instance.transform, animator, jointsWorld, obj.jointsVis, obj.skeletonKpCount, obj.categoryId, screen, freezeDogDistal);
                if (dogCacheForErr != null)
                {
                    TryLogCloudBoneErr(frame, obj.trackId, "Root", instance.transform, jointsWorld, 6);
                    TryLogCloudBoneErr(frame, obj.trackId, "Head", dogCacheForErr.head, jointsWorld, 4);
                    TryLogCloudBoneErr(frame, obj.trackId, "LeftHand", dogCacheForErr.leftFrontPaw, jointsWorld, 16);
                    TryLogCloudBoneErr(frame, obj.trackId, "RightHand", dogCacheForErr.rightFrontPaw, jointsWorld, 17);
                    TryLogCloudBoneErr(frame, obj.trackId, "LeftFoot", dogCacheForErr.leftRearPaw, jointsWorld, 18);
                    TryLogCloudBoneErr(frame, obj.trackId, "RightFoot", dogCacheForErr.rightRearPaw, jointsWorld, 19);
                }
                if (dogCheckBone != null)
                {
                    QueueAnimatorCheckSample(frame, obj.trackId, dogCheckBone.name, dogCheckBone, dogCheckBefore, dogCheckBone.position, animator);
                }
                TryLogAnchorCheck(frame, obj, modelPosBefore, instance.transform.position, rootWorld, jointsWorld);
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "none");
                return;
            }

            if (animator == null || !animator.isHuman)
            {
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "animator_missing_or_nonhuman");
                return;
            }

            HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
            if (cache == null || !cache.ready)
            {
                TryLogFrameApplySummary(
                    frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                    jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, "humanoid_cache_not_ready");
                return;
            }

            Transform hipsBoneForCheck = null;
            Vector3 hipsBeforeApply = Vector3.zero;
            if (ShouldEmitRigDiag(frame, obj.trackId))
            {
                hipsBoneForCheck = cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hb) ? hb : null;
                hipsBeforeApply = hipsBoneForCheck != null ? hipsBoneForCheck.position : Vector3.zero;
            }

            bool applied = ApplyHumanoidLimbs(cache, jointsWorld, obj.jointsVis, idx);
            if (ShouldEmitRigDiag(frame, obj.trackId))
            {
                TryLogCloudBoneErr(frame, obj.trackId, "Root", hipsBoneForCheck, jointsWorld, idx.leftHip >= 0 ? idx.leftHip : 0);
                TryLogCloudBoneErr(frame, obj.trackId, "Head", cache.bones.TryGetValue(HumanBodyBones.Head, out Transform headBone) ? headBone : null, jointsWorld, idx.nose);
                TryLogCloudBoneErr(frame, obj.trackId, "LeftHand", cache.bones.TryGetValue(HumanBodyBones.LeftHand, out Transform lHand) ? lHand : null, jointsWorld, idx.leftWrist);
                TryLogCloudBoneErr(frame, obj.trackId, "RightHand", cache.bones.TryGetValue(HumanBodyBones.RightHand, out Transform rHand) ? rHand : null, jointsWorld, idx.rightWrist);
                TryLogCloudBoneErr(frame, obj.trackId, "LeftFoot", cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform lFoot) ? lFoot : null, jointsWorld, idx.leftAnkle);
                TryLogCloudBoneErr(frame, obj.trackId, "RightFoot", cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rFoot) ? rFoot : null, jointsWorld, idx.rightAnkle);
                if (hipsBoneForCheck != null)
                {
                    QueueAnimatorCheckSample(frame, obj.trackId, "Hips", hipsBoneForCheck, hipsBeforeApply, hipsBoneForCheck.position, animator);
                }
            }
        if (alignFeetToAnkles)
        {
            AlignFeetToAnkles(cache, jointsWorld, obj.jointsVis, idx, instance.transform);
        }

            if (applied && !boneAppliedLogged)
            {
                boneAppliedLogged = true;
                Log(LogCategory.BONE, "BONE_STATUS applied=true");
            }

            TryLogFrameApplySummary(
                frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance.transform.position, applied ? "none" : "apply_returned_false");
            TryLogAnchorCheck(frame, obj, modelPosBefore, instance.transform.position, rootWorld, jointsWorld);
        }
        catch (System.Exception ex)
        {
            LogJointDebugSkip("exception_in_try_apply_skeleton", frame, obj.trackId);
            Debug.LogWarning($"TryApplySkeleton failed and was skipped. frame={frame} track={obj.trackId} ({ex.Message})");
            TryLogFrameApplySummary(
                frame, obj.trackId, obj.categoryId, kpCountForSummary, visCountForSummary, invalidCountForSummary,
                jointsSpaceForSummary, true, rootWorld, rootWorld, modelPosBefore, instance != null ? instance.transform.position : modelPosBefore, "exception_in_try_apply_skeleton");
        }
    }

}

