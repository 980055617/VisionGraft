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
        int frame = frameSnapshot.currentFrame;
        int metaFrameUsed = frameSnapshot.displayMetadataFrame;

        if (!TryReadFrameObjects(metaFrameUsed, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        frame = metaFrameUsed;
        ApplyOtherProxyBoxesForFrame(metaFrameObjects, frame);

        if (TryApplyDisplayedTracks(frame))
        {
            return;
        }

        MetaObj target = SelectAutoDisplayTarget(metaFrameObjects);
        int autoTrackId = (int)target.trackId;
        if (autoTrackId != lastAutoTrackId)
        {
            lastAutoTrackId = autoTrackId;
        }

        ApplyMetaTarget(target, frame);
    }


    private bool HasAnyDisplayPrefabConfigured()
    {
        return TrackPrefabResolver.HasAnyConfigured(replacePrefab, track0Prefab, track1Prefab, track2Prefab);
    }


    private bool TryApplyDisplayedTracks(int frame)
    {
        if (displayTrackIds == null || displayTrackIds.Length == 0)
        {
            return false;
        }

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
            else if (TryApplyInteractiveFrameOutTrack(trackId, frame))
            {
                appliedTracks.Add(trackId);
            }
        }

        HideUnselectedTrackInstances(appliedTracks);
        return selectedTracks.Count > 0;
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

        float bboxWAdjusted = target.bboxW;
        float bboxHAdjusted = target.bboxH;

        Vector3 anchorWorld = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);
        if (IsCategoryOther(target.categoryId) && target.hasOtherProxy &&
            TryOtherProxyWorld(target, screen, out Vector3 otherWorld, out _))
        {
            anchorWorld = otherWorld;
        }

        GameObject instance = GetOrCreateTrackInstance(target.trackId);
        if (instance != null)
        {
            SceneObjectWriter.ApplyActive(instance, true);
            Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
            rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole, screen != null ? screen.up : Vector3.up);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, screen);
            UpdateInteractiveMotionSchedule(instance, target, screen, frame);
            bool isHumanoidInteractiveRootPinned = IsHumanoidInteractiveRootPinned(target.trackId);
            bool preserveRootScreenHeightAfterSkeleton =
                IsCategoryPerson(target.categoryId) &&
                ShouldPreserveRootScreenHeightAfterHumanSkeletonPlacement() &&
                !isHumanoidInteractiveRootPinned;
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
            bool isHumanoidInteractiveMotionInPlace = IsHumanoidInteractiveMotionInPlace(target.trackId);
            bool shouldUseHumanSmplRootPlacement = ShouldUseHumanSmplRootPlacement(target, frame);
            if (isHumanoidInteractiveMotionInPlace && !isHumanoidInteractiveRootPinned)
            {
                if (ShouldInitialFitHumanoidInPlaceRootBeforePinning(
                    isHumanoidInteractiveMotionInPlace,
                    isHumanoidInteractiveRootPinned,
                    shouldUseHumanSmplRootPlacement))
                {
                    FitDisplayedModelToBBox(instance, target, screen, bboxHAdjusted);
                }
                PinHumanoidInteractiveRootAfterBBox(target.trackId, instance, screen);
            }
            else if (ShouldFitDisplayedModelToBBoxDuringInteractiveMotion(
                    IsInteractiveMotionReplacing(target.trackId),
                    isHumanoidInteractiveMotionInPlace) &&
                !shouldUseHumanSmplRootPlacement)
            {
                FitDisplayedModelToBBox(instance, target, screen, bboxHAdjusted);
            }
            ObserveInteractiveMotionDisplayedRoot(target.trackId, instance);
            return;
        }
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


    private GameObject GetOrCreateTrackInstance(uint trackId)
    {
        GameObject prefab = ResolveTrackPrefab(trackId);
        return TrackInstanceLifecycle.GetOrCreate(
            trackId,
            prefab,
            trackInstances,
            trackPrefabSources,
            lockedModelLocalScaleByTrack,
            ref selectedManualRotationTrackId);
    }


    private GameObject ResolveTrackPrefab(uint trackId)
    {
        return TrackPrefabResolver.Resolve(trackId, replacePrefab, track0Prefab, track1Prefab, track2Prefab);
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


    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxWAdjusted, float bboxHAdjusted, Transform screen)
    {
        if (instance == null)
        {
            return;
        }

        ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
        float modelHeight = model != null ? model.GetModelHeightMeters() : 0f;
        float userScale = model != null ? model.userScale : 1f;
        Vector3 baseScale = model != null ? model.baseLocalScale : Vector3.one;
        Vector2 baseBounds = model != null ? model.baseBoundsSize : Vector2.zero;
        bool isOther = IsCategoryOther(obj.categoryId);
        bool isAnimal = IsCategoryAnimal(obj.categoryId);
        bool lockScale = IsCategoryPerson(obj.categoryId) || isAnimal;
        bool hasFocalLengths = TryGetFocalLengths(out float fxScale, out float fyScale);
        Vector3 desiredScale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            baseScale,
            baseBounds,
            obj.otherProxySize,
            userScale,
            modelHeight,
            targetHeightMeters,
            bboxWAdjusted,
            bboxHAdjusted,
            obj.anchorZ,
            fxScale,
            fyScale,
            manifest != null ? manifest.eye_w : 0,
            manifest != null ? manifest.eye_h : 0,
            isOther,
            obj.hasOtherProxySize,
            isAnimal,
            hasFocalLengths));

        TrackPlacementWriter.ApplyAnchoredPose(
            instance.transform,
            world,
            rotation,
            instance.transform.localScale,
            model != null ? model.anchor : null);

        if (isOther && obj.hasOtherProxySize && obj.otherProxySize.sqrMagnitude > 0.000001f)
        {
            TrackPlacementWriter.Apply(
                instance.transform,
                TrackPlacementCommand.LocalScaleOnly(
                    instance.transform.position,
                    instance.transform.rotation,
                    desiredScale));
            return;
        }

        if (hasFocalLengths)
        {
            TrackPlacementWriter.ApplyLocalScaleWithGroundAlignment(
                instance.transform,
                lockScale ? GetOrLockModelLocalScale(obj.trackId, desiredScale) : desiredScale,
                model != null && model.anchor == null && model.alignToGround,
                model != null ? model.baseBottomOffsetLocal : 0f);
            Vector3 lossy = instance.transform.lossyScale;

            if (AlignModelToBBoxBottom && model != null)
            {
                Vector3 up = screen != null ? screen.up : Vector3.up;
                float vBottom = ResolveBBoxBottomVEye(obj);
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


    private Vector3 GetOrLockModelLocalScale(uint trackId, Vector3 desiredLocalScale)
    {
        if (lockedModelLocalScaleByTrack.TryGetValue(trackId, out Vector3 lockedScale))
        {
            return lockedScale;
        }

        lockedModelLocalScaleByTrack[trackId] = desiredLocalScale;
        return desiredLocalScale;
    }


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


    private void FitDisplayedModelToBBox(GameObject instance, MetaObj obj, Transform screen, float bboxH)
    {
        if (instance == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

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

        AlignProjectedModelBottomToBBox(instance.transform, screen, projectedBottomV, depthMeters, ResolveBBoxBottomVEye(obj));
    }

    private bool ShouldUseHumanSmplRootPlacement(MetaObj obj, int frame)
    {
        if (!IsCategoryPerson(obj.categoryId))
        {
            return false;
        }

        bool hasHumanSmplTranslation =
            TryGetHumanSmplPose(frame, obj.trackId, out HumanSmplPose pose) &&
            pose.hasTransl;
        return ShouldUseHumanSmplRootPlacementPolicy(true, hasHumanSmplTranslation);
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
