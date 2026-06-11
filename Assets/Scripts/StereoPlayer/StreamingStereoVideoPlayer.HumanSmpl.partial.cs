using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private struct HumanSmplPose
    {
        public bool hasGlobalOrient;
        public Quaternion globalOrient;
        public Quaternion[] bodyPose;
        public bool hasTransl;
        public Vector3 transl;
        public bool hasFocalLength;
        public Vector2 focalLength;
        public float[] betas;
        // Camera-to-world rotation (screen transform basis). Stored for potential future use.
        public Quaternion camRotation;
    }

    private sealed class HumanSmplRetargetState
    {
        public readonly Dictionary<HumanBodyBones, Quaternion> referenceUnityLocal = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Dictionary<HumanBodyBones, Quaternion> referenceSmplLocal = new Dictionary<HumanBodyBones, Quaternion>();
        public readonly Quaternion[] smplFk = new Quaternion[22]; // indices 0-21, reused each frame
        // Smoothed SMPL local rotations for temporal noise reduction (index 0 = worldFk0, 1-21 = body_pose joints)
        public readonly Quaternion[] smoothedSmplLocal = new Quaternion[22];
        public bool smoothingInitialized = false;
        public int debugFrameCount = 0;
    }

    private readonly Dictionary<int, Dictionary<uint, HumanSmplPose>> humanSmplPosesByFrame = new Dictionary<int, Dictionary<uint, HumanSmplPose>>();
    // meta.bin SMPL cache: populated per-frame during TryReadFrameObjects, takes priority over sidecar
    private readonly Dictionary<int, Dictionary<uint, HumanSmplPose>> humanSmplPosesMetaBin = new Dictionary<int, Dictionary<uint, HumanSmplPose>>();
    private readonly Dictionary<HumanoidRigCache, HumanSmplRetargetState> humanSmplRetargetStateByCache = new Dictionary<HumanoidRigCache, HumanSmplRetargetState>();
    private int humanSmplSourceWidth;
    private int humanSmplSourceHeight;

    // Topological order for FK traversal (parents always before children)
    private static readonly int[] SmplJointTopologicalOrder =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21
    };

    // SMPL parent joint index for joints 0-21 (index 0 = Pelvis root = -1)
    // Parents: LHip/RHip/Spine1→Pelvis, LKnee→LHip, RKnee→RHip, Spine2→Spine1,
    //          LAnkle→LKnee, RAnkle→RKnee, Spine3→Spine2, LFoot→LAnkle, RFoot→RAnkle,
    //          Neck/LCollar/RCollar→Spine3, Head→Neck,
    //          LShoulder→LCollar, RShoulder→RCollar,
    //          LElbow→LShoulder, RElbow→RShoulder,
    //          LWrist→LElbow, RWrist→RElbow
    private static readonly int[] SmplJointParentArray =
    {
        -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19
    };

    private static readonly Dictionary<int, HumanBodyBones> SmplJointToHumanBone = new Dictionary<int, HumanBodyBones>
    {
        { 1, HumanBodyBones.LeftUpperLeg },
        { 2, HumanBodyBones.RightUpperLeg },
        { 3, HumanBodyBones.Spine },
        { 4, HumanBodyBones.LeftLowerLeg },
        { 5, HumanBodyBones.RightLowerLeg },
        { 6, HumanBodyBones.Chest },
        { 7, HumanBodyBones.LeftFoot },
        { 8, HumanBodyBones.RightFoot },
        { 9, HumanBodyBones.UpperChest },
        { 10, HumanBodyBones.LeftToes },
        { 11, HumanBodyBones.RightToes },
        { 12, HumanBodyBones.Neck },
        { 13, HumanBodyBones.LeftShoulder },
        { 14, HumanBodyBones.RightShoulder },
        { 15, HumanBodyBones.Head },
        { 16, HumanBodyBones.LeftUpperArm },
        { 17, HumanBodyBones.RightUpperArm },
        { 18, HumanBodyBones.LeftLowerArm },
        { 19, HumanBodyBones.RightLowerArm },
        { 20, HumanBodyBones.LeftHand },
        { 21, HumanBodyBones.RightHand }
    };

    private void LoadHumanSmplSidecar(string path)
    {
        humanSmplPosesByFrame.Clear();
        humanSmplRetargetStateByCache.Clear();
        humanSmplSourceWidth = 0;
        humanSmplSourceHeight = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            object rootObj = MiniJson.Parse(File.ReadAllText(path));
            Dictionary<string, object> root = rootObj as Dictionary<string, object>;
            if (root == null)
            {
                return;
            }

            Dictionary<string, object> meta = GetDict(root, "meta");
            Dictionary<string, object> video = GetDict(meta, "video");
            humanSmplSourceWidth = GetInt(video, "width", 0);
            humanSmplSourceHeight = GetInt(video, "height", 0);

            List<object> frames = GetList(root, "frames");
            if (frames == null)
            {
                return;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                Dictionary<string, object> frame = frames[i] as Dictionary<string, object>;
                if (frame == null)
                {
                    continue;
                }

                int frameIndex = GetInt(frame, "frame_index", GetInt(frame, "frameIndex", -1));
                if (frameIndex < 0)
                {
                    continue;
                }

                List<object> objects = GetList(frame, "objects");
                if (objects == null)
                {
                    continue;
                }

                Dictionary<uint, HumanSmplPose> byTrack = null;
                for (int o = 0; o < objects.Count; o++)
                {
                    Dictionary<string, object> obj = objects[o] as Dictionary<string, object>;
                    if (obj == null)
                    {
                        continue;
                    }

                    uint trackId = GetUInt(obj, "trackId", uint.MaxValue);
                    if (trackId == uint.MaxValue)
                    {
                        continue;
                    }

                    Dictionary<string, object> smpl = GetDict(obj, "smpl");
                    if (smpl == null || !TryReadHumanSmplPose(smpl, out HumanSmplPose pose))
                    {
                        continue;
                    }

                    pose.hasFocalLength = TryReadVector2(GetList(obj, "focalLength"), 1f, out pose.focalLength);

                    if (byTrack == null)
                    {
                        byTrack = new Dictionary<uint, HumanSmplPose>();
                        humanSmplPosesByFrame[frameIndex] = byTrack;
                    }

                    byTrack[trackId] = pose;
                }
            }

            Debug.Log($"SVB human SMPL sidecar loaded: frames={humanSmplPosesByFrame.Count}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load SVB human_smpl_from_sam2 sidecar: {ex.Message}");
        }
    }

    private static bool TryReadHumanSmplPose(Dictionary<string, object> smpl, out HumanSmplPose pose)
    {
        pose = new HumanSmplPose
        {
            bodyPose = new Quaternion[23]
        };

        List<object> globalOrient = GetList(smpl, "globalOrient");
        if (globalOrient != null && globalOrient.Count > 0 && TryReadRotationMatrix(globalOrient[0] as List<object>, flipCameraY: true, transposeMatrix: false, out Quaternion rootRotation))
        {
            pose.hasGlobalOrient = true;
            pose.globalOrient = rootRotation;
        }

        List<object> bodyPose = GetList(smpl, "bodyPose");
        if (bodyPose == null || bodyPose.Count < 1)
        {
            return false;
        }

        int count = Mathf.Min(23, bodyPose.Count);
        for (int i = 0; i < count; i++)
        {
            if (TryReadRotationMatrix(bodyPose[i] as List<object>, flipCameraY: false, transposeMatrix: false, out Quaternion rotation))
            {
                pose.bodyPose[i] = rotation;
            }
            else
            {
                pose.bodyPose[i] = Quaternion.identity;
            }
        }

        pose.hasTransl = TryReadVector3(GetList(smpl, "transl"), 1f, out pose.transl) && IsFinite(pose.transl);
        pose.betas = ReadFloatArray(GetList(smpl, "betas"));
        return true;
    }

    private static float[] ReadFloatArray(List<object> list)
    {
        if (list == null)
        {
            return null;
        }

        float[] values = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            values[i] = GetFloat(list, i);
        }

        return values;
    }

    private static bool TryReadRotationMatrix(List<object> rows, bool flipCameraY, bool transposeMatrix, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (rows == null || rows.Count < 3)
        {
            return false;
        }

        List<object> row0 = rows[0] as List<object>;
        List<object> row1 = rows[1] as List<object>;
        List<object> row2 = rows[2] as List<object>;
        if (row0 == null || row1 == null || row2 == null ||
            row0.Count < 3 || row1.Count < 3 || row2.Count < 3)
        {
            return false;
        }

        float m00 = GetFloat(row0, 0);
        float m01 = GetFloat(row0, 1);
        float m02 = GetFloat(row0, 2);
        float m10 = GetFloat(row1, 0);
        float m11 = GetFloat(row1, 1);
        float m12 = GetFloat(row1, 2);
        float m20 = GetFloat(row2, 0);
        float m21 = GetFloat(row2, 1);
        float m22 = GetFloat(row2, 2);

        if (flipCameraY)
        {
            if (transposeMatrix)
            {
                // Stored as R^T. Row extraction reconstructs R (col0_R=row0_M, col1_R=row1_M, col2_R=row2_M).
                // D*R negates row1 of R. Row1 of R = col1 of R^T = (m01, m11, m21). Negate those.
                m01 = -m01;
                m11 = -m11;
                m21 = -m21;
            }
            else
            {
                // Stored as R. D*R negates row1 of R = (m10, m11, m12) in row-major storage.
                m10 = -m10;
                m11 = -m11;
                m12 = -m12;
            }
        }

        Vector3 right, up, forward;
        if (transposeMatrix)
        {
            // Bundle stores rotation matrices as R^T. Row extraction reconstructs R:
            //   row0_M = col0_R → right, row1_M = col1_R → up, row2_M = col2_R → forward.
            right = new Vector3(m00, m01, m02);
            up = new Vector3(m10, m11, m12);
            forward = new Vector3(m20, m21, m22);
        }
        else
        {
            // Standard column extraction: right=col0, up=col1, forward=col2.
            right = new Vector3(m00, m10, m20);
            up = new Vector3(m01, m11, m21);
            forward = new Vector3(m02, m12, m22);
        }

        if (right.sqrMagnitude < 0.000001f || up.sqrMagnitude < 0.000001f || forward.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        forward.Normalize();
        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude < 0.000001f)
        {
            up = Vector3.up;
        }
        up.Normalize();

        rotation = Quaternion.LookRotation(forward, up);
        return IsFinite(rotation);
    }

    private bool TryGetHumanSmplPose(int frameIndex, uint trackId, out HumanSmplPose pose)
    {
        pose = default(HumanSmplPose);
        // meta.bin takes priority over sidecar
        if (humanSmplPosesMetaBin.TryGetValue(frameIndex, out Dictionary<uint, HumanSmplPose> byTrackMeta) &&
            byTrackMeta.TryGetValue(trackId, out pose))
        {
            return true;
        }
        return humanSmplPosesByFrame.TryGetValue(frameIndex, out Dictionary<uint, HumanSmplPose> byTrack) &&
               byTrack.TryGetValue(trackId, out pose);
    }

    private bool TryGetHumanSmplRootWorld(Transform screen, HumanSmplPose pose, float fallbackDepthMeters, out Vector3 rootWorld)
    {
        rootWorld = Vector3.zero;
        if (!EnableHumanSmplMotion || !pose.hasTransl || !TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        float sourceDepth = Mathf.Abs(pose.transl.z);
        if (sourceDepth <= 0.0001f)
        {
            return false;
        }

        float targetDepth = Mathf.Max(0.001f, fallbackDepthMeters);
        if (TryGetHumanSmplRootEyePixel(pose, out float uEye, out float vEye))
        {
            rootWorld = AnchorUvZToWorldPinhole(screen, uEye, vEye, targetDepth);
            return IsFinite(rootWorld);
        }

        Vector3 transl = pose.transl;
        if (HumanSmplFlipY)
        {
            transl.y = -transl.y;
        }

        Vector3 normalizedCam = new Vector3(transl.x / sourceDepth, transl.y / sourceDepth, 1f) * targetDepth;
        rootWorld = camOrigin + (camRotation * normalizedCam);
        return IsFinite(rootWorld);
    }

    private bool TryGetHumanSmplRootEyePixel(HumanSmplPose pose, out float uEye, out float vEye)
    {
        uEye = 0f;
        vEye = 0f;
        if (!pose.hasTransl ||
            !pose.hasFocalLength ||
            pose.focalLength.x <= 0.0001f ||
            pose.focalLength.y <= 0.0001f ||
            humanSmplSourceWidth <= 0 ||
            humanSmplSourceHeight <= 0 ||
            manifest == null ||
            manifest.eye_w <= 0 ||
            manifest.eye_h <= 0)
        {
            return false;
        }

        float sourceDepth = Mathf.Abs(pose.transl.z);
        if (sourceDepth <= 0.0001f)
        {
            return false;
        }

        float uSource = (pose.transl.x / sourceDepth) * pose.focalLength.x + humanSmplSourceWidth * 0.5f;
        float vSource = (pose.transl.y / sourceDepth) * pose.focalLength.y + humanSmplSourceHeight * 0.5f;
        if (float.IsNaN(uSource) || float.IsInfinity(uSource) || float.IsNaN(vSource) || float.IsInfinity(vSource))
        {
            return false;
        }

        uEye = Mathf.Clamp(uSource * manifest.eye_w / humanSmplSourceWidth, 0f, manifest.eye_w - 1f);
        vEye = Mathf.Clamp(vSource * manifest.eye_h / humanSmplSourceHeight, 0f, manifest.eye_h - 1f);
        return true;
    }

    private bool TryGetHumanSmplRootRotation(Transform screen, HumanSmplPose pose, out Quaternion rootRotation)
    {
        rootRotation = Quaternion.identity;
        if (!EnableHumanSmplMotion || !pose.hasGlobalOrient || !IsFinite(pose.globalOrient))
        {
            return false;
        }

        Quaternion basis = GetPinholeBasisRotation(screen);
        return TryBuildHumanSmplUprightRootRotation(basis, pose.globalOrient, basis * Vector3.up, out rootRotation);
    }

    public static bool TryBuildHumanSmplUprightRootRotation(
        Quaternion cameraBasis,
        Quaternion smplGlobalOrient,
        Vector3 worldUp,
        out Quaternion rootRotation)
    {
        rootRotation = Quaternion.identity;
        if (!IsFinite(cameraBasis) || !IsFinite(smplGlobalOrient))
        {
            return false;
        }

        if (worldUp.sqrMagnitude < 0.000001f)
        {
            worldUp = Vector3.up;
        }
        worldUp.Normalize();

        Vector3 forward = cameraBasis * (smplGlobalOrient * Vector3.forward);
        forward = Vector3.ProjectOnPlane(forward, worldUp);
        if (forward.sqrMagnitude < 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(cameraBasis * Vector3.forward, worldUp);
        }

        if (forward.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        forward.Normalize();
        rootRotation = Quaternion.LookRotation(forward, worldUp);
        return IsFinite(rootRotation);
    }

    private bool TryApplyHumanSmplRotationOverlay(HumanoidRigCache cache, HumanSmplPose pose)
    {
        if (!EnableHumanSmplMotion || cache == null || !cache.ready || pose.bodyPose == null)
        {
            return false;
        }

        HumanSmplRetargetState state = GetOrCreateHumanSmplRetargetState(cache, pose);
        if (state == null)
        {
            return false;
        }

        float alpha = ResolveHumanSmplOrientationOverlayAlpha(HumanSmplRotationAlpha, boneApplyAlpha);
        bool appliedAny = false;

        if (ShouldUseSmplOnlyPose())
        {
            // Walk joints in topological order (parents before children) to accumulate SMPL FK.
            // Formula: correctedLocal = Inv(parentFk) * tPoseLocal * parentFk * smplLocal
            //
            // Each smplLocal is exponentially smoothed toward the new value to reduce capture noise.
            // Half-life = SmplSmoothHalfLifeSec: at 30fps a value moves ~37% toward target per frame.
            const float SmplSmoothHalfLifeSec = 0.05f; // 50ms: smooth enough to cut jitter, fast enough to track motion
            float dt = Time.deltaTime;
            // alpha: fraction of the way toward the new value this frame (frame-rate independent)
            float smoothAlpha = SmplSmoothHalfLifeSec > 0f
                ? 1f - Mathf.Exp(-dt * 0.693147f / SmplSmoothHalfLifeSec)
                : 1f;

            Quaternion[] fk = state.smplFk;
            // globalOrient is in Y-up camera space (after D*R conversion in TryReadRotationMatrix).
            // Multiply by camRotation (camera-to-world) to get the pelvis orientation in Unity world space.
            // For a standard VR setup: camRotation ≈ 180°Y, globalOrient for upright person ≈ 180°Y,
            // so fk[0] = camRotation * globalOrient ≈ identity → T-pose for upright person. ✓
            fk[0] = Quaternion.identity;
            if (TryGetHumanSmplLocalRotation(pose, 0, out Quaternion rawGlobalOrient) && IsFinite(rawGlobalOrient))
            {
                Quaternion worldFk0 = pose.camRotation * rawGlobalOrient;
                if (IsFinite(worldFk0))
                {
                    state.smoothedSmplLocal[0] = state.smoothingInitialized
                        ? Quaternion.Slerp(state.smoothedSmplLocal[0], worldFk0, smoothAlpha)
                        : worldFk0;
                    fk[0] = state.smoothedSmplLocal[0];
                }
            }

            if (cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hipsBone) &&
                hipsBone != null &&
                cache.bindRotWorld.TryGetValue(HumanBodyBones.Hips, out Quaternion bindHipsWorld) &&
                IsFinite(bindHipsWorld) && IsFinite(fk[0]))
            {
                Quaternion targetHipsWorld = bindHipsWorld * fk[0];
                if (IsFinite(targetHipsWorld))
                {
                    Quaternion targetHipsLocal = hipsBone.parent != null
                        ? Quaternion.Inverse(hipsBone.parent.rotation) * targetHipsWorld
                        : targetHipsWorld;
                    if (IsFinite(targetHipsLocal))
                    {
                        TransformWriter.ApplyLocalRotation(hipsBone, targetHipsLocal);
                    }
                }
            }

            // Log frame 0 (initial) AND frames ~30 and ~60 to capture non-outlier frames
            state.debugFrameCount = state.debugFrameCount + 1;
            bool debugLog = !state.smoothingInitialized || state.debugFrameCount == 30 || state.debugFrameCount == 60;
            if (debugLog)
            {
                Debug.Log($"[SMPL-FK-DBG] frame={state.debugFrameCount} camRot={pose.camRotation.eulerAngles} rawGO={pose.globalOrient.eulerAngles} fk0={fk[0].eulerAngles}");
                if (cache.bindRotWorld.TryGetValue(HumanBodyBones.Hips, out Quaternion dbgHipsWorld))
                    Debug.Log($"[SMPL-FK-DBG] bindHipsWorld={dbgHipsWorld.eulerAngles}");
            }

            // Body joints use body_pose-relative FK (starts from identity).
            // globalOrient is already applied to the Hips bone above.
            // Unity's kinematic chain propagates the Hips rotation to all descendants automatically,
            // so child joints only need body_pose applied relative to their own T-pose.
            // Resetting fk[0] here means parentFk for root-level joints (e.g. LeftUpperLeg) = identity,
            // which gives correctedLocal = bindRotLocal * body_pose → T-pose preserved when body_pose = identity.
            fk[0] = Quaternion.identity;

            for (int i = 0; i < SmplJointTopologicalOrder.Length; i++)
            {
                int joint = SmplJointTopologicalOrder[i];
                int parentJoint = SmplJointParentArray[joint];
                Quaternion parentFk = fk[parentJoint];

                Quaternion rawSmplLocal = Quaternion.identity;
                TryGetHumanSmplLocalRotation(pose, joint, out rawSmplLocal);

                // Smooth the input rotation before accumulating FK. Smoothing the input (not the output)
                // keeps the FK chain consistent: smoothed parent naturally carries the child.
                Quaternion smplLocal = state.smoothingInitialized
                    ? Quaternion.Slerp(state.smoothedSmplLocal[joint], rawSmplLocal, smoothAlpha)
                    : rawSmplLocal;
                state.smoothedSmplLocal[joint] = smplLocal;

                fk[joint] = parentFk * smplLocal;

                if (!SmplJointToHumanBone.TryGetValue(joint, out HumanBodyBones boneId)) continue;
                if (!cache.bones.TryGetValue(boneId, out Transform bone) || bone == null)
                {
                    if (debugLog) Debug.Log($"[SMPL-FK-DBG] joint={joint}({boneId}) BONE MISSING in rig");
                    continue;
                }
                if (!state.referenceUnityLocal.TryGetValue(boneId, out Quaternion tPoseLocal)) continue;

                Quaternion correctedLocal = Quaternion.Inverse(parentFk) * tPoseLocal * parentFk * smplLocal;
                if (!IsFinite(correctedLocal)) continue;

                // Log key joints: hips chain (1,4), ankle (7), toes (10), elbow (18), wrist (20), shoulder (16)
                bool logThisJoint = debugLog && (joint == 1 || joint == 4 || joint == 7 || joint == 10 || joint == 16 || joint == 18 || joint == 20);
                if (logThisJoint)
                {
                    Quaternion dbgBindWorld = cache.bindRotWorld.TryGetValue(boneId, out Quaternion dbgBW) ? dbgBW : Quaternion.identity;
                    Debug.Log($"[SMPL-FK-DBG] joint={joint}({boneId}) parentJoint={parentJoint} " +
                        $"smplLocal={smplLocal.eulerAngles} tPoseLocal={tPoseLocal.eulerAngles} " +
                        $"parentFk={parentFk.eulerAngles} correctedLocal={correctedLocal.eulerAngles} " +
                        $"bindRotWorld={dbgBindWorld.eulerAngles} boneWorld_before={bone.rotation.eulerAngles}");
                    Debug.Log($"[SMPL-FK-DBG]   before: bone.fwd={bone.forward:F2}  bone.up={bone.up:F2}  bone.pos={bone.position:F2}");
                }

                TransformWriter.ApplyLocalRotation(bone, correctedLocal);

                if (logThisJoint)
                {
                    Quaternion dbgBW2 = cache.bindRotWorld.TryGetValue(boneId, out Quaternion tmp2) ? tmp2 : Quaternion.identity;
                    Debug.Log($"[SMPL-FK-DBG]   boneWorld_after={bone.rotation.eulerAngles}  fk[j]={fk[joint].eulerAngles}  bindRotWorld*fk[j]={(dbgBW2 * fk[joint]).eulerAngles}");
                    Debug.Log($"[SMPL-FK-DBG]   bone.fwd={bone.forward:F2}  bone.up={bone.up:F2}  bone.pos={bone.position:F2}");
                }

                appliedAny = true;
            }
            state.smoothingInitialized = true;
            return appliedAny;
        }

        appliedAny |= ApplyHumanSmplBoneTwistOverlay(cache, state, pose, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 16, alpha * 0.65f);
        appliedAny |= ApplyHumanSmplBoneTwistOverlay(cache, state, pose, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 17, alpha * 0.65f);
        if (ShouldApplyHumanSmplLowerArmBendRotation())
        {
            appliedAny |= ApplyHumanSmplBoneFullOverlay(cache, state, pose, HumanBodyBones.LeftLowerArm, 18, alpha);
            appliedAny |= ApplyHumanSmplBoneFullOverlay(cache, state, pose, HumanBodyBones.RightLowerArm, 19, alpha);
        }
        else
        {
            appliedAny |= ApplyHumanSmplBoneTwistOverlay(cache, state, pose, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 18, alpha);
            appliedAny |= ApplyHumanSmplBoneTwistOverlay(cache, state, pose, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 19, alpha);
        }
        if (ShouldApplyHumanSmplFullHandRotation())
        {
            appliedAny |= ApplyHumanSmplBoneFullOverlay(cache, state, pose, HumanBodyBones.LeftHand, 20, alpha);
            appliedAny |= ApplyHumanSmplBoneFullOverlay(cache, state, pose, HumanBodyBones.RightHand, 21, alpha);
        }

        return appliedAny;
    }

    private HumanSmplRetargetState GetOrCreateHumanSmplRetargetState(HumanoidRigCache cache, HumanSmplPose pose)
    {
        if (cache == null || pose.bodyPose == null)
        {
            return null;
        }

        if (humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState existing) && existing != null)
        {
            return existing;
        }

        HumanSmplRetargetState state = new HumanSmplRetargetState();
        foreach (KeyValuePair<int, HumanBodyBones> kv in SmplJointToHumanBone)
        {
            HumanBodyBones boneId = kv.Value;

            if (!cache.bones.TryGetValue(boneId, out Transform bone) || bone == null)
            {
                continue;
            }

            // referenceUnityLocal = T-pose rotation from bindRotLocal (captured via HumanPoseHandler in GetOrBuildHumanoidCache).
            // referenceSmplLocal = identity (SMPL T-pose is all identity rotations).
            // Formula: targetLocal = T-pose_Unity * (Inv(identity) * smplLocal) = T-pose_Unity * smplLocal
            Quaternion tPoseRot = cache.bindRotLocal.TryGetValue(boneId, out Quaternion bindRot) ? bindRot : Quaternion.identity;
            state.referenceUnityLocal[boneId] = tPoseRot;
            state.referenceSmplLocal[boneId] = Quaternion.identity;
        }

        if (state.referenceUnityLocal.Count <= 0)
        {
            return null;
        }

        humanSmplRetargetStateByCache[cache] = state;
        return state;
    }

    private bool ApplyHumanSmplBoneFullOverlay(
        HumanoidRigCache cache,
        HumanSmplRetargetState state,
        HumanSmplPose pose,
        HumanBodyBones boneId,
        int smplJoint,
        float alpha)
    {
        if (alpha <= 0f || !TryGetHumanSmplTargetLocal(cache, state, pose, boneId, smplJoint, out Transform bone, out Quaternion targetLocal))
        {
            return false;
        }

        TransformWriter.ApplyLocalRotation(
            bone,
            Quaternion.Slerp(bone.localRotation, targetLocal, alpha));
        return true;
    }

    private bool ApplyHumanSmplBoneTwistOverlay(
        HumanoidRigCache cache,
        HumanSmplRetargetState state,
        HumanSmplPose pose,
        HumanBodyBones boneId,
        HumanBodyBones childId,
        int smplJoint,
        float alpha)
    {
        if (alpha <= 0f || !TryGetHumanSmplTargetLocal(cache, state, pose, boneId, smplJoint, out Transform bone, out Quaternion targetLocal))
        {
            return false;
        }

        Transform child = null;
        cache.bones.TryGetValue(childId, out child);
        if (child == null)
        {
            return false;
        }

        Vector3 axisWorld = child.position - bone.position;
        if (axisWorld.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        Vector3 axisLocal = bone.InverseTransformDirection(axisWorld.normalized);
        if (axisLocal.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        axisLocal.Normalize();

        Quaternion localDelta = Quaternion.Inverse(bone.localRotation) * targetLocal;
        Quaternion twist = ExtractTwist(localDelta, axisLocal);
        if (!IsFinite(twist))
        {
            return false;
        }

        TransformWriter.ApplyLocalRotation(
            bone,
            bone.localRotation * Quaternion.Slerp(Quaternion.identity, twist, alpha));
        return true;
    }

    private static bool TryGetHumanSmplTargetLocal(
        HumanoidRigCache cache,
        HumanSmplRetargetState state,
        HumanSmplPose pose,
        HumanBodyBones boneId,
        int smplJoint,
        out Transform bone,
        out Quaternion targetLocal)
    {
        bone = null;
        targetLocal = Quaternion.identity;
        if (cache == null || !TryGetHumanSmplLocalRotation(pose, smplJoint, out Quaternion smplLocal))
        {
            return false;
        }

        if (!cache.bones.TryGetValue(boneId, out bone) || bone == null)
        {
            return false;
        }

        if (!state.referenceUnityLocal.TryGetValue(boneId, out Quaternion referenceUnityLocal))
        {
            if (!cache.bindRotLocal.TryGetValue(boneId, out referenceUnityLocal))
            {
                referenceUnityLocal = bone.localRotation;
            }
        }

        if (!state.referenceSmplLocal.TryGetValue(boneId, out Quaternion referenceSmplLocal))
        {
            referenceSmplLocal = Quaternion.identity;
        }

        targetLocal = RetargetHumanSmplLocalRotation(referenceUnityLocal, referenceSmplLocal, smplLocal);
        return IsFinite(targetLocal);
    }

    public static Quaternion RetargetHumanSmplLocalRotation(
        Quaternion referenceUnityLocal,
        Quaternion referenceSmplLocal,
        Quaternion currentSmplLocal)
    {
        return referenceUnityLocal * (Quaternion.Inverse(referenceSmplLocal) * currentSmplLocal);
    }

    public static bool ShouldApplyHumanSmplFullHandRotation()
    {
        return true;
    }

    public static bool ShouldApplyHumanSmplLowerArmBendRotation()
    {
        return true;
    }

    public static bool ShouldUseHumanSmplRootOrientation()
    {
        return false;
    }

    public static bool ShouldUseHumanSmplRootPlacementPolicy(bool isPerson, bool hasHumanSmplTranslation)
    {
        return false;
    }

    public static bool ShouldUseSmplOnlyPose()
    {
        return true;
    }

    public static bool ShouldApplyHumanSmplBeforeLimbIk()
    {
        return false;
    }

    public static bool ShouldApplyHumanSmplAfterLimbIk()
    {
        return true;
    }

    public static bool ShouldUseKeypointIkForHumanBone(bool hasHumanSmplPose, bool hasValidHumanSmplRotationForBone)
    {
        return true;
    }

    public static float ResolveHumanSmplOrientationOverlayAlpha(float smplRotationAlpha, float boneApplyAlpha)
    {
        return Mathf.Clamp01(smplRotationAlpha * boneApplyAlpha);
    }

    public static bool ShouldPreserveRootScreenHeightAfterHumanSkeletonPlacement()
    {
        return true;
    }

    public static Vector3 ResolveRootPositionPreservingScreenHeight(
        Vector3 currentPosition,
        Vector3 referencePosition,
        Vector3 screenUp)
    {
        Vector3 up = screenUp.sqrMagnitude > 0.000001f ? screenUp.normalized : Vector3.up;
        return currentPosition + up * Vector3.Dot(referencePosition - currentPosition, up);
    }

    private static bool TryGetSmplJointForHumanBone(HumanBodyBones boneId, out int smplJoint)
    {
        foreach (KeyValuePair<int, HumanBodyBones> kv in SmplJointToHumanBone)
        {
            if (kv.Value == boneId)
            {
                smplJoint = kv.Key;
                return true;
            }
        }

        smplJoint = -1;
        return false;
    }

    private static bool TryGetHumanSmplLocalRotation(HumanSmplPose pose, int smplJoint, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (smplJoint == 0)
        {
            if (!pose.hasGlobalOrient || !IsFinite(pose.globalOrient))
            {
                return false;
            }

            rotation = pose.globalOrient;
            return true;
        }

        int bodyPoseIndex = smplJoint - 1;
        if (pose.bodyPose == null ||
            bodyPoseIndex < 0 ||
            bodyPoseIndex >= pose.bodyPose.Length ||
            !IsFinite(pose.bodyPose[bodyPoseIndex]))
        {
            return false;
        }

        rotation = pose.bodyPose[bodyPoseIndex];
        return true;
    }

    private static Quaternion ExtractTwist(Quaternion rotation, Vector3 axis)
    {
        if (axis.sqrMagnitude < 0.000001f)
        {
            return Quaternion.identity;
        }

        axis.Normalize();
        Vector3 r = new Vector3(rotation.x, rotation.y, rotation.z);
        Vector3 projected = Vector3.Project(r, axis);
        Quaternion twist = new Quaternion(projected.x, projected.y, projected.z, rotation.w);
        float mag = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        if (mag <= 0.000001f)
        {
            return Quaternion.identity;
        }

        twist.x /= mag;
        twist.y /= mag;
        twist.z /= mag;
        twist.w /= mag;
        return twist;
    }

    private static bool IsFinite(Quaternion q)
    {
        return
            !float.IsNaN(q.x) && !float.IsInfinity(q.x) &&
            !float.IsNaN(q.y) && !float.IsInfinity(q.y) &&
            !float.IsNaN(q.z) && !float.IsInfinity(q.z) &&
            !float.IsNaN(q.w) && !float.IsInfinity(q.w);
    }

    private static bool IsFinite(Vector3 v)
    {
        return
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
            !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }

    private static bool TryReadVector2(List<object> list, float unitScale, out Vector2 value)
    {
        value = Vector2.zero;
        if (list == null || list.Count < 2)
        {
            return false;
        }

        value = new Vector2(
            GetFloat(list, 0) * unitScale,
            GetFloat(list, 1) * unitScale);
        return true;
    }

    // Called from TryReadFrameObjects after consuming block_version (uint16).
    // Reads rotation_count, beta_count, then all SMPL floats, and stores the pose.
    // Always advances the stream by exactly (rotCount*9 + betaCount + 3) floats.
    internal void StoreSmplBlockFromBin(System.IO.BinaryReader br, int frameIndex, uint trackId)
    {
        ushort rotCount = br.ReadUInt16();
        ushort betaCount = br.ReadUInt16();

        long dataStart = br.BaseStream.Position;
        long dataBytes = (long)(rotCount * 9 + betaCount + 3) * sizeof(float);

        if (rotCount >= 1 && rotCount <= 64 && betaCount <= 64)
        {
            var pose = new HumanSmplPose { bodyPose = new Quaternion[23] };

            // rotations[0] = global_orient: flipCameraY=true (D*R)
            pose.hasGlobalOrient = TryReadRotationMatrixFromBin(br, flipCameraY: true, out pose.globalOrient);

            // rotations[1..23] = body_pose: flipCameraY=false
            int bodyCount = Mathf.Min(23, rotCount - 1);
            for (int i = 0; i < bodyCount; i++)
            {
                if (!TryReadRotationMatrixFromBin(br, flipCameraY: false, out pose.bodyPose[i]))
                {
                    pose.bodyPose[i] = Quaternion.identity;
                }
            }
            // Skip any extra rotations beyond 24
            int extraRot = rotCount - 1 - bodyCount;
            if (extraRot > 0)
            {
                br.BaseStream.Seek(extraRot * 9 * sizeof(float), System.IO.SeekOrigin.Current);
            }

            if (betaCount > 0)
            {
                pose.betas = new float[betaCount];
                for (int i = 0; i < betaCount; i++)
                {
                    pose.betas[i] = br.ReadSingle();
                }
            }

            float tx = br.ReadSingle();
            float ty = br.ReadSingle();
            float tz = br.ReadSingle();
            pose.transl = new Vector3(tx, ty, tz);
            pose.hasTransl = IsFinite(pose.transl) && Mathf.Abs(tz) > 0.0001f;

            if (!humanSmplPosesMetaBin.TryGetValue(frameIndex, out Dictionary<uint, HumanSmplPose> byTrack))
            {
                byTrack = new Dictionary<uint, HumanSmplPose>();
                humanSmplPosesMetaBin[frameIndex] = byTrack;
            }
            byTrack[trackId] = pose;
        }

        // Ensure stream is at exactly the right position regardless of parsing outcome
        long expectedEnd = dataStart + dataBytes;
        if (br.BaseStream.Position != expectedEnd)
        {
            br.BaseStream.Seek(expectedEnd, System.IO.SeekOrigin.Begin);
        }
    }

    // Reads 9 float32s (row-major: m00,m01,m02, m10,m11,m12, m20,m21,m22) and returns a Quaternion.
    // flipCameraY negates row1 (D*R for globalOrient). Column extraction gives right/up/forward.
    private static bool TryReadRotationMatrixFromBin(System.IO.BinaryReader br, bool flipCameraY, out Quaternion rotation)
    {
        float m00 = br.ReadSingle(), m01 = br.ReadSingle(), m02 = br.ReadSingle();
        float m10 = br.ReadSingle(), m11 = br.ReadSingle(), m12 = br.ReadSingle();
        float m20 = br.ReadSingle(), m21 = br.ReadSingle(), m22 = br.ReadSingle();

        if (flipCameraY)
        {
            m10 = -m10;
            m11 = -m11;
            m12 = -m12;
        }

        Vector3 right   = new Vector3(m00, m10, m20);
        Vector3 up      = new Vector3(m01, m11, m21);
        Vector3 forward = new Vector3(m02, m12, m22);

        if (right.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f || forward.sqrMagnitude < 1e-6f)
        {
            rotation = Quaternion.identity;
            return false;
        }

        forward.Normalize();
        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude < 1e-6f)
        {
            up = Vector3.up;
        }
        up.Normalize();

        rotation = Quaternion.LookRotation(forward, up);
        return IsFinite(rotation);
    }
}
