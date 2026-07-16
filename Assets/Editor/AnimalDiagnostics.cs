using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Diagnostic for user-reported issues (2026-07-16): white/no-skin materials on some models,
// Lion (04) being tiny, tail behavior. Dumps renderer/material/bounds info without needing
// Play Mode.
public static class AnimalDiagnostics
{
    [MenuItem("Tools/VisionGraft/Dump Animal Diagnostics (Reported Issues)")]
    public static void Run()
    {
        string[] paths =
        {
            "Assets/Resources/Models/Animal/00_Dog.prefab",
            "Assets/Resources/Models/Animal/04_Lion.prefab",
            "Assets/Resources/Models/Animal/13_Bloodhund.prefab",
            "Assets/Resources/Models/Animal/38_Lioness.prefab",
            "Assets/Resources/Models/Animal/49_Puma.prefab",
            "Assets/Resources/Models/Animal/51_Racoon.prefab",
            "Assets/Resources/Models/Animal/14_BoarV2.prefab",
            "Assets/Resources/Models/Animal/15_Deer.prefab",
            "Assets/Resources/Models/Animal/16_Deer1.prefab",
            "Assets/Resources/Models/Animal/17_Deer1.0.prefab",
            "Assets/Resources/Models/Animal/22_Elk1.0.prefab",
            "Assets/Resources/Models/Animal/30_Goose.prefab",
            "Assets/Resources/Models/Animal/41_Moose.prefab",
            "Assets/Resources/Models/Animal/42_Moose1.0.prefab",
            "Assets/Resources/Models/Animal/47_Pronghorn1.0.prefab",
        };

        var sb = new StringBuilder();
        foreach (string path in paths)
        {
            sb.AppendLine("=== " + path + " ===");
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                sb.AppendLine("(load failed)");
                continue;
            }

            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var mrs = go.GetComponentsInChildren<MeshRenderer>(true);
            sb.AppendLine($"SkinnedMeshRenderers: {smrs.Length}, MeshRenderers: {mrs.Length}");
            foreach (var smr in smrs)
            {
                sb.AppendLine($"  SMR '{smr.name}' mesh={(smr.sharedMesh != null ? smr.sharedMesh.name : "NULL")} bounds={smr.localBounds.size:F3}");
                var mats = smr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null)
                    {
                        sb.AppendLine($"    mat[{i}] = NULL");
                        continue;
                    }
                    string shaderName = mat.shader != null ? mat.shader.name : "NULL-SHADER";
                    bool hasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
                    bool hasBaseMap = mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null;
                    sb.AppendLine($"    mat[{i}] '{mat.name}' shader='{shaderName}' mainTex={hasMainTex} baseMap={hasBaseMap}");
                }
            }
            foreach (var mr in mrs)
            {
                var mats = mr.sharedMaterials;
                sb.AppendLine($"  MR '{mr.name}' materials={mats.Length}");
                foreach (var mat in mats)
                {
                    if (mat == null) { sb.AppendLine("    mat = NULL"); continue; }
                    bool hasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
                    bool hasBaseMap = mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null;
                    sb.AppendLine($"    mat '{mat.name}' shader='{(mat.shader != null ? mat.shader.name : "NULL")}' mainTex={hasMainTex} baseMap={hasBaseMap}");
                }
            }

            // Overall bounds (world-space, at identity root transform) for scale comparison.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(go);
            Bounds? combined = null;
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (combined == null) combined = r.bounds;
                else combined.Value.Encapsulate(r.bounds);
            }
            if (combined.HasValue)
            {
                sb.AppendLine($"Combined world bounds size (at prefab's own scale): {combined.Value.size:F4}");
            }
            Object.DestroyImmediate(instance);

            sb.AppendLine();
        }

        string outPath = Path.Combine(Application.dataPath, "..", "animal_diagnostics.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("Wrote " + outPath);
    }
}
