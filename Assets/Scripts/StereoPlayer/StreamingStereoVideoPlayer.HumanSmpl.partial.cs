using System.Collections.Generic;
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

    // meta.bin SMPL cache: populated per-frame during TryReadFrameObjects
    private readonly Dictionary<int, Dictionary<uint, HumanSmplPose>> humanSmplPosesMetaBin = new Dictionary<int, Dictionary<uint, HumanSmplPose>>();
    private readonly Dictionary<HumanoidRigCache, HumanSmplRetargetState> humanSmplRetargetStateByCache = new Dictionary<HumanoidRigCache, HumanSmplRetargetState>();
    // Smoothed root world positions for SMPL-only FK placement (reduces anchorZ depth noise → character forward/backward jitter)
    private readonly Dictionary<HumanoidRigCache, Vector3> humanSmplSmoothedRoot = new Dictionary<HumanoidRigCache, Vector3>();
    private readonly Dictionary<HumanoidRigCache, float> humanSmplSmoothedDepth = new Dictionary<HumanoidRigCache, float>();
    private readonly HashSet<HumanoidRigCache> humanSmplSmoothedRootInit = new HashSet<HumanoidRigCache>();
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
        // source/human_smpl_from_sam2.json は debug/検証用。runtime 配置には使用しない。
        // SMPL FK は meta.bin の SMPL block (humanSmplPosesMetaBin) のみを参照する。
        // Human-Other 接触判定に必要な「元映像上の 2D keypoint」も、meta.bin の
        // keypoints3d (jointsCam) を投影して作るため sidecar には依存しない
        // （ResetHumanOtherContactState / TryBuildHumanSourceContactPose 参照）。
        humanSmplRetargetStateByCache.Clear();
        ResetHumanOtherContactState();
    }

    // shot 境界で呼ぶ。humanSmplRetargetStateByCache 自体は破棄しない: referenceUnityLocal /
    // referenceSmplLocal はモデルの参照姿勢で shot とは無関係なため。時間方向の平滑化だけ
    // 未初期化に戻し、root 位置・深度の平滑化も捨てて新しい shot の先頭フレームへスナップさせる。
    private void ResetHumanSmplSmoothingForShotBoundary()
    {
        foreach (KeyValuePair<HumanoidRigCache, HumanSmplRetargetState> kv in humanSmplRetargetStateByCache)
        {
            if (kv.Value != null)
            {
                kv.Value.smoothingInitialized = false;
            }
        }

        humanSmplSmoothedRoot.Clear();
        humanSmplSmoothedDepth.Clear();
        humanSmplSmoothedRootInit.Clear();
    }

    private bool TryGetHumanSmplPose(int frameIndex, uint trackId, out HumanSmplPose pose)
    {
        pose = default(HumanSmplPose);
        return humanSmplPosesMetaBin.TryGetValue(frameIndex, out Dictionary<uint, HumanSmplPose> byTrack) &&
               byTrack.TryGetValue(trackId, out pose);
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
            fk[0] = Quaternion.identity;
            // globalOrient is in Y-up camera space (after D*R conversion in TryReadRotationMatrix).
            // Multiply by camRotation (camera-to-world) to get the pelvis orientation in Unity world space.
            // For a standard VR setup: camRotation ≈ 180°Y, globalOrient for upright person ≈ 180°Y,
            // so fk[0] = camRotation * globalOrient ≈ identity → T-pose for upright person. ✓
            if (TryGetHumanSmplLocalRotation(pose, 0, out Quaternion rawGlobalOrient))
            {
                Quaternion worldFk0 = ResolveHumanSmplFkRootWorldRotation(pose.camRotation, rawGlobalOrient);
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
                Quaternion targetHipsWorld = ResolveHumanSmplTargetWorldRotation(fk[0], Quaternion.identity, bindHipsWorld);
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

            // 正しい標準FK公式（2026-06-13 修正）:
            // bone.rotation[j] = parentTargetWorld[j] * bindRotLocal[j] * bodyPose[j]
            //
            // bindRotLocal[j] = T-pose での joint j の親相対ローカル回転。
            // bodyPose[j] は親フレームで定義されるので、bindRotLocal を先に適用して
            // T-pose フレームを確立してから bodyPose を重ねるのが正しい順序。
            //
            // T-pose 検証（bodyPose=identity）:
            //   tw[j] = parentTW * bindRotLocal[j] * identity = parentTW * bindRotLocal[j]
            //   → 展開すると worldGlobalOrient * bindRotWorld[j]  ✓
            //
            // 旧公式 (globalOrient * fk_body * bindRotWorld) との違い:
            //   旧: ... * bodyPose * bindRotWorld  （body_pose → bindRot の順 → 右腕 180°Z で約77°ズレ）
            //   新: ... * bindRotLocal * bodyPose  （bindRot → body_pose の順 → 全ジョイント正しい）
            //
            // NG (旧 cumulative local): parentTargetWorld * bindRotLocal * smplLocal
            //   ApplyLocalRotation を使うと UpperChest BONE MISSING で腕が誤方向
            //   → ApplyWorldRotation + tw[] 追跡で解決済み
            Quaternion worldGlobalOrient = fk[0];
            // tw[] = 各 joint のターゲット world rotation（fk 配列を再利用）
            Quaternion[] tw = fk;
            if (cache.bindRotWorld.TryGetValue(HumanBodyBones.Hips, out Quaternion bindHipsW) && IsFinite(bindHipsW))
                tw[0] = worldGlobalOrient * bindHipsW;
            else
                tw[0] = worldGlobalOrient;


            for (int i = 0; i < SmplJointTopologicalOrder.Length; i++)
            {
                int joint = SmplJointTopologicalOrder[i];
                int parentJoint = SmplJointParentArray[joint];
                Quaternion parentTW = tw[parentJoint];

                Quaternion rawSmplLocal = Quaternion.identity;
                TryGetHumanSmplLocalRotation(pose, joint, out rawSmplLocal);

                Quaternion smplLocal = state.smoothingInitialized
                    ? Quaternion.Slerp(state.smoothedSmplLocal[joint], rawSmplLocal, smoothAlpha)
                    : rawSmplLocal;
                state.smoothedSmplLocal[joint] = smplLocal;

                // SMPL エスティメーターは Spine・Chest の前傾角を過大推定する傾向がある。
                // FK 伝播に使うスケール済み回転（smoothedSmplLocal には未スケール値を保存）。
                const float SpineBodyPoseScale = 0.25f;
                Quaternion fkLocal = (joint == 3 || joint == 6)
                    ? Quaternion.Slerp(Quaternion.identity, smplLocal, SpineBodyPoseScale)
                    : smplLocal;

                // HumanBone マッピングなし → bindLoc=identity で tw 積算してスキップ
                if (!SmplJointToHumanBone.TryGetValue(joint, out HumanBodyBones boneId))
                {
                    tw[joint] = parentTW * fkLocal;
                    continue;
                }

                // bindRotLocal を取得（マッピングあり・キャッシュなしは identity）
                if (!cache.bindRotLocal.TryGetValue(boneId, out Quaternion bindLoc) || !IsFinite(bindLoc))
                    bindLoc = Quaternion.identity;

                // 標準FK: tw[j] = parentTW * bindRotLocal[j] * bodyPose[j]
                tw[joint] = parentTW * bindLoc * fkLocal;

                // BONE MISSING → bone 設定はスキップするが tw chain は積算済み
                // （UpperChest 等の tw は子 joint（肩・腕）の parentTW として使われる）
                if (!cache.bones.TryGetValue(boneId, out Transform bone) || bone == null)
                {
                    if (debugLog) Debug.Log($"[SMPL-FK-DBG] joint={joint}({boneId}) BONE MISSING smplLocal={smplLocal.eulerAngles} tw={tw[joint].eulerAngles}");
                    continue;
                }

                if (IsTerminalHumanSmplHandBone(boneId) &&
                    !ShouldApplyHumanSmplTerminalHandRotationInSmplOnlyPose())
                {
                    continue;
                }

                Quaternion targetWorld = tw[joint];

                if (!IsFinite(targetWorld))
                    continue;

                bool logThisJoint = debugLog && (joint == 3 || joint == 6 || joint == 13 || joint == 14 || joint == 16 || joint == 17 || joint == 18 || joint == 19 || joint == 20);
                if (logThisJoint)
                {
                    if (cache.bindRotWorld.TryGetValue(boneId, out Quaternion dbgBindW))
                    {
                        Vector3 tposeFwd = (worldGlobalOrient * dbgBindW) * Vector3.forward;
                        Debug.Log($"[SMPL-FK-DBG] joint={joint}({boneId}) smplLocal={smplLocal.eulerAngles:F1} fkLocal={fkLocal.eulerAngles:F1} " +
                            $"bindLoc={bindLoc.eulerAngles:F1} tw={tw[joint].eulerAngles:F1}");
                        Debug.Log($"[SMPL-FK-DBG]   tpose.fwd={tposeFwd:F2}");
                    }
                }

                TransformWriter.ApplyWorldRotation(bone, targetWorld);

                if (logThisJoint)
                {
                    Debug.Log($"[SMPL-FK-DBG]   after:  bone.fwd={bone.forward:F2}  bone.up={bone.up:F2}");
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

    // 手のFK を AimAt 補正後に適用する。
    // 親フレームを SMPL joint 世界座標（elbow→wrist ベクトル）＋ worldUp から構築することで
    // キャラクター固有の bone axis 差を完全に排除する。
    // - forward = normalize(smplWrist - smplElbow): 全キャラ共通（SMPL joint 位置由来）
    // - up      = ProjectOnPlane(Vector3.up, forward): 全キャラ共通（worldUp 投影）
    // → smplLocal を同じ親フレームに適用するので全キャラ同じ手の向きになる。
    private void TryApplyHandFkAfterAimAt(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] jointVis)
    {
        if (!EnableHumanSmplMotion || cache == null || !cache.ready) return;
        if (!humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState state) || state == null) return;
        if (!state.smoothingInitialized) return;

        ApplyHandFkWithCanonicalParent(cache, state, HumanBodyBones.LeftHand,  smplHandJoint: 20,
            jointsWorld, jointVis, idxElbow: SmplLeftElbow,  idxWrist: SmplLeftWrist);
        ApplyHandFkWithCanonicalParent(cache, state, HumanBodyBones.RightHand, smplHandJoint: 21,
            jointsWorld, jointVis, idxElbow: SmplRightElbow, idxWrist: SmplRightWrist);
    }

    private static void ApplyHandFkWithCanonicalParent(
        HumanoidRigCache cache,
        HumanSmplRetargetState state,
        HumanBodyBones handBoneId,
        int smplHandJoint,
        Vector3[] jointsWorld,
        byte[] vis,
        int idxElbow,
        int idxWrist)
    {
        if (!cache.bones.TryGetValue(handBoneId, out Transform handBone) || handBone == null) return;

        Quaternion smplLocal = state.smoothedSmplLocal[smplHandJoint];
        if (!IsFinite(smplLocal)) return;

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxElbow, out Vector3 posElbow)) return;
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxWrist, out Vector3 posWrist))  return;

        // canonical 親フレーム: forward = elbow→wrist、up = worldUp 投影。キャラクター固有値を一切含まない。
        Vector3 armDir = (posWrist - posElbow);
        if (armDir.sqrMagnitude < 0.0001f) return;
        armDir.Normalize();

        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, armDir);
        if (up.sqrMagnitude < 0.001f)
            up = Vector3.ProjectOnPlane(Vector3.right, armDir);
        up.Normalize();

        Quaternion canonicalParent = Quaternion.LookRotation(armDir, up);
        if (!IsFinite(canonicalParent)) return;

        // handBindCorrection = Inverse(canonicalTpose) * bindRotWorld[hand]
        // smplLocal=identity のとき tw = bindRotWorld[hand]（T-pose に一致）になる。
        // キャラクターごとの手ボーンのローカル軸差をこの補正量が吸収する。
        if (!cache.handBindCorrection.TryGetValue(handBoneId, out Quaternion correction))
            correction = Quaternion.identity;

        Quaternion tw = canonicalParent * smplLocal * correction;
        if (!IsFinite(tw)) return;

        if (state.debugFrameCount == 30 || state.debugFrameCount == 60)
        {
            Debug.Log($"[HAND-DBG] {handBoneId}: canonicalFwd={armDir:F3} smplLocal={smplLocal.eulerAngles:F1} correction={correction.eulerAngles:F1} → handFwd={tw * Vector3.forward:F3}");
        }

        TransformWriter.ApplyWorldRotation(handBone, tw);
    }

    // Exponential smoothing for SMPL-only root world position.
    //
    // Root cause of depth + lateral jitter (pinhole model):
    //   worldPos = camOrigin + anchorZ * ray
    //   X = xNdc * Z / fx,  Y = yNdc * Z / fy,  Z = anchorZ
    // anchorZ (from depth_npz) is noisy → X, Y, Z are all noisy together.
    // UV (anchorU/V from SAM2) is stable → ray direction is stable.
    //
    // Fix: smooth only the scalar depth Z, reconstruct along the same stable ray:
    //   smoothed = camOrigin + (target - camOrigin) * (smoothedZ / rawZ)
    private Vector3 GetSmoothedSmplRootWorld(HumanoidRigCache cache, Vector3 target, Vector3 camOrigin, Vector3 cameraForward)
    {
        const float HalfLifeSecDepth = 0.35f;
        float dt = Time.deltaTime;
        float alphaDepth = 1f - Mathf.Exp(-dt * 0.693147f / HalfLifeSecDepth);

        Vector3 offset = target - camOrigin;
        float rawDepth = cameraForward.sqrMagnitude > 0.0001f
            ? Vector3.Dot(offset, cameraForward.normalized)
            : offset.magnitude;

        if (!humanSmplSmoothedRootInit.Contains(cache))
        {
            humanSmplSmoothedRoot[cache] = target;
            humanSmplSmoothedDepth[cache] = rawDepth;
            humanSmplSmoothedRootInit.Add(cache);
            return target;
        }

        float prevDepth = humanSmplSmoothedDepth.TryGetValue(cache, out float pd) ? pd : rawDepth;
        float smoothedDepth = Mathf.Lerp(prevDepth, rawDepth, alphaDepth);
        humanSmplSmoothedDepth[cache] = smoothedDepth;

        // Scale the ray by smoothedDepth/rawDepth: UV direction unchanged, only depth is smoothed.
        Vector3 smoothed = rawDepth > 0.001f
            ? camOrigin + offset * (smoothedDepth / rawDepth)
            : target;

        humanSmplSmoothedRoot[cache] = smoothed;
        return smoothed;
    }

    // SMPL joint world 位置（jointsWorld）からのベクトル（B - A）を使って腕 bone の向きを整合する。
    // ApplyHumanoidBoneToward（targetPoint - bone.position）より正確:
    //   Unity bone 位置と SMPL joint 位置の不一致（スケール差等）の影響を受けない。
    // 両腕（左右 × 上腕・前腕）に適用する。IK ではなく per-bone の回転のみ。
    private void TryApplySmplArmsFromJointPositions(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis)
    {
        if (cache == null || jointsWorld == null || vis == null || jointsWorld.Length < 22) return;

        // Left upper arm: joint 16 (LeftUpperArm start) → joint 18 (LeftElbow = LeftLowerArm start)
        TryApplySmplArmSegment(cache, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
            jointsWorld, vis, SmplLeftShoulder, SmplLeftElbow);

        // Left lower arm: joint 18 (LeftElbow) → joint 20 (LeftWrist = LeftHand start)
        TryApplySmplArmSegment(cache, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            jointsWorld, vis, SmplLeftElbow, SmplLeftWrist);

        // Right upper arm: joint 17 (RightUpperArm start) → joint 19 (RightElbow = RightLowerArm start)
        TryApplySmplArmSegment(cache, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
            jointsWorld, vis, SmplRightShoulder, SmplRightElbow);

        // Right lower arm: joint 19 (RightElbow) → joint 21 (RightWrist = RightHand start)
        TryApplySmplArmSegment(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            jointsWorld, vis, SmplRightElbow, SmplRightWrist);
    }

    // SMPL ankle joint Y を基準にキャラクターの足首ボーン Y を揃える。
    // 骨盤を SMPL pelvis anchor に配置した後、キャラモデルの脚の長さが SMPL と異なる場合に
    // 足が浮いたり沈んだりする問題を補正する。XZ 位置は変更しない（骨盤基準を維持）。
    private void AlignHumanoidFeetYToSmplAnkles(Transform root, HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis)
    {
        if (root == null || cache == null || jointsWorld == null || vis == null) return;

        float smplMinY = float.MaxValue;
        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftAnkle,  out Vector3 leftAnkle))
            smplMinY = Mathf.Min(smplMinY, leftAnkle.y);
        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightAnkle, out Vector3 rightAnkle))
            smplMinY = Mathf.Min(smplMinY, rightAnkle.y);
        if (smplMinY == float.MaxValue) return;

        float charMinY = float.MaxValue;
        if (cache.bones.TryGetValue(HumanBodyBones.LeftFoot,  out Transform leftFoot)  && leftFoot  != null)
            charMinY = Mathf.Min(charMinY, leftFoot.position.y);
        if (cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot) && rightFoot != null)
            charMinY = Mathf.Min(charMinY, rightFoot.position.y);
        if (charMinY == float.MaxValue) return;

        float yOffset = smplMinY - charMinY;
        if (Mathf.Abs(yOffset) < 0.001f) return;

        root.position += new Vector3(0f, yOffset, 0f);
    }

    // SMPL joint world 位置（jointsWorld）からのベクトル（B - A）を使って脚 bone の向きを整合する。
    // 腕と同じ AimAt アプローチ。FK の body_pose 座標フレームと Unity bone フレームの誤差を補正する。
    private void TryApplySmplLegsFromJointPositions(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis)
    {
        if (cache == null || jointsWorld == null || vis == null || jointsWorld.Length < 12) return;

        // Left thigh: joint 1 (LeftHip) → joint 4 (LeftKnee)
        TryApplySmplArmSegment(cache, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
            jointsWorld, vis, SmplLeftHip, SmplLeftKnee);

        // Left shin: joint 4 (LeftKnee) → joint 7 (LeftAnkle)
        TryApplySmplArmSegment(cache, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            jointsWorld, vis, SmplLeftKnee, SmplLeftAnkle);

        // Right thigh: joint 2 (RightHip) → joint 5 (RightKnee)
        TryApplySmplArmSegment(cache, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
            jointsWorld, vis, SmplRightHip, SmplRightKnee);

        // Right shin: joint 5 (RightKnee) → joint 8 (RightAnkle)
        TryApplySmplArmSegment(cache, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            jointsWorld, vis, SmplRightKnee, SmplRightAnkle);

        // Left foot direction: joint 7 (LeftAnkle) → joint 10 (LeftToes)
        TryApplySmplArmSegment(cache, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            jointsWorld, vis, SmplLeftAnkle, SmplLeftFoot);

        // Right foot direction: joint 8 (RightAnkle) → joint 11 (RightToes)
        TryApplySmplArmSegment(cache, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
            jointsWorld, vis, SmplRightAnkle, SmplRightFoot);
    }

    private void TryApplySmplArmSegment(HumanoidRigCache cache,
        HumanBodyBones proxBone, HumanBodyBones distBone,
        Vector3[] jointsWorld, byte[] vis, int idxA, int idxB)
    {
        if (!cache.bones.TryGetValue(proxBone, out Transform proxT) || proxT == null) return;
        if (!cache.bones.TryGetValue(distBone, out Transform distT) || distT == null) return;
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxA, out Vector3 posA)) return;
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxB, out Vector3 posB)) return;

        Vector3 targetDir = (posB - posA).normalized;
        if (targetDir.sqrMagnitude < 0.0001f) return;

        Vector3 currentDir = (distT.position - proxT.position).normalized;
        if (currentDir.sqrMagnitude < 0.0001f) return;

        float dot = Vector3.Dot(currentDir, targetDir);
        if (dot > 0.9999f) return;  // 既に整合済み

        Quaternion delta = dot < -0.9999f
            ? Quaternion.AngleAxis(180f, Vector3.Cross(currentDir, Vector3.up).sqrMagnitude > 0.001f
                ? Vector3.Cross(currentDir, Vector3.up).normalized
                : Vector3.Cross(currentDir, Vector3.right).normalized)
            : Quaternion.FromToRotation(currentDir, targetDir);
        TransformWriter.ApplyWorldRotation(proxT, delta * proxT.rotation);
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

    public static Quaternion ResolveHumanSmplFkRootWorldRotation(
        Quaternion cameraToWorld,
        Quaternion cameraSpaceGlobalOrient)
    {
        if (!IsFinite(cameraToWorld) || !IsFinite(cameraSpaceGlobalOrient))
        {
            return Quaternion.identity;
        }

        return cameraToWorld * cameraSpaceGlobalOrient;
    }

    public static Quaternion ResolveHumanSmplTargetWorldRotation(
        Quaternion worldGlobalOrient,
        Quaternion bodyFk,
        Quaternion bindWorld)
    {
        if (!IsFinite(worldGlobalOrient) || !IsFinite(bodyFk) || !IsFinite(bindWorld))
        {
            return Quaternion.identity;
        }

        return worldGlobalOrient * bodyFk * bindWorld;
    }

    public static bool ShouldApplyHumanSmplFullHandRotation()
    {
        return true;
    }

    public static bool ShouldApplyHumanSmplTerminalHandRotationInSmplOnlyPose()
    {
        return false;
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

    public static bool ShouldUseHumanSmplRootAnchorForSmplOnlyPose(bool isPerson, bool hasHumanSmplTranslation)
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

    // Returns the body-relative FK of the nearest SMPL ancestor that has a real Unity bone.
    // When a SMPL parent joint (e.g. UpperChest) is absent from the rig, bindRotLocal[j] is
    // in the actual Unity parent's frame (the next present ancestor), so the correctedLocal
    // formula must use that ancestor's FK rather than the missing joint's FK.
    private static Quaternion FindEffectiveParentFk(HumanoidRigCache cache, int parentJoint, Quaternion[] fk)
    {
        int p = parentJoint;
        while (p >= 0)
        {
            bool hasBone;
            if (p == 0)
            {
                hasBone = cache.bones.ContainsKey(HumanBodyBones.Hips);
            }
            else
            {
                hasBone = SmplJointToHumanBone.TryGetValue(p, out HumanBodyBones pBoneId) &&
                          cache.bones.ContainsKey(pBoneId);
            }
            if (hasBone)
                return fk[p];
            p = SmplJointParentArray[p];
        }
        return Quaternion.identity;
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

    private static bool IsTerminalHumanSmplHandBone(HumanBodyBones boneId)
    {
        return boneId == HumanBodyBones.LeftHand || boneId == HumanBodyBones.RightHand;
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
