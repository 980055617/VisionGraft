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
    private uint activeTrackId = uint.MaxValue;
    private GameObject activeTrackInstance;
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
    private GameObject anchorPinholeCube;
    private GameObject anchorScreenCube;
    private readonly Dictionary<Animator, HumanoidRigCache> humanoidCaches = new Dictionary<Animator, HumanoidRigCache>();
    private static readonly int[] CocoEdges = new[]
    {
        5,7, 7,9, 6,8, 8,10, 11,13, 13,15, 12,14, 14,16, 5,6, 11,12
    };

    private struct BoneMap
    {
        public HumanBodyBones bone;
        public int jointA;
        public int jointB;
    }

    private sealed class HumanoidRigCache
    {
        public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<HumanBodyBones, Vector3> bindDirWorld = new Dictionary<HumanBodyBones, Vector3>();
        public readonly Dictionary<HumanBodyBones, Quaternion> bindRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
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
        if (replacePrefab == null && spawnedTestModel == null)
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

        bool cropApplied = hasCrop;
        float bboxWAdjusted = target.bboxW;
        float bboxHAdjusted = target.bboxH;
        // Intentional: crop mapping logs suppressed in category-only logger.

        if (ShouldLogBoneDetails(frame, (int)target.trackId))
        {
            LogBoneDetails(target, frame);
        }
        DebugScreenPinholeConsistency(screen, uEyeF, vEyeF, frame, (int)target.trackId);
        if (replacePrefab != null)
        {
            GameObject instance = GetOrCreateTrackInstance(target.trackId);
            if (instance == null)
            {
                return;
            }

            Camera viewCam = GetViewCamera() ?? Camera.main;
            Vector3 worldPinhole = AnchorUvZToWorldPinhole(uEyeF, vEyeF, target.anchorZ);
            Quaternion rotationPinhole = viewCam != null ? viewCam.transform.rotation : (screen != null ? screen.rotation : Quaternion.identity);
            float targetHeight = ComputeTargetHeightMeters(bboxHAdjusted, target.anchorZ);
            ApplyReplaceableModelTransform(instance, worldPinhole, rotationPinhole, targetHeight, target, uEyeF, vEyeF, bboxWAdjusted, bboxHAdjusted, frame);
            TryApplySkeleton(instance, target, worldPinhole, viewCam, frame);
            float bboxWorldH = 0f;
            if (TryGetFocalLengths(out _, out float fy))
            {
                bboxWorldH = (2f * bboxHAdjusted / manifest.eye_h) * (target.anchorZ / fy);
            }
            UpdateAnchorDebugCubes(screen, uEyeF, vEyeF, worldPinhole, viewCam, bboxWorldH);
            LogReprojectionError(target.trackId, uEyeF, vEyeF, target.anchorZ, worldPinhole, viewCam, frame);
            // Intentional: camera detail logs suppressed in category-only logger.

            Log(LogCategory.FOLLOW,
                $"f={frame} t={target.trackId} anchor=({target.anchorU},{target.anchorV}) uEye={uEyeF:F2} vEye={vEyeF:F2} screen={(isRightEye ? "R" : "L")} z={target.anchorZ:F3} pos=({worldPinhole.x:F3},{worldPinhole.y:F3},{worldPinhole.z:F3})",
                frame, (int)target.trackId);
            return;
        }

        Camera viewCamFallback = GetViewCamera() ?? Camera.main;
        Vector3 world = AnchorUvZToWorldPinhole(uEyeF, vEyeF, target.anchorZ);
        Quaternion rotation = viewCamFallback != null ? viewCamFallback.transform.rotation : (screen != null ? screen.rotation : Quaternion.identity);

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
        // Intentional: camera detail logs suppressed in category-only logger.

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

    private GameObject GetOrCreateTrackInstance(uint trackId)
    {
        if (trackInstances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            SetActiveTrackInstance(trackId, existing);
            return existing;
        }

        if (replacePrefab == null)
        {
            return null;
        }

        GameObject instance = Instantiate(replacePrefab, Vector3.zero, Quaternion.identity);
        instance.name = $"Track_{trackId}";
        if (instance.GetComponent<ReplaceableModel>() == null)
        {
            instance.AddComponent<ReplaceableModel>();
        }

        trackInstances[trackId] = instance;
        SetActiveTrackInstance(trackId, instance);
        return instance;
    }

    private void SetActiveTrackInstance(uint trackId, GameObject instance)
    {
        if (activeTrackId == trackId && activeTrackInstance == instance)
        {
            return;
        }

        foreach (var kvp in trackInstances)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            kvp.Value.SetActive(kvp.Key == trackId);
        }

        activeTrackId = trackId;
        activeTrackInstance = instance;
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

    private void ApplyReplaceableModelTransform(GameObject instance, Vector3 world, Quaternion rotation, float targetHeightMeters, MetaObj obj, float uEye, float vEye, float bboxWAdjusted, float bboxHAdjusted, int frame)
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
            float uniformScale = Mathf.Min(scaleW, scaleH);
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
                Camera cam = GetViewCamera() ?? Camera.main;
                Vector3 up = cam != null ? cam.transform.up : Vector3.up;
                float vBottom = vEye + bboxHAdjusted * bboxAnchorVToBottom;
                vBottom = Mathf.Clamp(vBottom, 0f, manifest.eye_h - 1f);
                Vector3 bottomWorld = AnchorUvZToWorldPinhole(uEye, vBottom, obj.anchorZ);
                float modelBottomOffset = model.baseBottomOffsetLocal * lossy.y;
                Vector3 modelBottomWorld = instance.transform.position - up * modelBottomOffset;
                Vector3 delta = bottomWorld - modelBottomWorld;
                instance.transform.position += delta;
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
    }

    private void TryApplySkeleton(GameObject instance, MetaObj obj, Vector3 rootWorld, Camera viewCam, int frame)
    {
        if (instance == null || !obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return;
        }

        int jointCount = obj.skeletonKpCount;
        if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
        {
            return;
        }

        Transform camXform = viewCam != null ? viewCam.transform : GetHeadTransform();
        if (camXform == null)
        {
            return;
        }

        Vector3 hipMid = (obj.jointsCam[11] + obj.jointsCam[12]) * 0.5f;
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
                ? rootWorld + camXform.TransformVector(joint)
                : camXform.TransformPoint(joint);
        }

        Log(LogCategory.BONE,
            $"f={frame} t={obj.trackId} J={jointCount} hipMid=({hipMid.x:F3},{hipMid.y:F3},{hipMid.z:F3}) mode={(rootRel ? "RootRel" : "CamSpace")} visOk={visOk}/{jointCount}",
            frame, (int)obj.trackId);

        if (ShouldLog(LogCategory.BONE, frame, (int)obj.trackId))
        {
            DrawCocoSkeleton(jointsWorld, obj.jointsVis);
        }

        if (!enableBoneApply)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        HumanoidRigCache cache = GetOrBuildHumanoidCache(animator);
        if (cache == null || !cache.ready)
        {
            return;
        }

        bool applied = ApplyHumanoidLimbs(cache, jointsWorld, obj.jointsVis);
        if (alignFeetToAnkles)
        {
            AlignFeetToAnkles(cache, jointsWorld, obj.jointsVis, instance.transform);
        }

        if (applied && !boneAppliedLogged)
        {
            boneAppliedLogged = true;
            Log(LogCategory.BONE, "BONE_STATUS applied=true");
        }
    }

    private void DrawCocoSkeleton(Vector3[] jointsWorld, byte[] vis)
    {
        if (jointsWorld == null || vis == null)
        {
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
        foreach (var map in GetBoneMaps())
        {
            Transform bone = animator.GetBoneTransform(map.bone);
            if (bone == null)
            {
                continue;
            }

            cache.bones[map.bone] = bone;
            cache.bindRotWorld[map.bone] = bone.rotation;
            Vector3 dir = Vector3.forward;
            if (bone.childCount > 0)
            {
                dir = (bone.GetChild(0).position - bone.position).normalized;
            }
            cache.bindDirWorld[map.bone] = dir == Vector3.zero ? Vector3.forward : dir;
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

    private bool ApplyHumanoidLimbs(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis)
    {
        if (cache == null || !cache.ready || jointsWorld == null || vis == null)
        {
            return false;
        }

        bool appliedAny = false;
        foreach (var map in GetBoneMaps())
        {
            if (!cache.bones.TryGetValue(map.bone, out Transform bone))
            {
                continue;
            }

            if (map.jointA >= jointsWorld.Length || map.jointB >= jointsWorld.Length)
            {
                continue;
            }

            if (vis[map.jointA] == 0 || vis[map.jointB] == 0)
            {
                continue;
            }

            Vector3 targetDir = (jointsWorld[map.jointB] - jointsWorld[map.jointA]).normalized;
            if (targetDir == Vector3.zero)
            {
                continue;
            }

            Vector3 bindDir = cache.bindDirWorld[map.bone];
            Quaternion bindRot = cache.bindRotWorld[map.bone];
            Quaternion targetRot = Quaternion.FromToRotation(bindDir, targetDir) * bindRot;
            bone.rotation = Quaternion.Slerp(bone.rotation, targetRot, boneApplyAlpha);
            appliedAny = true;
        }

        return appliedAny;
    }

    private void AlignFeetToAnkles(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, Transform root)
    {
        if (cache == null || jointsWorld == null || vis == null || root == null)
        {
            return;
        }

        if (vis.Length <= 16 || jointsWorld.Length <= 16)
        {
            return;
        }

        if (vis[15] == 0 || vis[16] == 0)
        {
            return;
        }

        if (!cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform leftFoot) ||
            !cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot))
        {
            return;
        }

        Vector3 targetMid = (jointsWorld[15] + jointsWorld[16]) * 0.5f;
        Vector3 currentMid = (leftFoot.position + rightFoot.position) * 0.5f;
        Vector3 delta = targetMid - currentMid;
        if (delta == Vector3.zero)
        {
            return;
        }

        root.position += delta * Mathf.Clamp01(footAlignAlpha);
    }

    private IEnumerable<BoneMap> GetBoneMaps()
    {
        yield return new BoneMap { bone = HumanBodyBones.LeftUpperArm, jointA = 5, jointB = 7 };
        yield return new BoneMap { bone = HumanBodyBones.LeftLowerArm, jointA = 7, jointB = 9 };
        yield return new BoneMap { bone = HumanBodyBones.RightUpperArm, jointA = 6, jointB = 8 };
        yield return new BoneMap { bone = HumanBodyBones.RightLowerArm, jointA = 8, jointB = 10 };
        yield return new BoneMap { bone = HumanBodyBones.LeftUpperLeg, jointA = 11, jointB = 13 };
        yield return new BoneMap { bone = HumanBodyBones.LeftLowerLeg, jointA = 13, jointB = 15 };
        yield return new BoneMap { bone = HumanBodyBones.RightUpperLeg, jointA = 12, jointB = 14 };
        yield return new BoneMap { bone = HumanBodyBones.RightLowerLeg, jointA = 14, jointB = 16 };
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
