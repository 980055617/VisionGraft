using System.Collections.Generic;
using UnityEngine;

public sealed partial class AnimalPoseApplier
{
    // SMAL parent joint index for joints 0-34.
    // Root (0) = -1. Virtual spine chain: 1-6 (no Unity bone). Front legs: 7-10, 11-14 (parent=6).
    // Neck/Head: 15-16 (parent=6). Rear legs: 17-20 (parent=0), 21-24 (parent=0).
    // Tail: 25-31 (parent=0 for 25, chain after). Mouth/Ears: 32-34 (parent=16).
    private static readonly int[] SmalJointParentArray =
    {
        -1,              // 0 root
        0, 1, 2, 3, 4, 5, // 1-6  pelvis0..spine3 (BONE MISSING)
        6, 7, 8, 9,      // 7-10  LLeg1..LFoot
        6, 11, 12, 13,   // 11-14 RLeg1..RFoot
        6, 15,           // 15-16 Neck, Head
        0, 17, 18, 19,   // 17-20 LLegBack1..LFootBack
        0, 21, 22, 23,   // 21-24 RLegBack1..RFootBack
        0, 25, 26, 27, 28, 29, 30, // 25-31 Tail1..7
        16, 16, 16,      // 32-34 Mouth, LEar, REar
    };

    // Topological order: parents always before children. Since all parent indices < child indices, order is 1..34.
    private static readonly int[] SmalJointTopologicalOrder =
    {
        1,  2,  3,  4,  5,  6,          // virtual spine chain
        7,  8,  9,  10,                  // front left
        11, 12, 13, 14,                  // front right
        15, 16,                          // neck, head
        17, 18, 19, 20,                  // rear left
        21, 22, 23, 24,                  // rear right
        25, 26, 27, 28, 29, 30, 31,      // tail
        32, 33, 34,                      // mouth, ears (BONE MISSING in first impl)
    };

    // Rest-pose bone direction (this joint -> its kinematic child), in SMAL's own native
    // coordinate frame, extracted from the SMAL rest skeleton J array
    // (Docs/smal-rest-skeleton.json, third_party/AniMer/data/smal/my_smpl_00781_4_all.pkl).
    // Used to compute a per-joint geometric correction directly from real rest geometry
    // (SMAL rest direction vs. the Unity dog rig's own actual bind direction for the same
    // joint), instead of reusing the single global canonicalCorrection - whose roll/twist
    // component was only ever constrained by a single nose-direction check and turned out
    // to be unreliable when reused for per-joint local rotations (2026-06-18, see
    // Docs/adr/0001-animal-smal-fk.md). Joints without an entry here keep using the
    // previous parentTW*localPose*bindLoc path - this covers the joints that also have a
    // registered Unity aim-child (RegisterAnimalAimPairs), which is what we need for an
    // independent, geometry-grounded Unity-side rest direction to compare against.
    private static readonly Dictionary<int, Vector3> SmalRestDirByJoint = new Dictionary<int, Vector3>
    {
        { 7,  new Vector3(0.044701f, -0.095438f, -0.994431f) },  // LLeg1 -> LLeg2
        { 8,  new Vector3(0.006267f, -0.179885f, -0.983668f) },  // LLeg2 -> LLeg3
        { 11, new Vector3(0.044701f, 0.095438f, -0.994431f) },   // RLeg1 -> RLeg2
        { 12, new Vector3(0.006267f, 0.179885f, -0.983668f) },   // RLeg2 -> RLeg3
        { 15, new Vector3(0.993402f, -0.000000f, -0.114686f) },  // Neck -> Head
        { 17, new Vector3(0.337957f, 0.086988f, -0.937133f) },   // LLegBack1 -> LLegBack2
        { 18, new Vector3(-0.194889f, -0.083158f, -0.977294f) }, // LLegBack2 -> LLegBack3
        { 21, new Vector3(0.337957f, -0.086988f, -0.937133f) },  // RLegBack1 -> RLegBack2
        { 22, new Vector3(-0.194889f, 0.083158f, -0.977294f) },  // RLegBack2 -> RLegBack3
        // Tail1 -> Tail2, Tail2 -> Tail3 (Docs/smal-rest-skeleton.json, joints 25/26).
        // Tail3 (joint 27, tailTip) intentionally has no entry here - its own further SMAL
        // child (Tail4, joint 28) has no corresponding Unity bone in the canonical rig, so it
        // stays in the same passive parentTW*bindLoc fallback paws/toes already use.
        { 25, new Vector3(-0.992647f, 0.000000f, -0.121171f) },
        { 26, new Vector3(-0.999506f, 0.000000f, 0.031008f) },
    };

    // Provisional damping for tailBase/tailMid body_pose (see comment at the joint==25/26
    // check below) - starting conservative at half strength until visually verified.
    private const float TailBodyPoseScale = 0.5f;

    // SMAL global_orient/pose rotation matrices are read from the bin in SMAL's own native
    // axis convention. This fixed correction re-expresses that raw decode into a usable Unity
    // world rotation - validated against DogRoot by comparing the model's nose direction
    // against the source video (see Docs/smpl-retargeting.md). Treated as a property of the
    // SMAL data/decoding convention itself, not of any specific animal rig (every per-model
    // adjustment needed instead lives in bindRotWorld[spine], which is captured directly per
    // model with no math involved) - see ADR-0002 for the (failed) attempts at deriving a
    // per-model replacement for this constant, and why we went back to it.
    private static readonly Quaternion SmalDataAxisCorrection = Quaternion.Euler(0f, 90f, 90f);

    private sealed class AnimalSmalRetargetState
    {
        public readonly Quaternion[] tw = new Quaternion[35];
        public readonly Quaternion[] smoothedLocal = new Quaternion[35]; // [0]=worldFk0, [1-34]=bodyPose smoothed
        public bool smoothingInitialized;
        public int debugFrameCount;
        // Per-model root yaw fix, decided once from real keypoint data (not bind-time
        // geometry guessing - see ADR-0002) and cached for the rest of the session so it
        // doesn't flicker frame to frame on noisy keypoints.
        public bool rootYawFixDecided;
        public Quaternion rootYawFix = Quaternion.identity;
        // Snapshot of tw[] at the previous *logged* sample (~every 30 ticks), plus real
        // elapsed time, so we can measure true rotation speed over a visually-relevant
        // window instead of a single-tick delta (which is too small to judge by) or a
        // misleading Euler-angle diff (which can look huge near gimbal/wrap singularities).
        public readonly Quaternion[] lastLoggedTw = new Quaternion[35];
        public readonly bool[] hasLoggedTw = new bool[35];
        public float lastLogRealTime;
        public bool hasLastLogRealTime;
        // Accumulated per-tick bend-direction angle change since the last logged sample,
        // for the bone-to-child "BEND" diagnostic (7=leftFrontUpper, 8=leftFrontLower,
        // 17=leftRearUpper, 21=rightRearUpper, 15=neck).
        public float bendAccumJoint7;
        public float bendAccumJoint8;
        public float bendAccumJoint17;
        public float bendAccumJoint21;
        public float bendAccumJoint15;
    }

    private readonly Dictionary<AnimalRigCache, AnimalSmalRetargetState> smalRetargetStates
        = new Dictionary<AnimalRigCache, AnimalSmalRetargetState>();

    // shot 境界で呼ぶ。state 自体は破棄しない: rootYawFix は「実データから一度だけ決めて
    // セッション中キャッシュする」もので shot とは無関係なため（消すとカットごとに
    // 向き判定がやり直しになりちらつく）。時間方向の平滑化だけ未初期化に戻して、
    // 新しい shot の先頭フレームでは前 shot の姿勢と混ざらない生値を採用させる。
    private void ResetSmalSmoothing()
    {
        foreach (KeyValuePair<AnimalRigCache, AnimalSmalRetargetState> kv in smalRetargetStates)
        {
            if (kv.Value != null)
            {
                kv.Value.smoothingInitialized = false;
            }
        }
    }

    private AnimalSmalRetargetState GetOrCreateSmalRetargetState(AnimalRigCache cache)
    {
        if (smalRetargetStates.TryGetValue(cache, out AnimalSmalRetargetState existing))
            return existing;
        var state = new AnimalSmalRetargetState();
        smalRetargetStates[cache] = state;
        return state;
    }

    private static Quaternion ExtractYawOnly(Quaternion rotation)
    {
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            return Quaternion.identity;
        }
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    private void TryApplyAnimalSmalFk(AnimalRigCache cache, AnimalSmalPose pose, AnimalPoseSettings settings, Vector3[] jointsWorld, byte[] jointVis, Transform instanceRoot)
    {
        if (cache == null || !cache.ready || !pose.hasGlobalOrient || pose.bodyPose == null)
            return;

        AnimalSmalRetargetState state = GetOrCreateSmalRetargetState(cache);

        // Set to 0 to disable smoothing and pass raw SMAL values directly.
        // [SMAL-FK-DBG] trueDeltaDegSincePrevSample logging (2026-06-16) showed the raw
        // per-frame body_pose is largely incoherent noise: 20-100+ deg swings every ~0.2s,
        // with bodyPose_maxAngle landing on a different random joint almost every sample
        // (no multi-sample run on the same limb, as a real gait would show). That noise,
        // applied raw, looks like high-frequency jitter rather than motion - visually
        // reads as "frozen/stuck" even though the Transforms are moving a lot. Smooth it.
        const float SmalSmoothHalfLifeSec = 0.12f;
        float dt = Time.deltaTime;
        float smoothAlpha = SmalSmoothHalfLifeSec > 0f
            ? 1f - Mathf.Exp(-dt * 0.693147f / SmalSmoothHalfLifeSec)
            : 1f;

        Quaternion[] tw = state.tw;

        // Joint 0 (root) orientation. 2026-06-18: reverted to the simple, DogRoot-validated
        // form after two failed attempts at a "smarter" per-model derivation (FromToRotation
        // bend - lost roll; full-basis conjugation via cache.root.TransformDirection - picked
        // up drift from unrelated placement code) each introduced new bugs without first
        // confirming the simple form was actually the problem on P_GermanShepherd. See
        // ADR-0002 for the full history - going back to first principles before guessing again.
        if (!IsFiniteQ(pose.camRotation) || !IsFiniteQ(pose.globalOrient) ||
            !cache.bindRotWorld.TryGetValue(cache.spine, out Quaternion spineBindWorldForRoot) ||
            !IsFiniteQ(spineBindWorldForRoot))
        {
            return;
        }

        // Per-model orientation correction (2026-07-09): all models must present the same
        // forward/up convention to the SMAL FK formula. DogRoot's T-pose has spine=identity
        // and the dog faces -Z in local space, so LookRotation(-Z,+Y) is the reference basis.
        // New models (Wolf, Bear, Boar…) have modelForwardLocal ≈ +X because their FBX was
        // imported with a different axis convention.
        //
        // Proof:  tw[0] = rawWorldFk0 * S   visual_forward = tw[0] * Inv(S) * modelFwdLocal
        //       = rawWorldFk0 * modelFwdLocal
        //       = (…*modelOrientFix) * modelFwdLocal
        //       = (…*refModelBasis*Inv(thisModelBasis)) * thisModelBasis * (+Z)
        //       = (…*refModelBasis) * (+Z)  = (…) * LookRotation(-Z,+Y) * (+Z) = (…)*(-Z) ✓
        Quaternion refModelBasis = Quaternion.LookRotation(Vector3.back, Vector3.up);
        // Project modelForwardLocal onto the XZ plane before building the basis.
        // globalOrient carries all pitch/tilt; the T-pose "forward" only needs the
        // horizontal facing direction. If modelForwardLocal has a Y component (front legs
        // higher than rear legs in T-pose) and we pass it raw into LookRotation, the
        // correction bakes that tilt into modelOrientFix and the model ends up pitched down.
        Vector3 thisModelFwdRaw = cache.modelForwardLocal.sqrMagnitude > 0.001f ? cache.modelForwardLocal.normalized : Vector3.back;
        Vector3 thisModelFwdFlat = new Vector3(thisModelFwdRaw.x, 0f, thisModelFwdRaw.z);
        Vector3 thisModelFwd = thisModelFwdFlat.sqrMagnitude > 0.001f ? thisModelFwdFlat.normalized : thisModelFwdRaw;
        Vector3 thisModelUp  = cache.modelUpLocal.sqrMagnitude > 0.001f  ? cache.modelUpLocal.normalized  : Vector3.up;
        Quaternion thisModelBasis  = Quaternion.LookRotation(thisModelFwd, thisModelUp);
        Quaternion modelOrientFix  = refModelBasis * Quaternion.Inverse(thisModelBasis);

        if (!state.rootYawFixDecided && jointsWorld != null && jointVis != null &&
            cache.spineToNeckBindDirWorld.sqrMagnitude > 0.000001f)
        {
            Vector3 preferredUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
            if (AnimalBodyBasisResolver.TryResolveFromJoints(jointsWorld, jointVis, preferredUp, out Vector3 kpForward, out _, out _) &&
                kpForward.sqrMagnitude > 0.000001f)
            {
                // candidate * modelOrientFix * spineToNeckBindDirWorld_world correctly predicts
                // the neck world direction because:
                //   tw[0] = candidate * modelOrientFix * S
                //   neck_world = tw[0] * Inv(S) * spineToNeck_world
                //              = candidate * modelOrientFix * spineToNeck_world
                Quaternion candidate0 = pose.camRotation * pose.globalOrient * SmalDataAxisCorrection * modelOrientFix;
                Quaternion candidate180 = candidate0 * Quaternion.Euler(0f, 180f, 0f);
                float dot0 = Vector3.Dot(candidate0 * cache.spineToNeckBindDirWorld, kpForward);
                float dot180 = Vector3.Dot(candidate180 * cache.spineToNeckBindDirWorld, kpForward);
                const float kRootYawFlipMinMargin = 0.3f;
                state.rootYawFix = (dot180 - dot0 > kRootYawFlipMinMargin) ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                state.rootYawFixDecided = true;
                Debug.Log($"[SMAL-FK-DBG] MODEL rootYawFix decided: dot0={dot0:F3} dot180={dot180:F3} chose180={state.rootYawFix != Quaternion.identity} kpForward={kpForward:F3} spineToNeckBindDirWorld={cache.spineToNeckBindDirWorld:F3} modelFwd={thisModelFwd:F3} modelOrientFix={modelOrientFix.eulerAngles:F1}");
            }
        }

        // Prepend the track root's own yaw so that interactive-motion gestures (which
        // explicitly set instanceRoot.rotation) compose naturally into the SMAL FK.
        // During normal playback AlignAnimalRootToSkeleton only touches position, so
        // instanceRootYaw = identity and this term has no effect.
        Quaternion instanceRootYaw = instanceRoot != null ? ExtractYawOnly(instanceRoot.rotation) : Quaternion.identity;
        // modelOrientFix aligns this model's T-pose axis convention to DogRoot (-Z fwd,
        // +Y up) so that tw[0] = worldFk0 * spineBindW produces the same visual direction
        // for every model. For DogRoot modelOrientFix ≡ identity (no change).
        Quaternion rawWorldFk0 = instanceRootYaw * pose.camRotation * pose.globalOrient * SmalDataAxisCorrection * state.rootYawFix * modelOrientFix;

        Quaternion worldFk0 = state.smoothingInitialized
            ? Quaternion.Slerp(state.smoothedLocal[0], rawWorldFk0, smoothAlpha)
            : rawWorldFk0;
        state.smoothedLocal[0] = worldFk0;

        state.debugFrameCount++;
        // Log first frame + every 30 frames to sample orientation across the full video.
        bool debugLog = !state.smoothingInitialized || state.debugFrameCount % 30 == 0;

        if (debugLog)
        {
            float realDt = state.hasLastLogRealTime ? Time.time - state.lastLogRealTime : 0f;
            Debug.Log($"[SMAL-FK-DBG] sampleRealTimeSec={Time.time:F2} dtSincePrevSample={realDt:F3} unityDeltaTime={dt:F4}");
            state.lastLogRealTime = Time.time;
            state.hasLastLogRealTime = true;

            // Per-model ground truth, logged once per sample so we can compare models
            // directly instead of re-deriving formulas from theory. bindSpineW != ~identity
            // means this model's spine bone is NOT the literal hierarchy root (DogRoot's
            // "ボーン" bone is; P_GermanShepherd's DEF-spine.004 sits several bones deep in
            // its own spine chain) - that changes what "correct" looks like to compare against.
            Debug.Log($"[SMAL-FK-DBG] MODEL spineName={cache.spine.name} bindSpineW.euler={spineBindWorldForRoot.eulerAngles:F1} modelForwardLocal={cache.modelForwardLocal:F3} modelUpLocal={cache.modelUpLocal:F3} rootYawFixApplied={state.rootYawFix != Quaternion.identity}");
        }

        // worldFk0, applied via LEFT-multiplication onto ANY bone's own bindRotWorld (not just
        // spine's), gives "this bone, rotated rigidly as part of the whole body" (2026-06-18,
        // see ADR-0002). Using this instead of parentTW*bindLoc for bones whose SMAL-logical
        // parent (e.g. virtual joint 6) isn't their real Unity parent sidesteps that mismatch
        // entirely - bindRotWorld[bone] already correctly encodes the bone's full real
        // ancestor chain via Unity's own bone.rotation, no matter how many untracked
        // intermediate bones (e.g. a long DEF-spine.005..009 chain on a model whose spine
        // isn't a single bone like DogRoot's) sit in between.

        // tw[0] = worldFk0 * bindRotWorld[spine]; apply to spine bone
        {
            tw[0] = worldFk0 * spineBindWorldForRoot;
            if (IsFiniteQ(tw[0]))
            {
                TransformWriter.ApplyWorldRotation(cache.spine, tw[0]);

                // Live visual calibration aid: compare these rays against the source video every frame.
                // Yellow = nose direction, cyan = up direction. Visible in Scene view (and Game view with Gizmos on).
                Vector3 spinePos = cache.spine.position;
                Debug.DrawRay(spinePos, -cache.spine.forward * 0.5f, Color.yellow, 0f, false);
                Debug.DrawRay(spinePos, cache.spine.up * 0.3f, Color.cyan, 0f, false);

                if (debugLog) Debug.Log($"[SMAL-FK-DBG] frame={state.debugFrameCount} camRot={pose.camRotation.eulerAngles:F1} rawGO={pose.globalOrient.eulerAngles:F1} worldFk0={worldFk0.eulerAngles:F1} tw0={tw[0].eulerAngles:F1}");
                if (debugLog) Debug.Log($"[SMAL-FK-DBG] spine.fwd={cache.spine.forward:F3} spine.up={cache.spine.up:F3} nose(=-spine.fwd)={(-cache.spine.forward):F3}");
            }
        }

        // Walk joints 1-34 in topological order
        for (int i = 0; i < SmalJointTopologicalOrder.Length; i++)
        {
            int joint = SmalJointTopologicalOrder[i];
            int parentJoint = SmalJointParentArray[joint];
            Quaternion parentTW = tw[parentJoint];

            Quaternion rawLocal = Quaternion.identity;
            int bodyPoseIdx = joint - 1; // bodyPose[0] = SMAL joint 1, ..., bodyPose[33] = SMAL joint 34
            if (bodyPoseIdx >= 0 && bodyPoseIdx < pose.bodyPose.Length && IsFiniteQ(pose.bodyPose[bodyPoseIdx]))
                rawLocal = pose.bodyPose[bodyPoseIdx];

            if (joint == 25 || joint == 26)
            {
                // Tail body_pose is being driven for the first time this session (2026-07-16)
                // and hasn't been validated the way legs/neck were - reported as clipping
                // into the body on some models. Damp it the same way Human SMPL's over-large
                // spine estimate needed SpineBodyPoseScale=0.25 (Docs/smpl-retargeting.md).
                // Tune/remove once tail motion has been visually verified per-model.
                rawLocal = Quaternion.Slerp(Quaternion.identity, rawLocal, TailBodyPoseScale);
            }

            Quaternion smalLocal = state.smoothingInitialized
                ? Quaternion.Slerp(state.smoothedLocal[joint], rawLocal, smoothAlpha)
                : rawLocal;
            state.smoothedLocal[joint] = smalLocal;

            Transform bone = GetSmalBoneForJoint(cache, joint);

            if (bone == null)
            {
                // BONE MISSING (virtual spine chain joints 1-6): no real bone, so there's no
                // bind-pose-specific local frame to re-express into - just accumulate in world
                // frame directly (reverted 2026-06-18 along with joint 0, see ADR-0002).
                tw[joint] = parentTW * smalLocal;
                continue;
            }

            if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindLoc) || !IsFiniteQ(bindLoc))
                bindLoc = Quaternion.identity;

            if (AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(joint))
            {
                // ApplyWorldRotation, not ApplyLocalRotation (CLAUDE.md: FK loop uses
                // ApplyWorldRotation only) - parentTW already is this bone's real Unity
                // parent's current world rotation (applied earlier in this same topological
                // walk), so parentTW * bindLoc is the world-space equivalent of setting
                // bone.localRotation = bindLoc under that parent.
                tw[joint] = parentTW * bindLoc;
                TransformWriter.ApplyWorldRotation(bone, tw[joint]);
                continue;
            }

            if (SmalRestDirByJoint.TryGetValue(joint, out Vector3 smalRestDir) &&
                cache.bindRotWorld.TryGetValue(bone, out Quaternion boneBindWorld) &&
                cache.bindDirLocal.TryGetValue(bone, out Vector3 boneBindDirLocal))
            {
                // Geometry-grounded per-joint correction (2026-06-18): instead of reusing the
                // single global canonicalCorrection (whose roll/twist was only ever constrained
                // by a one-off nose-direction check, and produced near-invisible "twist instead
                // of bend" results when reused per-joint - see ADR), derive the correction
                // directly from comparing this joint's REST bone direction in SMAL's own native
                // frame (smalRestDir, from the SMAL rest skeleton J array) against this exact
                // Unity bone's REAL rest bone direction.
                // Quaternion.FromToRotation between the two gives an unambiguous, per-joint
                // SMAL-frame -> Unity-frame map with no global-axis guessing involved.
                //
                // unityRestDirWorld uses restWorldRot (= worldFk0 * boneBindWorld) instead of
                // the static bind-time direction (boneBindWorld * boneBindDirLocal). The static
                // version is wrong for models whose T-pose faces a different world direction than
                // DogRoot (e.g. Wolf/Bear facing +X vs Dog facing -Z): the same SMAL neck-down
                // bend would map to a sideways roll for those models instead of a forward-down tilt.
                // worldFk0 already incorporates modelOrientFix, so restWorldRot * boneBindDirLocal
                // gives the rest-pose bone direction in a model-neutral frame. (2026-07-09)
                Vector3 smalPosedDir = (rawLocal * smalRestDir).normalized;
                Quaternion bendSmal = Quaternion.FromToRotation(smalRestDir, smalPosedDir);

                // worldFk0 * boneBindWorld (not parentTW * bindLoc): see the comment above the
                // joint-0 block. parentTW for this joint may be a virtual SMAL joint (e.g. 6)
                // that doesn't correspond to this bone's real Unity parent on models with a
                // multi-bone spine chain, which would silently compose bindLoc relative to the
                // wrong frame.
                Quaternion restWorldRot = worldFk0 * boneBindWorld;
                Vector3 unityRestDirWorld = (restWorldRot * boneBindDirLocal).normalized;
                Quaternion jointFrameMap = Quaternion.FromToRotation(smalRestDir, unityRestDirWorld);
                Quaternion bendUnity = jointFrameMap * bendSmal * Quaternion.Inverse(jointFrameMap);

                // 2026-07-17 diagnostic: FromToRotation's axis becomes ill-defined as the two
                // vectors approach anti-parallel (~180deg), which would make jointFrameMap (and
                // therefore bendUnity) unstable for models whose default/bind tail direction
                // points nearly opposite SMAL's canonical tail rest direction. Logging this
                // angle per model to check whether that's actually happening for any of the
                // 52 animal prefabs before considering a bind-pose realignment pass.
                if (debugLog && (joint == 25 || joint == 26))
                {
                    float restDirAngleDeg = Vector3.Angle(smalRestDir, unityRestDirWorld);
                    Debug.Log($"[SMAL-FK-DBG] TAIL-REST-CHECK model={cache.root?.name} joint={joint} smalRestDir={smalRestDir:F3} unityRestDirWorld={unityRestDirWorld:F3} restDirAngleDeg={restDirAngleDeg:F1} (150+=FromToRotation軸不定の疑いあり)");
                }

                tw[joint] = bendUnity * restWorldRot;
            }
            else
            {
                // No validated geometric correction exists yet for this joint (paws, head,
                // tail - none have a registered aim-child to derive a real Unity rest
                // direction from, see ADR-0001/0002). Rather than guess with an unvalidated
                // correction, just carry the rest pose through (no body_pose contribution) -
                // these bones still follow their parent's sway via parentTW * bindLoc.
                tw[joint] = parentTW * bindLoc;
            }

            if (!IsFiniteQ(tw[joint]))
                continue;

            Vector3 posBeforeApply = bone.position;

            // Quaternion.Angle includes twist-around-own-axis, which is invisible on a
            // limb segment. The thing that's actually visible is whether the direction
            // from this bone to its child swings - i.e. a genuine bend. Measure that
            // directly (child.position updates automatically once we rotate bone, since
            // it's a real Unity child) to settle whether the axis-correction conjugation
            // actually produced a visible bend or just changed which axis the twist is on.
            Transform bendChild = joint == 7 ? cache.leftFrontLower
                : joint == 8 ? cache.leftFrontPaw
                : joint == 17 ? cache.leftRearLower
                : joint == 21 ? cache.rightRearLower
                : joint == 15 ? cache.head
                : null;
            Vector3 bendDirBefore = bendChild != null ? (bendChild.position - bone.position).normalized : Vector3.zero;

            TransformWriter.ApplyWorldRotation(bone, tw[joint]);

            if (bendChild != null)
            {
                Vector3 bendDirAfter = (bendChild.position - bone.position).normalized;
                float bendDeg = Vector3.Angle(bendDirBefore, bendDirAfter);
                if (joint == 7) state.bendAccumJoint7 += bendDeg;
                else if (joint == 8) state.bendAccumJoint8 += bendDeg;
                else if (joint == 17) state.bendAccumJoint17 += bendDeg;
                else if (joint == 21) state.bendAccumJoint21 += bendDeg;
                else if (joint == 15) state.bendAccumJoint15 += bendDeg;

                if (debugLog)
                {
                    float accum = joint == 7 ? state.bendAccumJoint7
                        : joint == 8 ? state.bendAccumJoint8
                        : joint == 17 ? state.bendAccumJoint17
                        : joint == 21 ? state.bendAccumJoint21
                        : state.bendAccumJoint15;
                    Debug.Log($"[SMAL-FK-DBG] joint={joint} BEND childDirAngleAccumSincePrevSample={accum:F2}deg (sum of per-tick bone->child direction swings - this is the visually-relevant bend, vs the twist-inclusive Quaternion.Angle logged separately)");
                    if (joint == 7) state.bendAccumJoint7 = 0f;
                    else if (joint == 8) state.bendAccumJoint8 = 0f;
                    else if (joint == 17) state.bendAccumJoint17 = 0f;
                    else if (joint == 21) state.bendAccumJoint21 = 0f;
                    else state.bendAccumJoint15 = 0f;
                }
            }

            if (debugLog && (joint == 7 || joint == 8 || joint == 9 || joint == 10 ||
                              joint == 11 || joint == 12 || joint == 13 || joint == 14 ||
                              joint == 15 || joint == 16 || joint == 17 || joint == 21 ||
                              joint == 25 || joint == 26 || joint == 27))
            {
                // Compare against the snapshot taken at the PREVIOUS logged sample (~30
                // ticks ago), not the previous single tick, so the angle reflects motion
                // over a visually-relevant window. Quaternion.Angle is geodesic, so it
                // doesn't suffer from the Euler 0/360-wrap illusion of "huge" deltas above.
                float trueDeltaDegSincePrevSample = state.hasLoggedTw[joint]
                    ? Quaternion.Angle(state.lastLoggedTw[joint], tw[joint])
                    : 0f;
                state.lastLoggedTw[joint] = tw[joint];
                state.hasLoggedTw[joint] = true;

                Debug.Log($"[SMAL-FK-DBG] model={cache.root?.name} joint={joint} smalLocal={smalLocal.eulerAngles:F1} bindLoc={bindLoc.eulerAngles:F1} tw={tw[joint].eulerAngles:F1} boneRotAfter={bone.rotation.eulerAngles:F1} posDelta={(bone.position - posBeforeApply).magnitude:F4} trueDeltaDegSincePrevSample={trueDeltaDegSincePrevSample:F1}");
            }

            // Live visual calibration aid: lets us see in Scene view whether each limb/head
            // bone is actually rotating frame-to-frame, independent of whether the rendered
            // mesh appears to move (rules out "FK computes motion but wrong bone is driven").
            if (joint == 7 || joint == 11 || joint == 17 || joint == 21)
                Debug.DrawRay(bone.position, bone.forward * 0.2f, Color.green, 0f, false);
            else if (joint == 16)
                Debug.DrawRay(bone.position, bone.forward * 0.2f, Color.blue, 0f, false);
        }

        state.smoothingInitialized = true;

        if (debugLog)
        {
            // body_pose 全 joint の最大角度を計測 → genuinely near-zero か読み取りバグかを判断
            float maxBPAngle = 0f;
            int maxBPJoint = -1;
            for (int dbgI = 0; dbgI < pose.bodyPose.Length; dbgI++)
            {
                float a = Quaternion.Angle(Quaternion.identity, pose.bodyPose[dbgI]);
                if (a > maxBPAngle) { maxBPAngle = a; maxBPJoint = dbgI + 1; }
            }
            Debug.Log($"[SMAL-FK-DBG] bodyPose_maxAngle={maxBPAngle:F1}deg at SMAL_joint={maxBPJoint}  (near-zero=data問題, >20=データ正常)");
            // body mesh の実際の world 向き: nose = -spine.fwd を確認
            if (cache.spine != null)
            {
                // 子の中から "body" meshを探して向きをチェック
                Transform bodyMesh = null;
                for (int ci = 0; ci < cache.spine.childCount; ci++)
                {
                    Transform c = cache.spine.GetChild(ci);
                    if (c.name == "body") { bodyMesh = c; break; }
                }
                if (bodyMesh != null)
                    Debug.Log($"[SMAL-FK-DBG] bodyMesh.fwd={bodyMesh.forward:F3} bodyMesh.up={bodyMesh.up:F3}  [nose≈bodyMesh.up toward viewer=-Z]");
            }
            // 前脚・頭の実際の向きを確認 → 向きのズレを特定
            if (cache.leftFrontUpper != null)
                Debug.Log($"[SMAL-FK-DBG] leftFront.fwd={cache.leftFrontUpper.forward:F3} leftFront.up={cache.leftFrontUpper.up:F3}");
            if (cache.leftFrontLower != null)
                Debug.Log($"[SMAL-FK-DBG] leftFrontLower.fwd={cache.leftFrontLower.forward:F3} leftFrontLower.up={cache.leftFrontLower.up:F3}");
            if (cache.head != null)
                Debug.Log($"[SMAL-FK-DBG] head.fwd={cache.head.forward:F3} head.up={cache.head.up:F3}");
        }
    }

    private static Transform GetSmalBoneForJoint(AnimalRigCache cache, int joint)
    {
        switch (joint)
        {
            case 7:  return cache.leftFrontUpper;
            case 8:  return cache.leftFrontLower;
            case 9:  return cache.leftFrontPaw;
            case 11: return cache.rightFrontUpper;
            case 12: return cache.rightFrontLower;
            case 13: return cache.rightFrontPaw;
            case 15: return cache.neck;
            case 16: return cache.head;
            case 17: return cache.leftRearUpper;
            case 18: return cache.leftRearLower;
            case 19: return cache.leftRearPaw;
            case 20: return cache.leftRearToe;
            case 21: return cache.rightRearUpper;
            case 22: return cache.rightRearLower;
            case 23: return cache.rightRearPaw;
            case 24: return cache.rightRearToe;
            case 25: return cache.tailBase;
            case 26: return cache.tailMid;
            case 27: return cache.tailTip;
            default: return null;
        }
    }

    private static bool IsFiniteQ(Quaternion q)
    {
        return !float.IsNaN(q.x) && !float.IsInfinity(q.x)
            && !float.IsNaN(q.y) && !float.IsInfinity(q.y)
            && !float.IsNaN(q.z) && !float.IsInfinity(q.z)
            && !float.IsNaN(q.w) && !float.IsInfinity(q.w);
    }
}
