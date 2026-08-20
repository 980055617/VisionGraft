using System.Text;
using UnityEditor;
using UnityEngine;

// Human prefab の脚・腕の Humanoid ボーン間に中間ボーン（twist 等）が挟まっているかを出す。
// 骨長補正は LowerLeg / Foot の localPosition を倍率するので、間に別ボーンがあると
// 区間全体が倍率にならず補正が効かない。その検証用の一時ツール。
public static class BoneHierarchyDump
{
    public static void Run()
    {
        string path = "Assets/Resources/Models/Human/00_Female_A_01.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) { Debug.Log("[HIER] prefab not found: " + path); EditorApplication.Exit(0); return; }

        var go = Object.Instantiate(prefab);
        var anim = go.GetComponentInChildren<Animator>(true);
        if (anim == null || !anim.isHuman) { Debug.Log("[HIER] not humanoid"); Object.DestroyImmediate(go); EditorApplication.Exit(0); return; }

        (HumanBodyBones, HumanBodyBones, string)[] pairs =
        {
            (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  "大腿L"),
            (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      "下腿L"),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, "大腿R"),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     "下腿R"),
            (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  "上腕L"),
            (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      "前腕L"),
            (HumanBodyBones.Hips,          HumanBodyBones.Neck,          "胴"),
        };

        foreach (var (a, b, label) in pairs)
        {
            Transform ta = anim.GetBoneTransform(a), tb = anim.GetBoneTransform(b);
            if (ta == null || tb == null) { Debug.Log($"[HIER] {label}: ボーンなし"); continue; }

            var chain = new StringBuilder();
            int hops = 0;
            for (Transform t = tb; t != null && t != ta; t = t.parent)
            {
                chain.Insert(0, " <- " + t.name);
                hops++;
                if (hops > 12) break;
            }
            float world = Vector3.Distance(ta.position, tb.position);
            float local = tb.localPosition.magnitude;
            Debug.Log($"[HIER] {label}: hops={hops} world={world:F4} " +
                      $"tb.localPosition.mag={local:F4} 比={(world > 0 ? local / world : 0):F3} " +
                      $"chain={ta.name}{chain}");
        }

        // 実行時の [BONEIN] と同じ項目を prefab 側でも出して突き合わせる。
        float Dist(HumanBodyBones a, HumanBodyBones b)
        {
            Transform ta = anim.GetBoneTransform(a), tb = anim.GetBoneTransform(b);
            return (ta != null && tb != null) ? Vector3.Distance(ta.position, tb.position) : 0f;
        }

        float torso = Dist(HumanBodyBones.Hips, HumanBodyBones.Neck);
        float uArm = Dist(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        float fArm = Dist(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        float thigh = Dist(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        Transform lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
        Debug.Log($"[PREFABIN] torso={torso:F4} uArm={uArm:F4} fArm={fArm:F4} thigh={thigh:F4}" +
                  $" | 胴で正規化 fArm={fArm / torso:F3} uArm={uArm / torso:F3} thigh={thigh / torso:F3}" +
                  $" | lossyScale={go.transform.lossyScale.x:F4}" +
                  $" hand={(lh != null ? lh.name : "-")} handLocalMag={(lh != null ? lh.localPosition.magnitude : 0f):F4}" +
                  $" animatorEnabled={anim.enabled} applyRootMotion={anim.applyRootMotion}" +
                  $" controller={(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "なし")}");

        Object.DestroyImmediate(go);
        EditorApplication.Exit(0);
    }
}
