using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Entry point for animal pose application and root orientation.

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, Vector3 skeletonRoot, bool freezeAnimalDistal, bool hasControl, AnimalControlWorldData control)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null || jointCount < 20)
        {
            return;
        }

        AnimalRigCache cache = ApplyAnimalSkeletonPlacement(instanceRoot, animator, jointsWorld, vis, jointCount, skeletonRoot);
        if (!cache.ready)
        {
            return;
        }

        // Animal pose application only runs for the "animal" category path.
        // Root orientation follows the full body basis instead of yaw-only heading.
        TryApplyAnimalRootOrientation(instanceRoot, cache, jointsWorld, vis, Mathf.Clamp01(AnimalRootRotateAlpha), hasControl, control);
        AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot);

        float alpha = Mathf.Clamp01(boneApplyAlpha);

        ApplyAnimalHeadPose(cache, jointsWorld, vis, alpha, hasControl, control);
        ApplyAnimalTailPose(cache, alpha, hasControl, control);

        if (!EnableAnimalLimbApply)
        {
            return;
        }

        ApplyAnimalLimbPose(cache, jointsWorld, vis, alpha, freezeAnimalDistal, hasControl, control);
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float rotateAlpha, bool hasControl, AnimalControlWorldData control)
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

        if (StabilizeAnimalRootYaw)
        {
            StabilizeAnimalRootBasis(instanceRoot, worldUp, bodyForward, bodyUp, facingHint, out stabilizedForward, out stabilizedUp);
        }

        Vector3 modelForwardLocal = cache != null && cache.modelForwardLocal.sqrMagnitude > 0.000001f
            ? cache.modelForwardLocal
            : AnimalModelForwardLocal;
        Vector3 modelUpLocal = cache != null && cache.modelUpLocal.sqrMagnitude > 0.000001f
            ? cache.modelUpLocal
            : AnimalModelUpLocal;

        Vector3 modelForward = modelForwardLocal.sqrMagnitude > 0.000001f
            ? modelForwardLocal.normalized
            : Vector3.right;
        Vector3 modelUp = modelUpLocal.sqrMagnitude > 0.000001f
            ? modelUpLocal.normalized
            : Vector3.up;

        Quaternion modelBasis = Quaternion.LookRotation(modelForward, modelUp);
        Quaternion targetBasis = Quaternion.LookRotation(stabilizedForward, stabilizedUp);
        Quaternion targetRootRot = targetBasis * Quaternion.Inverse(modelBasis);
        instanceRoot.rotation = Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha));
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

        Vector3 prevForward = Vector3.zero;
        if (!animalRootYawForwardByRoot.TryGetValue(root, out prevForward) || prevForward.sqrMagnitude < 0.000001f)
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
        Vector3 chosenForward;
        Vector3 planarHint = Vector3.ProjectOnPlane(facingHint, worldUp);
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

    private OneEuroVector3Filter GetOrCreateAnimalRootVector3Filter(System.Collections.Generic.Dictionary<Transform, OneEuroVector3Filter> filters, Transform root)
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

    private bool TryGetAnimalBodyBasis(Vector3[] jointsWorld, byte[] vis, Transform instanceRoot, bool hasControl, AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
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

    private bool TryGetAnimalBodyBasisFromControl(AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
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

    private bool TryGetAnimalSkeletonRootWorld(Vector3[] jointsWorld, byte[] vis, int jointCount, out Vector3 rootWorld)
    {
        if (jointCount > 18 && TryGetMidPoint(jointsWorld, vis, 7, 18, out rootWorld))
        {
            return true;
        }

        if (jointCount > 7 && TryGetMidPoint(jointsWorld, vis, 6, 7, out rootWorld))
        {
            return true;
        }

        if (TryGetJointPoint(jointsWorld, vis, 0, out rootWorld))
        {
            return true;
        }

        if (!TryGetVisibleJointBounds(jointsWorld, vis, jointCount, out Bounds bounds))
        {
            rootWorld = Vector3.zero;
            return false;
        }

        rootWorld = bounds.center;
        return true;
    }

    private AnimalRigCache ApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, Vector3 skeletonRoot)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot);
        if (EnableSkeletonScaleCorrection)
        {
            ApplyAnimalSkeletonScale(instanceRoot, jointsWorld, vis, jointCount);
        }
        if (cache.ready)
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot);
        }
        else
        {
            instanceRoot.position = skeletonRoot;
        }

        return cache;
    }

    private void ApplyAnimalSkeletonScale(Transform root, Vector3[] jointsWorld, byte[] vis, int jointCount)
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
        float uniform = ClampSkeletonUniformScale((skeletonExtent / modelExtent) * model.userScale, bboxReferenceUniform);
        root.localScale = model.baseLocalScale * uniform;
    }

    private void AlignAnimalRootToSkeleton(Transform instanceRoot, AnimalRigCache cache, Vector3 skeletonRoot)
    {
        if (instanceRoot == null)
        {
            return;
        }

        instanceRoot.position += skeletonRoot - ResolveAnimalPlacementBone(cache).position;
    }

    private static Transform ResolveAnimalPlacementBone(AnimalRigCache cache)
    {
        return cache.spine ?? cache.neck ?? cache.tailBase ?? cache.root;
    }
}

