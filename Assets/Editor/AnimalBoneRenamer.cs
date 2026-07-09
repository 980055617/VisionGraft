using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 全 Animal prefab のボーンを CONTEXT.md 正準名にリネームする。
/// VisionGraft メニュー → Rename Animal Bones To Canonical Names
/// </summary>
public static class AnimalBoneRenamer
{
    private const string AnimalFolder = "Assets/Resources/Models/Animal";

    // 正準名マッピング: prefab 名 → { 元のボーン名 → 正準名 }
    private static readonly Dictionary<string, Dictionary<string, string>> Maps =
        new Dictionary<string, Dictionary<string, string>>
    {
        // ─────────── 既存モデル ───────────
        ["Dog"] = new Dictionary<string, string>
        {
            ["ボーン"]          = "spine",
            ["ボーン.007"]      = "neck",
            ["ボーン.009"]      = "head",
            ["ボーン.003"]      = "tail_base",
            ["ボーン.004"]      = "tail_mid",
            ["ボーン.005"]      = "tail_tip",
            ["ボーン_L.001"]    = "front_l_upper",
            ["ボーン_L.002"]    = "front_l_lower",
            ["ボーン_L.003"]    = "front_l_paw",
            ["ボーン_R.001"]    = "front_r_upper",
            ["ボーン_R.002"]    = "front_r_lower",
            ["ボーン_R.003"]    = "front_r_paw",
            ["ボーン.001_L.001"] = "rear_l_upper",
            ["ボーン.001_L.002"] = "rear_l_lower",
            ["ボーン.001_L.003"] = "rear_l_paw",
            ["ボーン.001_L.004"] = "rear_l_toe",
            ["ボーン.001_R.001"] = "rear_r_upper",
            ["ボーン.001_R.002"] = "rear_r_lower",
            ["ボーン.001_R.003"] = "rear_r_paw",
            ["ボーン.001_R.004"] = "rear_r_toe",
        },

        ["GermanShepherd"] = new Dictionary<string, string>
        {
            ["DEF-spine"]          = "spine",
            ["DEF-spine.010"]      = "neck",
            ["DEF-spine.011"]      = "head",
            ["DEF-front_thigh.L"]  = "front_l_upper",
            ["DEF-front_shin.L"]   = "front_l_lower",
            ["DEF-front_foot.L"]   = "front_l_paw",
            ["DEF-front_thigh.R"]  = "front_r_upper",
            ["DEF-front_shin.R"]   = "front_r_lower",
            ["DEF-front_foot.R"]   = "front_r_paw",
            ["DEF-thigh.L"]        = "rear_l_upper",
            ["DEF-shin.L"]         = "rear_l_lower",
            ["DEF-foot.L"]         = "rear_l_paw",
            ["DEF-toe.L"]          = "rear_l_toe",
            ["DEF-thigh.R"]        = "rear_r_upper",
            ["DEF-shin.R"]         = "rear_r_lower",
            ["DEF-foot.R"]         = "rear_r_paw",
            ["DEF-toe.R"]          = "rear_r_toe",
            // GermanShepherd にはテールボーンがないためスキップ
        },

        ["WildBoar"] = new Dictionary<string, string>
        {
            ["Base"]      = "spine",
            ["Neck"]      = "neck",
            ["Head"]      = "head",
            ["Front.L1"]  = "front_l_upper",
            ["Front.L2"]  = "front_l_lower",
            ["Front.L3"]  = "front_l_paw",
            ["Front.R1"]  = "front_r_upper",
            ["Front.R2"]  = "front_r_lower",
            ["Front.R3"]  = "front_r_paw",
            ["Back.L1"]   = "rear_l_upper",
            ["Back.L2"]   = "rear_l_lower",
            ["Back.L3"]   = "rear_l_paw",
            ["Back.R1"]   = "rear_r_upper",
            ["Back.R2"]   = "rear_r_lower",
            ["Back.R3"]   = "rear_r_paw",
        },

        ["Tiger"] = new Dictionary<string, string>
        {
            ["spine1_hiResHips"]         = "spine",
            ["head1_neck"]               = "neck",
            ["head1_head"]               = "head",
            ["leftLeg1_HiResUpperLeg"]   = "front_l_upper",
            ["leftLeg1_HiResLowerLeg"]   = "front_l_lower",
            ["leftLeg1_HiResFoot"]       = "front_l_paw",
            ["rightLeg1_HiResUpperLeg"]  = "front_r_upper",
            ["rightLeg1_HiResLowerLeg"]  = "front_r_lower",
            ["rightLeg1_HiResFoot"]      = "front_r_paw",
            ["leftLeg2_HiResUpperLeg"]   = "rear_l_upper",
            ["leftLeg2_HiResLowerLeg"]   = "rear_l_lower",
            ["leftLeg2_HiResFoot"]       = "rear_l_paw",
            ["rightLeg2_HiResUpperLeg"]  = "rear_r_upper",
            ["rightLeg2_HiResLowerLeg"]  = "rear_r_lower",
            ["rightLeg2_HiResFoot"]      = "rear_r_paw",
            ["tail1_Joint1"]             = "tail_base",
            ["tail1_Joint7"]             = "tail_mid",
            ["tail1_Joint13"]            = "tail_tip",
        },

        // ─────────── 新モデル（wild_animals パック） ───────────

        ["Bear"] = new Dictionary<string, string>
        {
            ["Spine_bear"]   = "spine",
            ["Neck"]         = "neck",
            ["Head"]         = "head",
            ["Front_Leg_L"]  = "front_l_upper",
            ["Front_Knee_L"] = "front_l_lower",
            ["Front_Foot_L"] = "front_l_paw",
            ["Front_Reg_R"]  = "front_r_upper",   // FBX 内のタイポ
            ["Front_Knee_R"] = "front_r_lower",
            ["Front_Foot_R"] = "front_r_paw",
            ["Hind_Leg_L"]   = "rear_l_upper",
            ["Hind_Knee_L"]  = "rear_l_lower",
            ["Hind_Foot_L"]  = "rear_l_paw",
            ["Hind_Reg_R"]   = "rear_r_upper",    // FBX 内のタイポ（Hind_Leg_R の誤り）
            ["Hind_Knee_R"]  = "rear_r_lower",
            ["Hind_Foot_R"]  = "rear_r_paw",
            ["Tail_1"]       = "tail_base",
        },

        ["Fox"] = new Dictionary<string, string>
        {
            ["Spine_fox"]      = "spine",
            ["Neck"]           = "neck",
            ["Head"]           = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]   = "front_l_lower",
            ["Front_Foot_L"]   = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]   = "front_r_lower",
            ["Front_Foot_R"]   = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]    = "rear_l_lower",
            ["Hind_Foot_L"]    = "rear_l_paw",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]    = "rear_r_lower",
            ["Hind_Foot_R"]    = "rear_r_paw",
            ["Tail_1"]         = "tail_base",
            ["Tail_2"]         = "tail_mid",
        },

        ["Rabbit"] = new Dictionary<string, string>
        {
            ["Spine_rabbot"]    = "spine",   // FBX 内のタイポ
            ["Neck"]            = "neck",
            ["Head"]            = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]    = "front_l_lower",
            ["Front_Foot_L"]    = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]    = "front_r_lower",
            ["Front_Foot_R"]    = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]     = "rear_l_lower",
            ["Hind_Foot_L"]     = "rear_l_paw",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]     = "rear_r_lower",
            ["Hind_Foot_R"]     = "rear_r_paw",
            ["Tail_1"]          = "tail_base",
        },

        ["Squirrel"] = new Dictionary<string, string>
        {
            ["Spine_squirel"]  = "spine",   // FBX 内のタイポ
            ["Neck"]           = "neck",
            ["Head"]           = "head",
            ["Front_Leg_L"]    = "front_l_upper",
            ["Front_Knee_L"]   = "front_l_lower",
            ["Front_Foot_L"]   = "front_l_paw",
            ["Front_Leg_R"]    = "front_r_upper",
            ["Front_Knee_R"]   = "front_r_lower",
            ["Front_Foot_R"]   = "front_r_paw",
            ["Hind_Leg_L"]     = "rear_l_upper",
            ["Hind_Knee_L"]    = "rear_l_lower",
            ["Hind_Foot_L"]    = "rear_l_paw",
            ["Hind_Reg_R"]     = "rear_r_upper",  // FBX 内のタイポ
            ["Hind_Knee_R"]    = "rear_r_lower",
            ["Hind_Foot_R"]    = "rear_r_paw",
            ["Tail_1"]         = "tail_base",
            ["Tail_2"]         = "tail_mid",
            ["Tail_3"]         = "tail_tip",
        },

        ["Hedhog"] = new Dictionary<string, string>
        {
            ["Spine_hedgog"]    = "spine",  // FBX 内のタイポ
            ["Neck"]            = "neck",
            // Head ボーンなし（ハリネズミは頭骨なし）
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]    = "front_l_lower",
            ["Front_Foot_L"]    = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]    = "front_r_lower",
            ["Front_Foot_R"]    = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]     = "rear_l_lower",
            ["Hind_Foot_L"]     = "rear_l_paw",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]     = "rear_r_lower",
            ["Hind_Foot_R"]     = "rear_r_paw",
        },

        ["Deer_1"] = new Dictionary<string, string>
        {
            ["Spine_deer1"]     = "spine",
            ["Neck"]            = "neck",
            ["Head"]            = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]    = "front_l_lower",
            ["Front_Foot_L"]    = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]    = "front_r_lower",
            ["Front_Foot_R"]    = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]     = "rear_l_lower",
            ["Hind_Foot_L"]     = "rear_l_paw",
            ["Hind_Foot_L_1"]   = "rear_l_toe",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]     = "rear_r_lower",
            ["Hind_Foot_R"]     = "rear_r_paw",
            ["Hind_Foot_R_1"]   = "rear_r_toe",
            ["Tail_1"]          = "tail_base",
        },

        ["Deer_2"] = new Dictionary<string, string>
        {
            ["Spine_deer1"]     = "spine",   // deer_2 も同じ spine 名
            ["Neck"]            = "neck",
            ["Head"]            = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]    = "front_l_lower",
            ["Front_Foot_L"]    = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]    = "front_r_lower",
            ["Front_Foot_R"]    = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]     = "rear_l_lower",
            ["Hind_Fooot_L"]    = "rear_l_paw",  // タイポ (3個のo)
            ["Hind_Hoof_L"]     = "rear_l_toe",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]     = "rear_r_lower",
            ["Hind_Fooot_R"]    = "rear_r_paw",  // タイポ (3個のo)
            ["Hind_Hoof_R"]     = "rear_r_toe",
            ["Tail_1"]          = "tail_base",
        },

        ["Boar"] = new Dictionary<string, string>
        {
            ["Spine_boar"]      = "spine",
            ["Neck"]            = "neck",
            ["Head"]            = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]    = "front_l_lower",
            ["Front_Foot"]      = "front_l_paw",      // L/R 区別なし
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]    = "front_r_lower",
            ["Front_Foot.001"]  = "front_r_paw",
            ["Hind_Shoulder_L"] = "rear_l_upper",
            ["Hind_Knee_L"]     = "rear_l_lower",
            ["Hind_Foot_L"]     = "rear_l_paw",
            ["Hind_Shoulder_R"] = "rear_r_upper",
            ["Hind_Knee_R"]     = "rear_r_lower",
            ["Hind_Foot_R"]     = "rear_r_paw",
            ["Tail_1"]          = "tail_base",
        },

        // Wolf1: 旧 Wolf アセットパック（診断ログのボーン名に基づく）
        ["Wolf1"] = new Dictionary<string, string>
        {
            ["Spine2"]       = "spine",
            ["Neck"]         = "neck",
            // "head" は FBX 側でそのまま "head" — リネーム不要
            ["tail_1"]       = "tail_base",
            ["tail_2"]       = "tail_mid",
            ["tail_3"]       = "tail_tip",
            ["LegFront1.L"]  = "front_l_upper",
            ["LegFront2.L"]  = "front_l_lower",
            ["FrontFoot1.L"] = "front_l_paw",
            // FrontFoot2.L/R は前足の先端（toe）だが canonical に front_toe がないためスキップ
            ["LegFront1.R"]  = "front_r_upper",
            ["LegFront2.R"]  = "front_r_lower",
            ["FrontFoot1.R"] = "front_r_paw",
            ["LegBack1.L"]   = "rear_l_upper",
            ["LegBack2.L"]   = "rear_l_lower",
            ["BackFoot1.L"]  = "rear_l_paw",
            ["BackFoot2.L"]  = "rear_l_toe",
            ["LegBack1.R"]   = "rear_r_upper",
            ["LegBack2.R"]   = "rear_r_lower",
            ["BackFoot1.R"]  = "rear_r_paw",
            ["BackFoot2.R"]  = "rear_r_toe",
        },

        // Wolf2: 新 wild animals パック (wolf.fbx)
        ["Wolf2"] = new Dictionary<string, string>
        {
            ["Spine_wolf"]       = "spine",
            ["Neck"]             = "neck",
            ["Head"]             = "head",
            ["Front_Shoulder_L"] = "front_l_upper",
            ["Front_Knee_L"]     = "front_l_lower",
            ["Front_Foot_L"]     = "front_l_paw",
            ["Front_Shoulder_R"] = "front_r_upper",
            ["Front_Knee_R"]     = "front_r_lower",
            ["Front_Foot_R"]     = "front_r_paw",
            ["Hind_Shoulder_L"]  = "rear_l_upper",
            ["Hind_Knee_L"]      = "rear_l_lower",
            ["Hind_Foot_L"]      = "rear_l_paw",
            ["Hind_Shoulder_R"]  = "rear_r_upper",
            ["Hind_Knee_R"]      = "rear_r_lower",
            ["Hind_Foot_R"]      = "rear_r_paw",
            ["Tail_1"]           = "tail_base",
        },

        // Cow: GermanShepherd と同系の Blender DEF- rig（診断ログで確認済み）
        // spine 番号は DEF-spine.002 〜 .018 が存在。.004 = 胴体中央、.012/.015 = 首/頭 付近（推測）
        ["Cow"] = new Dictionary<string, string>
        {
            ["DEF-spine.004"]      = "spine",
            ["DEF-spine.012"]      = "neck",
            ["DEF-spine.015"]      = "head",
            ["DEF-front_thigh.L"]  = "front_l_upper",
            ["DEF-front_shin.L"]   = "front_l_lower",
            ["DEF-front_foot.L"]   = "front_l_paw",
            ["DEF-front_thigh.R"]  = "front_r_upper",
            ["DEF-front_shin.R"]   = "front_r_lower",
            ["DEF-front_foot.R"]   = "front_r_paw",
            ["DEF-thigh.L"]        = "rear_l_upper",
            ["DEF-shin.L"]         = "rear_l_lower",
            ["DEF-foot.L"]         = "rear_l_paw",
            ["DEF-thigh.R"]        = "rear_r_upper",
            ["DEF-shin.R"]         = "rear_r_lower",
            ["DEF-foot.R"]         = "rear_r_paw",
            ["DEF-tail"]           = "tail_base",
            ["DEF-tail.001"]       = "tail_mid",
            ["DEF-tail.002"]       = "tail_tip",
        },

        // Owl は鳥（二足）のため対象外 → ログ警告のみ
    };

    // Owl は鳥（二足）のため SMAL FK 非対応 → 警告のみ
    private static readonly string[] SkippedPrefabs = { "Owl" };

    [MenuItem("VisionGraft/Rename Animal Bones To Canonical Names")]
    public static void Run()
    {
        foreach (string skipped in SkippedPrefabs)
        {
            Debug.LogWarning($"[BoneRenamer] {skipped}.prefab: スキップ — 鳥（二足）のため SMAL FK 非対応。対象外。");
        }

        int totalPrefabs = 0;
        int totalBones = 0;

        foreach (var entry in Maps)
        {
            string prefabName = entry.Key;
            Dictionary<string, string> map = entry.Value;
            string prefabPath = $"{AnimalFolder}/{prefabName}.prefab";

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            if (prefabRoot == null)
            {
                Debug.LogWarning($"[BoneRenamer] {prefabName}.prefab が見つかりません: {prefabPath}");
                continue;
            }

            Transform[] all = prefabRoot.GetComponentsInChildren<Transform>(true);
            int renamed = 0;

            foreach (var rename in map)
            {
                string original = rename.Key;
                string canonical = rename.Value;

                Transform found = FindByExactName(all, original);
                if (found != null)
                {
                    found.name = canonical;
                    renamed++;
                }
                else
                {
                    Debug.LogWarning($"[BoneRenamer] {prefabName}: ボーン '{original}' が見つかりません");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            PrefabUtility.UnloadPrefabContents(prefabRoot);

            Debug.Log($"[BoneRenamer] {prefabName}: {renamed}/{map.Count} ボーンをリネーム");
            totalPrefabs++;
            totalBones += renamed;
        }

        AssetDatabase.Refresh();
        Debug.Log($"[BoneRenamer] 完了 — {totalPrefabs} prefab, {totalBones} ボーンをリネーム");
    }

    private static Transform FindByExactName(Transform[] transforms, string name)
    {
        foreach (Transform t in transforms)
        {
            if (t.name == name)
                return t;
        }
        return null;
    }
}
