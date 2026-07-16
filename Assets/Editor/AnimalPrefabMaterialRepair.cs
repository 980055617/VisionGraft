using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Repairs Renderer.sharedMaterials slots that AnimalMaterialTextureFixer's
// AssetDatabase.ExtractAsset call broke on prefabs that were already fully unpacked/detached
// from their source FBX (14_BoarV2/17_Deer1.0/22_Elk1.0) - extracting an embedded material to
// a standalone .mat changes its identity, and a detached prefab's own serialized material
// reference does not get retargeted automatically the way the live model asset's reference
// does. This directly reassigns the now-extracted (and texture-fixed) material by renderer
// name, since the broken slots read back as null (no name to match by).
public static class AnimalPrefabMaterialRepair
{
    private struct Patch
    {
        public string prefabPath;
        public string rendererName;
        public int materialIndex;
        public string materialPath;
    }

    private static readonly Patch[] Patches =
    {
        new Patch { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", rendererName = "Boar_M_Fur.001", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Boar/MI_SusScrofa_M.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", rendererName = "Boar_Lower.001", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Boar/Lower.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", rendererName = "Boar_Lower", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Boar/Material.001.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", rendererName = "Boar_Upper.001", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Boar/Lower.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", rendererName = "Boar_Upper", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Boar/Lower.mat" },

        new Patch { prefabPath = "Assets/Resources/Models/Animal/17_Deer1.0.prefab", rendererName = "sm_4_0_0", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer_Body.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/17_Deer1.0.prefab", rendererName = "sm_4_0_0", materialIndex = 1, materialPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer_Head.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/17_Deer1.0.prefab", rendererName = "Chifre", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Chifre.mat" },

        new Patch { prefabPath = "Assets/Resources/Models/Animal/22_Elk1.0.prefab", rendererName = "sm_2_0_1", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Head.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/22_Elk1.0.prefab", rendererName = "sm_2_0_1", materialIndex = 2, materialPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Body.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/22_Elk1.0.prefab", rendererName = "Chifre", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Chifre.mat" },

        new Patch { prefabPath = "Assets/Resources/Models/Animal/41_Moose.prefab", rendererName = "Antler_Moose", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/1.0/Moose/Moose_Antler.mat" },

        new Patch { prefabPath = "Assets/Resources/Models/Animal/42_Moose1.0.prefab", rendererName = "Chifre_Moose", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Moose 1.0/Moose_Chifre.mat" },

        new Patch { prefabPath = "Assets/Resources/Models/Animal/47_Pronghorn1.0.prefab", rendererName = "sm_1_0_0", materialIndex = 0, materialPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn_Body.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/47_Pronghorn1.0.prefab", rendererName = "sm_1_0_0", materialIndex = 1, materialPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn_Teef.mat" },
        new Patch { prefabPath = "Assets/Resources/Models/Animal/47_Pronghorn1.0.prefab", rendererName = "sm_1_0_0", materialIndex = 2, materialPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn_Head.mat" },
    };

    [MenuItem("Tools/VisionGraft/Repair Animal Prefab Materials")]
    public static void Run()
    {
        var byPrefab = new Dictionary<string, List<Patch>>();
        foreach (var patch in Patches)
        {
            if (!byPrefab.TryGetValue(patch.prefabPath, out var list))
            {
                list = new List<Patch>();
                byPrefab[patch.prefabPath] = list;
            }
            list.Add(patch);
        }

        int applied = 0;
        foreach (var kvp in byPrefab)
        {
            string prefabPath = kvp.Key;
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (var patch in kvp.Value)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(patch.materialPath);
                    if (material == null)
                    {
                        Debug.LogError($"[AnimalPrefabMaterialRepair] Material not found: {patch.materialPath}");
                        continue;
                    }

                    Renderer target = null;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r.name == patch.rendererName)
                        {
                            target = r;
                            break;
                        }
                    }
                    if (target == null)
                    {
                        Debug.LogError($"[AnimalPrefabMaterialRepair] Renderer '{patch.rendererName}' not found in {prefabPath}");
                        continue;
                    }

                    var mats = target.sharedMaterials;
                    if (patch.materialIndex < 0 || patch.materialIndex >= mats.Length)
                    {
                        Debug.LogError($"[AnimalPrefabMaterialRepair] materialIndex {patch.materialIndex} out of range on '{patch.rendererName}' ({mats.Length} slots)");
                        continue;
                    }
                    mats[patch.materialIndex] = material;
                    target.sharedMaterials = mats;
                    applied++;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Debug.Log($"[AnimalPrefabMaterialRepair] Saved {prefabPath}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AnimalPrefabMaterialRepair] Done. Applied {applied}/{Patches.Length} patches.");
    }
}
