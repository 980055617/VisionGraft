using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int TestModelLockFrames = 30;
    private const int MetaRangeFrameWindow = 60;
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private const int MaxJointInvalidLogsPerFrame = 6;
    private const int MaxFrameApplySummaryLogsPerFrame = 4;
    private const int DiagFrameStart = 196;
    private const int DiagFrameEnd = 212;
    private const int MaxDiagLogsPerFrame = 24;
    private int lastAutoTrackId = int.MinValue;
    private int lastScreenPinholeLogFrame = -1;
    private float lastScreenPinholeSampleLogTime = -1f;
    private readonly Dictionary<uint, GameObject> trackInstances = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, GameObject> trackPrefabSources = new Dictionary<uint, GameObject>();
    private readonly Dictionary<uint, SortedDictionary<int, float>> manualYawKeyframesByTrack = new Dictionary<uint, SortedDictionary<int, float>>();
    private int selectedManualRotationTrackId = -1;
    private GameObject manualYawGuideRoot;
    private Transform manualYawGuideShaft;
    private Transform manualYawGuideTip;
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
    private readonly Dictionary<Transform, Vector3> debugAutoAxisByBone = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, Quaternion> debugAutoRestLocalRotByBone = new Dictionary<Transform, Quaternion>();
    private readonly HashSet<Transform> debugAutoAxisPickLogged = new HashSet<Transform>();
    private readonly HashSet<Transform> dogBonesDumpLoggedRoots = new HashSet<Transform>();
    private readonly HashSet<Transform> dogMappingLoggedRoots = new HashSet<Transform>();
    private readonly HashSet<int> animatorMetaLockLogged = new HashSet<int>();
    private int debugJointContextFrame = -1;
    private uint debugJointContextTrackId = 0u;
    private int debugJointInvalidLogFrame = -1;
    private int debugJointInvalidLogCount = 0;
    private int debugFrameApplySummaryLogFrame = -1;
    private int debugFrameApplySummaryLogCount = 0;
    private int debugDiagLogFrame = -1;
    private int debugDiagLogCount = 0;
    private readonly List<AnimatorCheckSample> pendingAnimatorChecks = new List<AnimatorCheckSample>(8);
    private sealed class DebugDrawTrackState
    {
        public Vector3[] jointsWorld;
        public byte[] jointsVis;
        public float[] jointsCamZ;
        public int jointCount;
        public byte categoryId;
        public bool hasAnchor;
        public Vector3 anchorWorld;
        public bool hasAxisCompare;
        public string axisBoneName;
        public int axisIdxA = -1;
        public int axisIdxB = -1;
        public Vector3 axisBase;
        public Vector3 axisTargetDir;
        public Vector3 axisBoneDir;
        public float axisAngleDeg;
        public int skeletonSkipCount;
    }
    private sealed class Meta2DOverlayItem
    {
        public uint trackId;
        public Rect eyeRect;
        public Vector2 anchor;
        public Rect bbox;
    }
    private sealed class Joints2DOverlayPoint
    {
        public Vector2 pos;
        public Color color;
    }
    private sealed class DebugProcessedJointState
    {
        public int frame = -1;
        public Vector3[] jointsCamProcessed;
        public byte[] jointsVis;
    }
    private sealed class AnimatorCheckSample
    {
        public int frame;
        public uint trackId;
        public string boneName;
        public Transform bone;
        public Vector3 boneBeforeApply;
        public Vector3 boneAfterApply;
        public bool animatorEnabled;
        public AnimatorUpdateMode updateMode;
    }
    private readonly Dictionary<uint, DebugDrawTrackState> debugDrawStateByTrack = new Dictionary<uint, DebugDrawTrackState>();
    private readonly List<Meta2DOverlayItem> meta2DOverlayItems = new List<Meta2DOverlayItem>(64);
    private readonly List<Joints2DOverlayPoint> joints2DOverlayPoints = new List<Joints2DOverlayPoint>(256);
    private readonly Dictionary<uint, DebugProcessedJointState> debugProcessedJointsByTrack = new Dictionary<uint, DebugProcessedJointState>();
    private int lastMeta2DLogFrame = -1;
    private int lastJoints2DLogFrame = -1;
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

    private Quaternion ApplyManualTrackYawOffset(uint trackId, int frame, Quaternion baseRotation, Vector3 upAxis)
    {
        float yawDeg = EvaluateManualYawOffsetDegForFrame(trackId, frame);

        if (Mathf.Abs(yawDeg) < 0.001f)
        {
            return baseRotation;
        }

        if (upAxis.sqrMagnitude < 0.000001f)
        {
            upAxis = Vector3.up;
        }

        return Quaternion.AngleAxis(yawDeg, upAxis.normalized) * baseRotation;
    }

    private float EvaluateManualYawOffsetDegForFrame(uint trackId, int frame)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null || keys.Count == 0)
        {
            return 0f;
        }

        if (keys.Count == 1)
        {
            foreach (KeyValuePair<int, float> kv in keys)
            {
                return kv.Value;
            }
        }

        int firstFrame = int.MaxValue;
        int lastFrame = int.MinValue;
        float firstYaw = 0f;
        float lastYaw = 0f;
        int prevFrame = int.MinValue;
        int nextFrame = int.MaxValue;
        float prevYaw = 0f;
        float nextYaw = 0f;

        foreach (KeyValuePair<int, float> kv in keys)
        {
            int keyFrame = kv.Key;
            float keyYaw = kv.Value;
            if (keyFrame < firstFrame)
            {
                firstFrame = keyFrame;
                firstYaw = keyYaw;
            }
            if (keyFrame > lastFrame)
            {
                lastFrame = keyFrame;
                lastYaw = keyYaw;
            }

            if (keyFrame <= frame && keyFrame > prevFrame)
            {
                prevFrame = keyFrame;
                prevYaw = keyYaw;
            }
            if (keyFrame >= frame && keyFrame < nextFrame)
            {
                nextFrame = keyFrame;
                nextYaw = keyYaw;
            }
        }

        if (frame <= firstFrame)
        {
            return firstYaw;
        }
        if (frame >= lastFrame)
        {
            return lastYaw;
        }
        if (prevFrame == int.MinValue)
        {
            return nextYaw;
        }
        if (nextFrame == int.MaxValue)
        {
            return prevYaw;
        }
        if (prevFrame == nextFrame)
        {
            return prevYaw;
        }

        float t = Mathf.InverseLerp(prevFrame, nextFrame, frame);
        return Mathf.Lerp(prevYaw, nextYaw, t);
    }

    private bool TryGetSelectedManualRotationTrack(out uint trackId)
    {
        trackId = 0u;
        if (selectedManualRotationTrackId < 0)
        {
            return false;
        }

        trackId = (uint)selectedManualRotationTrackId;
        return true;
    }

    private void EnsureSelectedManualRotationTrack()
    {
        if (selectedManualRotationTrackId >= 0)
        {
            return;
        }

        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids.Count <= 0)
        {
            return;
        }

        selectedManualRotationTrackId = (int)ids[0];
    }

    private bool StepSelectedManualRotationTrack(int direction)
    {
        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids.Count <= 0)
        {
            selectedManualRotationTrackId = -1;
            return false;
        }

        if (direction == 0)
        {
            selectedManualRotationTrackId = (int)ids[0];
            return true;
        }

        int current = selectedManualRotationTrackId;
        int index = ids.FindIndex(id => id == (uint)current);
        if (index < 0)
        {
            selectedManualRotationTrackId = (int)ids[0];
            return true;
        }

        int next = index + (direction > 0 ? 1 : -1);
        if (next < 0)
        {
            next = ids.Count - 1;
        }
        else if (next >= ids.Count)
        {
            next = 0;
        }

        selectedManualRotationTrackId = (int)ids[next];
        return true;
    }

    private List<uint> GetAvailableTrackIdsForManualRotation()
    {
        var ids = new List<uint>();
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value == null || !kv.Value.activeInHierarchy)
            {
                continue;
            }
            ids.Add(kv.Key);
        }

        ids.Sort();
        return ids;
    }

    private float GetManualYawOffsetDegForTrack(uint trackId)
    {
        return EvaluateManualYawOffsetDegForFrame(trackId, GetCurrentFrameIndex());
    }

    private void SetManualYawOffsetDegForTrack(uint trackId, float yawDeg)
    {
        int frame = GetCurrentFrameIndex();
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            keys = new SortedDictionary<int, float>();
            manualYawKeyframesByTrack[trackId] = keys;
        }

        keys[frame] = Mathf.Clamp(yawDeg, -180f, 180f);
    }

    private void ResetManualYawOffsetDegForTrack(uint trackId)
    {
        SetManualYawOffsetDegForTrack(trackId, 0f);
    }

    private int GetManualYawKeyCountForTrack(uint trackId)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return 0;
        }

        return keys.Count;
    }

    private bool HasManualYawKeyAtCurrentFrame(uint trackId)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return false;
        }

        return keys.ContainsKey(GetCurrentFrameIndex());
    }

    private void UpdateManualYawGuide(bool visible)
    {
        if (!visible)
        {
            SetManualYawGuideVisible(false);
            return;
        }

        if (!TryResolveGuideTrackInstance(out _, out GameObject instance))
        {
            SetManualYawGuideVisible(false);
            return;
        }

        EnsureManualYawGuideCreated();
        if (manualYawGuideShaft == null || manualYawGuideTip == null)
        {
            return;
        }

        Bounds b = ComputeObjectBounds(instance);
        float height = Mathf.Max(0.1f, b.size.y);
        float len = Mathf.Clamp(height * 0.5f, 0.3f, 0.85f);
        float y = Mathf.Clamp(height * 1.1f, 0.8f, 2.4f);

        if (manualYawGuideRoot.transform.parent != instance.transform)
        {
            manualYawGuideRoot.transform.SetParent(instance.transform, false);
        }
        manualYawGuideRoot.transform.localPosition = Vector3.zero;
        manualYawGuideRoot.transform.localRotation = Quaternion.identity;
        manualYawGuideRoot.transform.localScale = Vector3.one;

        manualYawGuideShaft.localPosition = new Vector3(0f, y, len * 0.5f);
        manualYawGuideShaft.localRotation = Quaternion.identity;
        manualYawGuideShaft.localScale = new Vector3(0.04f, 0.04f, len);
        manualYawGuideTip.localPosition = new Vector3(0f, y, len);
        manualYawGuideTip.localRotation = Quaternion.identity;
        manualYawGuideTip.localScale = new Vector3(0.14f, 0.14f, 0.14f);
        SetManualYawGuideVisible(true);
    }

    private bool TryResolveGuideTrackInstance(out uint trackId, out GameObject instance)
    {
        trackId = 0u;
        instance = null;

        if (TryGetSelectedManualRotationTrack(out uint selectedId) &&
            trackInstances.TryGetValue(selectedId, out GameObject selected) &&
            selected != null && selected.activeInHierarchy)
        {
            trackId = selectedId;
            instance = selected;
            return true;
        }

        selectedManualRotationTrackId = -1;
        EnsureSelectedManualRotationTrack();
        if (TryGetSelectedManualRotationTrack(out uint ensuredId) &&
            trackInstances.TryGetValue(ensuredId, out GameObject ensured) &&
            ensured != null && ensured.activeInHierarchy)
        {
            trackId = ensuredId;
            instance = ensured;
            return true;
        }

        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value == null || !kv.Value.activeInHierarchy)
            {
                continue;
            }

            trackId = kv.Key;
            instance = kv.Value;
            selectedManualRotationTrackId = (int)kv.Key;
            return true;
        }

        return false;
    }

    private void EnsureManualYawGuideCreated()
    {
        if (manualYawGuideRoot != null)
        {
            return;
        }

        manualYawGuideRoot = new GameObject("ManualYawGuide");
        manualYawGuideShaft = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
        manualYawGuideShaft.name = "Shaft";
        manualYawGuideShaft.SetParent(manualYawGuideRoot.transform, false);
        RemoveGuideCollider(manualYawGuideShaft.gameObject);
        TintGuideMesh(manualYawGuideShaft.gameObject, new Color(1f, 0.1f, 0.1f, 1f));

        manualYawGuideTip = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
        manualYawGuideTip.name = "Tip";
        manualYawGuideTip.SetParent(manualYawGuideRoot.transform, false);
        RemoveGuideCollider(manualYawGuideTip.gameObject);
        TintGuideMesh(manualYawGuideTip.gameObject, new Color(1f, 0.35f, 0.35f, 1f));

        SetManualYawGuideVisible(false);
    }

    private static void RemoveGuideCollider(GameObject go)
    {
        if (go == null)
        {
            return;
        }

        Collider c = go.GetComponent<Collider>();
        if (c != null)
        {
            Destroy(c);
        }
    }

    private static void TintGuideMesh(GameObject go, Color color)
    {
        if (go == null)
        {
            return;
        }

        Renderer r = go.GetComponent<Renderer>();
        if (r == null)
        {
            return;
        }

        Material m = r.material;
        if (m == null)
        {
            return;
        }

        if (m.HasProperty("_BaseColor"))
        {
            m.SetColor("_BaseColor", color);
        }
        if (m.HasProperty("_Color"))
        {
            m.SetColor("_Color", color);
        }

        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color * 0.7f);
        }
    }

    private static Bounds ComputeObjectBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            return new Bounds(go.transform.position, Vector3.one * 0.2f);
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            b.Encapsulate(renderers[i].bounds);
        }
        return b;
    }

    private void SetManualYawGuideVisible(bool visible)
    {
        if (manualYawGuideRoot == null)
        {
            return;
        }

        manualYawGuideRoot.SetActive(visible);
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

            // Root-relative判定は可視性に依存させず、元データ座標で評価する。
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

    private bool IsJointDebugEnabled()
    {
        return debugDrawJoints || debugDrawAnchor || debugDisableRigApply;
    }

    private void LogJointDebugSkip(string reason, int frame, uint trackId)
    {
        if (!IsJointDebugEnabled())
        {
            return;
        }

        Debug.Log($"JOINT_DEBUG_SKIP frame={frame} trackId={trackId} reason={reason}");
    }

    private void LogMeta2DFrameSummaryOnce(int frame)
    {
        if (!debugDrawMeta2D || frame == lastMeta2DLogFrame || metaFrameObjects == null || metaFrameObjects.Count == 0 || manifest == null)
        {
            return;
        }

        lastMeta2DLogFrame = frame;
        int minU = int.MaxValue;
        int maxU = int.MinValue;
        int minV = int.MaxValue;
        int maxV = int.MinValue;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            minU = Mathf.Min(minU, obj.anchorU);
            maxU = Mathf.Max(maxU, obj.anchorU);
            minV = Mathf.Min(minV, obj.anchorV);
            maxV = Mathf.Max(maxV, obj.anchorV);
        }

        Debug.Log(
            $"META_2D frame={frame} eye=({manifest.eye_w},{manifest.eye_h}) crop=({GetCropW()},{GetCropH()}) cropXY=({GetCropX()},{GetCropY()}) " +
            $"anchorU=[{minU},{maxU}] anchorV=[{minV},{maxV}] toggles(norm={uvIsNormalized},flipU={flipU},flipV={flipV},applyCropScale={applyCropScale})");
    }

    private void BuildJoints2DOverlayAndLog(int frame)
    {
        if (!debugDrawJoints2D || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || metaFrameObjects == null || metaFrameObjects.Count == 0)
        {
            return;
        }

        bool hasFxFy = TryGetProjectionIntrinsics(out float fx, out float fy, out float cx, out float cy);
        bool hasManifestNormIntrinsics = TryGetManifestNormalizedIntrinsics(out float fxNorm, out float fyNorm, out int intrEyeW, out int intrEyeH);
        int totalKp = 0;
        int totalValid = 0;
        int insideCount = 0;
        int zNonPositiveSkipped = 0;
        int zEq0Skipped = 0;
        float minAnchorZ = float.MaxValue;
        float maxAnchorZ = float.MinValue;
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        bool hasJointSamples = false;
        float projMinX = float.MaxValue;
        float projMaxX = float.MinValue;
        float projMinY = float.MaxValue;
        float projMaxY = float.MinValue;
        float projMinZ = float.MaxValue;
        float projMaxZ = float.MinValue;
        bool hasProjectedSourceSamples = false;
        int processedSourceObjCount = 0;
        int rawSourceObjCount = 0;
        bool loggedRefSet = false;
        float logRefBboxW = 0f;
        float logRefBboxH = 0f;
        float logRefAnchorU = 0f;
        float logRefAnchorV = 0f;

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            minAnchorZ = Mathf.Min(minAnchorZ, obj.anchorZ);
            maxAnchorZ = Mathf.Max(maxAnchorZ, obj.anchorZ);

            if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null || obj.skeletonKpCount <= 0)
            {
                continue;
            }

            if (!ResolveAnchorToScreen(obj.anchorU, out Transform screen, out int anchorUEye, out bool isRightEye))
            {
                continue;
            }
            if (!TryGetEyeScreenRect(screen, out Rect eyeRect))
            {
                continue;
            }

            float anchorUBase = anchorUEye;
            float anchorVBase = obj.anchorV;

            float bboxX = obj.bboxX;
            if (isRightEye && bboxX >= manifest.eye_w)
            {
                bboxX -= manifest.eye_w;
            }
            float bboxY = obj.bboxY;
            float bboxX2 = bboxX + obj.bboxW;
            float bboxY2 = bboxY + obj.bboxH;
            if (!TryMapMetaUvToEyePixel(ref bboxX, ref bboxY))
            {
                continue;
            }
            if (!TryMapMetaUvToEyePixel(ref bboxX2, ref bboxY2))
            {
                continue;
            }

            float bboxMinU = Mathf.Min(bboxX, bboxX2);
            float bboxMaxU = Mathf.Max(bboxX, bboxX2);
            float bboxMinV = Mathf.Min(bboxY, bboxY2);
            float bboxMaxV = Mathf.Max(bboxY, bboxY2);

            if (!loggedRefSet)
            {
                loggedRefSet = true;
                logRefBboxW = obj.bboxW;
                logRefBboxH = obj.bboxH;
                logRefAnchorU = obj.anchorU;
                logRefAnchorV = obj.anchorV;
            }

            Vector3[] drawJoints = obj.jointsCam;
            byte[] drawVis = obj.jointsVis;
            bool usingProcessedSource = false;
            if (joints2DMode == Joints2DMode.ProjectXYZ && !debugProjectXYZUseRaw &&
                debugProcessedJointsByTrack.TryGetValue(obj.trackId, out DebugProcessedJointState procState) &&
                procState != null && procState.frame == frame && procState.jointsCamProcessed != null)
            {
                drawJoints = procState.jointsCamProcessed;
                if (procState.jointsVis != null)
                {
                    drawVis = procState.jointsVis;
                }
                usingProcessedSource = true;
            }
            if (joints2DMode == Joints2DMode.ProjectXYZ)
            {
                if (usingProcessedSource)
                {
                    processedSourceObjCount++;
                }
                else
                {
                    rawSourceObjCount++;
                }
            }

            int kp = Mathf.Min((int)obj.skeletonKpCount, Mathf.Min(obj.jointsCam.Length, Mathf.Min(drawJoints.Length, drawVis.Length)));
            totalKp += kp;
            Color jointColor = (obj.trackId % 2u == 0u) ? new Color(1f, 1f, 0f, 0.95f) : new Color(1f, 0.35f, 0.15f, 0.95f);

            for (int j = 0; j < kp; j++)
            {
                Vector3 rawJ = obj.jointsCam[j];
                Vector3 jc = drawJoints[j];
                minX = Mathf.Min(minX, rawJ.x);
                maxX = Mathf.Max(maxX, rawJ.x);
                minY = Mathf.Min(minY, rawJ.y);
                maxY = Mathf.Max(maxY, rawJ.y);
                minZ = Mathf.Min(minZ, rawJ.z);
                maxZ = Mathf.Max(maxZ, rawJ.z);
                hasJointSamples = true;
                if (joints2DMode == Joints2DMode.ProjectXYZ)
                {
                    projMinX = Mathf.Min(projMinX, jc.x);
                    projMaxX = Mathf.Max(projMaxX, jc.x);
                    projMinY = Mathf.Min(projMinY, jc.y);
                    projMaxY = Mathf.Max(projMaxY, jc.y);
                    projMinZ = Mathf.Min(projMinZ, jc.z);
                    projMaxZ = Mathf.Max(projMaxZ, jc.z);
                    hasProjectedSourceSamples = true;
                }

                if (drawVis[j] == 0)
                {
                    continue;
                }

                totalValid++;
                float u;
                float v;
                if (joints2DMode == Joints2DMode.AsUV)
                {
                    u = jc.x;
                    v = jc.y;
                }
                else if (joints2DMode == Joints2DMode.UV01)
                {
                    u = jc.x * manifest.eye_w;
                    v = jc.y * manifest.eye_h;
                }
                else if (joints2DMode == Joints2DMode.NDC)
                {
                    u = (jc.x * 0.5f + 0.5f) * manifest.eye_w;
                    v = (0.5f - jc.y * 0.5f) * manifest.eye_h;
                }
                else if (joints2DMode == Joints2DMode.REL_PIX)
                {
                    u = anchorUBase + jc.x;
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y);
                }
                else if (joints2DMode == Joints2DMode.REL_BBOX01)
                {
                    u = anchorUBase + jc.x * obj.bboxW;
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y) * obj.bboxH;
                }
                else if (joints2DMode == Joints2DMode.REL_BBOXNDC)
                {
                    u = anchorUBase + jc.x * (obj.bboxW * 0.5f);
                    v = anchorVBase + (relFlipY ? -jc.y : jc.y) * (obj.bboxH * 0.5f);
                }
                else
                {
                    if (!hasFxFy)
                    {
                        continue;
                    }

                    bool zIsZero = Mathf.Approximately(jc.z, 0f);
                    bool shouldSkipForZ = debugSkipOnlyZeq0 ? zIsZero : jc.z <= 0f;
                    if (shouldSkipForZ)
                    {
                        if (jc.z <= 0f)
                        {
                            zNonPositiveSkipped++;
                        }
                        if (zIsZero)
                        {
                            zEq0Skipped++;
                        }
                        continue;
                    }

                    if (hasManifestNormIntrinsics)
                    {
                        // Match PC-side normalized pinhole projection.
                        float eyeW = intrEyeW;
                        float eyeH = intrEyeH;
                        u = (((jc.x / jc.z) * fxNorm) * 0.5f + 0.5f) * eyeW;
                        v = (0.5f - ((jc.y / jc.z) * fyNorm) * 0.5f) * eyeH;
                    }
                    else
                    {
                        u = fx * (jc.x / jc.z) + cx;
                        v = fy * (jc.y / jc.z) + cy;
                    }
                }

                if (joints2DMode == Joints2DMode.REL_PIX || joints2DMode == Joints2DMode.REL_BBOX01 || joints2DMode == Joints2DMode.REL_BBOXNDC)
                {
                    if (!TryMapMetaUvToEyePixel(ref u, ref v))
                    {
                        continue;
                    }
                }

                if (u >= bboxMinU && u <= bboxMaxU && v >= bboxMinV && v <= bboxMaxV)
                {
                    insideCount++;
                }

                Vector2 p = EyePixelToRectPixel(eyeRect, u, v);
                joints2DOverlayPoints.Add(new Joints2DOverlayPoint { pos = p, color = jointColor });
            }
        }

        if (lastJoints2DLogFrame == frame)
        {
            return;
        }

        lastJoints2DLogFrame = frame;
        string anchorRange = minAnchorZ <= maxAnchorZ ? $"[{minAnchorZ:F3},{maxAnchorZ:F3}]" : "[n/a,n/a]";
        string xRange = hasJointSamples ? $"[{minX:F3},{maxX:F3}]" : "[n/a,n/a]";
        string yRange = hasJointSamples ? $"[{minY:F3},{maxY:F3}]" : "[n/a,n/a]";
        string zRange = hasJointSamples ? $"[{minZ:F3},{maxZ:F3}]" : "[n/a,n/a]";
        string projXRange = hasProjectedSourceSamples ? $"[{projMinX:F3},{projMaxX:F3}]" : "[n/a,n/a]";
        string projYRange = hasProjectedSourceSamples ? $"[{projMinY:F3},{projMaxY:F3}]" : "[n/a,n/a]";
        string projZRange = hasProjectedSourceSamples ? $"[{projMinZ:F3},{projMaxZ:F3}]" : "[n/a,n/a]";
        string projectSource = "n/a";
        if (joints2DMode == Joints2DMode.ProjectXYZ)
        {
            if (debugProjectXYZUseRaw)
            {
                projectSource = "raw";
            }
            else if (processedSourceObjCount > 0 && rawSourceObjCount == 0)
            {
                projectSource = "processed";
            }
            else if (processedSourceObjCount > 0 && rawSourceObjCount > 0)
            {
                projectSource = "processed+raw_fallback";
            }
            else
            {
                projectSource = "raw_fallback";
            }
        }
        string zSkipText = joints2DMode == Joints2DMode.ProjectXYZ
            ? $" zNonPositiveSkipped={zNonPositiveSkipped} zEq0Skipped={zEq0Skipped} projectSource={projectSource} skipOnlyZeq0={debugSkipOnlyZeq0} projX={projXRange} projY={projYRange} projZ={projZRange}"
            : string.Empty;
        string bboxAnchorText = loggedRefSet
            ? $" bboxW={logRefBboxW:F1} bboxH={logRefBboxH:F1} anchorU={logRefAnchorU:F1} anchorV={logRefAnchorV:F1}"
            : " bboxW=n/a bboxH=n/a anchorU=n/a anchorV=n/a";
        Debug.Log(
            $"JOINTS_2D frame={frame} mode={joints2DMode} kpCount={totalKp} validCount={totalValid} insideCount={insideCount} " +
            $"anchorZ={anchorRange} jX={xRange} jY={yRange} jZ={zRange}{bboxAnchorText}{zSkipText}");
    }

    private void CaptureMeta2DOverlay(MetaObj obj, Transform screen, bool isRightEye, int uEyeFromResolve)
    {
        if (!debugDrawMeta2D || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || screen == null)
        {
            return;
        }

        if (!TryGetEyeScreenRect(screen, out Rect eyeRect))
        {
            return;
        }

        float anchorU = uEyeFromResolve;
        float anchorV = obj.anchorV;
        if (!TryMapMetaUvToEyePixel(ref anchorU, ref anchorV))
        {
            return;
        }

        float bboxX = obj.bboxX;
        if (isRightEye && bboxX >= manifest.eye_w)
        {
            bboxX -= manifest.eye_w;
        }
        float bboxY = obj.bboxY;
        float bboxX2 = bboxX + obj.bboxW;
        float bboxY2 = bboxY + obj.bboxH;
        if (!TryMapMetaUvToEyePixel(ref bboxX, ref bboxY))
        {
            return;
        }
        if (!TryMapMetaUvToEyePixel(ref bboxX2, ref bboxY2))
        {
            return;
        }

        Vector2 anchorPx = EyePixelToRectPixel(eyeRect, anchorU, anchorV);
        Vector2 p0 = EyePixelToRectPixel(eyeRect, bboxX, bboxY);
        Vector2 p1 = EyePixelToRectPixel(eyeRect, bboxX2, bboxY2);
        Rect bboxRect = Rect.MinMaxRect(Mathf.Min(p0.x, p1.x), Mathf.Min(p0.y, p1.y), Mathf.Max(p0.x, p1.x), Mathf.Max(p0.y, p1.y));

        meta2DOverlayItems.Add(new Meta2DOverlayItem
        {
            trackId = obj.trackId,
            eyeRect = eyeRect,
            anchor = anchorPx,
            bbox = bboxRect
        });
    }

    private bool TryMapMetaUvToEyePixel(ref float u, ref float v)
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        // Order of operations:
        // a) normalized -> pixel
        // b) flip
        // c) crop remap (offset-only or ApplyCropToEyePixel)
        // d) map to eye rect in OnGUI
        if (uvIsNormalized)
        {
            u *= manifest.eye_w;
            v *= manifest.eye_h;
        }

        if (flipU)
        {
            u = manifest.eye_w - u;
        }
        if (flipV)
        {
            v = manifest.eye_h - v;
        }

        if (applyCropScale)
        {
            ApplyCropToEyePixel(ref u, ref v);
        }
        else
        {
            u -= GetCropX();
            v -= GetCropY();
        }

        return true;
    }

    private bool TryGetEyeScreenRect(Transform screen, out Rect rect)
    {
        rect = Rect.zero;
        Camera cam = GetViewCamera() ?? Camera.main;
        if (cam == null || screen == null)
        {
            return false;
        }

        GetScreenMeshLocalBounds(screen, out Vector3 center, out Vector3 size);
        Vector3 e = size * 0.5f;
        Vector3[] local = new Vector3[4]
        {
            center + new Vector3(-e.x, -e.y, 0f),
            center + new Vector3( e.x, -e.y, 0f),
            center + new Vector3( e.x,  e.y, 0f),
            center + new Vector3(-e.x,  e.y, 0f),
        };

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 world = screen.TransformPoint(local[i]);
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f)
            {
                return false;
            }

            float gx = s.x;
            float gy = Screen.height - s.y;
            minX = Mathf.Min(minX, gx);
            minY = Mathf.Min(minY, gy);
            maxX = Mathf.Max(maxX, gx);
            maxY = Mathf.Max(maxY, gy);
        }

        rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return rect.width > 1f && rect.height > 1f;
    }

    private Vector2 EyePixelToRectPixel(Rect eyeRect, float u, float v)
    {
        float x = eyeRect.xMin + (u / manifest.eye_w) * eyeRect.width;
        float y = eyeRect.yMin + (v / manifest.eye_h) * eyeRect.height;
        return new Vector2(x, y);
    }

    private static void DrawRectOutline(Rect rect, Color color, float thickness)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static bool IsDebugJointPairValid(int idxA, int idxB, int jointCount, byte[] vis, float[] camZ)
    {
        if (idxA < 0 || idxB < 0 || idxA >= jointCount || idxB >= jointCount)
        {
            return false;
        }
        if (vis == null || idxA >= vis.Length || idxB >= vis.Length || vis[idxA] == 0 || vis[idxB] == 0)
        {
            return false;
        }
        if (camZ == null || idxA >= camZ.Length || idxB >= camZ.Length)
        {
            return false;
        }

        return !Mathf.Approximately(camZ[idxA], 0f) && !Mathf.Approximately(camZ[idxB], 0f);
    }

    private static int CountSkeletonLineSkipSegments(byte categoryId, int jointCount, byte[] vis, float[] camZ)
    {
        int skip = 0;
        if (categoryId == 2)
        {
            CountChainSkipSegments(DogLeftFrontChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogRightFrontChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogLeftRearChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogRightRearChain, jointCount, vis, camZ, ref skip);
            return skip;
        }

        for (int i = 0; i + 1 < CocoEdges.Length; i += 2)
        {
            int a = CocoEdges[i];
            int b = CocoEdges[i + 1];
            if (!IsDebugJointPairValid(a, b, jointCount, vis, camZ))
            {
                skip++;
            }
        }
        return skip;
    }

    private static void CountChainSkipSegments(int[] chain, int jointCount, byte[] vis, float[] camZ, ref int skip)
    {
        if (chain == null || chain.Length < 2)
        {
            return;
        }

        for (int i = 0; i + 1 < chain.Length; i++)
        {
            if (!IsDebugJointPairValid(chain[i], chain[i + 1], jointCount, vis, camZ))
            {
                skip++;
            }
        }
    }

    private void LogDogBonesOnce(Transform rigRoot)
    {
        if (rigRoot == null || dogBonesDumpLoggedRoots.Contains(rigRoot))
        {
            return;
        }

        dogBonesDumpLoggedRoots.Add(rigRoot);
        Transform[] all = rigRoot.GetComponentsInChildren<Transform>(true);
        string first = all != null && all.Length > 0 ? all[0].name : "n/a";
        string last = all != null && all.Length > 0 ? all[all.Length - 1].name : "n/a";
        int count = all != null ? all.Length : 0;
        Debug.Log($"[dog_bones] count={count} first={first} last={last}");
    }

    private void LogDogMappingOnce(AnimalRigCache cache, Transform rigRoot)
    {
        if (cache == null || rigRoot == null || dogMappingLoggedRoots.Contains(rigRoot))
        {
            return;
        }

        dogMappingLoggedRoots.Add(rigRoot);
        Debug.Log(
            "[dog_map] " +
            $"7-8->{(cache.leftFrontUpper != null ? cache.leftFrontUpper.name : "null")} " +
            $"8-12->{(cache.leftFrontLower != null ? cache.leftFrontLower.name : "null")} " +
            $"12-16->{(cache.leftFrontPaw != null ? cache.leftFrontPaw.name : "null")} " +
            $"7-9->{(cache.rightFrontUpper != null ? cache.rightFrontUpper.name : "null")} " +
            $"9-13->{(cache.rightFrontLower != null ? cache.rightFrontLower.name : "null")} " +
            $"13-17->{(cache.rightFrontPaw != null ? cache.rightFrontPaw.name : "null")} " +
            $"6-10->{(cache.leftRearUpper != null ? cache.leftRearUpper.name : "null")} " +
            $"10-14->{(cache.leftRearLower != null ? cache.leftRearLower.name : "null")} " +
            $"14-18->{(cache.leftRearPaw != null ? cache.leftRearPaw.name : "null")} " +
            $"6-11->{(cache.rightRearUpper != null ? cache.rightRearUpper.name : "null")} " +
            $"11-15->{(cache.rightRearLower != null ? cache.rightRearLower.name : "null")} " +
            $"15-19->{(cache.rightRearPaw != null ? cache.rightRearPaw.name : "null")}");
    }

    private bool TrySelectDogAxisComparePair(
        AnimalRigCache cache,
        int jointCount,
        byte[] vis,
        float[] camZ,
        out Transform selectedBone,
        out int selectedIdxA,
        out int selectedIdxB,
        out int skipMissingBone,
        out int skipZEq0,
        out int skipVis0,
        out int skipOutOfRange,
        out int totalSegments)
    {
        selectedBone = null;
        selectedIdxA = -1;
        selectedIdxB = -1;
        skipMissingBone = 0;
        skipZEq0 = 0;
        skipVis0 = 0;
        skipOutOfRange = 0;
        totalSegments = 0;

        if (cache == null)
        {
            return false;
        }

        (int a, int b, Transform bone)[] segs = new[]
        {
            (7, 8, cache.leftFrontUpper),
            (8, 12, cache.leftFrontLower),
            (12, 16, cache.leftFrontPaw),
            (7, 9, cache.rightFrontUpper),
            (9, 13, cache.rightFrontLower),
            (13, 17, cache.rightFrontPaw),
            (6, 10, cache.leftRearUpper),
            (10, 14, cache.leftRearLower),
            (14, 18, cache.leftRearPaw),
            (6, 11, cache.rightRearUpper),
            (11, 15, cache.rightRearLower),
            (15, 19, cache.rightRearPaw),
        };

        for (int i = 0; i < segs.Length; i++)
        {
            totalSegments++;
            int a = segs[i].a;
            int b = segs[i].b;
            Transform bone = segs[i].bone;

            if (bone == null)
            {
                skipMissingBone++;
                continue;
            }
            if (a < 0 || b < 0 || a >= jointCount || b >= jointCount || vis == null || camZ == null || a >= vis.Length || b >= vis.Length || a >= camZ.Length || b >= camZ.Length)
            {
                skipOutOfRange++;
                continue;
            }
            if (vis[a] == 0 || vis[b] == 0)
            {
                skipVis0++;
                continue;
            }
            if (Mathf.Approximately(camZ[a], 0f) || Mathf.Approximately(camZ[b], 0f))
            {
                skipZEq0++;
                continue;
            }

            selectedBone = bone;
            selectedIdxA = a;
            selectedIdxB = b;
            return true;
        }

        return false;
    }

    private static void DrawJointEdgeIfValid(Vector3[] jointsWorld, int jointCount, byte[] vis, float[] camZ, int a, int b)
    {
        if (!IsDebugJointPairValid(a, b, jointCount, vis, camZ))
        {
            return;
        }
        if (jointsWorld == null || a >= jointsWorld.Length || b >= jointsWorld.Length)
        {
            return;
        }
        Gizmos.DrawLine(jointsWorld[a], jointsWorld[b]);
    }

    private static void DrawJointChainIfValid(Vector3[] jointsWorld, int jointCount, byte[] vis, float[] camZ, int[] chain)
    {
        if (chain == null || chain.Length < 2)
        {
            return;
        }
        for (int i = 0; i + 1 < chain.Length; i++)
        {
            DrawJointEdgeIfValid(jointsWorld, jointCount, vis, camZ, chain[i], chain[i + 1]);
        }
    }

    private static void DrawDebugArrow(Vector3 origin, Vector3 direction, float length)
    {
        if (direction.sqrMagnitude < 0.000001f || length <= 0f)
        {
            return;
        }

        Vector3 dir = direction.normalized;
        Vector3 tip = origin + dir * length;
        Gizmos.DrawLine(origin, tip);

        Vector3 side = Vector3.Cross(dir, Vector3.up);
        if (side.sqrMagnitude < 0.000001f)
        {
            side = Vector3.Cross(dir, Vector3.right);
        }
        side.Normalize();

        float headLen = length * 0.25f;
        Vector3 back = -dir * headLen;
        Vector3 wing = side * (headLen * 0.55f);
        Gizmos.DrawLine(tip, tip + back + wing);
        Gizmos.DrawLine(tip, tip + back - wing);
    }

    private static void PickBestBoneAxisLocal(Transform bone, Vector3 targetDirWorld, out Vector3 selectedAxisLocal, out float minAngle)
    {
        selectedAxisLocal = Vector3.forward;
        minAngle = float.MaxValue;
        if (bone == null || targetDirWorld.sqrMagnitude < 0.000001f)
        {
            return;
        }

        Vector3[] candidates = new Vector3[]
        {
            Vector3.right, -Vector3.right,
            Vector3.up, -Vector3.up,
            Vector3.forward, -Vector3.forward
        };

        Vector3 targetDir = targetDirWorld.normalized;
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 axisWorld = bone.TransformDirection(candidates[i]).normalized;
            float angle = Vector3.Angle(axisWorld, targetDir);
            if (angle < minAngle)
            {
                minAngle = angle;
                selectedAxisLocal = candidates[i];
            }
        }
    }

    private void TryApplyAutoBoneAxis(Transform bone, Vector3 targetDirWorld, int frame, uint trackId, string boneName)
    {
        if (!debugAutoBoneAxis || bone == null || targetDirWorld.sqrMagnitude < 0.000001f)
        {
            return;
        }

        Transform parent = bone.parent;
        if (parent == null)
        {
            return;
        }

        Vector3 selectedAxisLocal;
        float minAngle = -1f;
        if (!debugAutoAxisByBone.TryGetValue(bone, out selectedAxisLocal))
        {
            PickBestBoneAxisLocal(bone, targetDirWorld, out selectedAxisLocal, out minAngle);
            debugAutoAxisByBone[bone] = selectedAxisLocal;
            if (debugLogAxisCompare && !debugAutoAxisPickLogged.Contains(bone))
            {
                debugAutoAxisPickLogged.Add(bone);
                Debug.Log(
                    $"AXIS_PICK frame={frame} trackId={trackId} bone={boneName} selectedAxisLocal=({selectedAxisLocal.x:F0},{selectedAxisLocal.y:F0},{selectedAxisLocal.z:F0}) minAngle={minAngle:F2}");
            }
        }

        if (!debugAutoRestLocalRotByBone.TryGetValue(bone, out Quaternion restLocalRotation))
        {
            restLocalRotation = bone.localRotation;
            debugAutoRestLocalRotByBone[bone] = restLocalRotation;
        }

        Vector3 targetDirParent = parent.InverseTransformDirection(targetDirWorld.normalized);
        if (targetDirParent.sqrMagnitude < 0.000001f)
        {
            return;
        }
        targetDirParent.Normalize();

        Vector3 axisParentNow = parent.InverseTransformDirection(bone.TransformDirection(selectedAxisLocal).normalized);
        if (axisParentNow.sqrMagnitude > 0.000001f)
        {
            axisParentNow.Normalize();
        }

        bool flipChosen = false;
        if (axisParentNow.sqrMagnitude > 0.000001f)
        {
            float angleNormal = Vector3.Angle(axisParentNow, targetDirParent);
            float angleFlipped = Vector3.Angle(axisParentNow, -targetDirParent);
            if (angleFlipped < angleNormal)
            {
                targetDirParent = -targetDirParent;
                flipChosen = true;
            }
        }

        Vector3 restAxisLocal = (restLocalRotation * selectedAxisLocal).normalized;
        float angleBefore = Vector3.Angle(bone.TransformDirection(selectedAxisLocal).normalized, targetDirWorld.normalized);
        Quaternion desiredLocal = Quaternion.FromToRotation(restAxisLocal, targetDirParent) * restLocalRotation;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, desiredLocal, Mathf.Clamp01(debugAutoBoneAxisAlpha));
        float angleAfter = Vector3.Angle(bone.TransformDirection(selectedAxisLocal).normalized, targetDirWorld.normalized);

        if (debugLogAxisCompare)
        {
            Debug.Log(
                $"AXIS_SOLVE frame={frame} trackId={trackId} bone={boneName} " +
                $"restAxisLocal=({restAxisLocal.x:F3},{restAxisLocal.y:F3},{restAxisLocal.z:F3}) " +
                $"targetDirParent=({targetDirParent.x:F3},{targetDirParent.y:F3},{targetDirParent.z:F3}) " +
                $"flipChosen={(flipChosen ? 1 : 0)} angleBefore={angleBefore:F2} angleAfter={angleAfter:F2}");
        }

        if (debugLogAxisCompare)
        {
            Debug.Log($"AXIS_COMPARE_AFTER frame={frame} trackId={trackId} bone={boneName} angleDeg={angleAfter:F2}");
        }
    }

    private DebugDrawTrackState GetOrCreateDebugDrawTrackState(uint trackId)
    {
        if (!debugDrawStateByTrack.TryGetValue(trackId, out DebugDrawTrackState state) || state == null)
        {
            state = new DebugDrawTrackState();
            debugDrawStateByTrack[trackId] = state;
        }

        return state;
    }

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, byte categoryId, Transform screen, bool freezeDogDistal)
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

        if (debugLogAxisCompare && categoryId == 2)
        {
            LogDogBonesOnce(rigRoot);
            LogDogMappingOnce(cache, rigRoot);
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
            ApplyAnimalLimbByJointSegments(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, DogLeftFrontChain, alpha, !freezeDogDistal);
            ApplyAnimalLimbByJointSegments(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, DogRightFrontChain, alpha, !freezeDogDistal);

            // Rear legs: segment mapping from joint points (J0->J1, J1->J2, J2->J3).
            ApplyAnimalLimbByJointSegments(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, DogLeftRearChain, alpha, !freezeDogDistal);
            ApplyAnimalLimbByJointSegments(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, DogRightRearChain, alpha, !freezeDogDistal);
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

    private void ApplyAnimalLimbByJointSegments(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (cache == null || chain == null || chain.Length < 4)
        {
            return;
        }

        // Joint-centric mapping: each bone uses the segment between adjacent meta joints.
        ApplyAnimalBoneFromJointsLocalOnly(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.9f);
        ApplyAnimalBoneFromJointsLocalOnly(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.85f);
        if (paw != null && applyDistal)
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

    private bool TryLogJointInvalid(int idx, int visFlag, Vector3 p, string reason)
    {
        if (!debugLogAxisCompare)
        {
            return false;
        }

        if (debugJointInvalidLogFrame != debugJointContextFrame)
        {
            debugJointInvalidLogFrame = debugJointContextFrame;
            debugJointInvalidLogCount = 0;
        }

        if (debugJointInvalidLogCount >= MaxJointInvalidLogsPerFrame)
        {
            return false;
        }

        debugJointInvalidLogCount++;
        Debug.Log(
            $"JOINT_INVALID frame={debugJointContextFrame} trackId={debugJointContextTrackId} idx={idx} vis={visFlag} " +
            $"p=({p.x:F3},{p.y:F3},{p.z:F3}) reason={reason}");
        return true;
    }

    private bool ShouldEmitRigDiag(int frame, uint trackId)
    {
        if (!debugLogAxisCompare)
        {
            return false;
        }
        if (frame < DiagFrameStart || frame > DiagFrameEnd)
        {
            return false;
        }
        return trackId == 0u || trackId == 1u;
    }

    private bool TryConsumeDiagBudget(int frame)
    {
        if (debugDiagLogFrame != frame)
        {
            debugDiagLogFrame = frame;
            debugDiagLogCount = 0;
        }
        if (debugDiagLogCount >= MaxDiagLogsPerFrame)
        {
            return false;
        }
        debugDiagLogCount++;
        return true;
    }

    private void TryLogSpaceCheck(int frame, MetaObj obj, bool rootRel, Vector3 rootWorld, Transform screen, Vector3[] jointsWorld)
    {
        if (!ShouldEmitRigDiag(frame, obj.trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        Vector3 jCam0 = (obj.jointsCam != null && obj.jointsCam.Length > 0) ? obj.jointsCam[0] : Vector3.zero;
        Vector3 jWorld0 = (jointsWorld != null && jointsWorld.Length > 0) ? jointsWorld[0] : Vector3.zero;
        Vector3 screenPos = screen != null ? screen.position : Vector3.zero;
        Debug.Log(
            $"SPACE_CHECK frame={frame} trackId={obj.trackId} jointsSpace={(rootRel ? "RootRel" : "CamSpace")} rootSubtracted={(rootRel ? 1 : 0)} " +
            $"jointsCam0=({jCam0.x:F3},{jCam0.y:F3},{jCam0.z:F3}) jointsWorld0=({jWorld0.x:F3},{jWorld0.y:F3},{jWorld0.z:F3}) " +
            $"instancePos=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) screenPos=({screenPos.x:F3},{screenPos.y:F3},{screenPos.z:F3})");
    }

    private void TryLogAnchorCheck(int frame, MetaObj obj, Vector3 modelPosBefore, Vector3 modelPosAfter, Vector3 rootWorld, Vector3[] jointsWorld)
    {
        if (!ShouldEmitRigDiag(frame, obj.trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        Vector3 jwRoot = (jointsWorld != null && jointsWorld.Length > 0) ? jointsWorld[0] : Vector3.zero;
        Debug.Log(
            $"ANCHOR_CHECK frame={frame} trackId={obj.trackId} anchorWorldUsed=1 anchor=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) " +
            $"rootWorld=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) instanceBefore=({modelPosBefore.x:F3},{modelPosBefore.y:F3},{modelPosBefore.z:F3}) " +
            $"instanceAfter=({modelPosAfter.x:F3},{modelPosAfter.y:F3},{modelPosAfter.z:F3}) jointsWorldRoot=({jwRoot.x:F3},{jwRoot.y:F3},{jwRoot.z:F3})");
    }

    private void TryLogCloudBoneErr(int frame, uint trackId, string boneName, Transform bone, Vector3[] jointsWorld, int jointIndex)
    {
        if (!ShouldEmitRigDiag(frame, trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        if (bone == null || jointsWorld == null || jointIndex < 0 || jointIndex >= jointsWorld.Length)
        {
            return;
        }
        Vector3 jw = jointsWorld[jointIndex];
        Vector3 bw = bone.position;
        float err = Vector3.Distance(jw, bw);
        Debug.Log(
            $"CLOUD_BONE_ERR frame={frame} trackId={trackId} bone={boneName} " +
            $"jointWorld=({jw.x:F3},{jw.y:F3},{jw.z:F3}) boneWorld=({bw.x:F3},{bw.y:F3},{bw.z:F3}) err={err:F3} " +
            $"usedJointIndex={jointIndex} usedArray=jointsWorld localOrWorld=world");
    }

    private void QueueAnimatorCheckSample(int frame, uint trackId, string boneName, Transform bone, Vector3 before, Vector3 after, Animator animator)
    {
        if (!ShouldEmitRigDiag(frame, trackId) || bone == null)
        {
            return;
        }
        pendingAnimatorChecks.Add(new AnimatorCheckSample
        {
            frame = frame,
            trackId = trackId,
            boneName = boneName,
            bone = bone,
            boneBeforeApply = before,
            boneAfterApply = after,
            animatorEnabled = animator != null && animator.enabled,
            updateMode = animator != null ? animator.updateMode : AnimatorUpdateMode.Normal
        });
    }

    private void FlushAnimatorCheckLateUpdate()
    {
        if (pendingAnimatorChecks.Count == 0)
        {
            return;
        }
        for (int i = 0; i < pendingAnimatorChecks.Count; i++)
        {
            AnimatorCheckSample s = pendingAnimatorChecks[i];
            if (!ShouldEmitRigDiag(s.frame, s.trackId) || !TryConsumeDiagBudget(s.frame))
            {
                continue;
            }
            Vector3 late = s.bone != null ? s.bone.position : Vector3.zero;
            Debug.Log(
                $"ANIMATOR_CHECK frame={s.frame} trackId={s.trackId} bone={s.boneName} " +
                $"boneBeforeApply=({s.boneBeforeApply.x:F3},{s.boneBeforeApply.y:F3},{s.boneBeforeApply.z:F3}) " +
                $"boneAfterApply=({s.boneAfterApply.x:F3},{s.boneAfterApply.y:F3},{s.boneAfterApply.z:F3}) " +
                $"boneAfterLateUpdate=({late.x:F3},{late.y:F3},{late.z:F3}) animatorEnabled={(s.animatorEnabled ? 1 : 0)} updateMode={s.updateMode}");
        }
        pendingAnimatorChecks.Clear();
    }

    private void TryLogFrameApplySummary(
        int frame,
        uint trackId,
        byte categoryId,
        int kpCount,
        int visCount,
        int invalidCount,
        string jointsSpaceMode,
        bool anchorWorldUsed,
        Vector3 anchorWorld,
        Vector3 rootWorld,
        Vector3 modelPosBefore,
        Vector3 modelPosAfter,
        string reasonSkipped)
    {
        if (!debugLogAxisCompare)
        {
            return;
        }

        if (debugFrameApplySummaryLogFrame != frame)
        {
            debugFrameApplySummaryLogFrame = frame;
            debugFrameApplySummaryLogCount = 0;
        }

        if (debugFrameApplySummaryLogCount >= MaxFrameApplySummaryLogsPerFrame)
        {
            return;
        }

        debugFrameApplySummaryLogCount++;
        Vector3 delta = modelPosAfter - modelPosBefore;
        Debug.Log(
            $"FRAME_APPLY_SUMMARY frame={frame} trackId={trackId} category={categoryId} kpCount={kpCount} visCount={visCount} invalidCount={invalidCount} " +
            $"jointsSpace={jointsSpaceMode} anchorWorldUsed={(anchorWorldUsed ? 1 : 0)} " +
            $"anchor=({anchorWorld.x:F3},{anchorWorld.y:F3},{anchorWorld.z:F3}) root=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) " +
            $"modelPosBefore=({modelPosBefore.x:F3},{modelPosBefore.y:F3},{modelPosBefore.z:F3}) modelPosAfter=({modelPosAfter.x:F3},{modelPosAfter.y:F3},{modelPosAfter.z:F3}) " +
            $"delta=({delta.x:F3},{delta.y:F3},{delta.z:F3}) reasonSkipped={reasonSkipped}");
    }

    private bool TryGetJointPoint(Vector3[] jointsWorld, byte[] vis, int idx, out Vector3 point)
    {
        point = Vector3.zero;
        if (jointsWorld == null || vis == null)
        {
            TryLogJointInvalid(idx, -1, Vector3.zero, "null_buffers");
            return false;
        }

        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length)
        {
            TryLogJointInvalid(idx, -1, Vector3.zero, "out_of_range");
            return false;
        }

        byte visFlag = vis[idx];
        Vector3 p = jointsWorld[idx];
        if (visFlag == 0)
        {
            TryLogJointInvalid(idx, visFlag, p, "vis0");
            return false;
        }

        if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
            float.IsNaN(p.y) || float.IsInfinity(p.y) ||
            float.IsNaN(p.z) || float.IsInfinity(p.z))
        {
            TryLogJointInvalid(idx, visFlag, p, "non_finite");
            return false;
        }

        if (p.sqrMagnitude <= InvalidJointSqrMagnitudeEpsilon)
        {
            TryLogJointInvalid(idx, visFlag, p, "near_zero");
            return false;
        }

        point = p;
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

    private void ApplyYawDepthDisambiguation(Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx, Transform root, Vector3 camOrigin)
    {
        if (jointsWorld == null || vis == null || root == null)
        {
            return;
        }

        float baseOffset = Mathf.Max(0f, yawDepthOffsetMeters) * Mathf.Clamp01(yawDepthBlend);
        if (baseOffset <= 0.0001f)
        {
            return;
        }

        Vector3 viewAxis = camOrigin - root.position;
        if (viewAxis.sqrMagnitude < 0.000001f)
        {
            return;
        }
        viewAxis.Normalize(); // Toward camera.

        Vector3 right = root.right;
        if (right.sqrMagnitude < 0.000001f)
        {
            return;
        }
        right.Normalize();

        // Positive means camera is more on model-right side: right joints should be closer.
        float sideSign = Mathf.Sign(Vector3.Dot(viewAxis, right));
        if (Mathf.Abs(sideSign) < 0.5f)
        {
            return;
        }

        ApplySideDepthOffset(jointsWorld, vis, idx.rightShoulder, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightElbow, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightWrist, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightHip, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightKnee, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightAnkle, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightFoot, viewAxis * (baseOffset * sideSign));

        ApplySideDepthOffset(jointsWorld, vis, idx.leftShoulder, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftElbow, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftWrist, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftHip, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftKnee, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftAnkle, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftFoot, viewAxis * (-baseOffset * sideSign));
    }

    private static void ApplySideDepthOffset(Vector3[] jointsWorld, byte[] vis, int idx, Vector3 offset)
    {
        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length || vis[idx] == 0)
        {
            return;
        }

        jointsWorld[idx] += offset;
    }

    private void ApplyManualYawToJoints(uint trackId, int frame, Vector3[] jointsWorld, byte[] vis, Vector3 pivotWorld, Vector3 upAxis)
    {
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return;
        }

        float yawDeg = EvaluateManualYawOffsetDegForFrame(trackId, frame);
        if (Mathf.Abs(yawDeg) < 0.001f)
        {
            return;
        }

        if (upAxis.sqrMagnitude < 0.000001f)
        {
            upAxis = Vector3.up;
        }

        Quaternion yawRot = Quaternion.AngleAxis(yawDeg, upAxis.normalized);
        for (int i = 0; i < jointsWorld.Length && i < vis.Length; i++)
        {
            if (vis[i] == 0)
            {
                continue;
            }

            Vector3 local = jointsWorld[i] - pivotWorld;
            jointsWorld[i] = pivotWorld + (yawRot * local);
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

        if (debugAutoBoneAxis && debugAutoBoneAxisApplyToRig && IsHumanoidAutoAxisLimbBone(boneId))
        {
            TryApplyAutoBoneAxis(bone, targetDir, -1, 0u, boneId.ToString());
            if (debugLogAxisCompare &&
                (boneId == HumanBodyBones.LeftLowerArm || boneId == HumanBodyBones.RightUpperArm))
            {
                Vector3 axisLocal = debugAutoAxisByBone.TryGetValue(bone, out Vector3 cachedAxis) ? cachedAxis : Vector3.forward;
                float afterAngle = Vector3.Angle(bone.TransformDirection(axisLocal).normalized, targetDir);
                Debug.Log($"AXIS_COMPARE_AFTER frame=-1 trackId=0 bone={boneId} angleDeg={afterAngle:F2}");
            }
            return true;
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

    private static bool IsHumanoidAutoAxisLimbBone(HumanBodyBones boneId)
    {
        return
            boneId == HumanBodyBones.LeftUpperArm ||
            boneId == HumanBodyBones.LeftLowerArm ||
            boneId == HumanBodyBones.RightUpperArm ||
            boneId == HumanBodyBones.RightLowerArm ||
            boneId == HumanBodyBones.LeftUpperLeg ||
            boneId == HumanBodyBones.LeftLowerLeg ||
            boneId == HumanBodyBones.RightUpperLeg ||
            boneId == HumanBodyBones.RightLowerLeg;
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

    private void OnGUI()
    {
        if ((!debugDrawMeta2D || meta2DOverlayItems == null || meta2DOverlayItems.Count == 0) &&
            (!debugDrawJoints2D || joints2DOverlayPoints == null || joints2DOverlayPoints.Count == 0))
        {
            return;
        }

        if (debugDrawMeta2D)
        {
            for (int i = 0; i < meta2DOverlayItems.Count; i++)
            {
                Meta2DOverlayItem item = meta2DOverlayItems[i];
                DrawRectOutline(item.eyeRect, new Color(0.1f, 0.9f, 1f, 0.8f), 1f);

                Color c = (item.trackId % 2u == 0u) ? new Color(1f, 0.92f, 0.1f, 0.95f) : new Color(1f, 0.35f, 0.15f, 0.95f);
                DrawRectOutline(item.bbox, c, 2f);
                Color old = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(new Rect(item.anchor.x - 3f, item.anchor.y - 3f, 6f, 6f), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }

        if (debugDrawJoints2D)
        {
            for (int i = 0; i < joints2DOverlayPoints.Count; i++)
            {
                Joints2DOverlayPoint p = joints2DOverlayPoints[i];
                Color old = GUI.color;
                GUI.color = p.color;
                GUI.DrawTexture(new Rect(p.pos.x - 2f, p.pos.y - 2f, 4f, 4f), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }
    }

    private void OnDrawGizmos()
    {
        foreach (KeyValuePair<uint, DebugDrawTrackState> kv in debugDrawStateByTrack)
        {
            DebugDrawTrackState state = kv.Value;
            if (state == null)
            {
                continue;
            }

            if (debugDrawAnchor && state.hasAnchor)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(state.anchorWorld, 0.025f);
            }

            // Draw points in world space reconstructed in TryApplySkeleton from jointsCam (+ pinhole basis).
            if (debugDrawJoints && state.jointsWorld != null && state.jointCount > 0)
            {
                Gizmos.color = Color.yellow;
                int n = Mathf.Min(state.jointCount, state.jointsWorld.Length);
                for (int i = 0; i < n; i++)
                {
                    if (state.jointsVis != null && i < state.jointsVis.Length && state.jointsVis[i] == 0)
                    {
                        continue;
                    }

                    Gizmos.DrawSphere(state.jointsWorld[i], 0.012f);
                }
            }

            if (debugDrawSkeletonLines3D && state.jointsWorld != null && state.jointCount > 0)
            {
                Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.9f);
                if (state.categoryId == 2)
                {
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogLeftFrontChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogRightFrontChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogLeftRearChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogRightRearChain);
                }
                else
                {
                    for (int i = 0; i + 1 < CocoEdges.Length; i += 2)
                    {
                        DrawJointEdgeIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, CocoEdges[i], CocoEdges[i + 1]);
                    }
                }
            }

            if (debugDrawBoneAxisCompare && state.hasAxisCompare)
            {
                float axisLen = 0.18f;
                Gizmos.color = Color.green;
                DrawDebugArrow(state.axisBase, state.axisTargetDir, axisLen);
                Gizmos.color = Color.magenta;
                DrawDebugArrow(state.axisBase, state.axisBoneDir, axisLen);
            }
        }
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
