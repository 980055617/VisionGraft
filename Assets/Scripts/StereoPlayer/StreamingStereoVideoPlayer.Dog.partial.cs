using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: animal rig cache, dog debug hash sets, debug axis comparison flow
    // Provides: dog bone/mapping diagnostics and dog-specific compare/head helpers

    private void LogDogBonesOnce(Transform rigRoot)
    {
        if (rigRoot == null || dogBonesDumpLoggedRoots.Contains(rigRoot))
        {
            return;
        }

        dogBonesDumpLoggedRoots.Add(rigRoot);
        Transform[] all = rigRoot.GetComponentsInChildren<Transform>(true);
        string first = all != null && all.Length > 0 ? all[0].name : "n/a";
        string last = all != null && all.Length > 0 ? all[all.Length - 1].name : "n/a";
        int count = all != null ? all.Length : 0;
        Debug.Log($"[dog_bones] count={count} first={first} last={last}");
    }


    private void LogDogMappingOnce(AnimalRigCache cache, Transform rigRoot)
    {
        if (cache == null || rigRoot == null || dogMappingLoggedRoots.Contains(rigRoot))
        {
            return;
        }

        dogMappingLoggedRoots.Add(rigRoot);
        Debug.Log(
            "[dog_map] " +
            $"7-8->{(cache.leftFrontUpper != null ? cache.leftFrontUpper.name : "null")} " +
            $"8-12->{(cache.leftFrontLower != null ? cache.leftFrontLower.name : "null")} " +
            $"12-16->{(cache.leftFrontPaw != null ? cache.leftFrontPaw.name : "null")} " +
            $"7-9->{(cache.rightFrontUpper != null ? cache.rightFrontUpper.name : "null")} " +
            $"9-13->{(cache.rightFrontLower != null ? cache.rightFrontLower.name : "null")} " +
            $"13-17->{(cache.rightFrontPaw != null ? cache.rightFrontPaw.name : "null")} " +
            $"6-10->{(cache.leftRearUpper != null ? cache.leftRearUpper.name : "null")} " +
            $"10-14->{(cache.leftRearLower != null ? cache.leftRearLower.name : "null")} " +
            $"14-18->{(cache.leftRearPaw != null ? cache.leftRearPaw.name : "null")} " +
            $"6-11->{(cache.rightRearUpper != null ? cache.rightRearUpper.name : "null")} " +
            $"11-15->{(cache.rightRearLower != null ? cache.rightRearLower.name : "null")} " +
            $"15-19->{(cache.rightRearPaw != null ? cache.rightRearPaw.name : "null")}");
    }


    private bool TrySelectDogAxisComparePair(
        AnimalRigCache cache,
        int jointCount,
        byte[] vis,
        float[] camZ,
        out Transform selectedBone,
        out int selectedIdxA,
        out int selectedIdxB,
        out int skipMissingBone,
        out int skipZEq0,
        out int skipVis0,
        out int skipOutOfRange,
        out int totalSegments)
    {
        selectedBone = null;
        selectedIdxA = -1;
        selectedIdxB = -1;
        skipMissingBone = 0;
        skipZEq0 = 0;
        skipVis0 = 0;
        skipOutOfRange = 0;
        totalSegments = 0;

        if (cache == null)
        {
            return false;
        }

        (int a, int b, Transform bone)[] segs = new[]
        {
            (7, 8, cache.leftFrontUpper),
            (8, 12, cache.leftFrontLower),
            (12, 16, cache.leftFrontPaw),
            (7, 9, cache.rightFrontUpper),
            (9, 13, cache.rightFrontLower),
            (13, 17, cache.rightFrontPaw),
            (6, 10, cache.leftRearUpper),
            (10, 14, cache.leftRearLower),
            (14, 18, cache.leftRearPaw),
            (6, 11, cache.rightRearUpper),
            (11, 15, cache.rightRearLower),
            (15, 19, cache.rightRearPaw),
        };

        for (int i = 0; i < segs.Length; i++)
        {
            totalSegments++;
            int a = segs[i].a;
            int b = segs[i].b;
            Transform bone = segs[i].bone;

            if (bone == null)
            {
                skipMissingBone++;
                continue;
            }
            if (a < 0 || b < 0 || a >= jointCount || b >= jointCount || vis == null || camZ == null || a >= vis.Length || b >= vis.Length || a >= camZ.Length || b >= camZ.Length)
            {
                skipOutOfRange++;
                continue;
            }
            if (vis[a] == 0 || vis[b] == 0)
            {
                skipVis0++;
                continue;
            }
            if (Mathf.Approximately(camZ[a], 0f) || Mathf.Approximately(camZ[b], 0f))
            {
                skipZEq0++;
                continue;
            }

            selectedBone = bone;
            selectedIdxA = a;
            selectedIdxB = b;
            return true;
        }

        return false;
    }


    private bool TryBuildDogHeadDirection(Vector3[] jointsWorld, byte[] vis, out Vector3 neckRoot, out Vector3 headTarget)
    {
        neckRoot = Vector3.zero;
        headTarget = Vector3.zero;

        // Fixed semantic indices provided by user:
        // 0=L_Eye, 1=R_Eye, 4=Nose, 5=Throat.
        bool hasThroat = TryGetJointPoint(jointsWorld, vis, 5, out Vector3 throat);
        bool hasNose = TryGetJointPoint(jointsWorld, vis, 4, out Vector3 nose);
        bool hasEyesMid = TryGetMidPoint(jointsWorld, vis, 0, 1, out Vector3 eyesMid);

        if (!hasThroat)
        {
            return false;
        }

        Vector3 sum = Vector3.zero;
        float w = 0f;
        if (hasNose)
        {
            sum += nose * 0.55f;
            w += 0.55f;
        }
        if (hasEyesMid)
        {
            sum += eyesMid * 0.45f;
            w += 0.45f;
        }

        if (w <= 0f)
        {
            return false;
        }

        neckRoot = throat;
        headTarget = sum / w;
        if ((headTarget - neckRoot).sqrMagnitude < 0.000001f)
        {
            return false;
        }

        return true;
    }

}

