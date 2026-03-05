using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private void ApplyAnimalLimbChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha)
    {
        if (cache == null || upper == null || lower == null || chain == null || chain.Length < 3)
        {
            return;
        }

        int rootIdx = chain[0];
        int bendHintIdx = chain.Length >= 3 ? chain[1] : -1;
        // For 3-bone limb rigs (upper/lower/paw), solve IK to the mid joint (chain[2]),
        // then let paw bone handle the final segment (chain[2] -> chain[3]).
        int ikTargetIdx = chain.Length >= 4 ? chain[2] : chain[2];
        int pawIdx = chain.Length >= 4 ? chain[3] : chain[2];
        if (!TryGetJointPoint(jointsWorld, vis, rootIdx, out Vector3 rootHint) ||
            !TryGetJointPoint(jointsWorld, vis, ikTargetIdx, out Vector3 ikTarget))
        {
            return;
        }

        if (bendHintIdx >= 0 && !TryGetJointPoint(jointsWorld, vis, bendHintIdx, out _))
        {
            bendHintIdx = -1;
        }

        if (!TrySolveTwoBoneIkMidPoint(upper, lower, paw, rootHint, ikTarget, jointsWorld, vis, bendHintIdx, out Vector3 solvedMid))
        {
            // Fallback to directional FK if IK can't be solved.
            ApplyAnimalBoneFromJoints(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.8f);
            if (chain.Length >= 3)
            {
                ApplyAnimalBoneFromJoints(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.45f);
            }
            if (chain.Length >= 4 && paw != null)
            {
                ApplyAnimalBoneFromJoints(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.25f);
            }
            return;
        }

        Vector3 upperRoot = upper.position;
        ApplyAnimalBoneFromPointsLocalOnly(cache, upper, upperRoot, solvedMid, alpha * 0.95f);
        ApplyAnimalBoneFromPointsLocalOnly(cache, lower, lower.position, ikTarget, alpha * 0.85f);

        if (paw != null && chain.Length >= 4)
        {
            if (TryGetJointPoint(jointsWorld, vis, pawIdx, out Vector3 pawTarget))
            {
                ApplyAnimalBoneFromPointsLocalOnly(cache, paw, paw.position, pawTarget, alpha * 0.35f);
            }
            else
            {
                ApplyAnimalBoneFromJointsLocalOnly(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.25f);
            }
        }
    }


    private void ApplyAnimalLimbByJointSegments(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (cache == null || chain == null || chain.Length < 4)
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


    private bool ApplyAnimalBoneFromJointsLocalOnly(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPointsLocalOnly(cache, bone, a, b, alpha);
    }


    private bool TrySolveTwoBoneIkMidPoint(
        Transform upper,
        Transform lower,
        Transform paw,
        Vector3 rootHint,
        Vector3 target,
        Vector3[] jointsWorld,
        byte[] vis,
        int kneeIdx,
        out Vector3 solvedMid)
    {
        solvedMid = Vector3.zero;
        if (upper == null || lower == null)
        {
            return false;
        }

        Vector3 root = upper.position;
        float l1 = Vector3.Distance(upper.position, lower.position);
        float l2 = 0f;
        if (paw != null)
        {
            l2 = Vector3.Distance(lower.position, paw.position);
        }
        if (l2 <= 0.0001f)
        {
            l2 = Mathf.Max(0.0001f, lower.childCount > 0
                ? Vector3.Distance(lower.position, lower.GetChild(0).position)
                : Vector3.Distance(lower.position, target));
        }
        if (l1 <= 0.0001f || l2 <= 0.0001f)
        {
            return false;
        }

        Vector3 toTarget = target - root;
        float d = toTarget.magnitude;
        if (d <= 0.0001f)
        {
            return false;
        }

        float maxReach = Mathf.Max(0.001f, l1 + l2 - 0.0001f);
        float minReach = Mathf.Abs(l1 - l2) + 0.0001f;
        d = Mathf.Clamp(d, minReach, maxReach);
        Vector3 dir = toTarget.normalized;

        float cosA = (l1 * l1 + d * d - l2 * l2) / (2f * l1 * d);
        cosA = Mathf.Clamp(cosA, -1f, 1f);
        float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));

        Vector3 bendNormal = Vector3.zero;
        if (kneeIdx >= 0 && TryGetJointPoint(jointsWorld, vis, kneeIdx, out Vector3 kneeHint))
        {
            bendNormal = Vector3.Cross(kneeHint - rootHint, target - kneeHint);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(upper.up, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.Cross(upper.right, dir);
        }
        if (bendNormal.sqrMagnitude < 0.000001f)
        {
            bendNormal = Vector3.up;
        }
        bendNormal.Normalize();

        Vector3 bendDir = Vector3.Cross(bendNormal, dir);
        if (bendDir.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        bendDir.Normalize();

        Vector3 candA = root + dir * (cosA * l1) + bendDir * (sinA * l1);
        Vector3 candB = root + dir * (cosA * l1) - bendDir * (sinA * l1);

        if (kneeIdx >= 0 && TryGetJointPoint(jointsWorld, vis, kneeIdx, out Vector3 kneeRef))
        {
            solvedMid = Vector3.Distance(candA, kneeRef) <= Vector3.Distance(candB, kneeRef) ? candA : candB;
        }
        else
        {
            solvedMid = candA;
        }

        return true;
    }
}
