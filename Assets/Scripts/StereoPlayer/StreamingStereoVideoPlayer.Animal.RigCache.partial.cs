using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
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

        // Animal rig concrete parent bones (derived from mesh-node parents):
        // body->Bone, neck->Bone.007, head.001->Bone.009,
        // er.L/R->Bone.009_L/R.001,
        // arm.001/002/003.L/R->Bone_L/R.001/002/003,
        // foot.001/002/003.L/R->Bone.001_L/R.001/002/003.
        cache.neck = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.007", "neck" }, "neck");
        cache.head = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.009", "head.001" }, "head");
        cache.spine = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3", "body" }, "body", "spine", "chest", "back");
        cache.tailBase = FindBoneByTokens(bones, "tail.002", "tail");

        cache.leftFrontUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.001", "arm.001.L" }, "arm.001.l");
        cache.leftFrontLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.002", "arm.002.L" }, "arm.002.l");
        cache.leftFrontPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_L.003", "arm.003.L" }, "arm.003.l");
        cache.rightFrontUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.001", "arm.001.R" }, "arm.001.r");
        cache.rightFrontLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.002", "arm.002.R" }, "arm.002.r");
        cache.rightFrontPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3_R.003", "arm.003.R" }, "arm.003.r");
        cache.leftRearUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.001", "foot.001.L" }, "foot.001.l", "foot.002.l");
        cache.leftRearLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.002", "foot.002.L" }, "foot.002.l", "foot.003.l");
        cache.leftRearPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_L.003", "foot.003.L" }, "foot.003.l", "foot.004.l");
        cache.rightRearUpper = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.001", "foot.001.R" }, "foot.001.r", "foot.002.r");
        cache.rightRearLower = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.002", "foot.002.R" }, "foot.002.r", "foot.003.r");
        cache.rightRearPaw = FindAnimalBone(bones, new[] { "\u30DC\u30FC\u30F3.001_R.003", "foot.003.R" }, "foot.003.r", "foot.004.r");

        PrimeAnimalBinds(
            cache,
            cache.neck, cache.head, cache.spine, cache.tailBase,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw);

        RegisterAnimalAimPairs(
            cache,
            cache.leftFrontUpper, cache.leftFrontLower,
            cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower,
            cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower,
            cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower,
            cache.rightRearLower, cache.rightRearPaw,
            cache.neck, cache.head,
            cache.spine, cache.neck);

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;
        return cache;
    }

    private Transform FindAnimalBone(Transform[] bones, string[] exactNames, params string[] tokens)
    {
        Transform exact = FindBoneByExactNames(bones, exactNames);
        return exact ?? FindBoneByTokens(bones, tokens);
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

    private Transform ResolveLikelyRigBone(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        // In this animal rig asset, names like arm.001.L / foot.001.L are mesh nodes.
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
}
