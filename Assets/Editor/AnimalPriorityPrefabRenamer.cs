using UnityEditor;
using UnityEngine;

// One-time tool: renames the priority Animal prefabs in Resources/Models/Animal with a
// leading "<index>_" prefix matching their position in AnimalModelPriorityOrder
// (StreamingStereoVideoPlayer.Core.partial.cs), so the Project window makes it obvious
// at a glance which number to type into the selectedAnimalIndex Inspector field.
// Must stay in sync with AnimalModelPriorityOrder - update both when adding a new
// priority species (see docs/animal-bone-rename-mapping.md).
public static class AnimalPriorityPrefabRenamer
{
    private static readonly (string path, string newName)[] Renames =
    {
        ("Assets/Resources/Models/Animal/Dog.prefab", "0_Dog"),
        ("Assets/Resources/Models/Animal/Wolf.prefab", "1_Wolf"),
        ("Assets/Resources/Models/Animal/WildBoar.prefab", "2_WildBoar"),
        ("Assets/Resources/Models/Animal/Buffalo.prefab", "3_Buffalo"),
        ("Assets/Resources/Models/Animal/Lion.prefab", "4_Lion"),
        ("Assets/Resources/Models/Animal/Horse.prefab", "5_Horse"),
    };

    [MenuItem("Tools/VisionGraft/Rename Priority Animal Prefabs (Index Prefix)")]
    public static void Run()
    {
        foreach (var (path, newName) in Renames)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                Debug.LogWarning("[AnimalPriorityPrefabRenamer] Not found (already renamed?): " + path);
                continue;
            }

            string error = AssetDatabase.RenameAsset(path, newName);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[AnimalPriorityPrefabRenamer] Rename failed for {path}: {error}");
                continue;
            }

            Debug.Log($"[AnimalPriorityPrefabRenamer] {path} -> {newName}.prefab");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[AnimalPriorityPrefabRenamer] Done.");
    }
}
