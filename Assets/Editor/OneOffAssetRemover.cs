using UnityEditor;
using UnityEngine;

// Removes AnimalRigBoneRenamer.cs's now-defunct sibling: WolfPrefabSetup.cs referenced
// Assets/Wolf/Models/Wolf.fbx, which was deleted as part of the 2026-07-16 unused-package
// cleanup. Its job (creating Wolf1/Wolf2.prefab from the old package) is moot - the current
// lineup uses 01_Wolf.prefab sourced from the "50+ Animated Animals" package instead.
public static class OneOffAssetRemover
{
    [MenuItem("Tools/VisionGraft/Delete WolfPrefabSetup Script")]
    public static void Run()
    {
        const string path = "Assets/Editor/WolfPrefabSetup.cs";
        if (AssetDatabase.LoadAssetAtPath<MonoScript>(path) == null)
        {
            Debug.LogWarning("[OneOffAssetRemover] Not found (already removed?): " + path);
            return;
        }
        bool ok = AssetDatabase.DeleteAsset(path);
        Debug.Log(ok ? "[OneOffAssetRemover] Deleted " + path : "[OneOffAssetRemover] Delete FAILED for " + path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}
