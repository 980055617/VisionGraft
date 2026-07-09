using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Assets/Resources/Models/Animal/ 内の FBX ソースファイルを
/// Sources/ サブフォルダに移動し、Prefab のみをルートに残す。
/// VisionGraft メニュー → Move Animal FBX to Sources Subfolder
/// </summary>
public static class AnimalFolderOrganizer
{
    private const string AnimalFolder  = "Assets/Resources/Models/Animal";
    private const string SourcesFolder = "Assets/Resources/Models/Animal/Sources";

    [MenuItem("VisionGraft/Move Animal FBX to Sources Subfolder")]
    public static void Run()
    {
        // Sources フォルダ作成（なければ）
        if (!AssetDatabase.IsValidFolder(SourcesFolder))
        {
            AssetDatabase.CreateFolder(AnimalFolder, "Sources");
            AssetDatabase.Refresh();
            Debug.Log($"[FolderOrganizer] フォルダ作成: {SourcesFolder}");
        }

        // Animal フォルダ直下の .fbx を全て移動
        string[] guids = AssetDatabase.FindAssets("t:Model", new[] { AnimalFolder });
        int moved = 0;

        foreach (string guid in guids)
        {
            string srcPath = AssetDatabase.GUIDToAssetPath(guid);

            // サブフォルダ内のものはスキップ
            if (!Path.GetDirectoryName(srcPath)
                    .Replace('\\', '/')
                    .Equals(AnimalFolder, System.StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = Path.GetFileName(srcPath);
            string dstPath  = $"{SourcesFolder}/{fileName}";

            string err = AssetDatabase.MoveAsset(srcPath, dstPath);
            if (string.IsNullOrEmpty(err))
            {
                Debug.Log($"[FolderOrganizer] 移動: {srcPath} → {dstPath}");
                moved++;
            }
            else
            {
                Debug.LogError($"[FolderOrganizer] 移動失敗 ({fileName}): {err}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[FolderOrganizer] 完了 — {moved} 件の FBX を Sources/ に移動しました");
    }
}
