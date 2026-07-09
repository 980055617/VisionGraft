using UnityEngine;
using UnityEditor;

/// <summary>
/// 旧 Wolf (Assets/Wolf/) から Wolf1.prefab を作成し、
/// 新 wolf.fbx の Wolf.prefab を Wolf2.prefab にリネームする。
/// VisionGraft メニュー → Setup Wolf1 and Wolf2 Prefabs
/// </summary>
public static class WolfPrefabSetup
{
    private const string OldWolfFbx  = "Assets/Wolf/Models/Wolf.fbx";
    private const string AnimalFolder = "Assets/Resources/Models/Animal";
    private const string Wolf1Path    = "Assets/Resources/Models/Animal/Wolf1.prefab";
    private const string Wolf2Path    = "Assets/Resources/Models/Animal/Wolf2.prefab";
    private const string WolfPath     = "Assets/Resources/Models/Animal/Wolf.prefab";

    [MenuItem("VisionGraft/Setup Wolf1 and Wolf2 Prefabs")]
    public static void Run()
    {
        // ── Wolf1: 旧 Wolf アセットから作成 ──────────────────────────
        GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(OldWolfFbx);
        if (fbxRoot == null)
        {
            Debug.LogError($"[WolfSetup] 旧 Wolf FBX が見つかりません: {OldWolfFbx}");
            return;
        }

        // FBX をインスタンス化（既存マテリアルはそのまま維持）
        GameObject wolf1Instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        if (wolf1Instance.GetComponent<Animator>() == null)
            wolf1Instance.AddComponent<Animator>();

        PrefabUtility.SaveAsPrefabAsset(wolf1Instance, Wolf1Path);
        Object.DestroyImmediate(wolf1Instance);
        Debug.Log($"[WolfSetup] Wolf1.prefab 作成完了: {Wolf1Path}");

        // ── Wolf2: 現在の Wolf.prefab をリネーム ─────────────────────
        bool wolf2Exists = AssetDatabase.LoadAssetAtPath<GameObject>(Wolf2Path) != null;
        if (wolf2Exists)
        {
            Debug.Log("[WolfSetup] Wolf2.prefab は既に存在します。スキップ。");
        }
        else
        {
            bool wolfExists = AssetDatabase.LoadAssetAtPath<GameObject>(WolfPath) != null;
            if (!wolfExists)
            {
                Debug.LogWarning("[WolfSetup] Wolf.prefab が見つかりません。Wolf2 のリネームをスキップ。");
            }
            else
            {
                string err = AssetDatabase.RenameAsset(WolfPath, "Wolf2");
                if (string.IsNullOrEmpty(err))
                    Debug.Log($"[WolfSetup] Wolf.prefab → Wolf2.prefab にリネーム完了");
                else
                    Debug.LogError($"[WolfSetup] リネーム失敗: {err}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[WolfSetup] 完了。BoneRenamer も実行してください。");
    }
}
