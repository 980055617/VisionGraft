using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 表示 Human モデルの「ボーンから体表面までの距離」を実測する一時ツール。
//
// めり込み判定で使う"部位の太さ"を身長比のラフな仮定（胴 0.13 など）で置いていたが、
// 半径のつもりで直径相当の値を使っている疑いがあるため実測する。
//
// 各頂点は boneWeights で実際にスキニングされているボーンが分かるので、最大重みの
// ボーンを所属とし、**そのボーンの軸（親→子の線分）への垂直距離**の分布を出す。
// 中央値がその部位の実効半径になる。
//
// 失敗した測り方 2 つ（2026-08-19）:
//   1. 対象ボーンを絞って「最寄りボーンを総当たり」→ 右半身の頂点が Neck 等に流れ込み膨張
//   2. ボーンの「位置」からの距離 → 関節は骨の端点なので、骨に沿った頂点まで拾って膨張
//      （LeftLowerLeg が 0.286 m と、すねの長さの 7 割になった）
public static class BodyThicknessDump
{
    public static void Run()
    {
        var all = Resources.LoadAll<GameObject>("Models/Human");
        int idx = Mathf.Clamp(16, 0, all.Length - 1);
        GameObject prefab = all[idx];
        Debug.Log($"[THICK] Resources {all.Length} 件 / index {idx} = {prefab.name}");

        var go = Object.Instantiate(prefab);
        var anim = go.GetComponentInChildren<Animator>(true);
        if (anim == null || !anim.isHuman)
        {
            Debug.Log("[THICK] humanoid でない");
            Object.DestroyImmediate(go); EditorApplication.Exit(0); return;
        }

        Transform head = anim.GetBoneTransform(HumanBodyBones.Head);
        Transform foot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        float skelH = (head != null && foot != null)
            ? Vector3.Distance(head.position, foot.position) / 0.89f : 1f;
        Debug.Log($"[THICK] 骨格身長 = {skelH:F4} m（この値で正規化）");

        // Transform -> HumanBodyBones の逆引き
        var boneName = new Dictionary<Transform, string>();
        foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (b == HumanBodyBones.LastBone) { continue; }
            Transform t = anim.GetBoneTransform(b);
            if (t != null && !boneName.ContainsKey(t)) { boneName[t] = b.ToString(); }
        }

        var buckets = new Dictionary<string, List<float>>();

        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh shared = smr.sharedMesh;
            if (shared == null) { continue; }
            BoneWeight[] w = shared.boneWeights;
            Transform[] bones = smr.bones;
            if (w.Length == 0 || bones == null || bones.Length == 0) { continue; }

            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            Vector3[] verts = baked.vertices;
            Transform mt = smr.transform;

            int n = Mathf.Min(verts.Length, w.Length);
            for (int i = 0; i < n; i++)
            {
                int bi = w[i].boneIndex0;
                if (bi < 0 || bi >= bones.Length || bones[bi] == null) { continue; }
                Transform bt = bones[bi];
                // Humanoid に載っていないボーン（twist / 指の末端など）は、
                // 親をたどって最初に見つかる Humanoid ボーンに寄せる。
                string label = null;
                for (Transform t = bt; t != null; t = t.parent)
                {
                    if (boneName.TryGetValue(t, out string nm)) { label = nm; bt = t; break; }
                }
                if (label == null) { continue; }

                // ボーンの軸（自分 → 子）への垂直距離を測る。子が無ければ親→自分を使う。
                Transform child = ChildOf(anim, label);
                Vector3 a = bt.position;
                Vector3 bEnd = child != null ? child.position
                              : (bt.parent != null ? a + (a - bt.parent.position) : a + Vector3.up * 0.1f);
                float d = DistanceToSegment(mt.TransformPoint(verts[i]), a, bEnd);
                if (!buckets.TryGetValue(label, out var list))
                {
                    list = new List<float>(); buckets[label] = list;
                }
                list.Add(d);
            }
            Object.DestroyImmediate(baked);
        }

        string[] order =
        {
            "Hips","Spine","Chest","UpperChest","Neck","Head",
            "LeftUpperArm","LeftLowerArm","LeftHand",
            "LeftUpperLeg","LeftLowerLeg","LeftFoot","LeftToes",
        };
        foreach (var key in order)
        {
            if (!buckets.TryGetValue(key, out var d) || d.Count < 20) { continue; }
            d.Sort();
            float med = d[d.Count / 2];
            Debug.Log($"[THICK] {key,-14} n={d.Count,6} p50={med:F4} p75={d[d.Count * 3 / 4]:F4} " +
                      $"p90={d[d.Count * 9 / 10]:F4} m | 身長比 p50={med / skelH:F4} p90={d[d.Count * 9 / 10] / skelH:F4}");
        }

        Object.DestroyImmediate(go);
        EditorApplication.Exit(0);
    }

    // 軸を張るための子ボーン。Humanoid の連結順に沿って選ぶ。
    private static Transform ChildOf(Animator anim, string label)
    {
        switch (label)
        {
            case "Hips":         return anim.GetBoneTransform(HumanBodyBones.Spine);
            case "Spine":        return anim.GetBoneTransform(HumanBodyBones.Chest);
            case "Chest":        return anim.GetBoneTransform(HumanBodyBones.UpperChest)
                                     ?? anim.GetBoneTransform(HumanBodyBones.Neck);
            case "UpperChest":   return anim.GetBoneTransform(HumanBodyBones.Neck);
            case "Neck":         return anim.GetBoneTransform(HumanBodyBones.Head);
            case "LeftUpperArm": return anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            case "LeftLowerArm": return anim.GetBoneTransform(HumanBodyBones.LeftHand);
            case "LeftUpperLeg": return anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            case "LeftLowerLeg": return anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            case "LeftFoot":     return anim.GetBoneTransform(HumanBodyBones.LeftToes);
            default:             return null;
        }
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-10f) { return Vector3.Distance(p, a); }
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
        return Vector3.Distance(p, a + ab * t);
    }
}
