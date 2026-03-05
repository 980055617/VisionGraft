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
}
