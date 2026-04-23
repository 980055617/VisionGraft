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
            cache.bindRotLocal[boneId] = bone.localRotation;
        }

        cache.ready = cache.bones.Count > 0;
        humanoidCaches[animator] = cache;
        return cache;
    }

}

