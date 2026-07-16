using System.Collections.Generic;
using UnityEngine;

public sealed partial class AnimalPoseApplier
{
    private readonly AnimalMotionFilter motionFilter;
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();

    public AnimalPoseApplier(AnimalFilterConfig filterConfig = default)
    {
        AnimalFilterConfig resolved = filterConfig.minCutoffHz <= 0f ? AnimalFilterConfig.Default : filterConfig;
        motionFilter = new AnimalMotionFilter(resolved);
    }

    // Snapshot every resolved canonical bone's local rotation, so a caller can blend the body
    // pose back toward whatever the tracked pipeline writes next (mirroring
    // CaptureHumanoidBoneLocalRotations/BlendHumanoidBoneLocalRotations for Human) - without
    // this, only the root smoothly catches up at the end of an interactive motion event while
    // the legs/spine/head/tail snap instantly into the live tracked pose the moment the
    // tracked pipeline writes it.
    public void CaptureBoneLocalRotations(Transform instanceRoot, Dictionary<Transform, Quaternion> destination)
    {
        Transform rigRoot = instanceRoot != null ? (instanceRoot.GetComponentInChildren<Animator>()?.transform ?? instanceRoot) : null;
        if (rigRoot == null || !animalRigCaches.TryGetValue(rigRoot, out AnimalRigCache cache) || cache == null)
        {
            return;
        }

        AddBoneLocalRotation(destination, cache.neck);
        AddBoneLocalRotation(destination, cache.head);
        AddBoneLocalRotation(destination, cache.spine);
        AddBoneLocalRotation(destination, cache.tailBase);
        AddBoneLocalRotation(destination, cache.tailMid);
        AddBoneLocalRotation(destination, cache.tailTip);
        AddBoneLocalRotation(destination, cache.leftFrontUpper);
        AddBoneLocalRotation(destination, cache.leftFrontLower);
        AddBoneLocalRotation(destination, cache.leftFrontPaw);
        AddBoneLocalRotation(destination, cache.rightFrontUpper);
        AddBoneLocalRotation(destination, cache.rightFrontLower);
        AddBoneLocalRotation(destination, cache.rightFrontPaw);
        AddBoneLocalRotation(destination, cache.leftRearUpper);
        AddBoneLocalRotation(destination, cache.leftRearLower);
        AddBoneLocalRotation(destination, cache.leftRearPaw);
        AddBoneLocalRotation(destination, cache.leftRearToe);
        AddBoneLocalRotation(destination, cache.rightRearUpper);
        AddBoneLocalRotation(destination, cache.rightRearLower);
        AddBoneLocalRotation(destination, cache.rightRearPaw);
        AddBoneLocalRotation(destination, cache.rightRearToe);
    }

    private static void AddBoneLocalRotation(Dictionary<Transform, Quaternion> destination, Transform bone)
    {
        if (bone != null)
        {
            destination[bone] = bone.localRotation;
        }
    }

    public static void BlendBoneLocalRotations(Dictionary<Transform, Quaternion> fromLocalRotations, float weight)
    {
        float clampedWeight = Mathf.Clamp01(weight);
        foreach (KeyValuePair<Transform, Quaternion> kv in fromLocalRotations)
        {
            if (kv.Key == null)
            {
                continue;
            }

            kv.Key.localRotation = Quaternion.Slerp(kv.Value, kv.Key.localRotation, clampedWeight);
        }
    }

    // There is no Humanoid-Avatar-style standard for which local axis an animal's nose points
    // along, so unlike Human (where local +Z reliably is the facing direction),
    // instanceRoot.rotation's own +Z cannot be assumed to be the nose. cache.spine.forward is
    // kept live and correct every frame by AnimalSmalFkApplier's FK write (with the project's
    // established "nose = -spine.forward" convention - see the gizmo in AnimalSmalFkApplier),
    // so reading it is the reliable way to measure which way the model is actually facing right
    // now, for callers that need to compute a relative turn (e.g. the FaceViewer interactive
    // motion preset) rather than assume an absolute local axis.
    public bool TryGetCurrentNoseWorldDirection(Transform instanceRoot, out Vector3 noseWorldDirection)
    {
        Transform rigRoot = instanceRoot != null ? (instanceRoot.GetComponentInChildren<Animator>()?.transform ?? instanceRoot) : null;
        if (rigRoot != null && animalRigCaches.TryGetValue(rigRoot, out AnimalRigCache cache) &&
            cache != null && cache.spine != null)
        {
            // Compute model-accurate nose direction: spine.rotation * Inv(spineBindWorld) * modelForwardLocal.
            // For Dog (bindSpineW=identity, modelForwardLocal=-Z) this equals -spine.forward (old hardcoded value).
            // For other models where the spine bone is not aligned with the nose at T-pose, this correctly
            // remaps modelForwardLocal through the spine's current rotation.
            if (cache.bindRotWorld.TryGetValue(cache.spine, out Quaternion spineBindWorld) &&
                cache.modelForwardLocal.sqrMagnitude > 0.000001f)
            {
                noseWorldDirection = cache.spine.rotation * (Quaternion.Inverse(spineBindWorld) * cache.modelForwardLocal);
            }
            else
            {
                noseWorldDirection = -cache.spine.forward;
            }
            return true;
        }

        noseWorldDirection = Vector3.forward;
        return false;
    }

    public void Apply(AnimalPoseRequest request)
    {
        Transform instanceRoot = request.instanceRoot;
        AnimalPoseWorldData pose = request.pose;
        if (instanceRoot == null)
            return;

        bool hasJoints = pose.jointsWorld != null && pose.jointVis != null && pose.jointCount >= 20;
        if (!hasJoints && !request.hasSmalPose)
            return;

        RuntimeClock.TickContext tick = request.tickContext;
        AnimalRigCache cache = ApplyAnimalSkeletonPlacement(
            instanceRoot, request.animator,
            pose.rootWorld, request.settings, tick);

        if (!request.enableBoneApply || cache == null || !cache.ready)
            return;

        if (request.hasSmalPose && IsAnimalRigReadyForSmalFk(cache))
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, pose.rootWorld, true, tick);
            TryApplyAnimalSmalFk(cache, request.smalPose, request.settings, pose.jointsWorld, pose.jointVis, instanceRoot);
            ApplyGestureOverlay(cache, request);
            return;
        }

        TryApplyAnimalRootOrientation(instanceRoot, cache, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(request.settings.animalRootRotateAlpha), pose.hasAnimalControl, pose.animalControl, request.settings, tick);
        AlignAnimalRootToSkeleton(instanceRoot, cache, pose.rootWorld, true, tick);

        float alpha = Mathf.Clamp01(request.settings.boneApplyAlpha);
        ApplyAnimalHeadPose(cache, pose.jointsWorld, pose.jointVis, alpha, pose.hasAnimalControl, pose.animalControl, tick);
        ApplyAnimalTailPose(cache, alpha, pose.hasAnimalControl, pose.animalControl, tick);

        if (request.settings.enableAnimalLimbApply)
        {
            ApplyAnimalLimbPose(cache, pose.jointsWorld, pose.jointVis, alpha, request.freezeAnimalDistal, pose.hasAnimalControl, pose.animalControl, tick);
        }

        ApplyGestureOverlay(cache, request);
    }

    // Runs after bone placement regardless of pose source (SMAL FK or keypoint/control-target
    // IK above), so an AnimalGesturePose gesture/walk clip works on any track - unlike the
    // animal-control-target-only presets (TailWag/PawWave/BodyTurnViewer), which stay pre-FK
    // and so still only apply when pose.hasAnimalControl is true.
    private static void ApplyGestureOverlay(AnimalRigCache cache, AnimalPoseRequest request)
    {
        if (request.gestureOverlayClip == null || cache == null)
        {
            return;
        }

        AnimalGesturePosePlayer.ApplyToRigCache(request.gestureOverlayClip, request.gestureOverlayNormalizedTime, cache);
    }

    // SMAL FK needs confident front/rear and left/right identification of all four leg
    // roots (plus spine) to compute a meaningful root orientation and per-joint bends - a
    // partially-wrong FK (e.g. front/rear legs swapped) looks worse than just falling back
    // to the existing keypoint-IK pipeline (ADR-0002 decision 3, 2026-06-18). This is
    // deliberately stricter than the general cache.ready bone-apply gate, which also covers
    // the keypoint-IK path and stays lenient so partial rigs still get some bone apply there.
    private static bool IsAnimalRigReadyForSmalFk(AnimalRigCache cache)
    {
        return cache != null
            && cache.spine != null
            && cache.leftFrontUpper != null
            && cache.rightFrontUpper != null
            && cache.leftRearUpper != null
            && cache.rightRearUpper != null;
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float rotateAlpha, bool hasControl, AnimalControlWorldData control, AnimalPoseSettings settings, RuntimeClock.TickContext tick)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null)
        {
            return;
        }

        if (!TryGetAnimalBodyBasis(jointsWorld, vis, instanceRoot, hasControl, control, out Vector3 bodyForward, out Vector3 bodyUp, out Vector3 facingHint))
        {
            return;
        }

        Vector3 worldUp = Vector3.up;
        Vector3 stabilizedForward = bodyForward;
        Vector3 stabilizedUp = bodyUp;

        if (settings.stabilizeAnimalRootYaw)
        {
            StabilizeAnimalRootBasis(instanceRoot, worldUp, bodyForward, bodyUp, facingHint, tick, out stabilizedForward, out stabilizedUp);
        }

        StabilizeAnimalRootPitchRoll(worldUp, stabilizedForward, stabilizedUp, settings.animalRootPitchRollBlend, out stabilizedForward, out stabilizedUp);

        Vector3 modelForwardLocal = cache != null && cache.modelForwardLocal.sqrMagnitude > 0.000001f
            ? cache.modelForwardLocal
            : settings.animalModelForwardLocal;
        Vector3 modelUpLocal = cache != null && cache.modelUpLocal.sqrMagnitude > 0.000001f
            ? cache.modelUpLocal
            : settings.animalModelUpLocal;

        Vector3 modelForward = modelForwardLocal.sqrMagnitude > 0.000001f ? modelForwardLocal.normalized : Vector3.right;
        Vector3 modelUp = modelUpLocal.sqrMagnitude > 0.000001f ? modelUpLocal.normalized : Vector3.up;

        Quaternion modelBasis = Quaternion.LookRotation(modelForward, modelUp);
        Quaternion targetBasis = Quaternion.LookRotation(stabilizedForward, stabilizedUp);
        Quaternion targetRootRot = targetBasis * Quaternion.Inverse(modelBasis);
        TrackPlacementWriter.Apply(
            instanceRoot,
            TrackPlacementCommand.RotationOnly(
                instanceRoot.position,
                Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha)),
                instanceRoot.localScale));
    }

    private static void StabilizeAnimalRootPitchRoll(Vector3 worldUp, Vector3 forward, Vector3 up, float pitchRollBlend, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        AnimalRootBasisMath.StabilizePitchRoll(
            worldUp,
            forward,
            up,
            pitchRollBlend,
            out stabilizedForward,
            out stabilizedUp);
    }

    private void StabilizeAnimalRootBasis(Transform root, Vector3 worldUp, Vector3 forward, Vector3 bodyUp, Vector3 facingHint, RuntimeClock.TickContext tick, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        stabilizedForward = forward;
        stabilizedUp = bodyUp;
        if (root == null)
        {
            return;
        }

        int currentFrame = tick.frameCount;
        bool seenRecently = motionFilter.WasSeenRecently(root, currentFrame);
        motionFilter.MarkRootSeen(root, currentFrame);

        Vector3 planarForward = Vector3.ProjectOnPlane(forward, worldUp);
        if (planarForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        planarForward.Normalize();

        if (!seenRecently)
        {
            motionFilter.ResetRootBasis(root, planarForward, bodyUp, worldUp);
            stabilizedForward = forward;
            stabilizedUp = bodyUp;
            return;
        }

        if (!motionFilter.TryGetPrevRootForward(root, out Vector3 prevForward))
        {
            prevForward = Vector3.ProjectOnPlane(root.forward, worldUp);
        }
        if (prevForward.sqrMagnitude < 0.000001f)
        {
            motionFilter.ResetRootBasis(root, planarForward, bodyUp, worldUp);
            return;
        }
        prevForward.Normalize();

        Vector3 candA = planarForward;
        Vector3 candB = -candA;
        Vector3 planarHint = Vector3.ProjectOnPlane(facingHint, worldUp);
        Vector3 chosenForward;
        if (planarHint.sqrMagnitude > 0.000001f)
        {
            planarHint.Normalize();
            chosenForward = Vector3.Dot(candA, planarHint) >= Vector3.Dot(candB, planarHint) ? candA : candB;
        }
        else
        {
            chosenForward = Vector3.Dot(prevForward, candA) >= Vector3.Dot(prevForward, candB) ? candA : candB;
        }

        float dt = tick.deltaTime;
        Vector3 filteredForward = motionFilter.FilterRootForward(root, chosenForward, dt);
        filteredForward = Vector3.ProjectOnPlane(filteredForward, worldUp);
        if (filteredForward.sqrMagnitude <= 0.000001f)
        {
            filteredForward = chosenForward;
        }
        filteredForward.Normalize();
        motionFilter.SetRootForward(root, filteredForward);

        Vector3 projectedUp = Vector3.ProjectOnPlane(bodyUp, filteredForward);
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            projectedUp = Vector3.ProjectOnPlane(root.up, filteredForward);
        }
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            projectedUp = Vector3.ProjectOnPlane(worldUp, filteredForward);
        }
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = filteredForward;
            stabilizedUp = bodyUp;
            return;
        }
        projectedUp.Normalize();

        Vector3 filteredUp = motionFilter.FilterRootUp(root, projectedUp, dt);
        filteredUp = Vector3.ProjectOnPlane(filteredUp, filteredForward);
        if (filteredUp.sqrMagnitude <= 0.000001f)
        {
            filteredUp = projectedUp;
        }
        filteredUp.Normalize();

        Vector3 right = Vector3.Cross(filteredForward, filteredUp);
        if (right.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = filteredForward;
            stabilizedUp = filteredUp;
            return;
        }
        right.Normalize();
        filteredUp = Vector3.Cross(right, filteredForward).normalized;

        stabilizedForward = filteredForward;
        stabilizedUp = filteredUp;
    }

    private static bool TryGetAnimalBodyBasis(Vector3[] jointsWorld, byte[] vis, Transform instanceRoot, bool hasControl, AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
    {
        forward = Vector3.zero;
        up = Vector3.zero;
        facingHint = Vector3.zero;
        if (hasControl && TryGetAnimalBodyBasisFromControl(control, out forward, out up, out facingHint))
        {
            return true;
        }

        Vector3 preferredUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
        return AnimalBodyBasisResolver.TryResolveFromJoints(
            jointsWorld,
            vis,
            preferredUp,
            out forward,
            out up,
            out facingHint);
    }

    public static bool TryGetAnimalBodyBasisFromControl(AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
    {
        return AnimalBodyBasisResolver.TryResolveFromControl(control, out forward, out up, out facingHint);
    }

    private AnimalRigCache ApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3 skeletonRoot, AnimalPoseSettings settings, RuntimeClock.TickContext tick)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot, instanceRoot, settings);
        if (cache != null && cache.ready)
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot, false, tick);
        }
        else
        {
            TrackPlacementWriter.Apply(
                instanceRoot,
                TrackPlacementCommand.PositionOnly(
                    skeletonRoot,
                    instanceRoot.rotation,
                    instanceRoot.localScale));
        }

        return cache;
    }

    private void AlignAnimalRootToSkeleton(Transform instanceRoot, AnimalRigCache cache, Vector3 skeletonRoot, bool smooth, RuntimeClock.TickContext tick)
    {
        if (instanceRoot == null || cache == null)
        {
            return;
        }

        Vector3 targetPosition = TrackPlacementWriter.CalculateAnchorAlignedPosition(
            instanceRoot,
            ResolveAnimalPlacementBone(cache),
            skeletonRoot);
        if (smooth)
        {
            targetPosition = SmoothAnimalRootPosition(instanceRoot, targetPosition, tick.deltaTime);
        }

        TrackPlacementWriter.Apply(
            instanceRoot,
            TrackPlacementCommand.PositionOnly(
                targetPosition,
                instanceRoot.rotation,
                instanceRoot.localScale));
    }

    private Vector3 SmoothAnimalRootPosition(Transform root, Vector3 targetPosition, float deltaTime)
    {
        if (root == null)
        {
            return targetPosition;
        }

        return motionFilter.FilterRootPosition(root, targetPosition, deltaTime);
    }

    private static Transform ResolveAnimalPlacementBone(AnimalRigCache cache)
    {
        return AnimalPlacementBoneSelector.Select(cache.spine, cache.neck, cache.tailBase, cache.root);
    }

    private void ApplyAnimalHeadPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (hasControl)
        {
            if (control.hasWithers && control.hasHeadRoot)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f, tick.deltaTime);
                ApplyAnimalBoneFromPoints(cache, cache.neck, control.withersWorld, headRoot, alpha * 0.42f);
            }

            if (control.hasHeadRoot && control.hasHeadTip)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f, tick.deltaTime);
                Vector3 headTip = SmoothAnimalPoseTarget(cache.head, control.headTipWorld, 2.0f, 0.08f, tick.deltaTime);
                ApplyAnimalBoneFromPoints(cache, cache.head, headRoot, headTip, alpha * 0.42f);
                return;
            }
        }

        ApplyAnimalBonesFromSegment(cache, cache.neck, cache.head, jointsWorld, vis, 24, 2, alpha * 0.35f, alpha * 0.35f);
    }

    private void ApplyAnimalTailPose(AnimalRigCache cache, float alpha, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (!hasControl || cache.tailBase == null)
        {
            return;
        }

        if (control.hasTailBase && control.hasTailTip)
        {
            Vector3 tailTip = SmoothAnimalPoseTarget(cache.tailBase, control.tailTipWorld, 1.5f, 0.04f, tick.deltaTime);
            ApplyAnimalBoneFromPoints(cache, cache.tailBase, control.tailBaseWorld, tailTip, alpha * 0.25f);
            return;
        }

        ApplyAnimalBoneFromChain(cache, cache.tailBase, null, null, control.tailWorld, alpha * 0.25f, false);
    }

    private void ApplyAnimalLimbPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool freezeAnimalDistal, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (hasControl)
        {
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, control.frontLeftLegWorld, alpha, false, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, control.frontRightLegWorld, alpha, false, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, control.rearLeftLegWorld, alpha, !freezeAnimalDistal, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, control.rearRightLegWorld, alpha, !freezeAnimalDistal, tick.deltaTime);
            return;
        }

        ApplyAnimalLimbIkByChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, AnimalPoseJointChains.LeftFront, alpha, false, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, AnimalPoseJointChains.RightFront, alpha, false, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, AnimalPoseJointChains.LeftRear, alpha, !freezeAnimalDistal, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, AnimalPoseJointChains.RightRear, alpha, !freezeAnimalDistal, tick.deltaTime);
    }

    private void ApplyAnimalBonesFromSegment(AnimalRigCache cache, Transform primary, Transform secondary, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float primaryAlpha, float secondaryAlpha)
    {
        ApplyAnimalBoneFromJoints(cache, primary, jointsWorld, vis, idxA, idxB, primaryAlpha);
        ApplyAnimalBoneFromJoints(cache, secondary, jointsWorld, vis, idxA, idxB, secondaryAlpha);
    }

    private void ApplyAnimalLimbByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        ApplyAnimalBoneFromJoints(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.9f);
        ApplyAnimalBoneFromJoints(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.85f);
        if (paw != null && applyDistal)
        {
            ApplyAnimalBoneFromJoints(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.7f);
        }
    }

    private void ApplyAnimalBoneFromChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal)
    {
        if (chainWorld == null || chainWorld.Length < 2)
        {
            return;
        }

        int upperStart = 0;
        int lowerStart = 1;
        int distalStart = 2;
        if (chainWorld.Length >= 5)
        {
            upperStart = 1;
            lowerStart = 2;
            distalStart = 3;
        }

        if (upper != null && upperStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, upper, chainWorld[upperStart], chainWorld[upperStart + 1], alpha * 0.9f);
        }

        if (lower != null && lowerStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, lower, chainWorld[lowerStart], chainWorld[lowerStart + 1], alpha * 0.85f);
        }

        if (paw != null && applyDistal && distalStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, paw, chainWorld[distalStart], chainWorld[distalStart + 1], alpha * 0.7f);
        }
    }

    private void ApplyAnimalLimbIkByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal, float deltaTime)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, chain[0], out Vector3 root) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, chain[1], out Vector3 mid) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, chain[2], out Vector3 end))
        {
            ApplyAnimalLimbByChain(cache, upper, lower, paw, jointsWorld, vis, chain, alpha, applyDistal);
            return;
        }

        Vector3 distal = end;
        if (applyDistal && TrackedJointPoints.TryGet(jointsWorld, vis, chain[3], out Vector3 toe))
        {
            distal = toe;
        }

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, root, mid, end, distal, alpha, applyDistal, deltaTime);
    }

    private void ApplyAnimalLimbIkFromControlChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal, float deltaTime)
    {
        if (chainWorld == null || chainWorld.Length < 3)
        {
            ApplyAnimalBoneFromChain(cache, upper, lower, paw, chainWorld, alpha, applyDistal);
            return;
        }

        int rootIndex = 0;
        int midIndex = 1;
        int endIndex = 2;
        int distalIndex = 3;
        if (chainWorld.Length >= 5)
        {
            rootIndex = 1;
            midIndex = 2;
            endIndex = 3;
            distalIndex = 4;
        }

        if (endIndex >= chainWorld.Length)
        {
            ApplyAnimalBoneFromChain(cache, upper, lower, paw, chainWorld, alpha, applyDistal);
            return;
        }

        Vector3 distal = chainWorld[endIndex];
        if (applyDistal && distalIndex < chainWorld.Length)
        {
            distal = chainWorld[distalIndex];
        }

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, chainWorld[rootIndex], chainWorld[midIndex], chainWorld[endIndex], distal, alpha, applyDistal, deltaTime);
    }

    private void ApplyAnimalTwoBoneLimbIk(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3 observedRoot, Vector3 observedMid, Vector3 observedEnd, Vector3 observedDistal, float alpha, bool applyDistal, float deltaTime)
    {
        if (upper == null || lower == null)
        {
            return;
        }

        Vector3 root = upper.position;
        Vector3 targetEnd = SmoothAnimalPoseTarget(lower, observedEnd, 2.2f, 0.1f, deltaTime);
        Vector3 bendHint = observedMid - observedRoot;
        if (bendHint.sqrMagnitude <= 0.000001f)
        {
            bendHint = lower.position - upper.position;
        }

        if (!TrySolveAnimalTwoBoneMidpoint(root, targetEnd, bendHint, upper, lower, paw, out Vector3 solvedMid))
        {
            ApplyAnimalBoneFromPoints(cache, upper, observedRoot, observedMid, alpha * 0.85f);
            ApplyAnimalBoneFromPoints(cache, lower, observedMid, observedEnd, alpha * 0.8f);
        }
        else
        {
            ApplyAnimalBoneFromPoints(cache, upper, root, solvedMid, alpha * 0.9f);
            ApplyAnimalBoneFromPoints(cache, lower, lower.position, targetEnd, alpha * 0.85f);
        }

        if (paw != null && applyDistal)
        {
            Vector3 targetDistal = SmoothAnimalPoseTarget(paw, observedDistal, 2.2f, 0.1f, deltaTime);
            ApplyAnimalBoneFromPoints(cache, paw, targetEnd, targetDistal, alpha * 0.35f);
        }
    }

    private static bool TrySolveAnimalTwoBoneMidpoint(Vector3 root, Vector3 targetEnd, Vector3 bendHint, Transform upper, Transform lower, Transform paw, out Vector3 solvedMid)
    {
        solvedMid = Vector3.zero;
        if (upper == null || lower == null)
        {
            return false;
        }

        float upperLen = Vector3.Distance(upper.position, lower.position);
        float lowerLen = paw != null ? Vector3.Distance(lower.position, paw.position) : upperLen;
        if (upperLen <= 0.0001f || lowerLen <= 0.0001f)
        {
            return false;
        }

        Vector3 toTarget = targetEnd - root;
        float distance = toTarget.magnitude;
        if (distance <= 0.0001f)
        {
            return false;
        }

        Vector3 aim = toTarget / distance;
        float clampedDistance = Mathf.Clamp(distance, Mathf.Abs(upperLen - lowerLen) + 0.001f, upperLen + lowerLen - 0.001f);
        float along = (upperLen * upperLen - lowerLen * lowerLen + clampedDistance * clampedDistance) / (2f * clampedDistance);
        float heightSq = Mathf.Max(0f, upperLen * upperLen - along * along);
        float height = Mathf.Sqrt(heightSq);

        Vector3 pole = Vector3.ProjectOnPlane(bendHint, aim);
        if (pole.sqrMagnitude <= 0.000001f)
        {
            pole = Vector3.ProjectOnPlane(lower.position - upper.position, aim);
        }
        if (pole.sqrMagnitude <= 0.000001f)
        {
            pole = Vector3.ProjectOnPlane(Vector3.up, aim);
        }
        if (pole.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        pole.Normalize();
        solvedMid = root + aim * along + pole * height;
        return true;
    }

    private Vector3 SmoothAnimalPoseTarget(Transform key, Vector3 target, float minCutoffHz, float beta, float deltaTime)
    {
        if (key == null)
        {
            return target;
        }

        return motionFilter.FilterLimbTarget(key, target, minCutoffHz, beta, deltaTime);
    }

    private void RegisterAnimalAimChild(AnimalRigCache cache, Transform bone, Transform aimChild)
    {
        if (bone == null || aimChild == null)
        {
            return;
        }

        cache.aimChildByBone[bone] = aimChild;
    }

    private void RegisterAnimalAimPairs(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i + 1 < bones.Length; i += 2)
        {
            RegisterAnimalAimChild(cache, bones[i], bones[i + 1]);
        }
    }

    private void PrimeAnimalBind(AnimalRigCache cache, Transform bone)
    {
        if (bone == null || cache.bindRotLocal.ContainsKey(bone))
        {
            return;
        }

        cache.bindRotLocal[bone] = bone.localRotation;
        cache.bindRotWorld[bone] = bone.rotation;
        Vector3 bindDirLocal = Vector3.forward;
        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 bindDirWorld))
        {
            bindDirLocal = bone.InverseTransformDirection(bindDirWorld);
        }
        cache.bindDirLocal[bone] = bindDirLocal == Vector3.zero ? Vector3.forward : bindDirLocal.normalized;
    }

    private void PrimeAnimalBinds(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i < bones.Length; i++)
        {
            PrimeAnimalBind(cache, bones[i]);
        }
    }

    private bool ApplyAnimalBoneFromJoints(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxA, out Vector3 a) || !TrackedJointPoints.TryGet(jointsWorld, vis, idxB, out Vector3 b))
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

        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 currentDir))
        {
            Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorld = deltaWorld * bone.rotation;
            float dot = Vector3.Dot(currentDir, targetDir);
            if (dot > -0.98f)
            {
                TransformWriter.ApplyWorldRotation(
                    bone,
                    Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha)));
                return true;
            }
        }

        Vector3 targetLocalDir = bone.parent != null ? bone.parent.InverseTransformDirection(targetDir) : targetDir;
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
        TransformWriter.ApplyLocalRotation(
            bone,
            Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha)));
        return true;
    }

    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (bone == null)
        {
            return null;
        }

        Transform registeredAimChild = null;
        if (cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            registeredAimChild = mapped;
        }

        Transform fallbackFirstChild = bone.childCount > 0 ? bone.GetChild(0) : null;

        return AnimalAimChildSelector.Select(registeredAimChild, fallbackFirstChild);
    }

    public static bool ShouldUseAnimalAimChildPivotDirection(bool hasRegisteredAimChild, bool isLimbBone)
    {
        return AnimalAimDirectionPolicy.ShouldUseChildPivotDirection(hasRegisteredAimChild, isLimbBone);
    }

    private bool TryGetBoneCenterDirectionWorld(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        if (bone == null)
        {
            return false;
        }

        bool hasRegisteredAimChild = cache.aimChildByBone.TryGetValue(bone, out Transform registeredAimChild) &&
            registeredAimChild != null;
        Transform centerTarget = ResolveAnimalAimChild(cache, bone);
        if (centerTarget != null &&
            ShouldUseAnimalAimChildPivotDirection(hasRegisteredAimChild, IsAnimalLimbBone(cache, bone)))
        {
            Vector3 childPivotDir = centerTarget.position - bone.position;
            if (childPivotDir.sqrMagnitude > 0.000001f)
            {
                dirWorld = childPivotDir.normalized;
                return true;
            }
        }

        if (centerTarget == null)
        {
            centerTarget = bone;
        }

        if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld))
        {
            return false;
        }

        Vector3 rawDir = centerWorld - bone.position;
        if (rawDir.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        dirWorld = rawDir.normalized;
        return true;
    }

    private static bool IsAnimalLimbBone(AnimalRigCache cache, Transform bone)
    {
        if (bone == null)
        {
            return false;
        }

        return
            bone == cache.leftFrontUpper ||
            bone == cache.leftFrontLower ||
            bone == cache.leftFrontPaw ||
            bone == cache.rightFrontUpper ||
            bone == cache.rightFrontLower ||
            bone == cache.rightFrontPaw ||
            bone == cache.leftRearUpper ||
            bone == cache.leftRearLower ||
            bone == cache.leftRearPaw ||
            bone == cache.rightRearUpper ||
            bone == cache.rightRearLower ||
            bone == cache.rightRearPaw;
    }

    private static bool TryGetTransformCenterWorld(Transform target, out Vector3 centerWorld)
    {
        centerWorld = Vector3.zero;
        if (target == null)
        {
            return false;
        }

        SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            centerWorld = target.TransformPoint(smr.localBounds.center);
            return true;
        }

        MeshFilter mf = target.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            centerWorld = target.TransformPoint(mf.sharedMesh.bounds.center);
            return true;
        }

        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            centerWorld = renderer.bounds.center;
            return true;
        }

        return false;
    }

    private AnimalRigCache GetOrBuildAnimalRigCache(Transform root, Transform skinSearchRoot, AnimalPoseSettings settings)
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
        AnimalBoneMappingOverride boneOverride = root.GetComponentInChildren<AnimalBoneMappingOverride>();

        cache.neck = ResolveBone(bones, boneOverride?.neck, AnimalRigDefinition.Neck);
        cache.head = ResolveBone(bones, boneOverride?.head, AnimalRigDefinition.Head);
        cache.spine = ResolveBone(bones, boneOverride?.spine, AnimalRigDefinition.Spine);
        cache.tailBase = ResolveBoneByTokens(bones, boneOverride?.tailBase, AnimalRigDefinition.TailBaseTokens);

        FillAnimalSpineFallbacks(cache, root, bones, settings);

        cache.leftFrontUpper = ResolveBone(bones, boneOverride?.frontLUpper, AnimalRigDefinition.LeftFrontUpper);
        cache.leftFrontLower = ResolveBone(bones, boneOverride?.frontLLower, AnimalRigDefinition.LeftFrontLower);
        cache.leftFrontPaw = ResolveBone(bones, boneOverride?.frontLPaw, AnimalRigDefinition.LeftFrontPaw);
        cache.rightFrontUpper = ResolveBone(bones, boneOverride?.frontRUpper, AnimalRigDefinition.RightFrontUpper);
        cache.rightFrontLower = ResolveBone(bones, boneOverride?.frontRLower, AnimalRigDefinition.RightFrontLower);
        cache.rightFrontPaw = ResolveBone(bones, boneOverride?.frontRPaw, AnimalRigDefinition.RightFrontPaw);
        cache.leftRearUpper = ResolveBone(bones, boneOverride?.rearLUpper, AnimalRigDefinition.LeftRearUpper);
        cache.leftRearLower = ResolveBone(bones, boneOverride?.rearLLower, AnimalRigDefinition.LeftRearLower);
        cache.leftRearPaw = ResolveBone(bones, boneOverride?.rearLPaw, AnimalRigDefinition.LeftRearPaw);
        cache.leftRearToe = ResolveBone(bones, boneOverride?.rearLToe, AnimalRigDefinition.LeftRearToe);
        cache.rightRearUpper = ResolveBone(bones, boneOverride?.rearRUpper, AnimalRigDefinition.RightRearUpper);
        cache.rightRearLower = ResolveBone(bones, boneOverride?.rearRLower, AnimalRigDefinition.RightRearLower);
        cache.rightRearPaw = ResolveBone(bones, boneOverride?.rearRPaw, AnimalRigDefinition.RightRearPaw);
        cache.rightRearToe = ResolveBone(bones, boneOverride?.rearRToe, AnimalRigDefinition.RightRearToe);
        cache.tailMid = ResolveBone(bones, boneOverride?.tailMid, AnimalRigDefinition.TailMid);
        cache.tailTip = ResolveBone(bones, boneOverride?.tailTip, AnimalRigDefinition.TailTip);
        ResolveAnimalModelBasis(root, cache, settings);

        if (cache.spine != null && cache.neck != null)
        {
            Vector3 spineToNeck = cache.neck.position - cache.spine.position;
            if (spineToNeck.sqrMagnitude > 0.000001f)
            {
                cache.spineToNeckBindDirWorld = spineToNeck.normalized;
            }
        }

        // Registered before PrimeAnimalBinds (not after, as it read previously): PrimeAnimalBind
        // captures bindDirLocal via ResolveAnimalAimChild, which prefers the registered aim
        // child over a bone's natural first Unity child. For leg/neck bones the two happened
        // to already coincide (their real first child always is the next canonical joint), so
        // the old order never visibly mattered there - but tailMid's first real Unity child is
        // often an intermediate undriven bone before tailTip (e.g. Buffalo's Tail2 -> Tail3 ->
        // Tail4, where Tail3 has no canonical name), so tail needs the registration to actually
        // be in effect at prime time.
        RegisterAnimalAimPairs(
            cache,
            cache.leftFrontUpper, cache.leftFrontLower,
            cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower,
            cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower,
            cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower,
            cache.rightRearLower, cache.rightRearPaw,
            cache.neck, cache.head,
            cache.spine, cache.neck,
            cache.tailBase, cache.tailMid,
            cache.tailMid, cache.tailTip);

        PrimeAnimalBinds(
            cache,
            cache.neck, cache.head, cache.spine, cache.tailBase,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw,
            cache.leftRearToe, cache.rightRearToe,
            cache.tailMid, cache.tailTip);

        // spineToNeckBindDirWorld is kept in world space (bind-time direction from spine to neck).
        // The rootYawFix detection formula uses: candidate * modelOrientFix * spineToNeckBindDirWorld,
        // which correctly predicts the neck world direction because:
        //   tw[0] = candidate * modelOrientFix * S  →  neck_world = tw[0] * Inv(S) * spineToNeck_world
        //                                            = candidate * modelOrientFix * spineToNeck_world
        // (see rawWorldFk0 formula in TryApplyAnimalSmalFk for the modelOrientFix definition).

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;

        // One-time diagnostic: confirm the bones we resolved are the same Transforms a
        // SkinnedMeshRenderer actually deforms with. If a resolved bone (e.g. leftFrontUpper)
        // is NOT in any renderer's bones[] array, rotating it will have zero visible effect on
        // the rendered mesh even though the FK math runs correctly every frame.
        // Search from skinSearchRoot (the model instance root), not the Armature/animator
        // subtree: SkinnedMeshRenderer is commonly a sibling of the Armature, not a descendant.
        LogAnimalBoneSkinningCheck(skinSearchRoot != null ? skinSearchRoot : root, cache);

        return cache;
    }

    private static void LogAnimalBoneSkinningCheck(Transform root, AnimalRigCache cache)
    {
        SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        HashSet<Transform> skinnedBones = new HashSet<Transform>();
        for (int i = 0; i < renderers.Length; i++)
        {
            Transform[] bones = renderers[i].bones;
            if (bones == null) continue;
            for (int j = 0; j < bones.Length; j++)
                if (bones[j] != null) skinnedBones.Add(bones[j]);
        }

        void Check(string label, Transform bone)
        {
            if (bone == null)
            {
                Debug.Log($"[SMAL-SKIN-CHECK] {label}=NULL (not found by name matching)");
                return;
            }
            bool isSkinned = skinnedBones.Contains(bone);
            Debug.Log($"[SMAL-SKIN-CHECK] {label}={bone.name} isSkinnedByAnyRenderer={isSkinned}");
        }

        Debug.Log($"[SMAL-SKIN-CHECK] root={root.name} skinnedMeshRendererCount={renderers.Length} totalSkinnedBoneRefs={skinnedBones.Count}");
        Check("head", cache.head);
        Check("neck", cache.neck);
        Check("spine", cache.spine);
        Check("leftFrontUpper", cache.leftFrontUpper);
        Check("leftFrontLower", cache.leftFrontLower);
        Check("leftFrontPaw", cache.leftFrontPaw);
        Check("rightFrontUpper", cache.rightFrontUpper);
        Check("rightFrontLower", cache.rightFrontLower);
        Check("rightFrontPaw", cache.rightFrontPaw);
        Check("leftRearUpper", cache.leftRearUpper);
        Check("leftRearLower", cache.leftRearLower);
        Check("leftRearPaw", cache.leftRearPaw);
        Check("rightRearUpper", cache.rightRearUpper);
        Check("rightRearLower", cache.rightRearLower);
        Check("rightRearPaw", cache.rightRearPaw);
        Check("tailBase", cache.tailBase);
        Check("tailMid", cache.tailMid);
        Check("tailTip", cache.tailTip);
    }

    private static void ResolveAnimalModelBasis(Transform root, AnimalRigCache cache, AnimalPoseSettings settings)
    {
        if (root == null || cache == null)
        {
            return;
        }

        if (TryAverageAnimalBonePosition(cache.leftFrontUpper, cache.rightFrontUpper, out Vector3 frontCenter) &&
            TryAverageAnimalBonePosition(cache.leftRearUpper, cache.rightRearUpper, out Vector3 rearCenter))
        {
            Debug.Log($"[SMAL-SKIN-CHECK] BASIS frontCenter={frontCenter:F3} rearCenter={rearCenter:F3} " +
                $"leftFrontUpper.pos={(cache.leftFrontUpper != null ? cache.leftFrontUpper.position : Vector3.zero):F3} " +
                $"leftRearUpper.pos={(cache.leftRearUpper != null ? cache.leftRearUpper.position : Vector3.zero):F3} " +
                $"head.pos={(cache.head != null ? cache.head.position : Vector3.zero):F3} " +
                $"tailBase.pos={(cache.tailBase != null ? cache.tailBase.position : Vector3.zero):F3}");

            Vector3 forwardWorld = frontCenter - rearCenter;
            if (forwardWorld.sqrMagnitude > 0.000001f)
            {
                cache.modelForwardLocal = root.InverseTransformDirection(forwardWorld).normalized;
            }
        }

        if (cache.modelForwardLocal.sqrMagnitude <= 0.000001f)
        {
            cache.modelForwardLocal = settings.animalModelForwardLocal.sqrMagnitude > 0.000001f
                ? settings.animalModelForwardLocal.normalized
                : Vector3.forward;
        }

        cache.modelUpLocal = settings.animalModelUpLocal.sqrMagnitude > 0.000001f
            ? settings.animalModelUpLocal.normalized
            : Vector3.up;
    }

    private static bool TryAverageAnimalBonePosition(Transform left, Transform right, out Vector3 center)
    {
        if (left != null && right != null)
        {
            center = (left.position + right.position) * 0.5f;
            return true;
        }

        Transform single = left != null ? left : right;
        if (single != null)
        {
            center = single.position;
            return true;
        }

        center = Vector3.zero;
        return false;
    }

    private void FillAnimalSpineFallbacks(AnimalRigCache cache, Transform root, Transform[] bones, AnimalPoseSettings settings)
    {
        if (cache == null || root == null || bones == null)
        {
            return;
        }

        List<Transform> spineBones = new List<Transform>();
        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone == null)
            {
                continue;
            }

            string name = bone.name.ToLowerInvariant();
            if (name == "def-spine" || name.StartsWith("def-spine."))
            {
                spineBones.Add(ResolveLikelyRigBone(bone));
            }
        }

        if (spineBones.Count == 0)
        {
            return;
        }

        Vector3 forwardLocal = settings.animalModelForwardLocal.sqrMagnitude > 0.000001f
            ? settings.animalModelForwardLocal.normalized
            : Vector3.forward;
        Vector3 forwardWorld = root.TransformDirection(forwardLocal);
        if (forwardWorld.sqrMagnitude <= 0.000001f)
        {
            forwardWorld = root.forward;
        }
        forwardWorld.Normalize();

        spineBones.Sort((a, b) =>
            Vector3.Dot(a.position - root.position, forwardWorld)
                .CompareTo(Vector3.Dot(b.position - root.position, forwardWorld)));

        Transform back = spineBones[0];
        Transform front = spineBones[spineBones.Count - 1];
        Transform neck = spineBones[Mathf.Max(0, spineBones.Count - 2)];
        Transform body = spineBones[Mathf.Clamp(spineBones.Count / 2, 0, spineBones.Count - 1)];

        if (cache.spine == null ||
            AnimalDefSpineFallbackPolicy.ShouldReplaceCanonicalSpineWithDefSpineChain(
                cache.tailBase != null,
                spineBones.Count))
        {
            cache.spine = body;
        }
        if (cache.neck == null)
        {
            cache.neck = neck;
        }
        if (cache.head == null)
        {
            cache.head = front;
        }
        if (cache.tailBase == null && back != cache.head)
        {
            cache.tailBase = back;
        }
    }

    private Transform FindAnimalBone(Transform[] bones, string[] exactNames, params string[] tokens)
    {
        Transform exact = FindBoneByExactNames(bones, exactNames);
        return exact ?? FindBoneByTokens(bones, tokens);
    }

    private Transform FindAnimalBone(Transform[] bones, AnimalBoneRule rule)
    {
        return FindAnimalBone(bones, rule.exactNames, rule.tokens);
    }

    private Transform ResolveBone(Transform[] bones, string overrideName, AnimalBoneRule rule)
    {
        if (!string.IsNullOrEmpty(overrideName))
            return FindBoneByExactNames(bones, overrideName);
        return FindAnimalBone(bones, rule);
    }

    private Transform ResolveBoneByTokens(Transform[] bones, string overrideName, params string[] tokens)
    {
        if (!string.IsNullOrEmpty(overrideName))
            return FindBoneByExactNames(bones, overrideName);
        return FindBoneByTokens(bones, tokens);
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

    private static Transform ResolveLikelyRigBone(Transform node)
    {
        if (node == null)
        {
            return null;
        }

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

}
