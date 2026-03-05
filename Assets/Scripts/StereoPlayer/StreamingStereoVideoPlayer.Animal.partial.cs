using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: AnimalRigCache/AnimalGraphChains and animal-related settings fields
    // Provides: animal skeleton apply, graph chain resolution, animal rig cache/bone solve

    private void ApplyAnimalSkeleton(Transform instanceRoot, Animator animator, Vector3[] jointsWorld, byte[] vis, int jointCount, byte categoryId, Transform screen, bool freezeDogDistal)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null || jointCount < 20)
        {
            return;
        }

        // Root orientation:
        // - dog: yaw-only style using world-up (screen tilt is ignored)
        // - others: previous behavior using screen-up
        float rootRotateAlpha = categoryId == 2
            ? GetEffectiveDogRootRotateAlpha()
            : Mathf.Clamp01(animalRootRotateAlpha);
        if (categoryId == 2)
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, null, rootRotateAlpha);
        }
        else
        {
            TryApplyAnimalRootOrientation(instanceRoot, jointsWorld, vis, screen, rootRotateAlpha);
        }

        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot);
        if (cache == null || !cache.ready)
        {
            return;
        }

        float alpha = categoryId == 2
            ? GetEffectiveDogBoneApplyAlpha()
            : Mathf.Clamp01(boneApplyAlpha);
        // Dog is handled in a reduced-drive mode:
        // root heading + head + limbs (no spine drive).
        if (categoryId == 2)
        {
            // Head only: fixed mapping (Throat -> Nose), no fallback.
            ApplyAnimalBoneFromJoints(cache, cache.neck, jointsWorld, vis, 5, 4, alpha * 0.65f);
            ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, 5, 4, alpha * 0.65f);

            if (!enableAnimalLimbApply)
            {
                return;
            }

            // Front legs: fixed mapping (no distal apply).
            // left front:
            //   001 (upper) <- 8 -> 12
            //   002 (lower) <- 12 -> 16
            //   003 (paw)   <- untouched
            ApplyAnimalBoneFromJoints(cache, cache.leftFrontUpper, jointsWorld, vis, 8, 12, alpha * 0.9f);
            ApplyAnimalBoneFromJoints(cache, cache.leftFrontLower, jointsWorld, vis, 12, 16, alpha * 0.85f);
            // right front:
            //   001 (upper) <- 9 -> 13
            //   002 (lower) <- 13 -> 17
            //   003 (paw)   <- untouched
            ApplyAnimalBoneFromJoints(cache, cache.rightFrontUpper, jointsWorld, vis, 9, 13, alpha * 0.9f);
            ApplyAnimalBoneFromJoints(cache, cache.rightFrontLower, jointsWorld, vis, 13, 17, alpha * 0.85f);

            // Rear legs: segment mapping from joint points (J0->J1, J1->J2, J2->J3).
            ApplyAnimalLimbByJointSegments(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, DogLeftRearChain, alpha, !freezeDogDistal);
            ApplyAnimalLimbByJointSegments(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, DogRightRearChain, alpha, !freezeDogDistal);
            return;
        }

        if (!TryResolveAnimalGraphChains(categoryId, jointsWorld, vis, screen, out AnimalGraphChains chains))
        {
            // Fallback to previous fixed mapping if graph extraction fails.
            ApplyAnimalBoneFromJoints(cache, cache.neck, jointsWorld, vis, 4, 0, alpha * 0.65f);
            ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, 4, 0, alpha * 0.65f);
            if (enableAnimalSpineApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.spine, jointsWorld, vis, 7, 4, alpha * 0.5f);
            }
            ApplyAnimalBoneFromJoints(cache, cache.tailBase, jointsWorld, vis, 4, 7, alpha * 0.3f);
            ApplyAnimalBoneFromJoints(cache, cache.tailMid, jointsWorld, vis, 4, 7, alpha * 0.2f);
            ApplyAnimalBoneFromJoints(cache, cache.tailTip, jointsWorld, vis, 4, 7, alpha * 0.15f);
            if (enableAnimalLimbApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontUpper, jointsWorld, vis, 5, 8, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontLower, jointsWorld, vis, 8, 12, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.leftFrontPaw, jointsWorld, vis, 12, 16, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontUpper, jointsWorld, vis, 6, 9, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontLower, jointsWorld, vis, 9, 13, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.rightFrontPaw, jointsWorld, vis, 13, 17, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearUpper, jointsWorld, vis, 7, 10, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearLower, jointsWorld, vis, 10, 14, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.leftRearPaw, jointsWorld, vis, 14, 18, alpha * 0.25f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearUpper, jointsWorld, vis, 7, 11, alpha * 0.8f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearLower, jointsWorld, vis, 11, 15, alpha * 0.45f);
                ApplyAnimalBoneFromJoints(cache, cache.rightRearPaw, jointsWorld, vis, 15, 19, alpha * 0.25f);
            }
            return;
        }

        if (chains.hasHead)
        {
            ApplyAnimalBoneFromPoints(cache, cache.neck, chains.headRoot, chains.headTip, alpha * 0.65f);
            ApplyAnimalBoneFromPoints(cache, cache.head, chains.headRoot, chains.headTip, alpha * 0.65f);
        }
        if (chains.hasTorso)
        {
            if (enableAnimalSpineApply)
            {
                ApplyAnimalBoneFromJoints(cache, cache.spine, jointsWorld, vis, chains.rearHub, chains.frontHub, alpha * 0.5f);
            }
            ApplyAnimalBoneFromJoints(cache, cache.tailBase, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.3f);
            ApplyAnimalBoneFromJoints(cache, cache.tailMid, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.2f);
            ApplyAnimalBoneFromJoints(cache, cache.tailTip, jointsWorld, vis, chains.frontHub, chains.rearHub, alpha * 0.15f);
        }

        if (!enableAnimalLimbApply)
        {
            return;
        }

        ApplyAnimalLimbChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, chains.leftFrontChain, alpha);
        ApplyAnimalLimbChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, chains.rightFrontChain, alpha);
        ApplyAnimalLimbChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, chains.leftRearChain, alpha);
        ApplyAnimalLimbChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, chains.rightRearChain, alpha);
    }


    private float GetEffectiveDogBoneApplyAlpha()
    {
        return Mathf.Clamp01(boneApplyAlpha);
    }


    private float GetEffectiveDogRootRotateAlpha()
    {
        return Mathf.Clamp01(animalRootRotateAlpha);
    }


    private void TryApplyAnimalRootOrientation(Transform instanceRoot, Vector3[] jointsWorld, byte[] vis, Transform screen, float rotateAlpha)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null)
        {
            return;
        }

        if (!TryGetAnimalBodyDirection(jointsWorld, vis, out Vector3 bodyForward))
        {
            return;
        }

        Vector3 up = screen != null && screen.up.sqrMagnitude > 0.0001f ? screen.up.normalized : Vector3.up;
        Vector3 planarForward = Vector3.ProjectOnPlane(bodyForward, up);
        if (planarForward.sqrMagnitude < 0.000001f)
        {
            planarForward = bodyForward.normalized;
        }
        else
        {
            planarForward.Normalize();
        }

        Vector3 modelForward = animalModelForwardLocal.sqrMagnitude > 0.000001f
            ? animalModelForwardLocal.normalized
            : Vector3.right;
        Vector3 modelUp = animalModelUpLocal.sqrMagnitude > 0.000001f
            ? animalModelUpLocal.normalized
            : Vector3.up;

        Quaternion modelBasis = Quaternion.LookRotation(modelForward, modelUp);
        Quaternion targetBasis = Quaternion.LookRotation(planarForward, up);
        Quaternion targetRootRot = targetBasis * Quaternion.Inverse(modelBasis);
        instanceRoot.rotation = Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha));
    }


    private bool TryGetAnimalBodyDirection(Vector3[] jointsWorld, byte[] vis, out Vector3 forward)
    {
        forward = Vector3.zero;
        bool hasRear = TryGetJointPoint(jointsWorld, vis, 6, out Vector3 rearHub);   // TailBase(hip)
        bool hasWithers = TryGetJointPoint(jointsWorld, vis, 7, out Vector3 withers);

        if (hasRear && hasWithers)
        {
            forward = (withers - rearHub).normalized;
        }

        return forward.sqrMagnitude > 0.000001f;
    }

    private struct AnimalGraphChains
    {
        public bool hasHead;
        public bool hasTorso;
        public int frontHub;
        public int rearHub;
        public Vector3 headRoot;
        public Vector3 headTip;
        public int[] leftFrontChain;
        public int[] rightFrontChain;
        public int[] leftRearChain;
        public int[] rightRearChain;
    }

    private bool TryResolveAnimalGraphChains(byte categoryId, Vector3[] jointsWorld, byte[] vis, Transform screen, out AnimalGraphChains chains)
    {
        chains = default;
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return false;
        }

        if (!TryGetCategoryEdges(categoryId, out ushort[] edgePairs) || edgePairs == null || edgePairs.Length < 2)
        {
            return false;
        }

        int n = jointsWorld.Length;
        List<int>[] adj = BuildJointAdjacency(n, edgePairs);
        if (adj == null)
        {
            return false;
        }

        List<int> endpoints = new List<int>();
        List<int> hubs = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int d = adj[i].Count;
            if (d == 1) endpoints.Add(i);
            if (d >= 3) hubs.Add(i);
        }
        if (hubs.Count < 2)
        {
            return false;
        }

        if (!TryFindHeadByEndpointParents(adj, endpoints, jointsWorld, vis, out int headEndA, out int headEndB, out int headRoot))
        {
            return false;
        }

        if (!TryFindFrontRearHubs(adj, hubs, headRoot, out int frontHub, out int rearHub))
        {
            return false;
        }

        Vector3 headTip = (jointsWorld[headEndA] + jointsWorld[headEndB]) * 0.5f;
        chains.hasHead = true;
        chains.headRoot = jointsWorld[headRoot];
        chains.headTip = headTip;
        chains.hasTorso = true;
        chains.frontHub = frontHub;
        chains.rearHub = rearHub;

        int frontLegHub = FindFrontLegHub(adj, frontHub, rearHub, headRoot);
        List<int[]> frontChains = new List<int[]>();
        if (frontLegHub >= 0)
        {
            for (int i = 0; i < adj[frontLegHub].Count; i++)
            {
                int next = adj[frontLegHub][i];
                if (next == frontHub || next == rearHub)
                {
                    continue;
                }

                if (TryTraceChainToEndpoint(adj, frontLegHub, next, out int[] chain))
                {
                    frontChains.Add(chain);
                }
            }
        }

        List<int[]> rearChains = new List<int[]>();
        for (int i = 0; i < adj[rearHub].Count; i++)
        {
            int next = adj[rearHub][i];
            if (next == frontHub || next == headRoot || next == frontLegHub)
            {
                continue;
            }

            if (TryTraceChainToEndpoint(adj, rearHub, next, out int[] chain))
            {
                rearChains.Add(chain);
            }
        }

        SplitLeftRightChains(frontChains, jointsWorld, screen, out chains.leftFrontChain, out chains.rightFrontChain);
        SplitLeftRightChains(rearChains, jointsWorld, screen, out chains.leftRearChain, out chains.rightRearChain);
        return true;
    }


    private List<int>[] BuildJointAdjacency(int jointCount, ushort[] edgePairs)
    {
        if (jointCount <= 0 || edgePairs == null || edgePairs.Length < 2)
        {
            return null;
        }

        var adj = new List<int>[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            adj[i] = new List<int>(4);
        }

        for (int i = 0; i + 1 < edgePairs.Length; i += 2)
        {
            int a = edgePairs[i];
            int b = edgePairs[i + 1];
            if (a < 0 || b < 0 || a >= jointCount || b >= jointCount || a == b)
            {
                continue;
            }

            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }

        return adj;
    }


    private bool TryFindHeadByEndpointParents(List<int>[] adj, List<int> endpoints, Vector3[] jointsWorld, byte[] vis, out int endA, out int endB, out int headRoot)
    {
        endA = -1;
        endB = -1;
        headRoot = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < endpoints.Count; i++)
        {
            int e1 = endpoints[i];
            if (e1 < 0 || e1 >= vis.Length || vis[e1] == 0 || adj[e1].Count != 1)
            {
                continue;
            }

            int p1 = adj[e1][0];
            for (int j = i + 1; j < endpoints.Count; j++)
            {
                int e2 = endpoints[j];
                if (e2 < 0 || e2 >= vis.Length || vis[e2] == 0 || adj[e2].Count != 1)
                {
                    continue;
                }

                int p2 = adj[e2][0];
                if (!adj[p1].Contains(p2))
                {
                    continue;
                }

                int common = -1;
                for (int k = 0; k < adj[p1].Count; k++)
                {
                    int c = adj[p1][k];
                    if (c != e1 && c != e2 && c != p2 && adj[p2].Contains(c))
                    {
                        common = c;
                        break;
                    }
                }
                if (common < 0)
                {
                    continue;
                }

                // Prefer the tighter pair likely representing the snout/face endpoints.
                float score = -Vector3.Distance(jointsWorld[e1], jointsWorld[e2]);
                if (score > bestScore)
                {
                    bestScore = score;
                    endA = e1;
                    endB = e2;
                    headRoot = common;
                }
            }
        }

        return endA >= 0 && endB >= 0 && headRoot >= 0;
    }


    private bool TryFindFrontRearHubs(List<int>[] adj, List<int> hubs, int headRoot, out int frontHub, out int rearHub)
    {
        frontHub = -1;
        rearHub = -1;
        int bestDegree = int.MinValue;
        for (int i = 0; i < hubs.Count; i++)
        {
            int h = hubs[i];
            if (!adj[headRoot].Contains(h))
            {
                continue;
            }
            if (adj[h].Count > bestDegree)
            {
                bestDegree = adj[h].Count;
                frontHub = h;
            }
        }
        if (frontHub < 0)
        {
            return false;
        }

        bestDegree = int.MinValue;
        for (int i = 0; i < hubs.Count; i++)
        {
            int h = hubs[i];
            if (h == frontHub || !adj[frontHub].Contains(h))
            {
                continue;
            }
            if (adj[h].Count > bestDegree)
            {
                bestDegree = adj[h].Count;
                rearHub = h;
            }
        }

        return rearHub >= 0;
    }


    private int FindFrontLegHub(List<int>[] adj, int frontHub, int rearHub, int headRoot)
    {
        int best = -1;
        int bestDegree = int.MinValue;
        for (int i = 0; i < adj[frontHub].Count; i++)
        {
            int n = adj[frontHub][i];
            if (n == rearHub || n == headRoot)
            {
                continue;
            }
            if (adj[n].Count > bestDegree)
            {
                bestDegree = adj[n].Count;
                best = n;
            }
        }
        return best;
    }


    private bool TryTraceChainToEndpoint(List<int>[] adj, int hub, int start, out int[] chain)
    {
        chain = null;
        List<int> path = new List<int>(5) { hub, start };
        int prev = hub;
        int cur = start;
        int guard = 0;
        while (guard++ < 16)
        {
            if (adj[cur].Count == 1)
            {
                chain = path.ToArray();
                return true;
            }

            int next = -1;
            for (int i = 0; i < adj[cur].Count; i++)
            {
                int c = adj[cur][i];
                if (c != prev)
                {
                    next = c;
                    break;
                }
            }
            if (next < 0 || adj[cur].Count > 3)
            {
                break;
            }

            prev = cur;
            cur = next;
            path.Add(cur);
        }

        if (path.Count >= 3)
        {
            chain = path.ToArray();
            return true;
        }

        return false;
    }


    private void SplitLeftRightChains(List<int[]> chains, Vector3[] jointsWorld, Transform screen, out int[] left, out int[] right)
    {
        left = null;
        right = null;
        if (chains == null || chains.Count == 0)
        {
            return;
        }
        if (chains.Count == 1)
        {
            left = chains[0];
            return;
        }

        Vector3 axis = screen != null && screen.right.sqrMagnitude > 0.0001f ? screen.right.normalized : Vector3.right;
        int[] c0 = chains[0];
        int[] c1 = chains[1];
        int e0 = c0[c0.Length - 1];
        int e1 = c1[c1.Length - 1];
        float d0 = Vector3.Dot(jointsWorld[e0], axis);
        float d1 = Vector3.Dot(jointsWorld[e1], axis);
        if (d0 <= d1)
        {
            left = c0;
            right = c1;
        }
        else
        {
            left = c1;
            right = c0;
        }
    }


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


    private AnimalRigCache GetOrBuildAnimalRigCache(Transform root)
    {
        if (root == null)
        {
            return null;
        }

        if (animalRigCaches.TryGetValue(root, out AnimalRigCache existing))
        {
            return existing;
        }

        AnimalRigCache cache = new AnimalRigCache();
        cache.root = root;
        Transform[] bones = root.GetComponentsInChildren<Transform>(true);

        // DogRoot concrete parent bones (derived from mesh-node parents):
        // body->Bone, neck->Bone.007, head.001->Bone.009,
        // er.L/R->Bone.009_L/R.001,
        // arm.001/002/003.L/R->Bone_L/R.001/002/003,
        // foot.001/002/003.L/R->Bone.001_L/R.001/002/003.
        cache.neck =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.007") ??
            FindRigBoneFromMeshNodeName(bones, "neck") ??
            FindBoneByTokens(bones, "neck");
        cache.head =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009") ??
            FindRigBoneFromMeshNodeName(bones, "head.001") ??
            FindBoneByTokens(bones, "head");
        cache.leftEar =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "er.L") ??
            FindBoneByTokens(bones, "er.l");
        cache.rightEar =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.009_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "er.R") ??
            FindBoneByTokens(bones, "er.r");
        cache.spine =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3") ??
            FindRigBoneFromMeshNodeName(bones, "body") ??
            FindBoneByTokens(bones, "body", "spine", "chest", "back");
        cache.tailBase = FindBoneByTokens(bones, "tail.002", "tail");
        cache.tailMid = FindBoneByTokens(bones, "tail.003", "tail");
        cache.tailTip = FindBoneByTokens(bones, "tail.004", "tail");

        cache.leftFrontUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "arm.001.L") ??
            FindBoneByTokens(bones, "arm.001.l");
        cache.leftFrontLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.002") ??
            FindRigBoneFromMeshNodeName(bones, "arm.002.L") ??
            FindBoneByTokens(bones, "arm.002.l");
        cache.leftFrontPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_L.003") ??
            FindRigBoneFromMeshNodeName(bones, "arm.003.L") ??
            FindBoneByTokens(bones, "arm.003.l");
        cache.rightFrontUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "arm.001.R") ??
            FindBoneByTokens(bones, "arm.001.r");
        cache.rightFrontLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.002") ??
            FindRigBoneFromMeshNodeName(bones, "arm.002.R") ??
            FindBoneByTokens(bones, "arm.002.r");
        cache.rightFrontPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3_R.003") ??
            FindRigBoneFromMeshNodeName(bones, "arm.003.R") ??
            FindBoneByTokens(bones, "arm.003.r");
        cache.leftRearUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.001") ??
            FindRigBoneFromMeshNodeName(bones, "foot.001.L") ??
            FindBoneByTokens(bones, "foot.001.l", "foot.002.l");
        cache.leftRearLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.002") ??
            FindRigBoneFromMeshNodeName(bones, "foot.002.L") ??
            FindBoneByTokens(bones, "foot.002.l", "foot.003.l");
        cache.leftRearPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_L.003") ??
            FindRigBoneFromMeshNodeName(bones, "foot.003.L") ??
            FindBoneByTokens(bones, "foot.003.l", "foot.004.l");
        cache.rightRearUpper =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.001") ??
            FindRigBoneFromMeshNodeName(bones, "foot.001.R") ??
            FindBoneByTokens(bones, "foot.001.r", "foot.002.r");
        cache.rightRearLower =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.002") ??
            FindRigBoneFromMeshNodeName(bones, "foot.002.R") ??
            FindBoneByTokens(bones, "foot.002.r", "foot.003.r");
        cache.rightRearPaw =
            FindBoneByExactNames(bones, "\u30DC\u30FC\u30F3.001_R.003") ??
            FindRigBoneFromMeshNodeName(bones, "foot.003.R") ??
            FindBoneByTokens(bones, "foot.003.r", "foot.004.r");

        PrimeAnimalBind(cache, cache.neck);
        PrimeAnimalBind(cache, cache.head);
        PrimeAnimalBind(cache, cache.leftEar);
        PrimeAnimalBind(cache, cache.rightEar);
        PrimeAnimalBind(cache, cache.spine);
        PrimeAnimalBind(cache, cache.tailBase);
        PrimeAnimalBind(cache, cache.tailMid);
        PrimeAnimalBind(cache, cache.tailTip);
        PrimeAnimalBind(cache, cache.leftFrontUpper);
        PrimeAnimalBind(cache, cache.leftFrontLower);
        PrimeAnimalBind(cache, cache.leftFrontPaw);
        PrimeAnimalBind(cache, cache.rightFrontUpper);
        PrimeAnimalBind(cache, cache.rightFrontLower);
        PrimeAnimalBind(cache, cache.rightFrontPaw);
        PrimeAnimalBind(cache, cache.leftRearUpper);
        PrimeAnimalBind(cache, cache.leftRearLower);
        PrimeAnimalBind(cache, cache.leftRearPaw);
        PrimeAnimalBind(cache, cache.rightRearUpper);
        PrimeAnimalBind(cache, cache.rightRearLower);
        PrimeAnimalBind(cache, cache.rightRearPaw);

        RegisterAnimalAimChild(cache, cache.leftFrontUpper, cache.leftFrontLower);
        RegisterAnimalAimChild(cache, cache.leftFrontLower, cache.leftFrontPaw);
        RegisterAnimalAimChild(cache, cache.rightFrontUpper, cache.rightFrontLower);
        RegisterAnimalAimChild(cache, cache.rightFrontLower, cache.rightFrontPaw);
        RegisterAnimalAimChild(cache, cache.leftRearUpper, cache.leftRearLower);
        RegisterAnimalAimChild(cache, cache.leftRearLower, cache.leftRearPaw);
        RegisterAnimalAimChild(cache, cache.rightRearUpper, cache.rightRearLower);
        RegisterAnimalAimChild(cache, cache.rightRearLower, cache.rightRearPaw);
        RegisterAnimalAimChild(cache, cache.neck, cache.head);
        RegisterAnimalAimChild(cache, cache.spine, cache.neck);
        RegisterAnimalAimChild(cache, cache.tailBase, cache.tailMid);
        RegisterAnimalAimChild(cache, cache.tailMid, cache.tailTip);

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;
        return cache;
    }


    private Transform FindBoneByTokens(Transform[] bones, params string[] tokens)
    {
        if (bones == null || tokens == null || tokens.Length == 0)
        {
            return null;
        }

        Transform best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            string needle = token.ToLowerInvariant();
            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                string name = bone.name.ToLowerInvariant();
                if (name.Contains(needle))
                {
                    int score = 0;
                    if (bone.GetComponent<Renderer>() == null)
                    {
                        score += 2;
                    }
                    if (bone.childCount > 0)
                    {
                        score += 1;
                    }
                    if (name == needle)
                    {
                        score += 2;
                    }
                    else if (name.StartsWith(needle))
                    {
                        score += 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = bone;
                    }
                }
            }
        }

        return ResolveLikelyRigBone(best);
    }


    private Transform FindBoneByExactNames(Transform[] bones, params string[] exactNames)
    {
        if (bones == null || exactNames == null || exactNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < exactNames.Length; i++)
        {
            string exact = exactNames[i];
            if (string.IsNullOrEmpty(exact))
            {
                continue;
            }

            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                if (bone.name == exact)
                {
                    return ResolveLikelyRigBone(bone);
                }
            }
        }

        return null;
    }


    private Transform FindRigBoneFromMeshNodeName(Transform[] bones, string exactName)
    {
        if (bones == null || string.IsNullOrEmpty(exactName))
        {
            return null;
        }

        for (int i = 0; i < bones.Length; i++)
        {
            Transform t = bones[i];
            if (t == null || t.name != exactName)
            {
                continue;
            }

            return ResolveLikelyRigBone(t);
        }

        return null;
    }


    private Transform ResolveLikelyRigBone(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        // In this dog asset, names like arm.001.L / foot.001.L are mesh nodes.
        // Drive the parent rig bone instead of rotating mesh parts directly.
        bool hasMesh =
            node.GetComponent<MeshRenderer>() != null ||
            node.GetComponent<MeshFilter>() != null ||
            node.GetComponent<SkinnedMeshRenderer>() != null;
        if (hasMesh && node.parent != null)
        {
            return node.parent;
        }

        return node;
    }


    private void RegisterAnimalAimChild(AnimalRigCache cache, Transform bone, Transform aimChild)
    {
        if (cache == null || bone == null || aimChild == null)
        {
            return;
        }

        cache.aimChildByBone[bone] = aimChild;
    }


    private void PrimeAnimalBind(AnimalRigCache cache, Transform bone)
    {
        if (cache == null || bone == null || cache.bindRotLocal.ContainsKey(bone))
        {
            return;
        }

        cache.bindRotLocal[bone] = bone.localRotation;
        Vector3 bindDirLocal = Vector3.forward;
        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 bindDirWorld))
        {
            bindDirLocal = bone.InverseTransformDirection(bindDirWorld);
        }
        cache.bindDirLocal[bone] = bindDirLocal == Vector3.zero ? Vector3.forward : bindDirLocal.normalized;
    }


    private bool ApplyAnimalBoneFromJoints(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPoints(cache, bone, a, b, alpha);
    }


    private bool ApplyAnimalBoneFromPoints(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 currentDir))
        {
            Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorld = deltaWorld * bone.rotation;
            // When nearly opposite, FromTo rotation axis is unstable and can spin frame-to-frame.
            // Fall back to bind-space solve for deterministic behavior.
            float dot = Vector3.Dot(currentDir, targetDir);
            if (dot > -0.98f)
            {
                bone.rotation = Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha));
                return true;
            }
        }

        Vector3 targetLocalDir = bone.parent != null
            ? bone.parent.InverseTransformDirection(targetDir)
            : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }


    private bool ApplyAnimalBoneFromPointsLocalOnly(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        Vector3 targetLocalDir = bone.parent != null
            ? bone.parent.InverseTransformDirection(targetDir)
            : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha));
        return true;
    }


    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (cache != null && bone != null && cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            return mapped;
        }

        if (bone != null && bone.childCount > 0)
        {
            return bone.GetChild(0);
        }

        return null;
    }


    private bool TryGetBoneCenterDirectionWorld(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        if (bone == null)
        {
            return false;
        }

        Transform centerTarget = ResolveAnimalAimChild(cache, bone);
        // For limb segments, prefer pure bone-to-bone pivot direction.
        if (centerTarget != null && IsAnimalLimbBone(cache, bone))
        {
            Vector3 childPivotDir = centerTarget.position - bone.position;
            if (childPivotDir.sqrMagnitude > 0.000001f)
            {
                dirWorld = childPivotDir.normalized;
                return true;
            }
        }

        if (centerTarget == null)
        {
            centerTarget = bone;
        }

        if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld))
        {
            return false;
        }

        Vector3 rawDir = centerWorld - bone.position;
        if (rawDir.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        dirWorld = rawDir.normalized;
        return true;
    }


    private static bool IsAnimalLimbBone(AnimalRigCache cache, Transform bone)
    {
        if (cache == null || bone == null)
        {
            return false;
        }

        return
            bone == cache.leftFrontUpper ||
            bone == cache.leftFrontLower ||
            bone == cache.leftFrontPaw ||
            bone == cache.rightFrontUpper ||
            bone == cache.rightFrontLower ||
            bone == cache.rightFrontPaw ||
            bone == cache.leftRearUpper ||
            bone == cache.leftRearLower ||
            bone == cache.leftRearPaw ||
            bone == cache.rightRearUpper ||
            bone == cache.rightRearLower ||
            bone == cache.rightRearPaw;
    }


    private bool TryGetTransformCenterWorld(Transform target, out Vector3 centerWorld)
    {
        centerWorld = Vector3.zero;
        if (target == null)
        {
            return false;
        }

        SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            centerWorld = target.TransformPoint(smr.localBounds.center);
            return true;
        }

        MeshFilter mf = target.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            centerWorld = target.TransformPoint(mf.sharedMesh.bounds.center);
            return true;
        }

        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            centerWorld = renderer.bounds.center;
            return true;
        }

        return false;
    }

}

