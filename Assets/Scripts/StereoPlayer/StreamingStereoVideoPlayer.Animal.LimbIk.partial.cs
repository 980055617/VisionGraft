using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void ApplyAnimalHeadPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool hasControl, AnimalControlWorldData control)
    {
        if (hasControl)
        {
            if (control.hasWithers && control.hasHeadRoot)
            {
                ApplyAnimalBoneFromPoints(cache, cache.neck, control.withersWorld, control.headRootWorld, alpha * 0.65f);
            }

            if (control.hasHeadRoot && control.hasHeadTip)
            {
                ApplyAnimalBoneFromPoints(cache, cache.head, control.headRootWorld, control.headTipWorld, alpha * 0.65f);
                return;
            }
        }

        ApplyAnimalBonesFromSegment(cache, cache.neck, cache.head, jointsWorld, vis, 24, 2, alpha * 0.65f, alpha * 0.65f);
    }

    private void ApplyAnimalTailPose(AnimalRigCache cache, float alpha, bool hasControl, AnimalControlWorldData control)
    {
        if (!hasControl || cache.tailBase == null)
        {
            return;
        }

        if (control.hasTailBase && control.hasTailTip)
        {
            ApplyAnimalBoneFromPoints(cache, cache.tailBase, control.tailBaseWorld, control.tailTipWorld, alpha * 0.5f);
            return;
        }

        ApplyAnimalBoneFromChain(cache, cache.tailBase, null, null, control.tailWorld, alpha * 0.5f, false);
    }

    private void ApplyAnimalLimbPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool freezeAnimalDistal, bool hasControl, AnimalControlWorldData control)
    {
        if (hasControl)
        {
            ApplyAnimalBoneFromChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, control.frontLeftLegWorld, alpha, false);
            ApplyAnimalBoneFromChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, control.frontRightLegWorld, alpha, false);
            ApplyAnimalBoneFromChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, control.rearLeftLegWorld, alpha, !freezeAnimalDistal);
            ApplyAnimalBoneFromChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, control.rearRightLegWorld, alpha, !freezeAnimalDistal);
            return;
        }

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

    private void ApplyAnimalBoneFromChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal)
    {
        if (chainWorld == null || chainWorld.Length < 2)
        {
            return;
        }

        int upperStart = 0;
        int lowerStart = 1;
        int distalStart = 2;
        if (chainWorld.Length >= 5)
        {
            upperStart = 1;
            lowerStart = 2;
            distalStart = 3;
        }

        if (upper != null && upperStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, upper, chainWorld[upperStart], chainWorld[upperStart + 1], alpha * 0.9f);
        }

        if (lower != null && lowerStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, lower, chainWorld[lowerStart], chainWorld[lowerStart + 1], alpha * 0.85f);
        }

        if (paw != null && applyDistal && distalStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, paw, chainWorld[distalStart], chainWorld[distalStart + 1], alpha * 0.7f);
        }
    }
}
