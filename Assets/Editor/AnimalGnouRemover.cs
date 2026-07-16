using UnityEditor;
using UnityEngine;

// One-time tool: removes 28_Gnou.prefab from the selectable Animal list. Its body has no
// matching base-color texture anywhere in the source package (only Gnou_Antler.png exists;
// the main body texture is likely only embedded in the accompanying Gnou.gltf/.bin, not
// exposed as an importable PNG) so it can't be fixed the way BoarV2/Deer1.0/Elk1.0/
// Moose/Moose1.0/Pronghorn1.0 were.
public static class AnimalGnouRemover
{
    [MenuItem("Tools/VisionGraft/Remove Gnou Prefab")]
    public static void Run()
    {
        const string path = "Assets/Resources/Models/Animal/28_Gnou.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
        {
            Debug.LogWarning("[AnimalGnouRemover] Not found (already removed?): " + path);
            return;
        }

        bool ok = AssetDatabase.DeleteAsset(path);
        Debug.Log(ok ? "[AnimalGnouRemover] Deleted " + path : "[AnimalGnouRemover] Delete FAILED for " + path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}
