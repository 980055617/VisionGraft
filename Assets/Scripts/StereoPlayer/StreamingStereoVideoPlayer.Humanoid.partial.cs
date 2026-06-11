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
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

}

