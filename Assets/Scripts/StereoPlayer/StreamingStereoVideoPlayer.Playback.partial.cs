using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: meta frame cache, track instance state, manifest/screen helpers, and manual-yaw partials
    // Provides: FollowTick pipeline, target selection, replaceable model apply, TryApplySkeleton entry

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

        int displayedFrame = lastFrameReadyFrame;
        int frame = GetCurrentFrameIndex();
        int metaFrameUsed = useFrameReadySync ? displayedFrame : frame;

        if (!TryReadFrameObjects(metaFrameUsed, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        frame = metaFrameUsed;

        if (TryApplyConfiguredTrackPrefabs(frame))
        {
            return;
        }

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
        if (!ResolveAnchorToScreen(target.anchorU, out Transform screen, out int uEye, out _))
        {
            return;
        }
        pickedScreen = screen;

        // Bundle writer stores anchor/bbox already mapped into eye pixel coordinates.
        float uEyeF = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        float vEyeF = Mathf.Clamp(target.anchorV, 0f, manifest.eye_h - 1f);

        float bboxWAdjusted = target.bboxW;
        float bboxHAdjusted = target.bboxH;

        Vector3 anchorWorld = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);

        GameObject instance = GetOrCreateTrackInstance(target.trackId);
        if (instance != null)
        {
            instance.SetActive(true);
            Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
            rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole, screen != null ? screen.up : Vector3.up);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, screen);
            TryApplySkeleton(instance, target, instance.transform.position, screen, frame);
            return;
        }

        if (spawnedTestModel == null)
        {
            return;
        }

        Quaternion rotation = GetPinholeBasisRotation(screen);

        spawnedTestModel.transform.SetPositionAndRotation(anchorWorld, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, anchorWorld, rotation);
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

        return best;
    }


    private GameObject GetOrCreateTrackInstance(uint trackId)
    {
        GameObject prefab = ResolveTrackPrefab(trackId);
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


    private GameObject ResolveTrackPrefab(uint trackId)
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


    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxWAdjusted, float bboxHAdjusted, Transform screen)
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

        if (model != null && model.anchor != null)
        {
            Vector3 anchorWorld = model.anchor.position;
            Vector3 rootWorld = instance.transform.position;
            Vector3 delta = anchorWorld - rootWorld;
            instance.transform.position = world - delta;
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
            if (model != null && model.anchor == null && model.alignToGround)
            {
                float offsetWorld = model.baseBottomOffsetLocal * lossy.y;
                instance.transform.position += instance.transform.up * offsetWorld;
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


    private static bool IsDogSegmentUsable(int idxA, int idxB, int jointCount, byte[] vis, Vector3[] jointsCam)
    {
        if (idxA < 0 || idxB < 0 || idxA >= jointCount || idxB >= jointCount)
        {
            return false;
        }
        if (vis == null || jointsCam == null || idxA >= vis.Length || idxB >= vis.Length || idxA >= jointsCam.Length || idxB >= jointsCam.Length)
        {
            return false;
        }
        if (vis[idxA] == 0 || vis[idxB] == 0)
        {
            return false;
        }

        return !Mathf.Approximately(jointsCam[idxA].z, 0f) && !Mathf.Approximately(jointsCam[idxB].z, 0f);
    }


    private static int CountDogSkipSegments(int jointCount, byte[] vis, Vector3[] jointsCam)
    {
        int skip = 0;
        for (int i = 0; i + 1 < DogLeftFrontChain.Length; i++)
        {
            if (!IsDogSegmentUsable(DogLeftFrontChain[i], DogLeftFrontChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }
        for (int i = 0; i + 1 < DogRightFrontChain.Length; i++)
        {
            if (!IsDogSegmentUsable(DogRightFrontChain[i], DogRightFrontChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }
        for (int i = 0; i + 1 < DogLeftRearChain.Length; i++)
        {
            if (!IsDogSegmentUsable(DogLeftRearChain[i], DogLeftRearChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }
        for (int i = 0; i + 1 < DogRightRearChain.Length; i++)
        {
            if (!IsDogSegmentUsable(DogRightRearChain[i], DogRightRearChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }

        return skip;
    }


    private void TryApplySkeleton(GameObject instance, MetaObj obj, Vector3 rootWorld, Transform screen, int frame)
    {
        if (instance == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        try
        {
            int jointCount = obj.skeletonKpCount;
            if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
            {
                return;
            }

            SkeletonIndices idx = ResolveSkeletonIndices(jointCount);
            if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
            {
                return;
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

            bool freezeDogDistal = false;
            if (obj.categoryId == 2)
            {
                int dogSkipSegments = CountDogSkipSegments(jointCount, obj.jointsVis, obj.jointsCam);
                freezeDogDistal =
                    enableDogDistalFreezeOnHighSkip &&
                    dogSkipSegments >= Mathf.Max(0, dogDistalFreezeSkipThreshold);
            }

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, jointsWorld, obj.jointsVis, Mathf.Clamp01(jointSmoothingAlpha));
            }

            ApplyManualYawToJoints(obj.trackId, frame, jointsWorld, obj.jointsVis, instance.transform.position, instance.transform.up);

            if (enableYawDepthDisambiguation)
            {
                ApplyYawDepthDisambiguation(jointsWorld, obj.jointsVis, idx, instance.transform, camOrigin, Mathf.Clamp01(yawDepthBlend));
            }

            if (!enableBoneApply)
            {
                return;
            }

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (obj.categoryId == 2)
            {
                ApplyAnimalSkeleton(instance.transform, animator, jointsWorld, obj.jointsVis, obj.skeletonKpCount, obj.categoryId, screen, freezeDogDistal);
                return;
            }

            if (animator == null || !animator.isHuman)
            {
                return;
            }

            HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
            if (cache == null || !cache.ready)
            {
                return;
            }

            ApplyHumanoidLimbs(cache, jointsWorld, obj.jointsVis, idx);
            if (alignFeetToAnkles)
            {
                AlignFeetToAnkles(cache, jointsWorld, obj.jointsVis, idx, instance.transform);
            }
        }
        catch
        {
        }
    }

}
