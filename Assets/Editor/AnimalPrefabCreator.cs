using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public static class AnimalPrefabCreator
{
    const string DestDir = "Assets/Resources/Models/Animal";

    static readonly string[] SearchRoots = {
        "Assets/50+ Animated Animals/1.0",
        "Assets/50+ Animated Animals/2.0",
    };

    static readonly string[] AnimationPrefixes = {
        "Loco_", "Attack_", "Death_", "Trans_", "Add_", "Alligator_",
        "Hit_", "Stand_", "Sitting_", "Lying_", "StandHind_", "StandAngry_",
        "CanonBall", "SittingSpec_",
    };

    static readonly string[] SkipKeywords = { "DEMO", "Skethfab", "Sketchfab" };

    [MenuItem("Tools/Create Animal Prefabs from 50+ Animated Animals")]
    public static void CreatePrefabs()
    {
        if (!Directory.Exists(DestDir))
            Directory.CreateDirectory(DestDir);

        var usedNames = new HashSet<string>();
        int created = 0, skipped = 0;

        foreach (string root in SearchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (string animalDir in Directory.GetDirectories(root).OrderBy(d => d))
            {
                string folderName = Path.GetFileName(animalDir);

                string modelPath = FindMainModel(animalDir);
                if (modelPath == null)
                {
                    Debug.Log($"[AnimalPrefabCreator] No model found in: {folderName}");
                    skipped++;
                    continue;
                }

                string prefabName = BuildPrefabName(folderName, usedNames);
                usedNames.Add(prefabName);

                string prefabPath = $"{DestDir}/{prefabName}.prefab";

                if (File.Exists(prefabPath))
                {
                    Debug.Log($"[AnimalPrefabCreator] Already exists, skipping: {prefabName}");
                    skipped++;
                    continue;
                }

                GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (source == null)
                {
                    Debug.LogWarning($"[AnimalPrefabCreator] Failed to load: {modelPath}");
                    skipped++;
                    continue;
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                Object.DestroyImmediate(instance);

                Debug.Log($"[AnimalPrefabCreator] Created: {prefabName}.prefab <- {modelPath}");
                created++;
            }
        }

        AssetDatabase.Refresh();
        Debug.Log($"[AnimalPrefabCreator] Done. created={created}, skipped={skipped}");
        EditorUtility.DisplayDialog("Animal Prefabs", $"完了\n作成: {created}\nスキップ: {skipped}", "OK");
    }

    static string FindMainModel(string dir)
    {
        var candidates = Directory
            .GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                string ext = Path.GetExtension(f).ToLower();
                if (ext != ".fbx" && ext != ".gltf") return false;
                string name = Path.GetFileNameWithoutExtension(f);
                if (SkipKeywords.Any(k => name.Contains(k))) return false;
                if (AnimationPrefixes.Any(p => name.StartsWith(p))) return false;
                return true;
            })
            .Select(f => f.Replace("\\", "/"))
            .ToList();

        if (candidates.Count == 0) return null;

        string folderName = Path.GetFileName(dir);

        // "Animated" 版を優先
        string animated = candidates.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).ToLower().Contains("animated"));
        if (animated != null) return animated;

        // フォルダ名と一致するものを優先（スペース/アンダースコア無視）
        string clean = folderName.Replace(" ", "").Replace("_", "").ToLower();
        string matched = candidates.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Replace(" ", "").Replace("_", "").ToLower() == clean);
        if (matched != null) return matched;

        // FBXを優先（gltfより）
        string fbx = candidates.FirstOrDefault(f => Path.GetExtension(f).ToLower() == ".fbx");
        return fbx ?? candidates[0];
    }

    static string BuildPrefabName(string folderName, HashSet<string> used)
    {
        // スペース・ハイフン除去してPascalCase化
        string name = string.Join("", folderName.Split(' ', '-', '_')
            .Where(s => s.Length > 0)
            .Select(s => char.ToUpper(s[0]) + s.Substring(1)));

        if (!used.Contains(name)) return name;

        // 衝突時は V2, V3... を付ける
        for (int i = 2; i < 99; i++)
        {
            string candidate = name + "V" + i;
            if (!used.Contains(candidate)) return candidate;
        }
        return name + "_dup";
    }
}
