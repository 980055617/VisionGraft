using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: HumanoidRigCache/SkeletonIndices and humanoid caches in Model.cs
    // Provides: humanoid cache build, skeleton index selection, limb and foot application

    private HumanoidRigCache GetOrBuildHumanoidCache(Animator animator)
    {
        if (humanoidCaches.TryGetValue(animator, out HumanoidRigCache existing))
        {
            return existing;
        }

        var cache = new HumanoidRigCache();
        foreach (HumanBodyBones boneId in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform bone = animator.GetBoneTransform(boneId);
            if (bone == null)
            {
                continue;
            }

            cache.bones[boneId] = bone;
            cache.bindRotWorld[boneId] = bone.rotation;
            Vector3 dir = Vector3.forward;
            if (bone.childCount > 0)
            {
                dir = (bone.GetChild(0).position - bone.position).normalized;
            }
            cache.bindDirWorld[boneId] = dir == Vector3.zero ? Vector3.forward : dir;
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }


    private SkeletonIndices ResolveSkeletonIndices(int jointCount)
    {
        // Keep schema choice strict to avoid misinterpreting unknown layouts.
        if (jointCount == 33)
        {
            return Blaze33Indices;
        }

        return Coco17Indices;
    }


    private bool ApplyHumanoidLimbs(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx)
    {
        if (cache == null || !cache.ready || jointsWorld == null || vis == null)
        {
            return false;
        }

        bool appliedAny = false;

        // Major limb chains (base behavior).
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftUpperArm, jointsWorld, vis, idx.leftShoulder, idx.leftElbow, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftLowerArm, jointsWorld, vis, idx.leftElbow, idx.leftWrist, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightUpperArm, jointsWorld, vis, idx.rightShoulder, idx.rightElbow, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightLowerArm, jointsWorld, vis, idx.rightElbow, idx.rightWrist, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftUpperLeg, jointsWorld, vis, idx.leftHip, idx.leftKnee, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.LeftLowerLeg, jointsWorld, vis, idx.leftKnee, idx.leftAnkle, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightUpperLeg, jointsWorld, vis, idx.rightHip, idx.rightKnee, boneApplyAlpha);
        appliedAny |= ApplyBoneFromJoints(cache, HumanBodyBones.RightLowerLeg, jointsWorld, vis, idx.rightKnee, idx.rightAnkle, boneApplyAlpha);

        if (useExtendedBoneMap &&
            TryGetMidPoint(jointsWorld, vis, idx.leftHip, idx.rightHip, out Vector3 hipsMid) &&
            TryGetMidPoint(jointsWorld, vis, idx.leftShoulder, idx.rightShoulder, out Vector3 shouldersMid))
        {
            // Low-alpha torso/head mapping for naturalness.
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Hips, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Spine, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Chest, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.UpperChest, hipsMid, shouldersMid, torsoBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.LeftShoulder, shouldersMid, jointsWorld[idx.leftShoulder], shoulderBoneApplyAlpha);
            appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.RightShoulder, shouldersMid, jointsWorld[idx.rightShoulder], shoulderBoneApplyAlpha);

            if (TryGetHeadTarget(jointsWorld, vis, shouldersMid, idx, out Vector3 headTarget))
            {
                appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Neck, shouldersMid, headTarget, headBoneApplyAlpha);
                appliedAny |= ApplyBoneFromPoints(cache, HumanBodyBones.Head, shouldersMid, headTarget, headBoneApplyAlpha);
            }
        }

        return appliedAny;
    }


    private bool ApplyBoneFromJoints(HumanoidRigCache cache, HumanBodyBones boneId, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyBoneFromPoints(cache, boneId, a, b, alpha);
    }


    private bool ApplyBoneFromPoints(HumanoidRigCache cache, HumanBodyBones boneId, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (cache == null || !cache.ready)
        {
            return false;
        }

        if (!cache.bones.TryGetValue(boneId, out Transform bone))
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        if (!cache.bindDirWorld.TryGetValue(boneId, out Vector3 bindDir) || bindDir == Vector3.zero)
        {
            bindDir = Vector3.forward;
        }

        if (!cache.bindRotWorld.TryGetValue(boneId, out Quaternion bindRot))
        {
            bindRot = bone.rotation;
        }

        Quaternion targetRot = Quaternion.FromToRotation(bindDir, targetDir) * bindRot;
        bone.rotation = Quaternion.Slerp(bone.rotation, targetRot, Mathf.Clamp01(alpha));
        return true;
    }


    private void AlignFeetToAnkles(HumanoidRigCache cache, Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx, Transform root)
    {
        if (!enableFootRootCorrection)
        {
            return;
        }

        if (cache == null || jointsWorld == null || vis == null || root == null)
        {
            return;
        }

        if (idx.leftAnkle < 0 || idx.rightAnkle < 0 ||
            idx.leftAnkle >= vis.Length || idx.rightAnkle >= vis.Length ||
            idx.leftAnkle >= jointsWorld.Length || idx.rightAnkle >= jointsWorld.Length)
        {
            return;
        }

        if (vis[idx.leftAnkle] == 0 || vis[idx.rightAnkle] == 0)
        {
            return;
        }

        if (!cache.bones.TryGetValue(HumanBodyBones.LeftFoot, out Transform leftFoot) ||
            !cache.bones.TryGetValue(HumanBodyBones.RightFoot, out Transform rightFoot))
        {
            return;
        }

        Vector3 targetMid = (jointsWorld[idx.leftAnkle] + jointsWorld[idx.rightAnkle]) * 0.5f;
        Vector3 currentMid = (leftFoot.position + rightFoot.position) * 0.5f;
        Vector3 delta = targetMid - currentMid;
        if (delta == Vector3.zero)
        {
            return;
        }

        // Guard against bad keypoint mapping spikes that can teleport the avatar.
        const float MaxFootAlignDeltaPerFrame = 0.08f;
        float mag = delta.magnitude;
        if (mag > MaxFootAlignDeltaPerFrame)
        {
            delta = delta * (MaxFootAlignDeltaPerFrame / mag);
        }

        // Feet alignment should mostly correct height; keep lateral shift small to reduce discomfort.
        Vector3 up = root.up.sqrMagnitude > 0.0001f ? root.up.normalized : Vector3.up;
        Vector3 vertical = Vector3.Project(delta, up);
        Vector3 lateral = delta - vertical;
        delta = vertical + lateral * 0.2f;

        root.position += delta * Mathf.Clamp01(footAlignAlpha);
    }

}

