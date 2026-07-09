using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 全 Animal prefab のボーン名を診断する。
/// VisionGraft → Diagnose Animal Rig Bones
///
/// 出力内容:
///   Found   — canonical names のうち既にリネーム済みのもの
///   MISSING — canonical names のうちまだ見つからないもの
///   Unmapped rig nodes — RendererなしのTransformのうちcanonical name以外のもの（マッピング候補）
/// </summary>
public static class AnimalBoneDiagnostics
{
    private const string AnimalFolder = "Assets/Resources/Models/Animal";

    private static readonly string[] CanonicalNames =
    {
        "spine", "neck", "head",
        "tail_base", "tail_mid", "tail_tip",
        "front_l_upper", "front_l_lower", "front_l_paw",
        "front_r_upper", "front_r_lower", "front_r_paw",
        "rear_l_upper",  "rear_l_lower",  "rear_l_paw",  "rear_l_toe",
        "rear_r_upper",  "rear_r_lower",  "rear_r_paw",  "rear_r_toe",
    };

    // SMAL FK の必須 5 ボーン
    private static readonly HashSet<string> SmalFkRequired = new HashSet<string>
    {
        "spine", "front_l_upper", "front_r_upper", "rear_l_upper", "rear_r_upper"
    };

    [MenuItem("VisionGraft/Diagnose Animal Rig Bones")]
    public static void Run()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { AnimalFolder });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Contains("/Sources/")) continue;

            string prefabName = System.IO.Path.GetFileNameWithoutExtension(path);

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                Debug.LogWarning($"[AnimalBoneDiag] {prefabName}: LoadPrefabContents 失敗");
                continue;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            HashSet<string> allNames = new HashSet<string>(all.Select(t => t.name));
            HashSet<string> canonicalSet = new HashSet<string>(CanonicalNames);

            List<string> found   = CanonicalNames.Where(n => allNames.Contains(n)).ToList();
            List<string> missing = CanonicalNames.Where(n => !allNames.Contains(n)).ToList();

            // Renderer なしの Transform のうち canonical でない名前 = FBX 側のボーン名
            List<string> unmapped = all
                .Where(t => t.GetComponent<Renderer>() == null && !canonicalSet.Contains(t.name))
                .Select(t => t.name)
                .Distinct()
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

            // SMAL FK 判定
            bool smalFkReady = SmalFkRequired.All(n => allNames.Contains(n));

            string missingRequired = string.Join(", ",
                SmalFkRequired.Where(n => !allNames.Contains(n)));

            Debug.Log(
                $"[AnimalBoneDiag] ===== {prefabName} =====\n" +
                $"  SMAL FK 実行可能: {(smalFkReady ? "YES" : "NO")} " +
                $"{(smalFkReady ? "" : $"(不足: {missingRequired})")}\n" +
                $"  Found  ({found.Count}/20): {string.Join(", ", found)}\n" +
                $"  MISSING ({missing.Count}): {(missing.Count == 0 ? "none" : string.Join(", ", missing))}\n" +
                $"  Unmapped rig nodes: {(unmapped.Count == 0 ? "none" : string.Join(", ", unmapped))}");

            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[AnimalBoneDiag] 診断完了 — Console を確認してください");
    }
}
