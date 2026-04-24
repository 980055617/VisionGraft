using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Category-level pose dispatch lives here. Person and animal pipelines should stay separate;
    // shared helpers are limited to raw camera-pose to world-pose conversion.

    private struct PoseWorldData
    {
        public int jointCount;
        public Vector3[] jointsWorld;
        public Vector3 camOrigin;
    }

    private void TryApplyPersonPosePipeline(GameObject instance, MetaObj obj, Vector3 rootWorld, Transform screen, int frame)
    {
        try
        {
            if (!TryBuildPoseWorld(obj, rootWorld, screen, ResolvePoseAxisSign(personBoneAxisSign), out PoseWorldData pose))
            {
                return;
            }

            if (pose.jointCount != 24)
            {
                return;
            }

            SkeletonIndices idx = MetrabsSmpl24Indices;

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, pose.jointsWorld, obj.jointsVis, Mathf.Clamp01(jointSmoothingAlpha));
            }

            if (!TryGetSmpl24RootWorld(pose.jointsWorld, obj.jointsVis, out Vector3 skeletonRoot))
            {
                return;
            }

            Vector3 yawAxis = screen != null ? screen.up : instance.transform.up;
            ApplyManualYawToJoints(obj.trackId, frame, pose.jointsWorld, obj.jointsVis, skeletonRoot, yawAxis);

            Animator animator = instance.GetComponentInChildren<Animator>();
            HumanoidRigCache cache = null;
            if (animator != null && animator.isHuman)
            {
                cache = GetOrBuildHumanoidCache(animator);
            }

            ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
            TryApplySmpl24HumanoidPlacement(instance.transform, model, cache, pose.jointsWorld, obj.jointsVis);

            if (!enableBoneApply)
            {
                return;
            }

            if (cache == null || !cache.ready)
            {
                return;
            }

            TryApplySmpl24HumanoidIk(instance.transform, cache, pose.jointsWorld, obj.jointsVis, pose.camOrigin, idx);
        }
        catch
        {
        }
    }

    private void TryApplyAnimalPosePipeline(GameObject instance, MetaObj obj, Vector3 rootWorld, Transform screen, int frame)
    {
        try
        {
            if (!TryBuildPoseWorld(obj, rootWorld, screen, ResolvePoseAxisSign(animalBoneAxisSign), out PoseWorldData pose))
            {
                return;
            }

            bool freezeAnimalDistal =
                enableAnimalDistalFreezeOnHighSkip &&
                CountAnimalSkipSegments(pose.jointCount, obj.jointsVis, obj.jointsCam) >= Mathf.Max(0, animalDistalFreezeSkipThreshold);

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, pose.jointsWorld, obj.jointsVis, Mathf.Clamp01(jointSmoothingAlpha));
            }

            if (!TryGetAnimalSkeletonRootWorld(pose.jointsWorld, obj.jointsVis, pose.jointCount, out Vector3 skeletonRoot))
            {
                return;
            }

            Vector3 yawAxis = screen != null ? screen.up : instance.transform.up;
            ApplyManualYawToJoints(obj.trackId, frame, pose.jointsWorld, obj.jointsVis, skeletonRoot, yawAxis);

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (!enableBoneApply)
            {
                ApplyAnimalSkeletonPlacement(instance.transform, animator, pose.jointsWorld, obj.jointsVis, pose.jointCount, skeletonRoot);
                return;
            }

            ApplyAnimalSkeleton(instance.transform, animator, pose.jointsWorld, obj.jointsVis, pose.jointCount, skeletonRoot, freezeAnimalDistal);
        }
        catch
        {
        }
    }

    private static bool IsAnimalSegmentUsable(int idxA, int idxB, int jointCount, byte[] vis, Vector3[] jointsCam)
    {
        if (idxA < 0 || idxB < 0 || idxA >= jointCount || idxB >= jointCount)
        {
            return false;
        }

        if (vis == null || jointsCam == null || idxA >= vis.Length || idxB >= vis.Length || idxA >= jointsCam.Length || idxB >= jointsCam.Length)
        {
            return false;
        }

        if (vis[idxA] == 0 || vis[idxB] == 0)
        {
            return false;
        }

        return !Mathf.Approximately(jointsCam[idxA].z, 0f) && !Mathf.Approximately(jointsCam[idxB].z, 0f);
    }

    private static int CountAnimalSkipSegments(int jointCount, byte[] vis, Vector3[] jointsCam)
    {
        int skip = 0;
        for (int i = 0; i + 1 < AnimalLeftFrontChain.Length; i++)
        {
            if (!IsAnimalSegmentUsable(AnimalLeftFrontChain[i], AnimalLeftFrontChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }

        for (int i = 0; i + 1 < AnimalRightFrontChain.Length; i++)
        {
            if (!IsAnimalSegmentUsable(AnimalRightFrontChain[i], AnimalRightFrontChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }

        for (int i = 0; i + 1 < AnimalLeftRearChain.Length; i++)
        {
            if (!IsAnimalSegmentUsable(AnimalLeftRearChain[i], AnimalLeftRearChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }

        for (int i = 0; i + 1 < AnimalRightRearChain.Length; i++)
        {
            if (!IsAnimalSegmentUsable(AnimalRightRearChain[i], AnimalRightRearChain[i + 1], jointCount, vis, jointsCam)) skip++;
        }

        return skip;
    }

    private bool TryBuildPoseWorld(MetaObj obj, Vector3 rootWorld, Transform screen, Vector3 axisSign, out PoseWorldData pose)
    {
        pose = default(PoseWorldData);
        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return false;
        }

        int jointCount = obj.skeletonKpCount;
        if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        bool useAbsoluteSkeletonRoot =
            obj.hasSkeletonRootCam &&
            (IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId));
        if (useAbsoluteSkeletonRoot && remapSkeletonDepthToScreenRange)
        {
            Vector3 rootCam = ApplyPoseAxisSign(obj.skeletonRootCam, axisSign);
            Vector3[] jointsCamAbs = new Vector3[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                jointsCamAbs[i] = rootCam + ApplyPoseAxisSign(obj.jointsCam[i], axisSign);
            }

            if (TryGetVisibleCameraDepthRange(jointsCamAbs, obj.jointsVis, jointCount, out float sourceNear, out float sourceFar) &&
                TryGetDisplayDepthRange(out float displayNear, out float displayFar))
            {
                float targetRootZ = RemapDepthScalar(rootCam.z, sourceNear, sourceFar, displayNear, displayFar);
                float uniformDepthScale = Mathf.Abs(rootCam.z) > 0.0001f ? targetRootZ / rootCam.z : 1f;
                Vector3[] remappedWorld = new Vector3[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    Vector3 remappedCam = jointsCamAbs[i] * uniformDepthScale;
                    remappedWorld[i] = camOrigin + (camRotation * remappedCam);
                }

                pose = new PoseWorldData
                {
                    jointCount = jointCount,
                    jointsWorld = remappedWorld,
                    camOrigin = camOrigin
                };
                return true;
            }
        }

        bool rootRel = obj.hasSkeletonRootCam || IsManifestJointsSpaceRootRelative();
        Vector3 rootBaseWorld = rootWorld;
        if (useAbsoluteSkeletonRoot)
        {
            Vector3 rootCam = ApplyPoseAxisSign(obj.skeletonRootCam, axisSign);
            rootBaseWorld = camOrigin + (camRotation * rootCam);
        }

        Vector3[] jointsWorld = new Vector3[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            Vector3 joint = ApplyPoseAxisSign(obj.jointsCam[i], axisSign);
            jointsWorld[i] = rootRel
                ? rootBaseWorld + (camRotation * joint)
                : camOrigin + (camRotation * joint);
        }

        pose = new PoseWorldData
        {
            jointCount = jointCount,
            jointsWorld = jointsWorld,
            camOrigin = camOrigin
        };
        return true;
    }

    private Vector3 ResolvePoseAxisSign(Vector3 axisSign)
    {
        if (axisSign.sqrMagnitude > 0.000001f)
        {
            return axisSign;
        }

        return Vector3.one;
    }

    private static Vector3 ApplyPoseAxisSign(Vector3 point, Vector3 axisSign)
    {
        return new Vector3(point.x * axisSign.x, point.y * axisSign.y, point.z * axisSign.z);
    }

    private bool TryGetDisplayDepthRange(out float nearDepth, out float farDepth)
    {
        nearDepth = Mathf.Max(0.001f, minDistanceFromHeadMeters);
        farDepth = Mathf.Max(0.001f, screenDistanceMeters - Mathf.Max(0f, epsilonMeters));
        if (farDepth <= nearDepth + 0.001f)
        {
            farDepth = nearDepth + 0.001f;
        }
        return true;
    }

    private static bool TryGetVisibleCameraDepthRange(Vector3[] pointsCam, byte[] vis, int jointCount, out float nearDepth, out float farDepth)
    {
        nearDepth = 0f;
        farDepth = 0f;
        if (pointsCam == null || vis == null || jointCount <= 0)
        {
            return false;
        }

        bool hasAny = false;
        int count = Mathf.Min(jointCount, Mathf.Min(pointsCam.Length, vis.Length));
        for (int i = 0; i < count; i++)
        {
            if (vis[i] == 0)
            {
                continue;
            }

            float z = pointsCam[i].z;
            if (float.IsNaN(z) || float.IsInfinity(z))
            {
                continue;
            }

            if (!hasAny)
            {
                nearDepth = z;
                farDepth = z;
                hasAny = true;
            }
            else
            {
                nearDepth = Mathf.Min(nearDepth, z);
                farDepth = Mathf.Max(farDepth, z);
            }
        }

        return hasAny;
    }

    private static float RemapDepthScalar(float depth, float sourceNear, float sourceFar, float displayNear, float displayFar)
    {
        float sourceSpan = sourceFar - sourceNear;
        float t = sourceSpan > 0.0001f
            ? Mathf.InverseLerp(sourceNear, sourceFar, depth)
            : 0.5f;
        return Mathf.Lerp(displayNear, displayFar, Mathf.Clamp01(t));
    }

    private float ClampSkeletonUniformScale(float uniform, float referenceUniform = 0f)
    {
        float min = Mathf.Max(0.0001f, skeletonScaleMin);
        float max = Mathf.Max(min, skeletonScaleMax);
        if (referenceUniform > 0.0001f)
        {
            float relMin = Mathf.Max(0.0001f, skeletonScaleRelativeMin);
            float relMax = Mathf.Max(relMin, skeletonScaleRelativeMax);
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
}
