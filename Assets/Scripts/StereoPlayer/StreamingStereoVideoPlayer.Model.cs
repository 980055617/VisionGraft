using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const int MetaRangeFrameWindow = 60;
    private int lastAutoTrackId = int.MinValue;
    private int lastScreenPinholeLogFrame = -1;
    private float lastScreenPinholeSampleLogTime = -1f;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private bool boneStatusLogged;
    private bool skeletonPresent;
    private bool metaRangeLogged;
    private bool boneAppliedLogged;
    private int metaRangeStartFrame = -1;
    private int metaRangeFrameCount;
    private int lastMetaRangeFrame = -1;
    private int metaRangeMinU = int.MaxValue;
    private int metaRangeMaxU = int.MinValue;
    private int metaRangeMinV = int.MaxValue;
    private int metaRangeMaxV = int.MinValue;
    private readonly HashSet<uint> outOfCropLoggedTracks = new HashSet<uint>();
    private readonly Dictionary<uint, Vector3[]> smoothedJointsByTrack = new Dictionary<uint, Vector3[]>();
    private GameObject anchorPinholeCube;
    private GameObject anchorScreenCube;
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();
    private static readonly int[] CocoEdges = new[]
    {
        0,1, 0,2, 1,3, 2,4, 0,5, 0,6, 5,6, 5,7, 7,9, 6,8, 8,10, 11,12, 11,13, 13,15, 12,14, 14,16, 5,11, 6,12
    };
    private static readonly int[] DogLeftFrontChain = { 7, 8, 12, 16 };
    private static readonly int[] DogRightFrontChain = { 7, 9, 13, 17 };
    private static readonly int[] DogLeftRearChain = { 6, 10, 14, 18 };
    private static readonly int[] DogRightRearChain = { 6, 11, 15, 19 };
    private struct SkeletonIndices
    {
        public int nose;
        public int leftEye;
        public int rightEye;
        public int leftShoulder;
        public int rightShoulder;
        public int leftElbow;
        public int rightElbow;
        public int leftWrist;
        public int rightWrist;
        public int leftHip;
        public int rightHip;
        public int leftKnee;
        public int rightKnee;
        public int leftAnkle;
        public int rightAnkle;
        public int leftFoot;
        public int rightFoot;
    }

    private static readonly SkeletonIndices Coco17Indices = new SkeletonIndices
    {
        nose = 0,
        leftEye = 1,
        rightEye = 2,
        leftShoulder = 5,
        rightShoulder = 6,
        leftElbow = 7,
        rightElbow = 8,
        leftWrist = 9,
        rightWrist = 10,
        leftHip = 11,
        rightHip = 12,
        leftKnee = 13,
        rightKnee = 14,
        leftAnkle = 15,
        rightAnkle = 16,
        leftFoot = 15,
        rightFoot = 16
    };

    private static readonly SkeletonIndices Blaze33Indices = new SkeletonIndices
    {
        nose = 0,
        leftEye = 2,
        rightEye = 5,
        leftShoulder = 11,
        rightShoulder = 12,
        leftElbow = 13,
        rightElbow = 14,
        leftWrist = 15,
        rightWrist = 16,
        leftHip = 23,
        rightHip = 24,
        leftKnee = 25,
        rightKnee = 26,
        leftAnkle = 27,
        rightAnkle = 28,
        leftFoot = 31,
        rightFoot = 32
    };

    private sealed class HumanoidRigCache
    {
        public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<HumanBodyBones, Vector3> bindDirWorld = new Dictionary<HumanBodyBones, Vector3>();
        public readonly Dictionary<HumanBodyBones, Quaternion> bindRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
        public bool ready;
    }

    private sealed class AnimalRigCache
    {
        public Transform root;
        public Transform neck;
        public Transform head;
        public Transform leftEar;
        public Transform rightEar;
        public Transform spine;
        public Transform tailBase;
        public Transform tailMid;
        public Transform tailTip;
        public Transform leftFrontUpper;
        public Transform leftFrontLower;
        public Transform leftFrontPaw;
        public Transform rightFrontUpper;
        public Transform rightFrontLower;
        public Transform rightFrontPaw;
        public Transform leftRearUpper;
        public Transform leftRearLower;
        public Transform leftRearPaw;
        public Transform rightRearUpper;
        public Transform rightRearLower;
        public Transform rightRearPaw;
        public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
        public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
        public bool ready;
    }
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

        LogResolvedManifestOnce();
        int frame = GetCurrentFrameIndex();
        if (!TryReadFrameObjects(frame, metaFrameObjects) || metaFrameObjects.Count == 0)
        {
            return;
        }

        UpdateMetaRange(frame);

        if (TryApplyConfiguredTrackPrefabs(frame))
        {
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
        GameObject instance = GetOrCreateTrackInstance(target.trackId, target.categoryId);
        if (instance != null)
        {
            instance.SetActive(true);
            Camera viewCam = GetViewCamera() ?? Camera.main;
            Vector3 worldPinhole = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);
            Quaternion rotationPinhole = GetPinholeBasisRotation(screen);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, worldPinhole, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, screen, frame);
            TryApplySkeleton(instance, target, instance.transform.position, screen, frame);
            float bboxWorldH = 0f;
            if (TryGetFocalLengths(out _, out float fy))
            {
                bboxWorldH = (2f * bboxHAdjusted / manifest.eye_h) * (target.anchorZ / fy);
            }
            UpdateAnchorDebugCubes(screen, uEyeF, vEyeF, worldPinhole, viewCam, bboxWorldH);
            LogReprojectionError(target.trackId, uEyeF, vEyeF, target.anchorZ, worldPinhole, viewCam, frame);

            Log(LogCategory.FOLLOW,
                $"f={frame} t={target.trackId} anchor=({target.anchorU},{target.anchorV}) uEye={uEyeF:F2} vEye={vEyeF:F2} screen={(isRightEye ? "R" : "L")} z={target.anchorZ:F3} pos=({worldPinhole.x:F3},{worldPinhole.y:F3},{worldPinhole.z:F3})",
                frame, (int)target.trackId);
            return;
        }

        if (spawnedTestModel == null)
        {
            return;
        }

        Camera viewCamFallback = GetViewCamera() ?? Camera.main;
        Vector3 world = AnchorUvZToWorldPinhole(screen, uEyeF, vEyeF, target.anchorZ);
        Quaternion rotation = GetPinholeBasisRotation(screen);

        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        AttachTransformLock(spawnedTestModel, world, rotation);
        float bboxWorldHTest = 0f;
        if (TryGetFocalLengths(out _, out float fyTest))
        {
            bboxWorldHTest = (2f * bboxHAdjusted / manifest.eye_h) * (target.anchorZ / fyTest);
        }
        UpdateAnchorDebugCubes(screen, uEyeF, vEyeF, world, viewCamFallback, bboxWorldHTest);
        LogReprojectionError(target.trackId, uEyeF, vEyeF, target.anchorZ, world, viewCamFallback, frame);

        Log(LogCategory.FOLLOW,
            $"f={frame} t={target.trackId} anchor=({target.anchorU},{target.anchorV}) uEye={uEyeF:F2} vEye={vEyeF:F2} screen={(isRightEye ? "R" : "L")} z={target.anchorZ:F3} pos=({world.x:F3},{world.y:F3},{world.z:F3})",
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
        bool rootRel = hipMid.magnitude < boneRootRelThreshold;

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

            // Root-relative判定は可視性に依存させず、元データ座標で評価する。
            Vector3 hipMid = Vector3.zero;
            if (idx.leftHip >= 0 && idx.rightHip >= 0 &&
                idx.leftHip < obj.jointsCam.Length && idx.rightHip < obj.jointsCam.Length)
            {
                hipMid = (obj.jointsCam[idx.leftHip] + obj.jointsCam[idx.rightHip]) * 0.5f;
            }
            bool rootRel = hipMid.magnitude < boneRootRelThreshold;
            int visOk = 0;
            Vector3[] jointsWorld = new Vector3[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                if (obj.jointsVis[i] > 0)
                {
                    visOk++;
                }

                Vector3 joint = obj.jointsCam[i];
                joint = new Vector3(joint.x * boneAxisSign.x, joint.y * boneAxisSign.y, joint.z * boneAxisSign.z);
                jointsWorld[i] = rootRel
                    ? rootWorld + (camRotation * joint)
                    : camOrigin + (camRotation * joint);
            }

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, jointsWorld, obj.jointsVis);
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
                return;
            }

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (obj.categoryId == 2)
            {
                ApplyAnimalSkeleton(instance.transform, animator, jointsWorld, obj.jointsVis, obj.skeletonKpCount, obj.categoryId, screen);
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

            bool applied = ApplyHumanoidLimbs(cache, jointsWorld, obj.jointsVis, idx);
        if (alignFeetToAnkles)
        {
            AlignFeetToAnkles(cache, jointsWorld, obj.jointsVis, idx, instance.transform);
        }

            if (applied && !boneAppliedLogged)
            {
                boneAppliedLogged = true;
                Log(LogCategory.BONE, "BONE_STATUS applied=true");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"TryApplySkeleton failed and was skipped. frame={frame} track={obj.trackId} ({ex.Message})");
        }
    }

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, byte categoryId, Transform screen)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null || jointCount < 20)
        {
            return;
        }

        // Root orientation:
        // - dog: yaw-only style using world-up (screen tilt is ignored)
        // - others: previous behavior using screen-up
        if (categoryId == 2)
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, null);
        }
        else
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, screen);
        }

        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot);
        if (cache == null || !cache.ready)
        {
            return;
        }

        float alpha = Mathf.Clamp01(boneApplyAlpha);
        // Dog is handled in a reduced-drive mode:
        // root heading + head + limbs (no spine drive).
        if (categoryId == 2)
        {
            // Head only.
            if (TryBuildDogHeadDirection(jointsWorld, vis, out Vector3 neckRoot, out Vector3 headTarget))
            {
                ApplyAnimalBoneFromPoints(cache, cache.neck, neckRoot, headTarget, alpha * 0.65f);
                ApplyAnimalBoneFromPoints(cache, cache.head, neckRoot, headTarget, alpha * 0.65f);
            }
            else
            {
                ApplyAnimalBoneFromJoints(cache, cache.neck, jointsWorld, vis, 5, 4, alpha * 0.65f); // Throat -> Nose
                ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, 5, 4, alpha * 0.65f); // Throat -> Nose
            }

            if (!enableAnimalLimbApply)
            {
                return;
            }

            // Front legs: segment mapping from joint points (J0->J1, J1->J2, J2->J3).
            ApplyAnimalLimbByJointSegments(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, DogLeftFrontChain, alpha);
            ApplyAnimalLimbByJointSegments(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, DogRightFrontChain, alpha);

            // Rear legs: segment mapping from joint points (J0->J1, J1->J2, J2->J3).
            ApplyAnimalLimbByJointSegments(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, DogLeftRearChain, alpha);
            ApplyAnimalLimbByJointSegments(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, DogRightRearChain, alpha);
            return;
        }

        if (!TryResolveAnimalGraphChains(categoryId, jointsWorld, vis, screen, out AnimalGraphChains chains))
        {
            // Fallback to previous fixed mapping if graph extraction fails.
            ApplyAnimalBoneFromJoints(cache, cache.neck, jointsWorld, vis, 4, 0, alpha * 0.65f);
            ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, 4, 0, alpha * 0.65f);
            if (enableAnimalSpineApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.spine, jointsWorld, vis, 7, 4, alpha * 0.5f);
            }
            ApplyAnimalBoneFromJoints(cache, cache.tailBase, jointsWorld, vis, 4, 7, alpha * 0.3f);
            ApplyAnimalBoneFromJoints(cache, cache.tailMid, jointsWorld, vis, 4, 7, alpha * 0.2f);
            ApplyAnimalBoneFromJoints(cache, cache.tailTip, jointsWorld, vis, 4, 7, alpha * 0.15f);
            if (enableAnimalLimbApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontUpper, jointsWorld, vis, 5, 8, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontLower, jointsWorld, vis, 8, 12, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontPaw, jointsWorld, vis, 12, 16, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontUpper, jointsWorld, vis, 6, 9, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontLower, jointsWorld, vis, 9, 13, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontPaw, jointsWorld, vis, 13, 17, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearUpper, jointsWorld, vis, 7, 10, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearLower, jointsWorld, vis, 10, 14, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearPaw, jointsWorld, vis, 14, 18, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearUpper, jointsWorld, vis, 7, 11, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearLower, jointsWorld, vis, 11, 15, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearPaw, jointsWorld, vis, 15, 19, alpha * 0.25f);
            }
            return;
        }

        if (chains.hasHead)
        {
            ApplyAnimalBoneFromPoints(cache, cache.neck, chains.headRoot, chains.headTip, alpha * 0.65f);
            ApplyAnimalBoneFromPoints(cache, cache.head, chains.headRoot, chains.headTip, alpha * 0.65f);
        }
        if (chains.hasTorso)
        {
            if (enableAnimalSpineApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.spine, jointsWorld, vis, chains.rearHub, chains.frontHub, alpha * 0.5f);
            }
            ApplyAnimalBoneFromJoints(cache, cache.tailBase, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.3f);
            ApplyAnimalBoneFromJoints(cache, cache.tailMid, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.2f);
            ApplyAnimalBoneFromJoints(cache, cache.tailTip, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.15f);
        }

        if (!enableAnimalLimbApply)
        {
            return;
        }

        ApplyAnimalLimbChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, chains.leftFrontChain, alpha);
        ApplyAnimalLimbChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, chains.rightFrontChain, alpha);
        ApplyAnimalLimbChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, chains.leftRearChain, alpha);
        ApplyAnimalLimbChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, chains.rightRearChain, alpha);
    }

    private bool TryBuildDogHeadDirection(Vector3[] jointsWorld, byte[] vis, out Vector3 neckRoot, out Vector3 headTarget)
    {
        neckRoot = Vector3.zero;
        headTarget = Vector3.zero;

        // Fixed semantic indices provided by user:
        // 0=L_Eye, 1=R_Eye, 4=Nose, 5=Throat.
        bool hasThroat = TryGetJointPoint(jointsWorld, vis, 5, out Vector3 throat);
        bool hasNose = TryGetJointPoint(jointsWorld, vis, 4, out Vector3 nose);
        bool hasEyesMid = TryGetMidPoint(jointsWorld, vis, 0, 1, out Vector3 eyesMid);

        if (!hasThroat)
        {
            return false;
        }

        Vector3 sum = Vector3.zero;
        float w = 0f;
        if (hasNose)
        {
            sum += nose * 0.55f;
            w += 0.55f;
        }
        if (hasEyesMid)
        {
            sum += eyesMid * 0.45f;
            w += 0.45f;
        }

        if (w <= 0f)
        {
            return false;
        }

        neckRoot = throat;
        headTarget = sum / w;
        if ((headTarget - neckRoot).sqrMagnitude < 0.000001f)
        {
            return false;
        }

        return true;
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, Vector3[] jointsWorld, byte[] vis, Transform screen)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null)
        {
            return;
        }

        if (!TryGetAnimalBodyDirection(jointsWorld, vis, out Vector3 bodyForward))
        {
            return;
        }

        Vector3 up = screen != null && screen.up.sqrMagnitude > 0.0001f ? screen.up.normalized : Vector3.up;
        Vector3 planarForward = Vector3.ProjectOnPlane(bodyForward, up);
        if (planarForward.sqrMagnitude < 0.000001f)
        {
            planarForward = bodyForward.normalized;
        }
        else
        {
            planarForward.Normalize();
        }

        Vector3 modelForward = animalModelForwardLocal.sqrMagnitude > 0.000001f
            ? animalModelForwardLocal.normalized
            : Vector3.right;
        Vector3 modelUp = animalModelUpLocal.sqrMagnitude > 0.000001f
            ? animalModelUpLocal.normalized
            : Vector3.up;

        Quaternion modelBasis = Quaternion.LookRotation(modelForward, modelUp);
        Quaternion targetBasis = Quaternion.LookRotation(planarForward, up);
        Quaternion targetRootRot = targetBasis * Quaternion.Inverse(modelBasis);
        instanceRoot.rotation = Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(animalRootRotateAlpha));
    }

    private bool TryGetAnimalBodyDirection(Vector3[] jointsWorld, byte[] vis, out Vector3 forward)
    {
        forward = Vector3.zero;
        bool hasRear = TryGetJointPoint(jointsWorld, vis, 6, out Vector3 rearHub);   // TailBase(hip)
        bool hasWithers = TryGetJointPoint(jointsWorld, vis, 7, out Vector3 withers);
        bool hasNose = TryGetJointPoint(jointsWorld, vis, 4, out Vector3 nose);

        if (hasRear && hasWithers)
        {
            forward = (withers - rearHub).normalized;
        }
        else if (hasRear && hasNose)
        {
            forward = (nose - rearHub).normalized;
        }
        else if (hasWithers && hasNose)
        {
            forward = (nose - withers).normalized;
        }

        return forward.sqrMagnitude > 0.000001f;
    }

    private struct AnimalGraphChains
    {
        public bool hasHead;
        public bool hasTorso;
        public int frontHub;
        public int rearHub;
        public Vector3 headRoot;
        public Vector3 headTip;
        public int[] leftFrontChain;
        public int[] rightFrontChain;
        public int[] leftRearChain;
        public int[] rightRearChain;
    }

    private bool TryResolveAnimalGraphChains(byte categoryId, Vector3[] jointsWorld, byte[] vis, Transform screen, out AnimalGraphChains chains)
    {
        chains = default;
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return false;
        }

        if (!TryGetCategoryEdges(categoryId, out ushort[] edgePairs) || edgePairs == null || edgePairs.Length < 2)
        {
            return false;
        }

        int n = jointsWorld.Length;
        List<int>[] adj = BuildJointAdjacency(n, edgePairs);
        if (adj == null)
        {
            return false;
        }

        List<int> endpoints = new List<int>();
        List<int> hubs = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int d = adj[i].Count;
            if (d == 1) endpoints.Add(i);
            if (d >= 3) hubs.Add(i);
        }
        if (hubs.Count < 2)
        {
            return false;
        }

        if (!TryFindHeadByEndpointParents(adj, endpoints, jointsWorld, vis, out int headEndA, out int headEndB, out int headRoot))
        {
            return false;
        }

        if (!TryFindFrontRearHubs(adj, hubs, headRoot, out int frontHub, out int rearHub))
        {
            return false;
        }

        Vector3 headTip = (jointsWorld[headEndA] + jointsWorld[headEndB]) * 0.5f;
        chains.hasHead = true;
        chains.headRoot = jointsWorld[headRoot];
        chains.headTip = headTip;
        chains.hasTorso = true;
        chains.frontHub = frontHub;
        chains.rearHub = rearHub;

        int frontLegHub = FindFrontLegHub(adj, frontHub, rearHub, headRoot);
        List<int[]> frontChains = new List<int[]>();
        if (frontLegHub >= 0)
        {
            for (int i = 0; i < adj[frontLegHub].Count; i++)
            {
                int next = adj[frontLegHub][i];
                if (next == frontHub || next == rearHub)
                {
                    continue;
                }

                if (TryTraceChainToEndpoint(adj, frontLegHub, next, out int[] chain))
                {
                    frontChains.Add(chain);
                }
            }
        }

        List<int[]> rearChains = new List<int[]>();
        for (int i = 0; i < adj[rearHub].Count; i++)
        {
            int next = adj[rearHub][i];
            if (next == frontHub || next == headRoot || next == frontLegHub)
            {
                continue;
            }

            if (TryTraceChainToEndpoint(adj, rearHub, next, out int[] chain))
            {
                rearChains.Add(chain);
            }
        }

        SplitLeftRightChains(frontChains, jointsWorld, screen, out chains.leftFrontChain, out chains.rightFrontChain);
        SplitLeftRightChains(rearChains, jointsWorld, screen, out chains.leftRearChain, out chains.rightRearChain);
        return true;
    }

    private List<int>[] BuildJointAdjacency(int jointCount, ushort[] edgePairs)
    {
        if (jointCount <= 0 || edgePairs == null || edgePairs.Length < 2)
        {
            return null;
        }

        var adj = new List<int>[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            adj[i] = new List<int>(4);
        }

        for (int i = 0; i + 1 < edgePairs.Length; i += 2)
        {
            int a = edgePairs[i];
            int b = edgePairs[i + 1];
            if (a < 0 || b < 0 || a >= jointCount || b >= jointCount || a == b)
            {
                continue;
            }

            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }

        return adj;
    }

    private bool TryFindHeadByEndpointParents(List<int>[] adj, List<int> endpoints, Vector3[] jointsWorld, byte[] vis, out int endA, out int endB, out int headRoot)
    {
        endA = -1;
        endB = -1;
        headRoot = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < endpoints.Count; i++)
        {
            int e1 = endpoints[i];
            if (e1 < 0 || e1 >= vis.Length || vis[e1] == 0 || adj[e1].Count != 1)
            {
                continue;
            }

            int p1 = adj[e1][0];
            for (int j = i + 1; j < endpoints.Count; j++)
            {
                int e2 = endpoints[j];
                if (e2 < 0 || e2 >= vis.Length || vis[e2] == 0 || adj[e2].Count != 1)
                {
                    continue;
                }

                int p2 = adj[e2][0];
                if (!adj[p1].Contains(p2))
                {
                    continue;
                }

                int common = -1;
                for (int k = 0; k < adj[p1].Count; k++)
                {
                    int c = adj[p1][k];
                    if (c != e1 && c != e2 && c != p2 && adj[p2].Contains(c))
                    {
                        common = c;
                        break;
                    }
                }
                if (common < 0)
                {
                    continue;
                }

                // Prefer the tighter pair likely representing the snout/face endpoints.
                float score = -Vector3.Distance(jointsWorld[e1], jointsWorld[e2]);
                if (score > bestScore)
                {
                    bestScore = score;
                    endA = e1;
                    endB = e2;
                    headRoot = common;
                }
            }
        }

        return endA >= 0 && endB >= 0 && headRoot >= 0;
    }

    private bool TryFindFrontRearHubs(List<int>[] adj, List<int> hubs, int headRoot, out int frontHub, out int rearHub)
    {
        frontHub = -1;
        rearHub = -1;
        int bestDegree = int.MinValue;
        for (int i = 0; i < hubs.Count; i++)
        {
            int h = hubs[i];
            if (!adj[headRoot].Contains(h))
            {
                continue;
            }
            if (adj[h].Count > bestDegree)
            {
                bestDegree = adj[h].Count;
                frontHub = h;
            }
        }
        if (frontHub < 0)
        {
            return false;
        }

        bestDegree = int.MinValue;
        for (int i = 0; i < hubs.Count; i++)
        {
            int h = hubs[i];
            if (h == frontHub || !adj[frontHub].Contains(h))
            {
                continue;
            }
            if (adj[h].Count > bestDegree)
            {
                bestDegree = adj[h].Count;
                rearHub = h;
            }
        }

        return rearHub >= 0;
    }

    private int FindFrontLegHub(List<int>[] adj, int frontHub, int rearHub, int headRoot)
    {
        int best = -1;
        int bestDegree = int.MinValue;
        for (int i = 0; i < adj[frontHub].Count; i++)
        {
            int n = adj[frontHub][i];
            if (n == rearHub || n == headRoot)
            {
                continue;
            }
            if (adj[n].Count > bestDegree)
            {
                bestDegree = adj[n].Count;
                best = n;
            }
        }
        return best;
    }

    private bool TryTraceChainToEndpoint(List<int>[] adj, int hub, int start, out int[] chain)
    {
        chain = null;
        List<int> path = new List<int>(5) { hub, start };
        int prev = hub;
        int cur = start;
        int guard = 0;
        while (guard++ < 16)
        {
            if (adj[cur].Count == 1)
            {
                chain = path.ToArray();
                return true;
            }

            int next = -1;
            for (int i = 0; i < adj[cur].Count; i++)
            {
                int c = adj[cur][i];
                if (c != prev)
                {
                    next = c;
                    break;
                }
            }
            if (next < 0 || adj[cur].Count > 3)
            {
                break;
            }

            prev = cur;
            cur = next;
            path.Add(cur);
        }

        if (path.Count >= 3)
        {
            chain = path.ToArray();
            return true;
        }

        return false;
    }

    private void SplitLeftRightChains(List<int[]> chains, Vector3[] jointsWorld, Transform screen, out int[] left, out int[] right)
    {
        left = null;
        right = null;
        if (chains == null || chains.Count == 0)
        {
            return;
        }
        if (chains.Count == 1)
        {
            left = chains[0];
            return;
        }

        Vector3 axis = screen != null && screen.right.sqrMagnitude > 0.0001f ? screen.right.normalized : Vector3.right;
        int[] c0 = chains[0];
        int[] c1 = chains[1];
        int e0 = c0[c0.Length - 1];
        int e1 = c1[c1.Length - 1];
        float d0 = Vector3.Dot(jointsWorld[e0], axis);
        float d1 = Vector3.Dot(jointsWorld[e1], axis);
        if (d0 <= d1)
        {
            left = c0;
            right = c1;
        }
        else
        {
            left = c1;
            right = c0;
        }
    }

    private void ApplyAnimalLimbChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha)
    {
        if (cache == null || upper == null || lower == null || chain == null || chain.Length < 3)
        {
            return;
        }

        int rootIdx = chain[0];
        int bendHintIdx = chain.Length >= 3 ? chain[1] : -1;
        // For 3-bone limb rigs (upper/lower/paw), solve IK to the mid joint (chain[2]),
        // then let paw bone handle the final segment (chain[2] -> chain[3]).
        int ikTargetIdx = chain.Length >= 4 ? chain[2] : chain[2];
        int pawIdx = chain.Length >= 4 ? chain[3] : chain[2];
        if (!TryGetJointPoint(jointsWorld, vis, rootIdx, out Vector3 rootHint) ||
            !TryGetJointPoint(jointsWorld, vis, ikTargetIdx, out Vector3 ikTarget))
        {
            return;
        }

        if (bendHintIdx >= 0 && !TryGetJointPoint(jointsWorld, vis, bendHintIdx, out _))
        {
            bendHintIdx = -1;
        }

        if (!TrySolveTwoBoneIkMidPoint(upper, lower, paw, rootHint, ikTarget, jointsWorld, vis, bendHintIdx, out Vector3 solvedMid))
        {
            // Fallback to directional FK if IK can't be solved.
            ApplyAnimalBoneFromJoints(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.8f);
            if (chain.Length >= 3)
            {
                ApplyAnimalBoneFromJoints(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.45f);
            }
            if (chain.Length >= 4 && paw != null)
            {
                ApplyAnimalBoneFromJoints(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.25f);
            }
            return;
        }

        Vector3 upperRoot = upper.position;
        ApplyAnimalBoneFromPointsLocalOnly(cache, upper, upperRoot, solvedMid, alpha * 0.95f);
        ApplyAnimalBoneFromPointsLocalOnly(cache, lower, lower.position, ikTarget, alpha * 0.85f);

        if (paw != null && chain.Length >= 4)
        {
            if (TryGetJointPoint(jointsWorld, vis, pawIdx, out Vector3 pawTarget))
            {
                ApplyAnimalBoneFromPointsLocalOnly(cache, paw, paw.position, pawTarget, alpha * 0.35f);
            }
            else
            {
                ApplyAnimalBoneFromJointsLocalOnly(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.25f);
            }
        }
    }

    private void ApplyAnimalLimbByJointSegments(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha)
    {
        if (cache == null || chain == null || chain.Length < 4)
        {
            return;
        }

        // Joint-centric mapping: each bone uses the segment between adjacent meta joints.
        ApplyAnimalBoneFromJointsLocalOnly(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.9f);
        ApplyAnimalBoneFromJointsLocalOnly(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.85f);
        if (paw != null)
        {
            ApplyAnimalBoneFromJointsLocalOnly(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.7f);
        }
    }

    private bool ApplyAnimalBoneFromJointsLocalOnly(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPointsLocalOnly(cache, bone, a, b, alpha);
    }

    private bool TrySolveTwoBoneIkMidPoint(
        Transform upper,
        Transform lower,
        Transform paw,
        Vector3 rootHint,
        Vector3 target,
        Vector3[] jointsWorld,
        byte[] vis,
        int kneeIdx,
        out Vector3 solvedMid)
    {
        solvedMid = Vector3.zero;
        if (upper == null || lower == null)
        {
            return false;
        }

        Vector3 root = upper.position;
        float l1 = Vector3.Distance(upper.position, lower.position);
        float l2 = 0f;
        if (paw != null)
        {
            l2 = Vector3.Distance(lower.position, paw.position);
        }
        if (l2 <= 0.0001f)
        {
            l2 = Mathf.Max(0.0001f, lower.childCount > 0
                ? Vector3.Distance(lower.position, lower.GetChild(0).position)
                : Vector3.Distance(lower.position, target));
        }
        if (l1 <= 0.0001f || l2 <= 0.0001f)
        {
            return false;
        }

        Vector3 toTarget = target - root;
        float d = toTarget.magnitude;
        if (d <= 0.0001f)
        {
            return false;
        }

        float maxReach = Mathf.Max(0.001f, l1 + l2 - 0.0001f);
        float minReach = Mathf.Abs(l1 - l2) + 0.0001f;
        d = Mathf.Clamp(d, minReach, maxReach);
        Vector3 dir = toTarget.normalized;

        float cosA = (l1 * l1 + d * d - l2 * l2) / (2f * l1 * d);
        cosA = Mathf.Clamp(cosA, -1f, 1f);
        float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));

        Vector3 bendNormal = Vector3.zero;
        if (kneeIdx >= 0 && TryGetJointPoint(jointsWorld, vis, kneeIdx, out Vector3 kneeHint))
        {
            bendNormal = Vector3.Cross(kneeHint - rootHint, target - kneeHint);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(upper.up, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(upper.right, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.up;
        }
        bendNormal.Normalize();

        Vector3 bendDir = Vector3.Cross(bendNormal, dir);
        if (bendDir.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        bendDir.Normalize();

        Vector3 candA = root + dir * (cosA * l1) + bendDir * (sinA * l1);
        Vector3 candB = root + dir * (cosA * l1) - bendDir * (sinA * l1);

        if (kneeIdx >= 0 && TryGetJointPoint(jointsWorld, vis, kneeIdx, out Vector3 kneeRef))
        {
            solvedMid = Vector3.Distance(candA, kneeRef) <= Vector3.Distance(candB, kneeRef) ? candA : candB;
        }
        else
        {
            solvedMid = candA;
        }

        return true;
    }

    private AnimalRigCache GetOrBuildAnimalRigCache(Transform root)
    {
        if (root == null)
        {
            return null;
        }

        if (animalRigCaches.TryGetValue(root, out AnimalRigCache existing))
        {
            return existing;
        }

        AnimalRigCache cache = new AnimalRigCache();
        cache.root = root;
        Transform[] bones = root.GetComponentsInChildren<Transform>(true);

        // DogRoot concrete parent bones (derived from mesh-node parents):
        // body->Bone, neck->Bone.007, head.001->Bone.009,
        // er.L/R->Bone.009_L/R.001,
        // arm.001/002/003.L/R->Bone_L/R.001/002/003,
        // foot.001/002/003.L/R->Bone.001_L/R.001/002/003.
        cache.neck =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.007") ??
            FindRigBoneFromMeshNodeName(bones, "neck") ??
            FindBoneByTokens(bones, "neck");
        cache.head =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009") ??
            FindRigBoneFromMeshNodeName(bones, "head.001") ??
            FindBoneByTokens(bones, "head");
        cache.leftEar =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "er.L") ??
            FindBoneByTokens(bones, "er.l");
        cache.rightEar =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "er.R") ??
            FindBoneByTokens(bones, "er.r");
        cache.spine =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3") ??
            FindRigBoneFromMeshNodeName(bones, "body") ??
            FindBoneByTokens(bones, "body", "spine", "chest", "back");
        cache.tailBase = FindBoneByTokens(bones, "tail.002", "tail");
        cache.tailMid = FindBoneByTokens(bones, "tail.003", "tail");
        cache.tailTip = FindBoneByTokens(bones, "tail.004", "tail");

        cache.leftFrontUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "arm.001.L") ??
            FindBoneByTokens(bones, "arm.001.l");
        cache.leftFrontLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.002") ??
            FindRigBoneFromMeshNodeName(bones, "arm.002.L") ??
            FindBoneByTokens(bones, "arm.002.l");
        cache.leftFrontPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.003") ??
            FindRigBoneFromMeshNodeName(bones, "arm.003.L") ??
            FindBoneByTokens(bones, "arm.003.l");
        cache.rightFrontUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "arm.001.R") ??
            FindBoneByTokens(bones, "arm.001.r");
        cache.rightFrontLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.002") ??
            FindRigBoneFromMeshNodeName(bones, "arm.002.R") ??
            FindBoneByTokens(bones, "arm.002.r");
        cache.rightFrontPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.003") ??
            FindRigBoneFromMeshNodeName(bones, "arm.003.R") ??
            FindBoneByTokens(bones, "arm.003.r");
        cache.leftRearUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "foot.001.L") ??
            FindBoneByTokens(bones, "foot.001.l", "foot.002.l");
        cache.leftRearLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.002") ??
            FindRigBoneFromMeshNodeName(bones, "foot.002.L") ??
            FindBoneByTokens(bones, "foot.002.l", "foot.003.l");
        cache.leftRearPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.003") ??
            FindRigBoneFromMeshNodeName(bones, "foot.003.L") ??
            FindBoneByTokens(bones, "foot.003.l", "foot.004.l");
        cache.rightRearUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "foot.001.R") ??
            FindBoneByTokens(bones, "foot.001.r", "foot.002.r");
        cache.rightRearLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.002") ??
            FindRigBoneFromMeshNodeName(bones, "foot.002.R") ??
            FindBoneByTokens(bones, "foot.002.r", "foot.003.r");
        cache.rightRearPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.003") ??
            FindRigBoneFromMeshNodeName(bones, "foot.003.R") ??
            FindBoneByTokens(bones, "foot.003.r", "foot.004.r");

        PrimeAnimalBind(cache, cache.neck);
        PrimeAnimalBind(cache, cache.head);
        PrimeAnimalBind(cache, cache.leftEar);
        PrimeAnimalBind(cache, cache.rightEar);
        PrimeAnimalBind(cache, cache.spine);
        PrimeAnimalBind(cache, cache.tailBase);
        PrimeAnimalBind(cache, cache.tailMid);
        PrimeAnimalBind(cache, cache.tailTip);
        PrimeAnimalBind(cache, cache.leftFrontUpper);
        PrimeAnimalBind(cache, cache.leftFrontLower);
        PrimeAnimalBind(cache, cache.leftFrontPaw);
        PrimeAnimalBind(cache, cache.rightFrontUpper);
        PrimeAnimalBind(cache, cache.rightFrontLower);
        PrimeAnimalBind(cache, cache.rightFrontPaw);
        PrimeAnimalBind(cache, cache.leftRearUpper);
        PrimeAnimalBind(cache, cache.leftRearLower);
        PrimeAnimalBind(cache, cache.leftRearPaw);
        PrimeAnimalBind(cache, cache.rightRearUpper);
        PrimeAnimalBind(cache, cache.rightRearLower);
        PrimeAnimalBind(cache, cache.rightRearPaw);

        RegisterAnimalAimChild(cache, cache.leftFrontUpper, cache.leftFrontLower);
        RegisterAnimalAimChild(cache, cache.leftFrontLower, cache.leftFrontPaw);
        RegisterAnimalAimChild(cache, cache.rightFrontUpper, cache.rightFrontLower);
        RegisterAnimalAimChild(cache, cache.rightFrontLower, cache.rightFrontPaw);
        RegisterAnimalAimChild(cache, cache.leftRearUpper, cache.leftRearLower);
        RegisterAnimalAimChild(cache, cache.leftRearLower, cache.leftRearPaw);
        RegisterAnimalAimChild(cache, cache.rightRearUpper, cache.rightRearLower);
        RegisterAnimalAimChild(cache, cache.rightRearLower, cache.rightRearPaw);
        RegisterAnimalAimChild(cache, cache.neck, cache.head);
        RegisterAnimalAimChild(cache, cache.spine, cache.neck);
        RegisterAnimalAimChild(cache, cache.tailBase, cache.tailMid);
        RegisterAnimalAimChild(cache, cache.tailMid, cache.tailTip);

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;
        return cache;
    }

    private Transform FindBoneByTokens(Transform[] bones, params string[] tokens)
    {
        if (bones == null || tokens == null || tokens.Length == 0)
        {
            return null;
        }

        Transform best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            string needle = token.ToLowerInvariant();
            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                string name = bone.name.ToLowerInvariant();
                if (name.Contains(needle))
                {
                    int score = 0;
                    if (bone.GetComponent<Renderer>() == null)
                    {
                        score += 2;
                    }
                    if (bone.childCount > 0)
                    {
                        score += 1;
                    }
                    if (name == needle)
                    {
                        score += 2;
                    }
                    else if (name.StartsWith(needle))
                    {
                        score += 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = bone;
                    }
                }
            }
        }

        return ResolveLikelyRigBone(best);
    }

    private Transform FindBoneByExactNames(Transform[] bones, params string[] exactNames)
    {
        if (bones == null || exactNames == null || exactNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < exactNames.Length; i++)
        {
            string exact = exactNames[i];
            if (string.IsNullOrEmpty(exact))
            {
                continue;
            }

            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                if (bone.name == exact)
                {
                    return ResolveLikelyRigBone(bone);
                }
            }
        }

        return null;
    }

    private Transform FindRigBoneFromMeshNodeName(Transform[] bones, string exactName)
    {
        if (bones == null || string.IsNullOrEmpty(exactName))
        {
            return null;
        }

        for (int i = 0; i < bones.Length; i++)
        {
            Transform t = bones[i];
            if (t == null || t.name != exactName)
            {
                continue;
            }

            return ResolveLikelyRigBone(t);
        }

        return null;
    }

    private Transform ResolveLikelyRigBone(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        // In this dog asset, names like arm.001.L / foot.001.L are mesh nodes.
        // Drive the parent rig bone instead of rotating mesh parts directly.
        bool hasMesh =
            node.GetComponent<MeshRenderer>() != null ||
            node.GetComponent<MeshFilter>() != null ||
            node.GetComponent<SkinnedMeshRenderer>() != null;
        if (hasMesh && node.parent != null)
        {
            return node.parent;
        }

        return node;
    }

    private void RegisterAnimalAimChild(AnimalRigCache cache, Transform bone, Transform aimChild)
    {
        if (cache == null || bone == null || aimChild == null)
        {
            return;
        }

        cache.aimChildByBone[bone] = aimChild;
    }

    private void PrimeAnimalBind(AnimalRigCache cache, Transform bone)
    {
        if (cache == null || bone == null || cache.bindRotLocal.ContainsKey(bone))
        {
            return;
        }

        cache.bindRotLocal[bone] = bone.localRotation;
        Vector3 bindDirLocal = Vector3.forward;
        if (bone.childCount > 0)
        {
            Vector3 childDirWorld = (bone.GetChild(0).position - bone.position);
            if (childDirWorld.sqrMagnitude > 0.000001f)
            {
                bindDirLocal = bone.InverseTransformDirection(childDirWorld.normalized);
            }
        }
        cache.bindDirLocal[bone] = bindDirLocal == Vector3.zero ? Vector3.forward : bindDirLocal.normalized;
    }

    private bool ApplyAnimalBoneFromJoints(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPoints(cache, bone, a, b, alpha);
    }

    private bool ApplyAnimalBoneFromPoints(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        Transform aimChild = ResolveAnimalAimChild(cache, bone);
        if (aimChild != null)
        {
            Vector3 currentDir = (aimChild.position - bone.position);
            if (currentDir.sqrMagnitude > 0.000001f)
            {
                currentDir.Normalize();
                Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
                Quaternion targetWorld = deltaWorld * bone.rotation;
                // When nearly opposite, FromTo rotation axis is unstable and can spin frame-to-frame.
                // Fall back to bind-space solve for deterministic behavior.
                float dot = Vector3.Dot(currentDir, targetDir);
                if (dot > -0.98f)
                {
                    bone.rotation = Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha));
                    return true;
                }
            }
        }

        Vector3 targetLocalDir = bone.parent != null
            ? bone.parent.InverseTransformDirection(targetDir)
            : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }

    private bool ApplyAnimalBoneFromPointsLocalOnly(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        Vector3 targetLocalDir = bone.parent != null
            ? bone.parent.InverseTransformDirection(targetDir)
            : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }

    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (cache != null && bone != null && cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            return mapped;
        }

        if (bone != null && bone.childCount > 0)
        {
            return bone.GetChild(0);
        }

        return null;
    }

    private void DrawSkeleton(Vector3[] jointsWorld, byte[] vis, byte categoryId)
    {
        if (jointsWorld == null || vis == null)
        {
            return;
        }

        if (TryGetCategoryEdges(categoryId, out ushort[] edgePairs) && edgePairs != null && edgePairs.Length >= 2)
        {
            for (int i = 0; i + 1 < edgePairs.Length; i += 2)
            {
                int a = edgePairs[i];
                int b = edgePairs[i + 1];
                if (a < 0 || b < 0 || a >= jointsWorld.Length || b >= jointsWorld.Length)
                {
                    continue;
                }

                if (vis[a] == 0 || vis[b] == 0)
                {
                    continue;
                }

                Debug.DrawLine(jointsWorld[a], jointsWorld[b], Color.yellow, 0f, false);
            }
            return;
        }

        for (int i = 0; i < CocoEdges.Length; i += 2)
        {
            int a = CocoEdges[i];
            int b = CocoEdges[i + 1];
            if (a < 0 || b < 0 || a >= jointsWorld.Length || b >= jointsWorld.Length)
            {
                continue;
            }

            if (vis[a] == 0 || vis[b] == 0)
            {
                continue;
            }

            Debug.DrawLine(jointsWorld[a], jointsWorld[b], Color.yellow, 0f, false);
        }
    }

    private HumanoidRigCache GetOrBuildHumanoidCache(Animator animator)
    {
        if (humanoidCaches.TryGetValue(animator, out HumanoidRigCache existing))
        {
            return existing;
        }

        var cache = new HumanoidRigCache();
        foreach (HumanBodyBones boneId in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform bone = animator.GetBoneTransform(boneId);
            if (bone == null)
            {
                continue;
            }

            cache.bones[boneId] = bone;
            cache.bindRotWorld[boneId] = bone.rotation;
            Vector3 dir = Vector3.forward;
            if (bone.childCount > 0)
            {
                dir = (bone.GetChild(0).position - bone.position).normalized;
            }
            cache.bindDirWorld[boneId] = dir == Vector3.zero ? Vector3.forward : dir;
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

    private SkeletonIndices ResolveSkeletonIndices(int jointCount)
    {
        // Keep schema choice strict to avoid misinterpreting unknown layouts.
        if (jointCount == 33)
        {
            return Blaze33Indices;
        }

        return Coco17Indices;
    }

    private bool ApplyHumanoidLimbs(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx)
    {
        if (cache == null || !cache.ready || jointsWorld == null || vis == null)
        {
            return false;
        }

        bool appliedAny = false;

        // Major limb chains (base behavior).
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftUpperArm, jointsWorld, vis, idx.leftShoulder, idx.leftElbow, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftLowerArm, jointsWorld, vis, idx.leftElbow, idx.leftWrist, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightUpperArm, jointsWorld, vis, idx.rightShoulder, idx.rightElbow, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightLowerArm, jointsWorld, vis, idx.rightElbow, idx.rightWrist, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftUpperLeg, jointsWorld, vis, idx.leftHip, idx.leftKnee, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftLowerLeg, jointsWorld, vis, idx.leftKnee, idx.leftAnkle, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightUpperLeg, jointsWorld, vis, idx.rightHip, idx.rightKnee, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightLowerLeg, jointsWorld, vis, idx.rightKnee, idx.rightAnkle, boneApplyAlpha);

        if (useExtendedBoneMap &&
            TryGetMidPoint(jointsWorld, vis, idx.leftHip, idx.rightHip, out Vector3 hipsMid) &&
            TryGetMidPoint(jointsWorld, vis, idx.leftShoulder, idx.rightShoulder, out Vector3 shouldersMid))
        {
            // Low-alpha torso/head mapping for naturalness.
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Hips, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Spine, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Chest, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.UpperChest, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.LeftShoulder, shouldersMid, jointsWorld[idx.leftShoulder], shoulderBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.RightShoulder, shouldersMid, jointsWorld[idx.rightShoulder], shoulderBoneApplyAlpha);

            if (TryGetHeadTarget(jointsWorld, vis, shouldersMid, idx, out Vector3 headTarget))
            {
                appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Neck, shouldersMid, headTarget, headBoneApplyAlpha);
                appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Head, shouldersMid, headTarget, headBoneApplyAlpha);
            }
        }

        return appliedAny;
    }

    private bool TryGetJointPoint(Vector3[] jointsWorld, byte[] vis, int idx, out Vector3 point)
    {
        point = Vector3.zero;
        if (jointsWorld == null || vis == null)
        {
            return false;
        }

        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length || vis[idx] == 0)
        {
            return false;
        }

        point = jointsWorld[idx];
        return true;
    }

    private bool TryGetMidPoint(Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, out Vector3 mid)
    {
        mid = Vector3.zero;
        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        mid = (a + b) * 0.5f;
        return true;
    }

    private bool TryGetHeadTarget(Vector3[] jointsWorld, byte[] vis, Vector3 shouldersMid, SkeletonIndices idx, out Vector3 headTarget)
    {
        headTarget = Vector3.zero;

        bool hasNose = TryGetJointPoint(jointsWorld, vis, idx.nose, out Vector3 nose);
        bool hasEyes = TryGetMidPoint(jointsWorld, vis, idx.leftEye, idx.rightEye, out Vector3 eyesMid);
        if (hasNose && hasEyes)
        {
            headTarget = (nose + eyesMid) * 0.5f;
            return true;
        }

        if (hasNose)
        {
            headTarget = nose;
            return true;
        }

        if (hasEyes)
        {
            headTarget = eyesMid;
            return true;
        }

        // Final fallback when face points are missing.
        headTarget = shouldersMid + Vector3.up * 0.12f;
        return true;
    }

    private void SmoothJointsWorld(uint trackId, Vector3[] jointsWorld, byte[] vis)
    {
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return;
        }

        if (!smoothedJointsByTrack.TryGetValue(trackId, out Vector3[] smoothed) || smoothed == null || smoothed.Length != jointsWorld.Length)
        {
            smoothed = new Vector3[jointsWorld.Length];
            for (int i = 0; i < jointsWorld.Length; i++)
            {
                smoothed[i] = jointsWorld[i];
            }
            smoothedJointsByTrack[trackId] = smoothed;
            return;
        }

        float a = Mathf.Clamp01(jointSmoothingAlpha);
        for (int i = 0; i < jointsWorld.Length; i++)
        {
            if (i >= vis.Length || vis[i] == 0)
            {
                jointsWorld[i] = smoothed[i];
                continue;
            }

            Vector3 next = Vector3.Lerp(smoothed[i], jointsWorld[i], a);
            smoothed[i] = next;
            jointsWorld[i] = next;
        }
    }

    private bool ApplyBoneFromJoints(HumanoidRigCache cache, HumanBodyBones boneId, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyBoneFromPoints(cache, boneId, a, b, alpha);
    }

    private bool ApplyBoneFromPoints(HumanoidRigCache cache, HumanBodyBones boneId, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (cache == null || !cache.ready)
        {
            return false;
        }

        if (!cache.bones.TryGetValue(boneId, out Transform bone))
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        if (!cache.bindDirWorld.TryGetValue(boneId, out Vector3 bindDir) || bindDir == Vector3.zero)
        {
            bindDir = Vector3.forward;
        }

        if (!cache.bindRotWorld.TryGetValue(boneId, out Quaternion bindRot))
        {
            bindRot = bone.rotation;
        }

        Quaternion targetRot = Quaternion.FromToRotation(bindDir, targetDir) * bindRot;
        bone.rotation = Quaternion.Slerp(bone.rotation, targetRot, Mathf.Clamp01(alpha));
        return true;
    }

    private void AlignFeetToAnkles(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx, Transform root)
    {
        if (!enableFootRootCorrection)
        {
            return;
        }

        if (cache == null || jointsWorld == null || vis == null || root == null)
        {
            return;
        }

        if (idx.leftAnkle < 0 || idx.rightAnkle < 0 ||
            idx.leftAnkle >= vis.Length || idx.rightAnkle >= vis.Length ||
            idx.leftAnkle >= jointsWorld.Length || idx.rightAnkle >= jointsWorld.Length)
        {
            return;
        }

        if (vis[idx.leftAnkle] == 0 || vis[idx.rightAnkle] == 0)
        {
            return;
        }

        if (!cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform leftFoot) ||
            !cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot))
        {
            return;
        }

        Vector3 targetMid = (jointsWorld[idx.leftAnkle] + jointsWorld[idx.rightAnkle]) * 0.5f;
        Vector3 currentMid = (leftFoot.position + rightFoot.position) * 0.5f;
        Vector3 delta = targetMid - currentMid;
        if (delta == Vector3.zero)
        {
            return;
        }

        // Guard against bad keypoint mapping spikes that can teleport the avatar.
        const float MaxFootAlignDeltaPerFrame = 0.08f;
        float mag = delta.magnitude;
        if (mag > MaxFootAlignDeltaPerFrame)
        {
            delta = delta * (MaxFootAlignDeltaPerFrame / mag);
        }

        // Feet alignment should mostly correct height; keep lateral shift small to reduce discomfort.
        Vector3 up = root.up.sqrMagnitude > 0.0001f ? root.up.normalized : Vector3.up;
        Vector3 vertical = Vector3.Project(delta, up);
        Vector3 lateral = delta - vertical;
        delta = vertical + lateral * 0.2f;

        root.position += delta * Mathf.Clamp01(footAlignAlpha);
    }

    private void UpdateAnchorDebugCubes(Transform screen, float uEye, float vEye, Vector3 worldPinhole, Camera viewCam, float bboxWorldH)
    {
        if (!showAnchorDebugCubes)
        {
            return;
        }

        if (screen == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (anchorPinholeCube == null)
        {
            anchorPinholeCube = CreateAnchorCube("AnchorPinholeCube", Color.cyan);
        }

        if (anchorScreenCube == null)
        {
            anchorScreenCube = CreateAnchorCube("AnchorScreenCube", Color.yellow);
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);
        Vector3 pinholePos = worldPinhole;
        Vector3 screenPos = worldOnPlane;
        if (anchorDebugAlignBottom && bboxWorldH > 0f)
        {
            Vector3 upCam = viewCam != null ? viewCam.transform.up : screen.up;
            pinholePos -= upCam * (bboxWorldH * 0.5f);
            screenPos -= screen.up * (bboxWorldH * 0.5f);
        }

        anchorPinholeCube.transform.position = pinholePos;
        anchorScreenCube.transform.position = screenPos;
    }

    private GameObject CreateAnchorCube(string name, Color color)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.localScale = Vector3.one * anchorDebugCubeSize;
        var collider = cube.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        var renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (mat == null)
            {
                mat = new Material(Shader.Find("Unlit/Color"));
            }
            if (mat != null)
            {
                mat.color = color;
                renderer.material = mat;
            }
        }

        return cube;
    }

    private void UpdateMetaRange(int frame)
    {
        if (metaRangeLogged || frame == lastMetaRangeFrame)
        {
            return;
        }

        lastMetaRangeFrame = frame;
        if (metaRangeStartFrame < 0)
        {
            metaRangeStartFrame = frame;
        }

        metaRangeFrameCount++;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            metaRangeMinU = Mathf.Min(metaRangeMinU, obj.anchorU);
            metaRangeMaxU = Mathf.Max(metaRangeMaxU, obj.anchorU);
            metaRangeMinV = Mathf.Min(metaRangeMinV, obj.anchorV);
            metaRangeMaxV = Mathf.Max(metaRangeMaxV, obj.anchorV);
            if (obj.hasSkeleton)
            {
                skeletonPresent = true;
            }
        }

        if (metaRangeFrameCount >= MetaRangeFrameWindow)
        {
            Log(LogCategory.META_RANGE,
                $"u[{metaRangeMinU},{metaRangeMaxU}] v[{metaRangeMinV},{metaRangeMaxV}] eyeH={manifest.eye_h} metaH={GetMetaH()} crop_y0={GetCropY()} crop_h={GetCropH()}");
            metaRangeLogged = true;
            if (!boneStatusLogged && !skeletonPresent)
            {
                boneStatusLogged = true;
                Log(LogCategory.BONE, "BONE_STATUS no skeleton");
            }
        }
    }

    private bool ShouldLogBoneDetails(int frame, int track)
    {
        if (debugLog == null)
        {
            return false;
        }

        return debugLog.onlyFrame >= 0 && debugLog.onlyTrack >= 0
            && debugLog.onlyFrame == frame && debugLog.onlyTrack == track;
    }

    private void LogBoneDetails(MetaObj obj, int frame)
    {
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null || obj.skeletonKpCount == 0)
        {
            return;
        }

        int kp = obj.skeletonKpCount;
        int show = Mathf.Min(3, kp);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("BONE_DETAIL f=");
        sb.Append(frame);
        sb.Append(" t=");
        sb.Append(obj.trackId);
        sb.Append(" kp=");
        sb.Append(kp);
        sb.Append(" joints=");
        for (int i = 0; i < show; i++)
        {
            Vector3 j = obj.jointsCam.Length > i ? obj.jointsCam[i] : Vector3.zero;
            if (i > 0)
            {
                sb.Append(",");
            }
            sb.Append("[");
            sb.Append(j.x.ToString("F3"));
            sb.Append(",");
            sb.Append(j.y.ToString("F3"));
            sb.Append(",");
            sb.Append(j.z.ToString("F3"));
            sb.Append("]");
        }
        Log(LogCategory.BONE, sb.ToString(), frame, (int)obj.trackId);
    }

    private void LogReprojectionError(uint trackId, float uEye, float vEye, float zMeters, Vector3 world, Camera viewCam, int frame)
    {
        return;
    }

    private bool ApplyCropToEyePixel(ref float uEye, ref float vEye)
    {
        if (manifest == null)
        {
            return false;
        }

        int cw = GetCropW();
        int ch = GetCropH();
        bool hasCrop = manifest.has_crop || (cw > 0 && ch > 0);
        if (!hasCrop)
        {
            return false;
        }

        int cx = GetCropX();
        int cy = GetCropY();
        if (cw <= 0 || ch <= 0)
        {
            return false;
        }

        uEye = (uEye - cx) / cw * manifest.eye_w;
        vEye = (vEye - cy) / ch * manifest.eye_h;
        uEye = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        vEye = Mathf.Clamp(vEye, 0f, manifest.eye_h - 1f);
        return true;
    }

    private void LogCropMapping(float uBefore, float vBefore, float uAfter, float vAfter, float bboxH, float bboxHAdjusted)
    {
        return;
    }

    private void DebugScreenPinholeConsistency(Transform screen, float uEye, float vEye, int frame, int track)
    {
        if (!verboseLog)
        {
            return;
        }

        if (!ShouldLog(LogCategory.PINHOLE_ERR, frame, track))
        {
            return;
        }

        if (screen == null || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        if (!TryGetFocalLengths(out float fx, out float fy))
        {
            return;
        }

        Camera viewCam = GetViewCamera() ?? Camera.main;
        if (viewCam == null)
        {
            return;
        }

        if (frame == lastScreenPinholeLogFrame)
        {
            return;
        }

        lastScreenPinholeLogFrame = frame;

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);

        float xNdc = (uEye / manifest.eye_w - 0.5f) * 2f;
        float yNdc = (0.5f - vEye / manifest.eye_h) * 2f;
        Vector3 dirCamLocal = new Vector3(xNdc / fx, yNdc / fy, 1f).normalized;
        Vector3 dirWorld = viewCam.transform.TransformDirection(dirCamLocal);

        GetScreenMeshLocalBounds(screen, out Vector3 center, out _);
        Vector3 planePoint = screen.TransformPoint(Vector3.Scale(center, screen.localScale));
        Plane plane = new Plane(screen.forward, planePoint);
        Ray ray = new Ray(viewCam.transform.position, dirWorld);
        if (!plane.Raycast(ray, out float t))
        {
            return;
        }

        Vector3 hit = ray.GetPoint(t);
        float err = Vector3.Distance(hit, worldOnPlane);
        float fovxDeg = 0f;
        TryGetFovxDeg(out fovxDeg);
        Log(LogCategory.PINHOLE_ERR,
            $"f={frame} t={track} err={err:F4} cam={viewCam.name} fov={fovxDeg:F1} screenDist={screenDistanceMeters:F3}",
            frame, track, err);

        LogScreenPinholeSamples(viewCam, screen, fx, fy, frame, track);
    }

    private void LogScreenPinholeSamples(Camera viewCam, Transform screen, float fx, float fy, int frame, int track)
    {
        if (!verboseLog)
        {
            return;
        }

        float now = Time.time;
        if (lastScreenPinholeSampleLogTime >= 0f && now - lastScreenPinholeSampleLogTime < 1f)
        {
            return;
        }

        lastScreenPinholeSampleLogTime = now;
        float w = manifest.eye_w;
        float h = manifest.eye_h;
        LogScreenPinholeSample(viewCam, screen, fx, fy, w * 0.5f, h * 0.5f, "center", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, 0f, 0f, "tl", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, w - 1f, 0f, "tr", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, 0f, h - 1f, "bl", frame, track);
        LogScreenPinholeSample(viewCam, screen, fx, fy, w - 1f, h - 1f, "br", frame, track);
    }

    private void LogScreenPinholeSample(Camera viewCam, Transform screen, float fx, float fy, float uEye, float vEye, string label, int frame, int track)
    {
        if (viewCam == null || screen == null)
        {
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(uEye, vEye, screen, manifest.eye_w, manifest.eye_h, 0f);
        float xNdc = (uEye / manifest.eye_w - 0.5f) * 2f;
        float yNdc = (0.5f - vEye / manifest.eye_h) * 2f;
        Vector3 dirCamLocal = new Vector3(xNdc / fx, yNdc / fy, 1f).normalized;
        Vector3 dirWorld = viewCam.transform.TransformDirection(dirCamLocal);
        GetScreenMeshLocalBounds(screen, out Vector3 center, out _);
        Vector3 planePoint = screen.TransformPoint(Vector3.Scale(center, screen.localScale));
        Plane plane = new Plane(screen.forward, planePoint);
        Ray ray = new Ray(viewCam.transform.position, dirWorld);
        if (!plane.Raycast(ray, out float t))
        {
            return;
        }

        Vector3 hit = ray.GetPoint(t);
        float err = Vector3.Distance(hit, worldOnPlane);
        float fovxDeg = 0f;
        TryGetFovxDeg(out fovxDeg);
        Log(LogCategory.PINHOLE_ERR,
            $"f={frame} t={track} sample={label} err={err:F4} cam={viewCam.name} fov={fovxDeg:F1} screenDist={screenDistanceMeters:F3}",
            frame, track, err);
    }

    private bool ResolveAnchorToScreen(ushort anchorU, out Transform screen, out int uEye, out bool isRightEye)
    {
        screen = pickedScreen != null ? pickedScreen : leftScreen;
        uEye = anchorU;
        isRightEye = false;

        if (manifest == null || manifest.eye_w <= 0)
        {
            return false;
        }

        int fullWidth = GetFullWidth();
        if (fullWidth >= manifest.eye_w * 2 && rightScreen != null)
        {
            if (anchorU >= manifest.eye_w)
            {
                screen = rightScreen;
                uEye = anchorU - manifest.eye_w;
                isRightEye = true;
            }
            else
            {
                screen = leftScreen;
                uEye = anchorU;
            }
        }

        if (screen == null)
        {
            return false;
        }

        uEye = Mathf.Clamp(uEye, 0, manifest.eye_w - 1);
        return true;
    }

    private void TrySpawnTestModel()
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (leftScreen == null)
        {
            Debug.LogWarning("Test model skipped: leftScreen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("Test model skipped: manifest eye_w/eye_h invalid or not loaded.");
            return;
        }

        Vector2Int finalPixel = testPixel;
        if (finalPixel.x < 0 || finalPixel.y < 0)
        {
            finalPixel = new Vector2Int(manifest.eye_w / 2, manifest.eye_h / 2);
        }

        TrySpawnOrMoveTestModelAtPixel(leftScreen, finalPixel.x, finalPixel.y);
    }

    private void TrySpawnOrMoveTestModel(PickResult pick)
    {
        if (pick.screen == null)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModel: screen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModel: manifest not ready.");
            return;
        }

        if (!pick.hasHitDistance)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        Vector3 rayDir = pick.ray.direction.normalized;
        if (rayDir == Vector3.zero)
        {
            TrySpawnOrMoveTestModelAtPixel(pick.screen, pick.pixel.x, pick.pixel.y);
            return;
        }

        float depthTowardCamera = testDepthMeters;
        float placeDist = Mathf.Max(0.05f, pick.hitDistance - depthTowardCamera);
        Vector3 world = pick.ray.origin + rayDir * placeDist;

        Vector3 right = Vector3.Cross(Vector3.up, rayDir);
        if (right.sqrMagnitude < 0.000001f)
        {
            right = pick.screen.right;
        }
        right.Normalize();
        Vector3 up = Vector3.Cross(rayDir, right).normalized;

        world += right * testModelOffsetMeters.x + up * testModelOffsetMeters.y;
        Quaternion rotation = Quaternion.LookRotation(-rayDir, up);

        ApplyTestModelTransform(world, rotation, pick.screen, pick.pixel.x, pick.pixel.y, pick.hitDistance, depthTowardCamera, "ray");
    }

    private void TrySpawnOrMoveTestModelAtPixel(Transform screen, int u, int v)
    {
        if (replacePrefab != null)
        {
            return;
        }

        if (screen == null)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: screen is null.");
            return;
        }

        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            Debug.LogWarning("TrySpawnOrMoveTestModelAtPixel: manifest not ready.");
            return;
        }

        Vector3 worldOnPlane = EyePixelToWorldOnScreen(u, v, screen, manifest.eye_w, manifest.eye_h, 0f);
        Transform head = GetViewCamera() != null ? GetViewCamera().transform : GetHeadTransform();
        Vector3 frontDir = (head != null)
            ? (head.position - screen.position).normalized
            : GetScreenFrontDirection(screen);
        if (frontDir == Vector3.zero)
        {
            frontDir = GetScreenFrontDirection(screen);
        }

        float dist = head != null ? Vector3.Distance(head.position, screen.position) : testDepthMeters;
        float maxDepth = Mathf.Max(0.01f, dist - 0.05f);
        float depth = Mathf.Clamp(testDepthMeters, 0.01f, maxDepth);

        Vector3 world = worldOnPlane
            + screen.right * testModelOffsetMeters.x
            + screen.up * testModelOffsetMeters.y
            + frontDir * depth;
        Quaternion rotation = Quaternion.LookRotation(-frontDir, screen.up);

        ApplyTestModelTransform(world, rotation, screen, u, v, dist, depth, "pixel");
    }

    private void ApplyTestModelTransform(Vector3 world, Quaternion rotation, Transform screen, int u, int v, float dist, float depth, string mode)
    {
        if (destroyPreviousTestModel && spawnedTestModel != null)
        {
            Destroy(spawnedTestModel);
            spawnedTestModel = null;
        }

        if (spawnedTestModel == null)
        {
            spawnedTestModel = testModelPrefab != null
                ? Instantiate(testModelPrefab, world, rotation)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            spawnedTestModel.name = "TestModel(auto)";
            if (testModelPrefab == null)
            {
                var collider = spawnedTestModel.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }
        }
        spawnedTestModel.transform.SetParent(null, true);
        EnsureTestModelComponents(spawnedTestModel);
        spawnedTestModel.transform.SetPositionAndRotation(world, rotation);
        spawnedTestModel.transform.localScale = Vector3.one * testModelSizeMeters;
        LogModel($"SpawnOrMoveTestModel({mode}): screen={screen.name} pixel=({u},{v}) world={world} rot={rotation.eulerAngles}");
        LogModel($"SpawnOrMoveTestModelDepth({mode}): dist={dist:F3} depth={depth:F3} screen={screen.position}");
        LogModel(
            $"SpawnOrMoveTestModelDebug: worldPos={spawnedTestModel.transform.position} localPos={spawnedTestModel.transform.localPosition} " +
            $"parent={(spawnedTestModel.transform.parent != null ? spawnedTestModel.transform.parent.name : "null")}");
        AttachTransformLock(spawnedTestModel, world, rotation);

        float posError = Vector3.Distance(spawnedTestModel.transform.position, world);
        if (posError > 0.001f)
        {
            Debug.LogWarning(
                $"TestModelPositionMismatch: expected={world} actual={spawnedTestModel.transform.position} " +
                $"error={posError:F4} active={spawnedTestModel.activeInHierarchy} " +
                $"components={DescribeMovementComponents(spawnedTestModel)}");
        }
    }

    private void EnsureTestModelComponents(GameObject model)
    {
        if (model == null)
        {
            return;
        }

        var rb = model.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        var animator = model.GetComponent<Animator>();
        if (animator != null)
        {
            animator.enabled = false;
        }
    }

    private void AttachTransformLock(GameObject model, Vector3 world, Quaternion rotation)
    {
        if (model == null)
        {
            return;
        }

        var locker = model.GetComponent<TestModelTransformLock>();
        if (locker == null)
        {
            locker = model.AddComponent<TestModelTransformLock>();
        }

        locker.Arm(world, rotation, TestModelLockFrames);
    }

    private string DescribeMovementComponents(GameObject go)
    {
        if (go == null)
        {
            return "null";
        }

        var components = go.GetComponents<Component>();
        string allComponents = components != null && components.Length > 0
            ? string.Join(",", System.Array.ConvertAll(components, c => c != null ? c.GetType().Name : "null"))
            : "none";

        return $"Components[{allComponents}]";
    }

    private sealed class TestModelTransformLock : MonoBehaviour
    {
        private Vector3 targetPos;
        private Quaternion targetRot;
        private int framesLeft;

        public void Arm(Vector3 world, Quaternion rotation, int frames)
        {
            targetPos = world;
            targetRot = rotation;
            framesLeft = Mathf.Max(1, frames);
        }

        private void LateUpdate()
        {
            if (framesLeft <= 0)
            {
                Destroy(this);
                return;
            }

            transform.SetPositionAndRotation(targetPos, targetRot);
            framesLeft--;
        }
    }
}
