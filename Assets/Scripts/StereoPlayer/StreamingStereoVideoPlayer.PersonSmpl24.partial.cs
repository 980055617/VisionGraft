using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int SmplPelvis = 0;
    private const int SmplLeftHip = 1;
    private const int SmplRightHip = 2;
    private const int SmplLeftKnee = 4;
    private const int SmplRightKnee = 5;
    private const int SmplLeftAnkle = 7;
    private const int SmplRightAnkle = 8;
    private const int SmplLeftFoot = 10;
    private const int SmplRightFoot = 11;
    private const int SmplNeck = 12;
    private const int SmplHead = 15;
    private const int SmplLeftShoulder = 16;
    private const int SmplRightShoulder = 17;
    private const int SmplLeftElbow = 18;
    private const int SmplRightElbow = 19;
    private const int SmplLeftWrist = 20;
    private const int SmplRightWrist = 21;

    private bool TryApplySmpl24HumanoidIk(
        Transform root,
        HumanoidRigCache cache,
        Vector3[] jointsWorld,
        byte[] vis,
        Vector3 camOrigin,
        SkeletonIndices idx)
    {
        if (root == null || cache == null || !cache.ready || jointsWorld == null || vis == null || jointsWorld.Length < 24 || vis.Length < 24)
        {
            return false;
        }

        if (EnableYawDepthDisambiguation)
        {
            ApplyYawDepthDisambiguation(jointsWorld, vis, idx, root, camOrigin, Mathf.Clamp01(YawDepthBlend));
        }

        float limbAlpha = Mathf.Clamp01(Smpl24LimbIkAlpha * boneApplyAlpha);
        bool appliedAny = false;

        string rootKey = root.GetInstanceID().ToString();
        appliedAny |= ApplySmpl24TwoBoneIk(cache,
            rootKey,
            "LeftArm",
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            jointsWorld,
            vis,
            SmplLeftShoulder,
            SmplLeftElbow,
            SmplLeftWrist,
            limbAlpha);

        appliedAny |= ApplySmpl24TwoBoneIk(cache,
            rootKey,
            "RightArm",
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            jointsWorld,
            vis,
            SmplRightShoulder,
            SmplRightElbow,
            SmplRightWrist,
            limbAlpha);

        appliedAny |= ApplySmpl24TwoBoneIk(cache,
            rootKey,
            "LeftLeg",
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            jointsWorld,
            vis,
            SmplLeftHip,
            SmplLeftKnee,
            SmplLeftAnkle,
            limbAlpha);

        appliedAny |= ApplySmpl24TwoBoneIk(cache,
            rootKey,
            "RightLeg",
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            jointsWorld,
            vis,
            SmplRightHip,
            SmplRightKnee,
            SmplRightAnkle,
            limbAlpha);

        ApplySmpl24TorsoAndHead(cache, jointsWorld, vis);
        ApplySmpl24Feet(cache, jointsWorld, vis, limbAlpha * 0.45f);

        return appliedAny;
    }

    private bool TryGetSmpl24RootWorld(Vector3[] jointsWorld, byte[] vis, out Vector3 rootWorld)
    {
        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplPelvis, out rootWorld))
        {
            return true;
        }

        return TryGetMidPoint(jointsWorld, vis, SmplLeftHip, SmplRightHip, out rootWorld);
    }

    private bool TryApplySmpl24HumanoidPlacement(
        Transform root,
        ReplaceableModel model,
        HumanoidRigCache cache,
        Vector3[] jointsWorld,
        byte[] vis,
        bool resetLocalRotations = true,
        bool hasRootRotationOverride = false,
        Quaternion rootRotationOverride = default(Quaternion))
    {
        if (root == null || jointsWorld == null || vis == null || jointsWorld.Length < 24 || vis.Length < 24)
        {
            return false;
        }

        if (!TryGetSmpl24RootWorld(jointsWorld, vis, out Vector3 skeletonRoot))
        {
            return false;
        }

        if (EnableSkeletonScaleCorrection)
        {
            ApplySmpl24SkeletonScale(root, model, jointsWorld, vis);
        }
        if (hasRootRotationOverride && IsFinite(rootRotationOverride))
        {
            TrackPlacementWriter.Apply(
                root,
                TrackPlacementCommand.RotationOnly(
                    root.position,
                    Quaternion.Slerp(root.rotation, rootRotationOverride, Mathf.Clamp01(Smpl24RootRotateAlpha)),
                    root.localScale));
        }
        else
        {
            TryApplySmpl24RootOrientation(root, jointsWorld, vis);
        }

        if (cache != null && cache.ready)
        {
            if (resetLocalRotations)
            {
                ResetHumanoidLocalRotations(cache);
            }
            AlignHumanoidHipsToSmplRoot(root, cache, skeletonRoot);
        }
        else
        {
            TrackPlacementWriter.Apply(
                root,
                TrackPlacementCommand.PositionOnly(
                    skeletonRoot,
                    root.rotation,
                    root.localScale));
        }

        return true;
    }

    private void ApplySmpl24SkeletonScale(Transform root, ReplaceableModel model, Vector3[] jointsWorld, byte[] vis)
    {
        if (root == null || model == null)
        {
            return;
        }

        float modelHeight = model.GetModelHeightMeters();
        if (modelHeight <= 0.0001f || !TryGetSmpl24SkeletonHeight(jointsWorld, vis, out float skeletonHeight))
        {
            return;
        }

        float bboxReferenceUniform = ResolveCurrentUniformScale(root, model);
        float uniform = ClampSkeletonUniformScale((skeletonHeight / modelHeight) * model.userScale, bboxReferenceUniform);
        TrackPlacementWriter.Apply(
            root,
            TrackPlacementCommand.LocalScaleOnly(
                root.position,
                root.rotation,
                model.baseLocalScale * uniform));
    }

    private bool TryGetSmpl24SkeletonHeight(Vector3[] jointsWorld, byte[] vis, out float height)
    {
        height = 0f;
        if (!TryGetMidPoint(jointsWorld, vis, SmplLeftHip, SmplRightHip, out Vector3 hipsMid) ||
            !TryGetMidPoint(jointsWorld, vis, SmplLeftShoulder, SmplRightShoulder, out Vector3 shouldersMid))
        {
            return false;
        }

        Vector3 up = shouldersMid - hipsMid;
        if (up.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        up.Normalize();

        bool hasAny = false;
        float min = 0f;
        float max = 0f;
        int count = Mathf.Min(24, Mathf.Min(jointsWorld.Length, vis.Length));
        for (int i = 0; i < count; i++)
        {
            if (!TrackedJointPoints.TryGet(jointsWorld, vis, i, out Vector3 p))
            {
                continue;
            }

            float d = Vector3.Dot(p, up);
            if (!hasAny)
            {
                min = d;
                max = d;
                hasAny = true;
            }
            else
            {
                min = Mathf.Min(min, d);
                max = Mathf.Max(max, d);
            }
        }

        height = max - min;
        return hasAny && height > 0.0001f;
    }

    private void AlignHumanoidHipsToSmplRoot(Transform root, HumanoidRigCache cache, Vector3 skeletonRoot)
    {
        if (root == null || cache == null)
        {
            return;
        }

        if (cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hips) && hips != null)
        {
            TrackPlacementWriter.ApplyAnchorPosition(root, hips, skeletonRoot);
            return;
        }

        TrackPlacementWriter.Apply(
            root,
            TrackPlacementCommand.PositionOnly(
                skeletonRoot,
                root.rotation,
                root.localScale));
    }

    private void TryApplySmpl24RootOrientation(Transform root, Vector3[] jointsWorld, byte[] vis)
    {
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftHip, out Vector3 leftHip) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightHip, out Vector3 rightHip) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftShoulder, out Vector3 leftShoulder) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightShoulder, out Vector3 rightShoulder))
        {
            return;
        }

        Vector3 hipsMid = (leftHip + rightHip) * 0.5f;
        Vector3 shouldersMid = (leftShoulder + rightShoulder) * 0.5f;
        Vector3 up = shouldersMid - hipsMid;
        if (up.sqrMagnitude < 0.000001f)
        {
            return;
        }
        up.Normalize();

        Vector3 right = ((rightHip - leftHip) + (rightShoulder - leftShoulder)) * 0.5f;
        right = Vector3.ProjectOnPlane(right, up);
        if (right.sqrMagnitude < 0.000001f)
        {
            return;
        }
        right.Normalize();

        Vector3 forward = Vector3.Cross(right, up);
        if (forward.sqrMagnitude < 0.000001f)
        {
            return;
        }
        forward.Normalize();

        if (StabilizePersonRootYaw)
        {
            forward = StabilizeHumanoidYawForward(root, up, forward);
        }

        Quaternion targetRoot = Quaternion.LookRotation(forward, up);
        TrackPlacementWriter.Apply(
            root,
            TrackPlacementCommand.RotationOnly(
                root.position,
                Quaternion.Slerp(root.rotation, targetRoot, Mathf.Clamp01(Smpl24RootRotateAlpha)),
                root.localScale));
    }

    private Vector3 StabilizeHumanoidYawForward(Transform root, Vector3 up, Vector3 forward)
    {
        if (root == null)
        {
            return forward;
        }

        Vector3 prevForward = Vector3.zero;
        if (!personRootYawForwardByRoot.TryGetValue(root, out prevForward) || prevForward.sqrMagnitude < 0.000001f)
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

        float dt = GetRuntimeTickContext().deltaTime;
        float maxStep = Mathf.Max(1f, PersonRootYawMaxDegreesPerSecond) * dt;
        float signedAngle = Vector3.SignedAngle(prevForward, chosen, up);
        float clampedAngle = Mathf.Clamp(signedAngle, -maxStep, maxStep);
        Vector3 stabilized = Quaternion.AngleAxis(clampedAngle, up) * prevForward;
        if (stabilized.sqrMagnitude < 0.000001f)
        {
            stabilized = chosen;
        }
        stabilized.Normalize();
        personRootYawForwardByRoot[root] = stabilized;
        return stabilized;
    }

    private void ResetHumanoidLocalRotations(HumanoidRigCache cache)
    {
        foreach (var kv in cache.bindRotLocal)
        {
            if (cache.bones.TryGetValue(kv.Key, out Transform bone) && bone != null)
            {
                PoseTransformWriter.ApplyLocalRotation(bone, kv.Value);
            }
        }
    }

    private bool ApplySmpl24TwoBoneIk(
        HumanoidRigCache cache,
        string rootKey,
        string limbKey,
        HumanBodyBones upperId,
        HumanBodyBones lowerId,
        HumanBodyBones endId,
        Vector3[] jointsWorld,
        byte[] vis,
        int rootIdx,
        int midIdx,
        int targetIdx,
        float alpha)
    {
        if (!cache.bones.TryGetValue(upperId, out Transform upper) ||
            !cache.bones.TryGetValue(lowerId, out Transform lower) ||
            !cache.bones.TryGetValue(endId, out Transform end))
        {
            return false;
        }

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, rootIdx, out Vector3 rootHint) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, midIdx, out Vector3 midHint) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, targetIdx, out Vector3 target))
        {
            return false;
        }

        if (!TrySolveHumanoidTwoBoneTargets(upper, lower, end, rootHint, midHint, target, BuildHumanoidLimbKey(rootKey, limbKey), out Vector3 solvedMid, out Vector3 solvedEnd))
        {
            ApplyHumanoidBoneToward(upper, lower, midHint, alpha * 0.85f);
            ApplyHumanoidBoneToward(lower, end, target, alpha * 0.7f);
            return true;
        }

        ApplyHumanoidBoneToward(upper, lower, solvedMid, alpha);
        ApplyHumanoidBoneToward(lower, end, solvedEnd, alpha * 0.95f);
        return true;
    }

    private bool TrySolveHumanoidTwoBoneTargets(
        Transform upper,
        Transform lower,
        Transform end,
        Vector3 rootHint,
        Vector3 midHint,
        Vector3 target,
        string bendCacheKey,
        out Vector3 solvedMid,
        out Vector3 solvedEnd)
    {
        solvedMid = Vector3.zero;
        solvedEnd = Vector3.zero;
        if (upper == null || lower == null || end == null)
        {
            return false;
        }

        Vector3 root = upper.position;
        float l1 = Vector3.Distance(upper.position, lower.position);
        float l2 = Vector3.Distance(lower.position, end.position);
        if (l1 <= 0.0001f || l2 <= 0.0001f)
        {
            return false;
        }

        Vector3 toTargetRaw = target - root;
        float rawDistance = toTargetRaw.magnitude;
        if (rawDistance <= 0.0001f)
        {
            return false;
        }

        float maxReach = Mathf.Max(0.001f, l1 + l2 - 0.0001f);
        float minReach = Mathf.Abs(l1 - l2) + 0.0001f;
        float d = ResolveHumanoidTwoBoneTargetDistance(rootHint, midHint, target, l1, l2, rawDistance);
        d = Mathf.Clamp(d, minReach, maxReach);
        Vector3 dir = toTargetRaw.normalized;
        solvedEnd = ResolveHumanoidTwoBoneEndTarget(root, target, d);

        float cosA = (l1 * l1 + d * d - l2 * l2) / (2f * l1 * d);
        cosA = Mathf.Clamp(cosA, -1f, 1f);
        float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));

        if (!TryResolveHumanoidTwoBoneBendDirection(rootHint, midHint, target, dir, upper.up, upper.right, out Vector3 bendDir))
        {
            return false;
        }

        bendDir = StabilizeHumanoidLimbBendDirection(bendCacheKey, bendDir);
        if (bendDir.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        bendDir.Normalize();

        solvedMid = root + dir * (cosA * l1) + bendDir * (sinA * l1);
        return true;
    }

    private static string BuildHumanoidLimbKey(string rootKey, string limbKey)
    {
        return string.IsNullOrEmpty(rootKey) ? limbKey : rootKey + ":" + limbKey;
    }

    private Vector3 StabilizeHumanoidLimbBendDirection(string key, Vector3 direction)
    {
        if (direction.sqrMagnitude < 0.000001f)
        {
            return direction;
        }

        direction.Normalize();
        if (string.IsNullOrEmpty(key))
        {
            return direction;
        }

        if (humanoidLimbBendDirectionByKey.TryGetValue(key, out Vector3 previous) && previous.sqrMagnitude > 0.000001f)
        {
            direction = StabilizeHumanoidBendNormal(previous, direction);
        }

        humanoidLimbBendDirectionByKey[key] = direction;
        return direction;
    }

    public static float ResolveHumanoidTwoBoneTargetDistance(
        Vector3 sourceRoot,
        Vector3 sourceMid,
        Vector3 sourceEnd,
        float modelUpperLength,
        float modelLowerLength,
        float rawModelTargetDistance)
    {
        if (modelUpperLength <= 0.0001f || modelLowerLength <= 0.0001f || rawModelTargetDistance <= 0.0001f)
        {
            return rawModelTargetDistance;
        }

        float sourceUpper = Vector3.Distance(sourceRoot, sourceMid);
        float sourceLower = Vector3.Distance(sourceMid, sourceEnd);
        float sourceReach = sourceUpper + sourceLower;
        float sourceDistance = Vector3.Distance(sourceRoot, sourceEnd);
        if (sourceReach <= 0.0001f || sourceDistance <= 0.0001f)
        {
            return rawModelTargetDistance;
        }

        float sourceRatio = Mathf.Clamp01(sourceDistance / sourceReach);
        float modelReach = modelUpperLength + modelLowerLength;
        float normalizedDistance = modelReach * sourceRatio;
        return Mathf.Min(rawModelTargetDistance, normalizedDistance);
    }

    public static Vector3 ResolveHumanoidTwoBoneEndTarget(Vector3 modelRoot, Vector3 rawTarget, float resolvedDistance)
    {
        Vector3 toTarget = rawTarget - modelRoot;
        if (toTarget.sqrMagnitude <= 0.00000001f || resolvedDistance <= 0.0001f)
        {
            return rawTarget;
        }

        return modelRoot + toTarget.normalized * resolvedDistance;
    }

    public static bool TryResolveHumanoidTwoBoneBendDirection(
        Vector3 sourceRoot,
        Vector3 sourceMid,
        Vector3 sourceEnd,
        Vector3 targetDirection,
        Vector3 fallbackA,
        Vector3 fallbackB,
        out Vector3 bendDirection)
    {
        bendDirection = Vector3.zero;
        if (targetDirection.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        Vector3 dir = targetDirection.normalized;
        Vector3 bendNormal = Vector3.Cross(sourceMid - sourceRoot, sourceEnd - sourceMid);
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(fallbackA, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(fallbackB, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        bendNormal.Normalize();

        bendDirection = Vector3.Cross(dir, bendNormal);
        if (bendDirection.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        bendDirection.Normalize();
        return true;
    }

    public static Vector3 StabilizeHumanoidBendNormal(Vector3 previous, Vector3 current)
    {
        if (current.sqrMagnitude < 0.000001f)
        {
            return current;
        }

        current.Normalize();
        if (previous.sqrMagnitude > 0.000001f)
        {
            previous.Normalize();
            if (Vector3.Dot(previous, current) < 0f)
            {
                current = -current;
            }
        }

        return current;
    }

    private bool ApplyHumanoidBoneToward(Transform bone, Transform aimChild, Vector3 targetPoint, float alpha)
    {
        if (bone == null || aimChild == null)
        {
            return false;
        }

        Vector3 currentDir = aimChild.position - bone.position;
        Vector3 targetDir = targetPoint - bone.position;
        if (currentDir.sqrMagnitude < 0.000001f || targetDir.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        Quaternion delta = Quaternion.FromToRotation(currentDir.normalized, targetDir.normalized);
        Quaternion targetRot = delta * bone.rotation;
        PoseTransformWriter.ApplyWorldRotation(
            bone,
            Quaternion.Slerp(bone.rotation, targetRot, Mathf.Clamp01(alpha)));
        return true;
    }

    private void ApplySmpl24TorsoAndHead(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis)
    {
        float alpha = Mathf.Clamp01(Smpl24SpineAlpha);
        if (alpha <= 0f)
        {
            return;
        }

        if (TryGetMidPoint(jointsWorld, vis, SmplLeftHip, SmplRightHip, out Vector3 hipsMid) &&
            TryGetMidPoint(jointsWorld, vis, SmplLeftShoulder, SmplRightShoulder, out Vector3 shouldersMid))
        {
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.Spine, HumanBodyBones.Chest, shouldersMid, alpha);
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.Chest, HumanBodyBones.UpperChest, shouldersMid, alpha * 0.75f);
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.UpperChest, HumanBodyBones.Neck, shouldersMid, alpha * 0.65f);
        }

        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplNeck, out Vector3 neck) &&
            TrackedJointPoints.TryGet(jointsWorld, vis, SmplHead, out Vector3 head))
        {
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.Neck, HumanBodyBones.Head, head, alpha * 0.75f);
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.Head, HumanBodyBones.LastBone, head + (head - neck), alpha * 0.5f);
        }
    }

    private void ApplySmpl24Feet(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha)
    {
        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplLeftFoot, out Vector3 leftFoot))
        {
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, leftFoot, alpha);
        }

        if (TrackedJointPoints.TryGet(jointsWorld, vis, SmplRightFoot, out Vector3 rightFoot))
        {
            ApplySmpl24BoneToPoint(cache, HumanBodyBones.RightFoot, HumanBodyBones.RightToes, rightFoot, alpha);
        }
    }

    private bool ApplySmpl24BoneToPoint(HumanoidRigCache cache, HumanBodyBones boneId, HumanBodyBones childId, Vector3 targetPoint, float alpha)
    {
        if (!cache.bones.TryGetValue(boneId, out Transform bone) || bone == null)
        {
            return false;
        }

        Transform child = null;
        if (childId != HumanBodyBones.LastBone)
        {
            cache.bones.TryGetValue(childId, out child);
        }
        if (child == null && bone.childCount > 0)
        {
            child = bone.GetChild(0);
        }

        return ApplyHumanoidBoneToward(bone, child, targetPoint, alpha);
    }
}
