using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;

// Sport_Balls のマテリアルは Built-in RP の Standard シェーダー（fileID 46）のまま Saritasa アセットから
// 取り込まれており、プロジェクトが URP のため紫（エラーシェーダー）表示になる。URP 公式の
// MaterialUpgrader（StandardUpgrader）で Universal Render Pipeline/Lit に変換し、
// _MainTex→_BaseMap 等のプロパティ移行も自動で行う。ElseModelImporter と同じく
// Unity Editor バッチモードでの一回限りの実行を想定。
public static class SportBallMaterialUpgrader
{
    private static readonly string[] MaterialNames =
    {
        "Baseball", "Basketball", "Football", "Golf", "Soccer", "Tennis", "Volleyball"
    };

    private const string MaterialDir = "Assets/Saritasa/Models/Sport_Balls/Materials";

    [MenuItem("Tools/VisionGraft/Upgrade Sport Ball Materials To URP")]
    public static void Upgrade()
    {
        List<MaterialUpgrader> upgraders = new List<MaterialUpgrader> { new StandardUpgrader("Standard") };

        int upgraded = 0;
        foreach (string name in MaterialNames)
        {
            string path = $"{MaterialDir}/{name}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Debug.LogWarning($"[SportBallMaterialUpgrader] Not found: {path}");
                continue;
            }

            if (mat.shader != null && mat.shader.name == "Universal Render Pipeline/Lit")
            {
                Debug.Log($"[SportBallMaterialUpgrader] Already URP, skip: {path}");
                continue;
            }

            string message = string.Empty;
            if (MaterialUpgrader.Upgrade(mat, upgraders, MaterialUpgrader.UpgradeFlags.LogMessageWhenNoUpgraderFound, ref message))
            {
                upgraded++;
            }
            else
            {
                Debug.LogWarning($"[SportBallMaterialUpgrader] Upgrade failed for {path}: {message}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[SportBallMaterialUpgrader] Done. Upgraded {upgraded}/{MaterialNames.Length} materials to URP/Lit.");
    }
}
