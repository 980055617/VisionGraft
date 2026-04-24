using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Entry point for animal pose application and root orientation.

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, Vector3 skeletonRoot, bool freezeAnimalDistal)
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
        // Keep root heading as yaw-only against world-up.
        TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, Mathf.Clamp01(animalRootRotateAlpha));
        AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot);

        float alpha = Mathf.Clamp01(boneApplyAlpha);

        ApplyAnimalHeadPose(cache, jointsWorld, vis, alpha);

        if (!enableAnimalLimbApply)
        {
            return;
        }

        ApplyAnimalLimbPose(cache, jointsWorld, vis, alpha, freezeAnimalDistal);
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, Vector3[] jointsWorld, byte[] vis, float rotateAlpha)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null)
        {
            return;
        }

        if (!TryGetAnimalBodyDirection(jointsWorld, vis, out Vector3 bodyForward))
        {
            return;
        }

        Vector3 up = Vector3.up;
        Vector3 planarForward = Vector3.ProjectOnPlane(bodyForward, up);
        if (planarForward.sqrMagnitude < 0.000001f)
        {
            planarForward = bodyForward.normalized;
        }
        else
        {
            planarForward.Normalize();
        }

        if (stabilizeAnimalRootYaw)
        {
            planarForward = StabilizeAnimalYawForward(instanceRoot, up, planarForward);
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

    private Vector3 StabilizeAnimalYawForward(Transform root, Vector3 up, Vector3 forward)
    {
        if (root == null)
        {
            return forward;
        }

        Vector3 prevForward = Vector3.zero;
        if (!animalRootYawForwardByRoot.TryGetValue(root, out prevForward) || prevForward.sqrMagnitude < 0.000001f)
        {
            prevForward = Vector3.ProjectOnPlane(root.forward, up);
        }
        if (prevForward.sqrMagnitude < 0.000001f)
        {
            return forward;
        }
        prevForward.Normalize();

        Vector3 candA = Vector3.ProjectOnPlane(forward, up);
        if (candA.sqrMagnitude < 0.000001f)
        {
            return forward;
        }
        candA.Normalize();

        Vector3 candB = -candA;
        Vector3 chosen = Vector3.Dot(prevForward, candA) >= Vector3.Dot(prevForward, candB) ? candA : candB;

        float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : (1f / 60f);
        float maxStep = Mathf.Max(1f, animalRootYawMaxDegreesPerSecond) * dt;
        float signedAngle = Vector3.SignedAngle(prevForward, chosen, up);
        float clampedAngle = Mathf.Clamp(signedAngle, -maxStep, maxStep);
        Vector3 stabilized = Quaternion.AngleAxis(clampedAngle, up) * prevForward;
        if (stabilized.sqrMagnitude < 0.000001f)
        {
            stabilized = chosen;
        }
        stabilized.Normalize();
        animalRootYawForwardByRoot[root] = stabilized;
        return stabilized;
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

    private AnimalRigCache ApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, Vector3 skeletonRoot)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot);
        if (enableSkeletonScaleCorrection)
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

