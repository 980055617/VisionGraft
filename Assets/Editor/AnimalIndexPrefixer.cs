using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// One-time tool: prefixes EVERY prefab in Resources/Models/Animal with its current runtime
// array index (2-digit zero-padded, e.g. "07_Badger"), so the selectedAnimalIndex Inspector
// field / in-VR picker index always maps to an unambiguous, stable filename - no more guessing
// from alphabetical order. Zero-padding (not the plain "0_"/"1_" scheme used for the first 6
// priority models) matters here: Unity's ordinal string sort would otherwise put "10_Foo"
// before "6_Bar" (since '1' < '6' as characters), silently breaking the index<->name mapping
// for the 7th model onward. Must be run with Unity Editor closed (batch mode only, since it
// mirrors StreamingStereoVideoPlayer's own load+sort order via a live Resources.LoadAll call).
public static class AnimalIndexPrefixer
{
    // All models already carry a 2-digit zero-padded index prefix from the first pass, so
    // plain ordinal sort of the current name already reproduces the intended order (no
    // priority-list indirection needed anymore) - this just re-derives it after a model gets
    // deleted/added, so numbering has no gaps and stays index==position.
    [MenuItem("Tools/VisionGraft/Prefix All Animal Models With Index (2-digit)")]
    public static void Run()
    {
        GameObject[] all = Resources.LoadAll<GameObject>("Models/Animal");
        var filtered = new List<GameObject>();
        foreach (var go in all)
        {
            if (go != null && go.name.Length > 0 && (char.IsUpper(go.name[0]) || char.IsDigit(go.name[0])))
            {
                filtered.Add(go);
            }
        }

        filtered.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));

        // Capture (path, targetName) up front before renaming anything, so later renames
        // can't perturb the AssetDatabase paths captured for earlier entries.
        var plan = new List<(string path, string targetName)>();
        for (int i = 0; i < filtered.Count; i++)
        {
            string path = AssetDatabase.GetAssetPath(filtered[i]);
            string bareName = StripExistingDigitPrefix(filtered[i].name);
            string targetName = $"{i:D2}_{bareName}";
            plan.Add((path, targetName));
        }

        int renamed = 0;
        foreach (var (path, targetName) in plan)
        {
            var current = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (current == null)
            {
                Debug.LogError("[AnimalIndexPrefixer] Missing at rename time: " + path);
                continue;
            }
            if (current.name == targetName)
            {
                continue;
            }

            string error = AssetDatabase.RenameAsset(path, targetName);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[AnimalIndexPrefixer] Rename failed for {path} -> {targetName}: {error}");
                continue;
            }
            renamed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[AnimalIndexPrefixer] Done. Renamed {renamed}/{plan.Count}.");
    }

    private static string StripExistingDigitPrefix(string rawName)
    {
        int underscoreIndex = rawName.IndexOf('_');
        if (underscoreIndex <= 0)
        {
            return rawName;
        }
        for (int i = 0; i < underscoreIndex; i++)
        {
            if (!char.IsDigit(rawName[i]))
            {
                return rawName;
            }
        }
        return rawName.Substring(underscoreIndex + 1);
    }
}
