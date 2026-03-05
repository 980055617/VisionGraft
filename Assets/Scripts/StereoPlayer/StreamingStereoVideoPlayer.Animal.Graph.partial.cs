using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
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
}
