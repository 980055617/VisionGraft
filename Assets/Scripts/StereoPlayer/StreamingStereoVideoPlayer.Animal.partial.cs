using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Entry point for animal pose application and root orientation.

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, byte categoryId, Transform screen, bool freezeDogDistal)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null || jointCount < 20)
        {
            return;
        }

        AnimalRigCache cache = TryApplyAnimalSkeletonPlacement(instanceRoot, animator, jointsWorld, vis, jointCount);
        if (cache == null || !cache.ready)
        {
            return;
        }

        // Root orientation:
        // - dog: yaw-only style using world-up (screen tilt is ignored)
        // - others: previous behavior using screen-up
        bool isAnimalCategory = IsCategoryAnimal(categoryId);
        float rootRotateAlpha = isAnimalCategory
            ? GetEffectiveDogRootRotateAlpha()
            : Mathf.Clamp01(animalRootRotateAlpha);
        if (isAnimalCategory)
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, null, rootRotateAlpha);
        }
        else
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, screen, rootRotateAlpha);
        }

        if (TryGetAnimalSkeletonRootWorld(jointsWorld, vis, jointCount, out Vector3 skeletonRoot))
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot);
        }

        float alpha = isAnimalCategory
            ? GetEffectiveDogBoneApplyAlpha()
            : Mathf.Clamp01(boneApplyAlpha);
        // Dog is handled in a reduced-drive mode:
        // root heading + head + limbs (no spine drive).
        if (isAnimalCategory)
        {
            // Head only: fixed mapping (Throat -> Nose), no fallback.
            ApplyAnimalBoneFromJoints(cache, cache.neck, jointsWorld, vis, 5, 4, alpha * 0.65f);
            ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, 5, 4, alpha * 0.65f);

            if (!enableAnimalLimbApply)
            {
                return;
            }

            // Front legs: fixed mapping (no distal apply).
            // left front:
            //   001 (upper) <- 8 -> 12
            //   002 (lower) <- 12 -> 16
            //   003 (paw)   <- untouched
            ApplyAnimalBoneFromJoints(cache, cache.leftFrontUpper, jointsWorld, vis, 8, 12, alpha * 0.9f);
            ApplyAnimalBoneFromJoints(cache, cache.leftFrontLower, jointsWorld, vis, 12, 16, alpha * 0.85f);
            // right front:
            //   001 (upper) <- 9 -> 13
            //   002 (lower) <- 13 -> 17
            //   003 (paw)   <- untouched
            ApplyAnimalBoneFromJoints(cache, cache.rightFrontUpper, jointsWorld, vis, 9, 13, alpha * 0.9f);
            ApplyAnimalBoneFromJoints(cache, cache.rightFrontLower, jointsWorld, vis, 13, 17, alpha * 0.85f);

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

    private float GetEffectiveDogBoneApplyAlpha()
    {
        return Mathf.Clamp01(boneApplyAlpha);
    }

    private float GetEffectiveDogRootRotateAlpha()
    {
        return Mathf.Clamp01(animalRootRotateAlpha);
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, Vector3[] jointsWorld, byte[] vis, Transform screen, float rotateAlpha)
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
        instanceRoot.rotation = Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha));
    }

    private bool TryGetAnimalBodyDirection(Vector3[] jointsWorld, byte[] vis, out Vector3 forward)
    {
        forward = Vector3.zero;
        bool hasRear = TryGetJointPoint(jointsWorld, vis, 6, out Vector3 rearHub);   // TailBase(hip)
        bool hasWithers = TryGetJointPoint(jointsWorld, vis, 7, out Vector3 withers);

        if (hasRear && hasWithers)
        {
            forward = (withers - rearHub).normalized;
        }

        return forward.sqrMagnitude > 0.000001f;
    }

    private bool TryGetAnimalSkeletonRootWorld(Vector3[] jointsWorld, byte[] vis, int jointCount, out Vector3 rootWorld)
    {
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

    private AnimalRigCache TryApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount)
    {
        if (instanceRoot == null)
        {
            return null;
        }

        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot);
        if (TryGetAnimalSkeletonRootWorld(jointsWorld, vis, jointCount, out Vector3 skeletonRoot))
        {
            if (enableSkeletonScaleCorrection)
            {
                ApplyAnimalSkeletonScale(instanceRoot, jointsWorld, vis, jointCount);
            }
            if (cache == null || !cache.ready)
            {
                instanceRoot.position = skeletonRoot;
            }
            else
            {
                AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot);
            }
        }

        if (cache == null || !cache.ready)
        {
            return cache;
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

        Transform rootBone = cache != null ? cache.root : null;
        if (rootBone != null)
        {
            instanceRoot.position += skeletonRoot - rootBone.position;
            return;
        }

        instanceRoot.position = skeletonRoot;
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

}

