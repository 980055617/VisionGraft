using UnityEditor;
using UnityEngine;

// One-time cleanup (2026-07-16): removes leftover standalone demo packages confirmed to have
// zero GUID references from any Resources/Models/Animal or Human prefab, any Scene, or any
// script (verified via cross-reference before running this).
public static class LegacyPackageRemover
{
    private static readonly string[] Folders =
    {
        "Assets/Tiger",
        "Assets/Wolf",
        "Assets/RedCambala",
        "Assets/Shepherd_Valley",
    };

    [MenuItem("Tools/VisionGraft/Delete Unused Legacy Demo Packages")]
    public static void Run()
    {
        foreach (var folder in Folders)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning("[LegacyPackageRemover] Not found (already removed?): " + folder);
                continue;
            }

            bool ok = AssetDatabase.DeleteAsset(folder);
            Debug.Log(ok ? "[LegacyPackageRemover] Deleted " + folder : "[LegacyPackageRemover] Delete FAILED for " + folder);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[LegacyPackageRemover] Done.");
    }
}
