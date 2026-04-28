using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Category-level pose dispatch lives here. Person and animal pipelines should stay separate;
    // shared helpers are limited to raw camera-pose to world-pose conversion.
    private static readonly Vector3 AnimalPoseAxisSign = Vector3.one;

    private struct PoseWorldData
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

    private struct AnimalControlWorldData
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

    private void TryApplyPersonPosePipeline(GameObject instance, MetaObj obj, Transform screen, int frame)
    {
        try
        {
            if (!TryBuildPersonPoseWorld(obj, screen, out PoseWorldData pose))
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
                SmoothJointsWorld(obj.trackId, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(jointSmoothingAlpha));
            }

            if (!TryGetSmpl24RootWorld(pose.jointsWorld, pose.jointVis, out Vector3 skeletonRoot))
            {
                return;
            }

            Vector3 yawAxis = screen != null ? screen.up : instance.transform.up;
            ApplyManualYawToJoints(obj.trackId, frame, pose.jointsWorld, pose.jointVis, skeletonRoot, yawAxis);

            Animator animator = instance.GetComponentInChildren<Animator>();
            HumanoidRigCache cache = null;
            if (animator != null && animator.isHuman)
            {
                cache = GetOrBuildHumanoidCache(animator);
            }

            ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
            TryApplySmpl24HumanoidPlacement(instance.transform, model, cache, pose.jointsWorld, pose.jointVis);

            if (!enableBoneApply)
            {
                return;
            }

            if (cache == null || !cache.ready)
            {
                return;
            }

            TryApplySmpl24HumanoidIk(instance.transform, cache, pose.jointsWorld, pose.jointVis, pose.camOrigin, idx);
        }
        catch
        {
        }
    }

    private void TryApplyAnimalPosePipeline(GameObject instance, MetaObj obj, Transform screen, int frame)
    {
        try
        {
            if (!TryBuildAnimalPoseWorld(obj, screen, frame, out PoseWorldData pose))
            {
                return;
            }

            bool freezeAnimalDistal =
                EnableAnimalDistalFreezeOnHighSkip &&
                (pose.hasAnimalControl
                    ? CountAnimalControlSkipSegments(pose.animalControl)
                    : CountAnimalSkipSegments(pose.jointCount, pose.jointVis, pose.jointsCam)) >= Mathf.Max(0, AnimalDistalFreezeSkipThreshold);

            if (enableJointSmoothing)
            {
                SmoothJointsWorld(obj.trackId, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(jointSmoothingAlpha));
            }

            Vector3 skeletonRoot = pose.rootWorld;

            Vector3 yawAxis = screen != null ? screen.up : instance.transform.up;
            ApplyManualYawToJoints(obj.trackId, frame, pose.jointsWorld, pose.jointVis, skeletonRoot, yawAxis);

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (!enableBoneApply)
            {
                ApplyAnimalSkeletonPlacement(instance.transform, animator, pose.jointsWorld, pose.jointVis, pose.jointCount, skeletonRoot);
                return;
            }

            ApplyAnimalSkeleton(instance.transform, animator, pose.jointsWorld, pose.jointVis, pose.jointCount, skeletonRoot, freezeAnimalDistal, pose.hasAnimalControl, pose.animalControl);
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

    private bool TryBuildPersonPoseWorld(MetaObj obj, Transform screen, out PoseWorldData pose)
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

        if (!TryGetAnchorWorld(obj, screen, out Vector3 anchorWorld))
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3[] jointsWorld = new Vector3[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            jointsWorld[i] = anchorWorld + (camRotation * obj.jointsCam[i]);
        }

        pose = new PoseWorldData
        {
            jointCount = jointCount,
            jointsWorld = jointsWorld,
            jointsCam = obj.jointsCam,
            jointVis = obj.jointsVis,
            camOrigin = camOrigin,
            rootWorld = anchorWorld
        };
        return true;
    }

    private bool TryBuildAnimalPoseWorld(MetaObj obj, Transform screen, int frame, out PoseWorldData pose)
    {
        pose = default(PoseWorldData);
        if (TryGetAnimalControlPose(frame, obj.trackId, out AnimalControlPose controlPose))
        {
            if (!TryGetPinholeBasis(screen, out Vector3 controlCamOrigin, out Quaternion controlCamRotation))
            {
                return false;
            }

            int controlJointCount = controlPose.kpCount;
            if (controlJointCount <= 0 || controlPose.jointsCamAbs == null || controlPose.jointsVis == null)
            {
                return false;
            }

            Vector3[] controlJointsWorld = new Vector3[controlJointCount];
            Vector3[] controlJointsCam = new Vector3[controlJointCount];
            for (int i = 0; i < controlJointCount; i++)
            {
                Vector3 jointCam = ApplyPoseAxisSign(controlPose.jointsCamAbs[i], AnimalPoseAxisSign);
                controlJointsCam[i] = jointCam;
                controlJointsWorld[i] = controlCamOrigin + (controlCamRotation * jointCam);
            }

            pose = new PoseWorldData
            {
                jointCount = controlJointCount,
                jointsWorld = controlJointsWorld,
                jointsCam = controlJointsCam,
                jointVis = controlPose.jointsVis,
                hasAnimalControl = true,
                animalControl = BuildAnimalControlWorldData(controlPose, controlCamOrigin, controlCamRotation),
                camOrigin = controlCamOrigin,
                rootWorld = controlCamOrigin + (controlCamRotation * ApplyPoseAxisSign(controlPose.rootCamAbs, AnimalPoseAxisSign))
            };
            return true;
        }

        if (!obj.hasSkeleton || obj.jointsCam == null || obj.jointsVis == null)
        {
            return false;
        }

        int jointCount = obj.skeletonKpCount;
        if (jointCount <= 0 || obj.jointsCam.Length < jointCount || obj.jointsVis.Length < jointCount)
        {
            return false;
        }

        if (!TryGetAnchorWorld(obj, screen, out Vector3 anchorWorld))
        {
            return false;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return false;
        }

        Vector3[] jointsWorld = new Vector3[jointCount];
        Vector3[] jointsCam = new Vector3[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            Vector3 jointCam = ApplyPoseAxisSign(obj.jointsCam[i], AnimalPoseAxisSign);
            jointsCam[i] = jointCam;
            jointsWorld[i] = anchorWorld + (camRotation * jointCam);
        }

        pose = new PoseWorldData
        {
            jointCount = jointCount,
            jointsWorld = jointsWorld,
            jointsCam = jointsCam,
            jointVis = obj.jointsVis,
            hasAnimalControl = false,
            camOrigin = camOrigin,
            rootWorld = anchorWorld
        };
        return true;
    }

    private AnimalControlWorldData BuildAnimalControlWorldData(AnimalControlPose controlPose, Vector3 camOrigin, Quaternion camRotation)
    {
        AnimalControlWorldData world = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.rootCamAbs, AnimalPoseAxisSign)),
            hasWithers = controlPose.hasWithersCamAbs,
            hasHeadRoot = controlPose.hasHeadRootCamAbs,
            hasHeadTip = controlPose.hasHeadTipCamAbs,
            hasTailBase = controlPose.hasTailBaseCamAbs,
            hasTailTip = controlPose.hasTailTipCamAbs,
            hasForwardHint = controlPose.hasForwardHintCamAbs,
            hasUpHint = controlPose.hasUpHintCamAbs
        };

        if (controlPose.hasWithersCamAbs)
        {
            world.withersWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.withersCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasHeadRootCamAbs)
        {
            world.headRootWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.headRootCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasHeadTipCamAbs)
        {
            world.headTipWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.headTipCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasTailBaseCamAbs)
        {
            world.tailBaseWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.tailBaseCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasTailTipCamAbs)
        {
            world.tailTipWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.tailTipCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasForwardHintCamAbs)
        {
            world.forwardHintWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.forwardHintCamAbs, AnimalPoseAxisSign));
        }
        if (controlPose.hasUpHintCamAbs)
        {
            world.upHintWorld = camOrigin + (camRotation * ApplyPoseAxisSign(controlPose.upHintCamAbs, AnimalPoseAxisSign));
        }

        world.frontLeftLegWorld = TransformAnimalControlChain(controlPose.frontLeftLegChainCamAbs, camOrigin, camRotation);
        world.frontRightLegWorld = TransformAnimalControlChain(controlPose.frontRightLegChainCamAbs, camOrigin, camRotation);
        world.rearLeftLegWorld = TransformAnimalControlChain(controlPose.rearLeftLegChainCamAbs, camOrigin, camRotation);
        world.rearRightLegWorld = TransformAnimalControlChain(controlPose.rearRightLegChainCamAbs, camOrigin, camRotation);
        world.headWorld = TransformAnimalControlChain(controlPose.headChainCamAbs, camOrigin, camRotation);
        world.tailWorld = TransformAnimalControlChain(controlPose.tailChainCamAbs, camOrigin, camRotation);
        return world;
    }

    private Vector3[] TransformAnimalControlChain(Vector3[] chainCamAbs, Vector3 camOrigin, Quaternion camRotation)
    {
        if (chainCamAbs == null || chainCamAbs.Length == 0)
        {
            return null;
        }

        Vector3[] chainWorld = new Vector3[chainCamAbs.Length];
        for (int i = 0; i < chainCamAbs.Length; i++)
        {
            chainWorld[i] = camOrigin + (camRotation * ApplyPoseAxisSign(chainCamAbs[i], AnimalPoseAxisSign));
        }

        return chainWorld;
    }

    private int CountAnimalControlSkipSegments(AnimalControlWorldData control)
    {
        int skip = 0;
        skip += CountAnimalControlChainSkips(control.frontLeftLegWorld);
        skip += CountAnimalControlChainSkips(control.frontRightLegWorld);
        skip += CountAnimalControlChainSkips(control.rearLeftLegWorld);
        skip += CountAnimalControlChainSkips(control.rearRightLegWorld);
        return skip;
    }

    private static int CountAnimalControlChainSkips(Vector3[] chainWorld)
    {
        if (chainWorld == null || chainWorld.Length < 4)
        {
            return 3;
        }

        int segmentCount = chainWorld.Length >= 5 ? 3 : chainWorld.Length - 1;
        int start = chainWorld.Length >= 5 ? 1 : 0;
        int skip = 0;
        for (int i = 0; i < segmentCount; i++)
        {
            Vector3 a = chainWorld[start + i];
            Vector3 b = chainWorld[start + i + 1];
            if ((b - a).sqrMagnitude <= 0.000001f)
            {
                skip++;
            }
        }
        return skip;
    }

    private bool TryGetAnchorWorld(MetaObj obj, Transform screen, out Vector3 anchorWorld)
    {
        anchorWorld = Vector3.zero;
        Transform resolvedScreen = screen;
        int uEye = obj.anchorU;
        if (!ResolveAnchorToScreen(obj.anchorU, out Transform anchorScreen, out uEye, out _))
        {
            return false;
        }

        if (resolvedScreen == null)
        {
            resolvedScreen = anchorScreen;
        }

        if (resolvedScreen == null || manifest == null)
        {
            return false;
        }

        float uEyeF = Mathf.Clamp(uEye, 0f, manifest.eye_w - 1f);
        float vEyeF = Mathf.Clamp(obj.anchorV, 0f, manifest.eye_h - 1f);
        anchorWorld = AnchorUvZToWorldPinhole(resolvedScreen, uEyeF, vEyeF, obj.anchorZ);
        return true;
    }

    private static Vector3 ApplyPoseAxisSign(Vector3 point, Vector3 axisSign)
    {
        return new Vector3(point.x * axisSign.x, point.y * axisSign.y, point.z * axisSign.z);
    }

    private float ClampSkeletonUniformScale(float uniform, float referenceUniform = 0f)
    {
        float min = Mathf.Max(0.0001f, SkeletonScaleMin);
        float max = Mathf.Max(min, SkeletonScaleMax);
        if (referenceUniform > 0.0001f)
        {
            float relMin = Mathf.Max(0.0001f, SkeletonScaleRelativeMin);
            float relMax = Mathf.Max(relMin, SkeletonScaleRelativeMax);
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
