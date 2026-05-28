using System.Collections.Generic;
using UnityEngine;

public sealed class AnimalPoseApplier
{
    private const float InvalidJointSqrMagnitudeEpsilon = 1e-10f;
    private const float AnimalRootOneEuroMinCutoffHz = 1.0f;
    private const float AnimalRootOneEuroBeta = 0.15f;
    private const float AnimalRootOneEuroDerivativeCutoffHz = 1.0f;
    private static readonly int[] AnimalLeftFrontChain = { 18, 13, 9, 15 };
    private static readonly int[] AnimalRightFrontChain = { 18, 12, 8, 14 };
    private static readonly int[] AnimalLeftRearChain = { 7, 11, 17, 6 };
    private static readonly int[] AnimalRightRearChain = { 7, 10, 16, 5 };

    private readonly Dictionary<Transform, Vector3> animalRootYawForwardByRoot = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, int> animalRootYawLastSeenFrameByRoot = new Dictionary<Transform, int>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalRootForwardFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalRootUpFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalLimbTargetFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Transform, OneEuroVector3Filter> animalRootPositionFilters = new Dictionary<Transform, OneEuroVector3Filter>();
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();

    public void Apply(AnimalPoseRequest request)
    {
        Transform instanceRoot = request.instanceRoot;
        AnimalPoseWorldData pose = request.pose;
        if (instanceRoot == null || pose.jointsWorld == null || pose.jointVis == null || pose.jointCount < 20)
        {
            return;
        }

        AnimalRigCache cache = ApplyAnimalSkeletonPlacement(instanceRoot, request.animator, pose.jointsWorld, pose.jointVis, pose.jointCount, pose.rootWorld, request.settings);
        if (!request.enableBoneApply || cache == null || !cache.ready)
        {
            return;
        }

        TryApplyAnimalRootOrientation(instanceRoot, cache, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(request.settings.animalRootRotateAlpha), pose.hasAnimalControl, pose.animalControl, request.settings);
        AlignAnimalRootToSkeleton(instanceRoot, cache, pose.rootWorld, true);

        float alpha = Mathf.Clamp01(request.settings.boneApplyAlpha);
        ApplyAnimalHeadPose(cache, pose.jointsWorld, pose.jointVis, alpha, pose.hasAnimalControl, pose.animalControl);
        ApplyAnimalTailPose(cache, alpha, pose.hasAnimalControl, pose.animalControl);

        if (request.settings.enableAnimalLimbApply)
        {
            ApplyAnimalLimbPose(cache, pose.jointsWorld, pose.jointVis, alpha, request.freezeAnimalDistal, pose.hasAnimalControl, pose.animalControl);
        }
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float rotateAlpha, bool hasControl, AnimalControlWorldData control, AnimalPoseSettings settings)
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
            StabilizeAnimalRootBasis(instanceRoot, worldUp, bodyForward, bodyUp, facingHint, out stabilizedForward, out stabilizedUp);
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
        instanceRoot.rotation = Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha));
    }

    private static void StabilizeAnimalRootPitchRoll(Vector3 worldUp, Vector3 forward, Vector3 up, float pitchRollBlend, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        stabilizedForward = forward;
        stabilizedUp = up;

        Vector3 planarForward = Vector3.ProjectOnPlane(forward, worldUp);
        if (planarForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        planarForward.Normalize();

        float tiltBlend = Mathf.Clamp01(pitchRollBlend);
        Vector3 blendedForward = Vector3.Slerp(planarForward, forward.normalized, tiltBlend);
        if (blendedForward.sqrMagnitude <= 0.000001f)
        {
            blendedForward = planarForward;
        }
        blendedForward.Normalize();

        Vector3 blendedUp = Vector3.Slerp(worldUp, up.sqrMagnitude > 0.000001f ? up.normalized : worldUp, tiltBlend);
        blendedUp = Vector3.ProjectOnPlane(blendedUp, blendedForward);
        if (blendedUp.sqrMagnitude <= 0.000001f)
        {
            blendedUp = Vector3.ProjectOnPlane(worldUp, blendedForward);
        }
        if (blendedUp.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = blendedForward;
            stabilizedUp = worldUp;
            return;
        }

        blendedUp.Normalize();
        Vector3 right = Vector3.Cross(blendedForward, blendedUp);
        if (right.sqrMagnitude > 0.000001f)
        {
            right.Normalize();
            blendedUp = Vector3.Cross(right, blendedForward).normalized;
        }

        stabilizedForward = blendedForward;
        stabilizedUp = blendedUp;
    }

    private void StabilizeAnimalRootBasis(Transform root, Vector3 worldUp, Vector3 forward, Vector3 bodyUp, Vector3 facingHint, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        stabilizedForward = forward;
        stabilizedUp = bodyUp;
        if (root == null)
        {
            return;
        }

        int currentFrame = Time.frameCount;
        bool seenRecently =
            animalRootYawLastSeenFrameByRoot.TryGetValue(root, out int lastSeenFrame) &&
            currentFrame - lastSeenFrame <= 1;
        animalRootYawLastSeenFrameByRoot[root] = currentFrame;

        Vector3 planarForward = Vector3.ProjectOnPlane(forward, worldUp);
        if (planarForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        planarForward.Normalize();

        if (!seenRecently)
        {
            ResetAnimalRootBasisFilters(root, planarForward, bodyUp, worldUp);
            stabilizedForward = forward;
            stabilizedUp = bodyUp;
            return;
        }

        if (!animalRootYawForwardByRoot.TryGetValue(root, out Vector3 prevForward) || prevForward.sqrMagnitude < 0.000001f)
        {
            prevForward = Vector3.ProjectOnPlane(root.forward, worldUp);
        }
        if (prevForward.sqrMagnitude < 0.000001f)
        {
            ResetAnimalRootBasisFilters(root, planarForward, bodyUp, worldUp);
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

        float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : (1f / 60f);
        OneEuroVector3Filter forwardFilter = GetOrCreateAnimalRootVector3Filter(animalRootForwardFilters, root);
        Vector3 filteredForward = forwardFilter.Filter(chosenForward, dt);
        filteredForward = Vector3.ProjectOnPlane(filteredForward, worldUp);
        if (filteredForward.sqrMagnitude <= 0.000001f)
        {
            filteredForward = chosenForward;
        }
        filteredForward.Normalize();
        animalRootYawForwardByRoot[root] = filteredForward;

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

        OneEuroVector3Filter upFilter = GetOrCreateAnimalRootVector3Filter(animalRootUpFilters, root);
        Vector3 filteredUp = upFilter.Filter(projectedUp, dt);
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

    private void ResetAnimalRootBasisFilters(Transform root, Vector3 planarForward, Vector3 bodyUp, Vector3 worldUp)
    {
        animalRootYawForwardByRoot[root] = planarForward;
        GetOrCreateAnimalRootVector3Filter(animalRootForwardFilters, root).Reset(planarForward);

        Vector3 resetUp = Vector3.ProjectOnPlane(bodyUp, planarForward);
        if (resetUp.sqrMagnitude <= 0.000001f)
        {
            resetUp = Vector3.ProjectOnPlane(worldUp, planarForward);
        }
        if (resetUp.sqrMagnitude > 0.000001f)
        {
            resetUp.Normalize();
            GetOrCreateAnimalRootVector3Filter(animalRootUpFilters, root).Reset(resetUp);
        }
    }

    private static OneEuroVector3Filter GetOrCreateAnimalRootVector3Filter(Dictionary<Transform, OneEuroVector3Filter> filters, Transform root)
    {
        if (filters.TryGetValue(root, out OneEuroVector3Filter existing) && existing != null)
        {
            return existing;
        }

        OneEuroVector3Filter created = new OneEuroVector3Filter(
            AnimalRootOneEuroMinCutoffHz,
            AnimalRootOneEuroBeta,
            AnimalRootOneEuroDerivativeCutoffHz);
        filters[root] = created;
        return created;
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

        bool hasPelvis = TryGetJointPoint(jointsWorld, vis, 7, out Vector3 pelvisHub);
        bool hasWithers = TryGetJointPoint(jointsWorld, vis, 18, out Vector3 withersHub);
        bool hasHeadRoot = TryGetJointPoint(jointsWorld, vis, 24, out Vector3 headRoot);

        if (hasPelvis && hasWithers)
        {
            forward = (withersHub - pelvisHub).normalized;
        }

        if (forward.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        if (hasWithers && hasHeadRoot)
        {
            facingHint = (headRoot - withersHub).normalized;
            if (facingHint.sqrMagnitude > 0.000001f && Vector3.Dot(forward, facingHint) < 0f)
            {
                forward = -forward;
            }
        }

        bool hasLeftShoulder = TryGetJointPoint(jointsWorld, vis, 12, out Vector3 leftShoulder);
        bool hasRightShoulder = TryGetJointPoint(jointsWorld, vis, 13, out Vector3 rightShoulder);
        bool hasLeftHip = TryGetJointPoint(jointsWorld, vis, 10, out Vector3 leftHip);
        bool hasRightHip = TryGetJointPoint(jointsWorld, vis, 11, out Vector3 rightHip);

        Vector3 rightAxis = Vector3.zero;
        if (hasLeftShoulder && hasRightShoulder)
        {
            rightAxis += (rightShoulder - leftShoulder);
        }
        if (hasLeftHip && hasRightHip)
        {
            rightAxis += (rightHip - leftHip);
        }

        if (rightAxis.sqrMagnitude > 0.000001f)
        {
            rightAxis.Normalize();
            Vector3 upA = Vector3.Cross(rightAxis, forward);
            Vector3 upB = -upA;
            Vector3 preferredUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
            if (preferredUp.sqrMagnitude < 0.000001f)
            {
                preferredUp = Vector3.up;
            }

            up = Vector3.Dot(upA, preferredUp) >= Vector3.Dot(upB, preferredUp) ? upA : upB;
        }

        if (up.sqrMagnitude <= 0.000001f)
        {
            Vector3 fallbackUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
            if (fallbackUp.sqrMagnitude <= 0.000001f)
            {
                fallbackUp = Vector3.up;
            }

            up = Vector3.ProjectOnPlane(fallbackUp, forward);
        }

        if (up.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        up.Normalize();
        Vector3 right = Vector3.Cross(forward, up);
        if (right.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        right.Normalize();
        up = Vector3.Cross(right, forward).normalized;
        return up.sqrMagnitude > 0.000001f;
    }

    private static bool TryGetAnimalBodyBasisFromControl(AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
    {
        forward = Vector3.zero;
        up = Vector3.zero;
        facingHint = Vector3.zero;

        if (control.hasRoot && control.hasForwardHint)
        {
            forward = (control.forwardHintWorld - control.rootWorld).normalized;
        }
        else if (control.hasRoot && control.hasWithers)
        {
            forward = (control.withersWorld - control.rootWorld).normalized;
        }

        if (control.hasHeadRoot && control.hasHeadTip)
        {
            facingHint = (control.headTipWorld - control.headRootWorld).normalized;
            if (forward.sqrMagnitude > 0.000001f && facingHint.sqrMagnitude > 0.000001f && Vector3.Dot(forward, facingHint) < 0f)
            {
                forward = -forward;
            }
        }

        if (control.hasRoot && control.hasUpHint)
        {
            up = (control.upHintWorld - control.rootWorld).normalized;
        }

        if (forward.sqrMagnitude <= 0.000001f || up.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        up.Normalize();
        Vector3 right = Vector3.Cross(forward, up);
        if (right.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        right.Normalize();
        up = Vector3.Cross(right, forward).normalized;
        return true;
    }

    private AnimalRigCache ApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, Vector3 skeletonRoot, AnimalPoseSettings settings)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot, settings);
        if (settings.enableSkeletonScaleCorrection)
        {
            ApplyAnimalSkeletonScale(instanceRoot, jointsWorld, vis, jointCount, settings);
        }
        if (cache != null && cache.ready)
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot, false);
        }
        else
        {
            instanceRoot.position = skeletonRoot;
        }

        return cache;
    }

    private static void ApplyAnimalSkeletonScale(Transform root, Vector3[] jointsWorld, byte[] vis, int jointCount, AnimalPoseSettings settings)
    {
        if (root == null || !TryGetVisibleJointBounds(jointsWorld, vis, jointCount, out Bounds bounds))
        {
            return;
        }

        ReplaceableModel model = root.GetComponent<ReplaceableModel>();
        if (model == null)
        {
            return;
        }

        float skeletonExtent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        float modelExtent = Mathf.Max(model.baseBoundsSize.x, model.baseBoundsSize.y);
        if (skeletonExtent <= 0.0001f || modelExtent <= 0.0001f)
        {
            return;
        }

        float bboxReferenceUniform = ResolveCurrentUniformScale(root, model);
        float uniform = ClampSkeletonUniformScale((skeletonExtent / modelExtent) * model.userScale, bboxReferenceUniform, settings);
        root.localScale = model.baseLocalScale * uniform;
    }

    private void AlignAnimalRootToSkeleton(Transform instanceRoot, AnimalRigCache cache, Vector3 skeletonRoot, bool smooth)
    {
        if (instanceRoot == null || cache == null)
        {
            return;
        }

        Vector3 targetPosition = instanceRoot.position + skeletonRoot - ResolveAnimalPlacementBone(cache).position;
        if (smooth)
        {
            targetPosition = SmoothAnimalRootPosition(instanceRoot, targetPosition);
        }

        instanceRoot.position = targetPosition;
    }

    private Vector3 SmoothAnimalRootPosition(Transform root, Vector3 targetPosition)
    {
        if (root == null)
        {
            return targetPosition;
        }

        float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : (1f / 60f);
        if (!animalRootPositionFilters.TryGetValue(root, out OneEuroVector3Filter filter) || filter == null)
        {
            filter = new OneEuroVector3Filter(1.6f, 0.08f, 1.0f);
            animalRootPositionFilters[root] = filter;
            filter.Reset(targetPosition);
            return targetPosition;
        }

        return filter.Filter(targetPosition, dt);
    }

    private static Transform ResolveAnimalPlacementBone(AnimalRigCache cache)
    {
        return cache.spine ?? cache.neck ?? cache.tailBase ?? cache.root;
    }

    private void ApplyAnimalHeadPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool hasControl, AnimalControlWorldData control)
    {
        if (hasControl)
        {
            if (control.hasWithers && control.hasHeadRoot)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f);
                ApplyAnimalBoneFromPoints(cache, cache.neck, control.withersWorld, headRoot, alpha * 0.42f);
            }

            if (control.hasHeadRoot && control.hasHeadTip)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f);
                Vector3 headTip = SmoothAnimalPoseTarget(cache.head, control.headTipWorld, 2.0f, 0.08f);
                ApplyAnimalBoneFromPoints(cache, cache.head, headRoot, headTip, alpha * 0.42f);
                return;
            }
        }

        ApplyAnimalBonesFromSegment(cache, cache.neck, cache.head, jointsWorld, vis, 24, 2, alpha * 0.35f, alpha * 0.35f);
    }

    private void ApplyAnimalTailPose(AnimalRigCache cache, float alpha, bool hasControl, AnimalControlWorldData control)
    {
        if (!hasControl || cache.tailBase == null)
        {
            return;
        }

        if (control.hasTailBase && control.hasTailTip)
        {
            Vector3 tailTip = SmoothAnimalPoseTarget(cache.tailBase, control.tailTipWorld, 1.5f, 0.04f);
            ApplyAnimalBoneFromPoints(cache, cache.tailBase, control.tailBaseWorld, tailTip, alpha * 0.25f);
            return;
        }

        ApplyAnimalBoneFromChain(cache, cache.tailBase, null, null, control.tailWorld, alpha * 0.25f, false);
    }

    private void ApplyAnimalLimbPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool freezeAnimalDistal, bool hasControl, AnimalControlWorldData control)
    {
        if (hasControl)
        {
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, control.frontLeftLegWorld, alpha, false);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, control.frontRightLegWorld, alpha, false);
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, control.rearLeftLegWorld, alpha, !freezeAnimalDistal);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, control.rearRightLegWorld, alpha, !freezeAnimalDistal);
            return;
        }

        ApplyAnimalLimbIkByChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, AnimalLeftFrontChain, alpha, false);
        ApplyAnimalLimbIkByChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, AnimalRightFrontChain, alpha, false);
        ApplyAnimalLimbIkByChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, AnimalLeftRearChain, alpha, !freezeAnimalDistal);
        ApplyAnimalLimbIkByChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, AnimalRightRearChain, alpha, !freezeAnimalDistal);
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

    private void ApplyAnimalLimbIkByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        if (!TryGetJointPoint(jointsWorld, vis, chain[0], out Vector3 root) ||
            !TryGetJointPoint(jointsWorld, vis, chain[1], out Vector3 mid) ||
            !TryGetJointPoint(jointsWorld, vis, chain[2], out Vector3 end))
        {
            ApplyAnimalLimbByChain(cache, upper, lower, paw, jointsWorld, vis, chain, alpha, applyDistal);
            return;
        }

        Vector3 distal = end;
        if (applyDistal && TryGetJointPoint(jointsWorld, vis, chain[3], out Vector3 toe))
        {
            distal = toe;
        }

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, root, mid, end, distal, alpha, applyDistal);
    }

    private void ApplyAnimalLimbIkFromControlChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal)
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

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, chainWorld[rootIndex], chainWorld[midIndex], chainWorld[endIndex], distal, alpha, applyDistal);
    }

    private void ApplyAnimalTwoBoneLimbIk(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3 observedRoot, Vector3 observedMid, Vector3 observedEnd, Vector3 observedDistal, float alpha, bool applyDistal)
    {
        if (upper == null || lower == null)
        {
            return;
        }

        Vector3 root = upper.position;
        Vector3 targetEnd = SmoothAnimalPoseTarget(lower, observedEnd, 2.2f, 0.1f);
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
            Vector3 targetDistal = SmoothAnimalPoseTarget(paw, observedDistal, 2.2f, 0.1f);
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

    private Vector3 SmoothAnimalPoseTarget(Transform key, Vector3 target, float minCutoffHz, float beta)
    {
        if (key == null)
        {
            return target;
        }

        float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : (1f / 60f);
        if (!animalLimbTargetFilters.TryGetValue(key, out OneEuroVector3Filter filter) || filter == null)
        {
            filter = new OneEuroVector3Filter(minCutoffHz, beta, 1.0f);
            animalLimbTargetFilters[key] = filter;
            filter.Reset(target);
            return target;
        }

        return filter.Filter(target, dt);
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

        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 currentDir))
        {
            Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorld = deltaWorld * bone.rotation;
            float dot = Vector3.Dot(currentDir, targetDir);
            if (dot > -0.98f)
            {
                bone.rotation = Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha));
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
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }

    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (bone != null && cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            return mapped;
        }

        if (bone != null && bone.childCount > 0)
        {
            return bone.GetChild(0);
        }

        return null;
    }

    private bool TryGetBoneCenterDirectionWorld(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        if (bone == null)
        {
            return false;
        }

        Transform centerTarget = ResolveAnimalAimChild(cache, bone);
        if (centerTarget != null && IsAnimalLimbBone(cache, bone))
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

    private AnimalRigCache GetOrBuildAnimalRigCache(Transform root, AnimalPoseSettings settings)
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

        cache.neck = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.007", "neck", "DEF-spine.010" }, "neck");
        cache.head = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.009", "head.001", "DEF-spine.011" }, "head");
        cache.spine = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3", "body" }, "body", "spine", "chest", "back");
        cache.tailBase = FindBoneByTokens(bones, "tail.002", "tail");

        FillAnimalSpineFallbacks(cache, root, bones, settings);

        cache.leftFrontUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.001", "arm.001.L", "DEF-front_thigh.L" }, "arm.001.l", "def-front_thigh.l");
        cache.leftFrontLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.002", "arm.002.L", "DEF-front_shin.L" }, "arm.002.l", "def-front_shin.l");
        cache.leftFrontPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.003", "arm.003.L", "DEF-front_foot.L" }, "arm.003.l", "def-front_foot.l", "def-front_toe.l");
        cache.rightFrontUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.001", "arm.001.R", "DEF-front_thigh.R" }, "arm.001.r", "def-front_thigh.r");
        cache.rightFrontLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.002", "arm.002.R", "DEF-front_shin.R" }, "arm.002.r", "def-front_shin.r");
        cache.rightFrontPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.003", "arm.003.R", "DEF-front_foot.R" }, "arm.003.r", "def-front_foot.r", "def-front_toe.r");
        cache.leftRearUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.001", "foot.001.L", "DEF-thigh.L" }, "foot.001.l", "foot.002.l", "def-thigh.l");
        cache.leftRearLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.002", "foot.002.L", "DEF-shin.L" }, "foot.002.l", "foot.003.l", "def-shin.l");
        cache.leftRearPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.003", "foot.003.L", "DEF-foot.L" }, "foot.003.l", "foot.004.l", "def-foot.l", "def-toe.l");
        cache.rightRearUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.001", "foot.001.R", "DEF-thigh.R" }, "foot.001.r", "foot.002.r", "def-thigh.r");
        cache.rightRearLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.002", "foot.002.R", "DEF-shin.R" }, "foot.002.r", "foot.003.r", "def-shin.r");
        cache.rightRearPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.003", "foot.003.R", "DEF-foot.R" }, "foot.003.r", "foot.004.r", "def-foot.r", "def-toe.r");
        ResolveAnimalModelBasis(root, cache, settings);

        PrimeAnimalBinds(
            cache,
            cache.neck, cache.head, cache.spine, cache.tailBase,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw);

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
            cache.spine, cache.neck);

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;
        return cache;
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

        if (cache.spine == null)
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

    private static bool TryGetJointPoint(Vector3[] jointsWorld, byte[] vis, int idx, out Vector3 point)
    {
        point = Vector3.zero;
        if (jointsWorld == null || vis == null)
        {
            return false;
        }

        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length)
        {
            return false;
        }

        byte visFlag = vis[idx];
        Vector3 p = jointsWorld[idx];
        if (visFlag == 0)
        {
            return false;
        }

        if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
            float.IsNaN(p.y) || float.IsInfinity(p.y) ||
            float.IsNaN(p.z) || float.IsInfinity(p.z))
        {
            return false;
        }

        if (p.sqrMagnitude <= InvalidJointSqrMagnitudeEpsilon)
        {
            return false;
        }

        point = p;
        return true;
    }

    private static bool TryGetVisibleJointBounds(Vector3[] jointsWorld, byte[] vis, int jointCount, out Bounds bounds)
    {
        bounds = default(Bounds);
        if (jointsWorld == null || vis == null || jointCount <= 0)
        {
            return false;
        }

        bool hasAny = false;
        int count = Mathf.Min(jointCount, Mathf.Min(jointsWorld.Length, vis.Length));
        for (int i = 0; i < count; i++)
        {
            if (vis[i] == 0)
            {
                continue;
            }

            Vector3 p = jointsWorld[i];
            if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
                float.IsNaN(p.y) || float.IsInfinity(p.y) ||
                float.IsNaN(p.z) || float.IsInfinity(p.z))
            {
                continue;
            }

            if (!hasAny)
            {
                bounds = new Bounds(p, Vector3.zero);
                hasAny = true;
            }
            else
            {
                bounds.Encapsulate(p);
            }
        }

        return hasAny;
    }

    private static float ClampSkeletonUniformScale(float uniform, float referenceUniform, AnimalPoseSettings settings)
    {
        float min = Mathf.Max(0.0001f, settings.skeletonScaleMin);
        float max = Mathf.Max(min, settings.skeletonScaleMax);
        if (referenceUniform > 0.0001f)
        {
            float relMin = Mathf.Max(0.0001f, settings.skeletonScaleRelativeMin);
            float relMax = Mathf.Max(relMin, settings.skeletonScaleRelativeMax);
            min = Mathf.Max(min, referenceUniform * relMin);
            max = Mathf.Min(max, referenceUniform * relMax);
        }

        return Mathf.Clamp(uniform, min, max);
    }

    private static float ResolveCurrentUniformScale(Transform root, ReplaceableModel model)
    {
        if (root == null)
        {
            return 0f;
        }

        if (model == null)
        {
            return Mathf.Max(root.localScale.x, Mathf.Max(root.localScale.y, root.localScale.z));
        }

        Vector3 baseScale = model.baseLocalScale;
        Vector3 localScale = root.localScale;
        float sum = 0f;
        int count = 0;
        if (Mathf.Abs(baseScale.x) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.x / baseScale.x);
            count++;
        }
        if (Mathf.Abs(baseScale.y) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.y / baseScale.y);
            count++;
        }
        if (Mathf.Abs(baseScale.z) > 0.0001f)
        {
            sum += Mathf.Abs(localScale.z / baseScale.z);
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }

    private sealed class AnimalRigCache
    {
        public Transform root;
        public Transform neck;
        public Transform head;
        public Transform spine;
        public Transform tailBase;
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
        public Vector3 modelForwardLocal;
        public Vector3 modelUpLocal;
        public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
        public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
        public bool ready;
    }

    private sealed class LowPassFilter1D
    {
        private bool initialized;
        private float previousValue;

        public float Filter(float value, float alpha)
        {
            if (!initialized)
            {
                initialized = true;
                previousValue = value;
                return value;
            }

            previousValue = alpha * value + (1f - alpha) * previousValue;
            return previousValue;
        }

        public void Reset(float value)
        {
            initialized = true;
            previousValue = value;
        }
    }

    private sealed class OneEuroFilter1D
    {
        private readonly LowPassFilter1D valueFilter = new LowPassFilter1D();
        private readonly LowPassFilter1D derivativeFilter = new LowPassFilter1D();
        private readonly float minCutoff;
        private readonly float beta;
        private readonly float derivativeCutoff;
        private bool initialized;
        private float previousRawValue;

        public OneEuroFilter1D(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            minCutoff = Mathf.Max(0.0001f, minCutoffHz);
            beta = Mathf.Max(0f, betaValue);
            derivativeCutoff = Mathf.Max(0.0001f, derivativeCutoffHz);
        }

        public float Filter(float value, float deltaTime)
        {
            float dt = Mathf.Max(0.0001f, deltaTime);
            if (!initialized)
            {
                initialized = true;
                previousRawValue = value;
                valueFilter.Reset(value);
                derivativeFilter.Reset(0f);
                return value;
            }

            float derivative = (value - previousRawValue) / dt;
            previousRawValue = value;
            float filteredDerivative = derivativeFilter.Filter(derivative, ComputeAlpha(derivativeCutoff, dt));
            float cutoff = minCutoff + beta * Mathf.Abs(filteredDerivative);
            return valueFilter.Filter(value, ComputeAlpha(cutoff, dt));
        }

        public void Reset(float value)
        {
            initialized = true;
            previousRawValue = value;
            valueFilter.Reset(value);
            derivativeFilter.Reset(0f);
        }

        private static float ComputeAlpha(float cutoff, float deltaTime)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
            return 1f / (1f + tau / Mathf.Max(0.0001f, deltaTime));
        }
    }

    private sealed class OneEuroVector3Filter
    {
        private readonly OneEuroFilter1D x;
        private readonly OneEuroFilter1D y;
        private readonly OneEuroFilter1D z;

        public OneEuroVector3Filter(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            x = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            y = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            z = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
        }

        public Vector3 Filter(Vector3 value, float deltaTime)
        {
            return new Vector3(
                x.Filter(value.x, deltaTime),
                y.Filter(value.y, deltaTime),
                z.Filter(value.z, deltaTime));
        }

        public void Reset(Vector3 value)
        {
            x.Reset(value.x);
            y.Reset(value.y);
            z.Reset(value.z);
        }
    }
}

public struct AnimalPoseRequest
{
    public Transform instanceRoot;
    public Animator animator;
    public AnimalPoseWorldData pose;
    public AnimalPoseSettings settings;
    public bool freezeAnimalDistal;
    public bool enableBoneApply;
}

public struct AnimalPoseWorldData
{
    public int jointCount;
    public Vector3[] jointsWorld;
    public Vector3[] jointsCam;
    public byte[] jointVis;
    public bool hasAnimalControl;
    public AnimalControlWorldData animalControl;
    public Vector3 camOrigin;
    public Vector3 rootWorld;
}

public struct AnimalControlWorldData
{
    public bool hasRoot;
    public Vector3 rootWorld;
    public bool hasWithers;
    public Vector3 withersWorld;
    public bool hasHeadRoot;
    public Vector3 headRootWorld;
    public bool hasHeadTip;
    public Vector3 headTipWorld;
    public bool hasTailBase;
    public Vector3 tailBaseWorld;
    public bool hasTailTip;
    public Vector3 tailTipWorld;
    public bool hasForwardHint;
    public Vector3 forwardHintWorld;
    public bool hasUpHint;
    public Vector3 upHintWorld;
    public Vector3[] frontLeftLegWorld;
    public Vector3[] frontRightLegWorld;
    public Vector3[] rearLeftLegWorld;
    public Vector3[] rearRightLegWorld;
    public Vector3[] headWorld;
    public Vector3[] tailWorld;
}

public struct AnimalPoseSettings
{
    public float boneApplyAlpha;
    public bool enableAnimalLimbApply;
    public bool stabilizeAnimalRootYaw;
    public float animalRootRotateAlpha;
    public float animalRootPitchRollBlend;
    public Vector3 animalModelForwardLocal;
    public Vector3 animalModelUpLocal;
    public bool enableSkeletonScaleCorrection;
    public float skeletonScaleMin;
    public float skeletonScaleMax;
    public float skeletonScaleRelativeMin;
    public float skeletonScaleRelativeMax;
}
