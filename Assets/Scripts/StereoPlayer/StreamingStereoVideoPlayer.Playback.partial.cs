using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: meta frame cache, track instance state, manifest/screen helpers, and manual-yaw partials
    // Provides: model display pipeline, target selection, replaceable model apply, TryApplySkeleton entry

    public void DisplayModelTick()
    {
        if (!displayModel || !metaLoaded)
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

        int displayedFrame = lastFrameReadyFrame;
        int frame = GetCurrentFrameIndex();
        int metaFrameUsed = UseFrameReadySync ? displayedFrame : frame;

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
        if (replacePrefab != null)
        {
            return true;
        }

        return track0Prefab != null || track1Prefab != null || track2Prefab != null;
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

            kv.Value.SetActive(false);
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
            instance.SetActive(true);
            Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
            rotationPinhole = ApplyManualTrackYawOffset(target.trackId, frame, rotationPinhole, screen != null ? screen.up : Vector3.up);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, anchorWorld, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, screen);
            TryApplySkeleton(instance, target, screen, frame);
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
        if (trackId == 0u && track0Prefab != null)
        {
            return track0Prefab;
        }

        if (trackId == 1u && track1Prefab != null)
        {
            return track1Prefab;
        }

        if (trackId == 2u && track2Prefab != null)
        {
            return track2Prefab;
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

        if (IsCategoryOther(obj.categoryId) && obj.hasOtherProxySize && obj.otherProxySize.sqrMagnitude > 0.000001f)
        {
            Vector3 proxySize = AbsVector(obj.otherProxySize);
            Vector2 baseBounds = model != null ? model.baseBoundsSize : Vector2.zero;
            float scaleW = baseBounds.x > 0.000001f ? proxySize.x / baseBounds.x : targetUniform;
            float scaleH = baseBounds.y > 0.000001f ? proxySize.y / baseBounds.y : targetUniform;
            float proxyUniform = Mathf.Max(scaleW, scaleH) * userScale;
            if (proxyUniform <= 0.000001f)
            {
                proxyUniform = targetUniform;
            }

            instance.transform.localScale = baseScale * proxyUniform;
            return;
        }

        if (TryGetFocalLengths(out float fxScale, out float fyScale))
        {
            float bboxWorldW = (2f * bboxWAdjusted / manifest.eye_w) * (obj.anchorZ / fxScale);
            float bboxWorldH = (2f * bboxHAdjusted / manifest.eye_h) * (obj.anchorZ / fyScale);
            Vector2 baseBounds = model != null ? model.baseBoundsSize : Vector2.zero;
            float scaleW = baseBounds.x > 0f ? bboxWorldW / baseBounds.x : targetUniform;
            float scaleH = baseBounds.y > 0f ? bboxWorldH / baseBounds.y : targetUniform;
            float uniformScale = IsCategoryAnimal(obj.categoryId) ? Mathf.Min(scaleW, scaleH) : scaleH;
            instance.transform.localScale = baseScale * uniformScale;
            Vector3 lossy = instance.transform.lossyScale;
            if (model != null && model.anchor == null && model.alignToGround)
            {
                float offsetWorld = model.baseBottomOffsetLocal * lossy.y;
                instance.transform.position += instance.transform.up * offsetWorld;
            }

            if (AlignModelToBBoxBottom && model != null)
            {
                Vector3 up = screen != null ? screen.up : Vector3.up;
                float vBottom = vEye + bboxHAdjusted * BboxAnchorVToBottom;
                vBottom = Mathf.Clamp(vBottom, 0f, manifest.eye_h - 1f);
                Vector3 bottomWorld = AnchorUvZToWorldPinhole(screen, uEye, vBottom, obj.anchorZ);
                bottomWorld += up * ModelBottomExtraOffsetMeters;
                float modelBottomOffset = model.baseBottomOffsetLocal * lossy.y;
                Vector3 modelBottomWorld = instance.transform.position - up * modelBottomOffset;
                Vector3 delta = bottomWorld - modelBottomWorld;
                if (BottomAlignVerticalOnly)
                {
                    float d = Vector3.Dot(delta, up);
                    instance.transform.position += up * d;
                }
                else
                {
                    instance.transform.position += delta;
                }
            }

            if (EnableHeadHeightScaleCorrection && model != null && IsCategoryPerson(obj.categoryId))
            {
                TryApplyHumanoidHeadHeightScaleCorrection(instance.transform, model, obj, screen: screen, baseScale: baseScale, uniformScale: uniformScale);
            }
            return;
        }

        instance.transform.localScale = baseScale * targetUniform;
        if (model != null && model.anchor == null && model.alignToGround)
        {
            float offsetWorld = model.baseBottomOffsetLocal * instance.transform.lossyScale.y;
            instance.transform.position += instance.transform.up * offsetWorld;
        }

        if (EnableHeadHeightScaleCorrection && model != null && IsCategoryPerson(obj.categoryId))
        {
            TryApplyHumanoidHeadHeightScaleCorrection(instance.transform, model, obj, screen: screen, baseScale: baseScale, uniformScale: targetUniform);
        }
    }


    private void TryApplyHumanoidHeadHeightScaleCorrection(Transform root, ReplaceableModel model, MetaObj obj, Transform screen, Vector3 baseScale, float uniformScale)
    {
        if (root == null || model == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        if (!TryGetHeadTargetWorld(obj, screen, out Vector3 headTargetWorld))
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
        float clampedRatio = Mathf.Clamp(ratio, HeadHeightScaleMin, HeadHeightScaleMax);
        float blendedRatio = Mathf.Lerp(1f, clampedRatio, Mathf.Clamp01(HeadHeightScaleAlpha));
        float correctedUniformScale = uniformScale * blendedRatio;
        root.localScale = baseScale * correctedUniformScale;

        // Keep feet fixed while applying height correction.
        float correctedBottomOffsetWorld = model.baseBottomOffsetLocal * root.lossyScale.y;
        root.position = footWorld + up * correctedBottomOffsetWorld;
    }


    private bool TryGetHeadTargetWorld(MetaObj obj, Transform screen, out Vector3 headTargetWorld)
    {
        headTargetWorld = Vector3.zero;
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return false;
        }

        SkeletonIndices idx = MetrabsSmpl24Indices;
        if (!TryBuildPersonPoseWorld(obj, screen, out PoseWorldData pose) ||
            pose.jointCount != 24)
        {
            return false;
        }

        if (idx.leftShoulder < 0 || idx.rightShoulder < 0 ||
            idx.leftShoulder >= pose.jointCount || idx.rightShoulder >= pose.jointCount ||
            idx.leftShoulder >= obj.jointsVis.Length || idx.rightShoulder >= obj.jointsVis.Length ||
            obj.jointsVis[idx.leftShoulder] == 0 || obj.jointsVis[idx.rightShoulder] == 0)
        {
            return false;
        }

        Vector3 shouldersMid = (pose.jointsWorld[idx.leftShoulder] + pose.jointsWorld[idx.rightShoulder]) * 0.5f;
        if (!TryGetHeadTarget(pose.jointsWorld, obj.jointsVis, shouldersMid, idx, out Vector3 head))
        {
            return false;
        }

        headTargetWorld = head;
        return true;
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
