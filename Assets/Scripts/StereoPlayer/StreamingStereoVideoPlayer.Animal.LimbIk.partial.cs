using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void ApplyAnimalHeadPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha)
    {
        // Head uses a single fixed segment (Throat -> Nose) for both neck and head.
        ApplyAnimalBonesFromSegment(cache, cache.neck, cache.head, jointsWorld, vis, 5, 4, alpha * 0.65f, alpha * 0.65f);
    }

    private void ApplyAnimalLimbPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool freezeAnimalDistal)
    {
        // Front limbs use the same chain mapping but keep distal paw untouched.
        ApplyAnimalLimbByChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, AnimalLeftFrontChain, alpha, false);
        ApplyAnimalLimbByChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, AnimalRightFrontChain, alpha, false);

        // Rear limbs use full segment mapping including distal paw when enabled.
        ApplyAnimalLimbByChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, AnimalLeftRearChain, alpha, !freezeAnimalDistal);
        ApplyAnimalLimbByChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, AnimalRightRearChain, alpha, !freezeAnimalDistal);
    }

    private void ApplyAnimalBonesFromSegment(AnimalRigCache cache, Transform primary, Transform secondary, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float primaryAlpha, float secondaryAlpha)
    {
        ApplyAnimalBoneFromJoints(cache, primary, jointsWorld, vis, idxA, idxB, primaryAlpha);
        ApplyAnimalBoneFromJoints(cache, secondary, jointsWorld, vis, idxA, idxB, secondaryAlpha);
    }

    private void ApplyAnimalLimbByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        // Joint-centric mapping: each bone uses the segment between adjacent meta joints.
        ApplyAnimalBoneFromJoints(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.9f);
        ApplyAnimalBoneFromJoints(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.85f);
        if (paw != null && applyDistal)
        {
            ApplyAnimalBoneFromJoints(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.7f);
        }
    }
}
