using UnityEditor;
using UnityEngine;

// Fixes materials embedded in some "50+ Animated Animals" FBX files that imported with no
// base-color texture assigned (material name didn't match any texture filename, so Unity's
// automatic material-texture linking silently skipped them - unrelated to bone renaming).
// Confirmed via Tools > VisionGraft > Dump Animal Diagnostics: BoarV2(14)/Deer1.0(17)/
// Elk1.0(22) all had mainTex=False/baseMap=False. Gnou(28) has no matching texture file at
// all in its package folder (likely only embedded in its .gltf/.bin, not exposed as a
// separate importable PNG) so it is not fixed here.
public static class AnimalMaterialTextureFixer
{
    private struct Fix
    {
        public string fbxPath;
        public string materialName;
        public string texturePath;
    }

    private static readonly Fix[] Fixes =
    {
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Boar/Boar.fbx", materialName = "MI_SusScrofa_M", texturePath = "Assets/50+ Animated Animals/2.0/Boar/Boar_M_BC.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Boar/Boar.fbx", materialName = "Lower", texturePath = "Assets/50+ Animated Animals/2.0/Boar/Lower.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Boar/Boar.fbx", materialName = "Material.001", texturePath = "Assets/50+ Animated Animals/2.0/Boar/Lower.png" },

        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Animated.fbx", materialName = "Deer_Body", texturePath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Body.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Animated.fbx", materialName = "Deer_Head", texturePath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Head.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Animated.fbx", materialName = "Chifre", texturePath = "Assets/50+ Animated Animals/2.0/Deer 1.0/Deer Horn.png" },

        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Animated.fbx", materialName = "Head", texturePath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Head.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Animated.fbx", materialName = "Body", texturePath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Body.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Animated.fbx", materialName = "Chifre", texturePath = "Assets/50+ Animated Animals/2.0/Elk 1.0/Elk Horn.png" },

        new Fix { fbxPath = "Assets/50+ Animated Animals/1.0/Moose/Moose.fbx", materialName = "Moose_Antler", texturePath = "Assets/50+ Animated Animals/1.0/Moose/textures/T_moose_antler.png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Moose 1.0/Moose Animated.fbx", materialName = "Moose_Chifre", texturePath = "Assets/50+ Animated Animals/2.0/Moose 1.0/Moose Horn.png" },

        // (1)=head (face/eyes/horns), (2)=teeth, (3)=body fur - confirmed by viewing the PNGs
        // directly after the initial file-size-based guess put Body/Head backwards (2026-07-16).
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn Animated.fbx", materialName = "Pronghorn_Head", texturePath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn (1).png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn Animated.fbx", materialName = "Pronghorn_Teef", texturePath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn (2).png" },
        new Fix { fbxPath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn Animated.fbx", materialName = "Pronghorn_Body", texturePath = "Assets/50+ Animated Animals/2.0/Pronghorn 1.0/Pronghorn (3).png" },
    };

    [MenuItem("Tools/VisionGraft/Fix Animal Material Textures")]
    public static void Run()
    {
        int fixedCount = 0;
        foreach (var fix in Fixes)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(fix.texturePath);
            if (texture == null)
            {
                Debug.LogError($"[AnimalMaterialTextureFixer] Texture not found: {fix.texturePath}");
                continue;
            }

            // Already-extracted materials (from a prior run) live as a standalone .mat next
            // to the FBX - check that first, since a material that's been extracted no longer
            // exists as an embedded sub-asset of the FBX at all (re-running the embedded-search
            // below would wrongly report "not found").
            string folder = System.IO.Path.GetDirectoryName(fix.fbxPath).Replace('\\', '/');
            string matPath = $"{folder}/{fix.materialName}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            if (material == null)
            {
                Material embedded = null;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fix.fbxPath))
                {
                    if (asset is Material m && m.name == fix.materialName)
                    {
                        embedded = m;
                        break;
                    }
                }

                if (embedded == null)
                {
                    Debug.LogError($"[AnimalMaterialTextureFixer] Material '{fix.materialName}' not found in {fix.fbxPath}");
                    continue;
                }

                // Embedded FBX materials get regenerated on reimport, so editing them in place
                // (SetTexture + SetDirty + SaveAssets) silently doesn't persist. Extract to a
                // standalone .mat next to the FBX first - Unity automatically redirects the
                // model's material slot to the extracted asset, and edits on it persist normally.
                string error = AssetDatabase.ExtractAsset(embedded, matPath);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogError($"[AnimalMaterialTextureFixer] ExtractAsset failed for {fix.materialName}: {error}");
                    continue;
                }
                AssetDatabase.Refresh();
                material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            }

            if (material == null)
            {
                Debug.LogError($"[AnimalMaterialTextureFixer] Extracted material not found at {matPath}");
                continue;
            }

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            EditorUtility.SetDirty(material);
            fixedCount++;
            Debug.Log($"[AnimalMaterialTextureFixer] {matPath} -> {fix.texturePath}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[AnimalMaterialTextureFixer] Done. Fixed {fixedCount}/{Fixes.Length}.");
    }
}
