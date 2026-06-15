using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: HumanoidRigCache and humanoid caches in Model.cs
    // Provides: humanoid cache build for the SMPL24 person pipeline

    private HumanoidRigCache GetOrBuildHumanoidCache(Animator animator)
    {
        if (humanoidCaches.TryGetValue(animator, out HumanoidRigCache existing))
        {
            return existing;
        }

        var cache = new HumanoidRigCache();

        // First collect all bone transforms
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
        }

        // UpperChest が Avatar 未登録の場合、LeftShoulder の親 hierarchy から Transform を取得する。
        // LeftShoulder.parent が Chest でなければその Transform が UpperChest にあたる。
        // SMPL FK の累積 local rotation 計算で UpperChest の T-pose rotation が必要なため。
        if (!cache.bones.ContainsKey(HumanBodyBones.UpperChest))
        {
            Transform chestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
            Transform leftShoulderBone = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
            if (leftShoulderBone != null && chestBone != null
                && leftShoulderBone.parent != null
                && leftShoulderBone.parent != chestBone)
            {
                cache.bones[HumanBodyBones.UpperChest] = leftShoulderBone.parent;
            }
        }

        // Sample T-pose using HumanPoseHandler so bindRotLocal reflects true bind rotations,
        // not the animated pose that may be playing at cache-creation time.
        bool sampledTpose = false;
        if (animator.avatar != null && animator.avatar.isHuman && animator.avatar.isValid)
        {
            try
            {
                var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                HumanPose savedPose = default(HumanPose);
                handler.GetHumanPose(ref savedPose);

                var tPose = new HumanPose();
                tPose.bodyPosition = savedPose.bodyPosition;
                tPose.bodyRotation = savedPose.bodyRotation;
                tPose.muscles = new float[savedPose.muscles.Length]; // all zeros = T-pose
                handler.SetHumanPose(ref tPose);

                foreach (var kv in cache.bones)
                {
                    cache.bindRotLocal[kv.Key] = kv.Value != null ? kv.Value.localRotation : Quaternion.identity;
                    cache.bindRotWorld[kv.Key] = kv.Value != null ? kv.Value.rotation : Quaternion.identity;
                }

                // T-pose 中（SetHumanPose で muscles=0 のまま）に bone.position を読む必要があるため
                // handler.SetHumanPose(ref savedPose) の前に呼ぶ。
                ComputeHandBindCorrection(cache, HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand);
                ComputeHandBindCorrection(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);

                handler.SetHumanPose(ref savedPose);
                sampledTpose = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to sample T-pose for humanoid cache: {ex.Message}");
            }
        }

        if (!sampledTpose)
        {
            foreach (var kv in cache.bones)
            {
                cache.bindRotLocal[kv.Key] = kv.Value != null ? kv.Value.localRotation : Quaternion.identity;
                cache.bindRotWorld[kv.Key] = kv.Value != null ? kv.Value.rotation : Quaternion.identity;
            }
            // フォールバック: 現在の pose（T-pose でない可能性あり）で計算するが最善の近似とする。
            ComputeHandBindCorrection(cache, HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand);
            ComputeHandBindCorrection(cache, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

    private static void ComputeHandBindCorrection(HumanoidRigCache cache, HumanBodyBones elbowBoneId, HumanBodyBones handBoneId)
    {
        if (!cache.bones.TryGetValue(elbowBoneId, out Transform elbowBone) || elbowBone == null) return;
        if (!cache.bones.TryGetValue(handBoneId,  out Transform handBone)  || handBone  == null) return;
        if (!cache.bindRotWorld.TryGetValue(handBoneId, out Quaternion bindHandWorld)) return;

        // T-pose サンプリング直後に呼ばれるため bone.position が T-pose 位置になっている。
        Vector3 armDir = handBone.position - elbowBone.position;
        if (armDir.sqrMagnitude < 0.0001f) return;
        armDir.Normalize();

        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, armDir);
        if (up.sqrMagnitude < 0.001f)
            up = Vector3.ProjectOnPlane(Vector3.right, armDir);
        up.Normalize();

        Quaternion canonicalTpose = Quaternion.LookRotation(armDir, up);
        cache.handBindCorrection[handBoneId] = Quaternion.Inverse(canonicalTpose) * bindHandWorld;
    }

}

