using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Same treatment as AnimalIndexPrefixer, applied to Resources/Models/Human: prefixes every
// model with its current runtime array index (2-digit zero-padded), so selectedHumanIndex /
// the in-VR picker index always maps to an unambiguous filename. Also dumps the resulting
// order to a text file for verification. Run with Unity Editor closed (batch mode).
public static class HumanIndexPrefixer
{
    [MenuItem("Tools/VisionGraft/Dump Current Human Index Order")]
    public static void DumpOnly()
    {
        var filtered = Collect();
        var sb = new StringBuilder();
        for (int i = 0; i < filtered.Count; i++)
        {
            sb.AppendLine($"{i}\t{filtered[i].name}\t{AssetDatabase.GetAssetPath(filtered[i])}");
        }
        string outPath = Path.Combine(Application.dataPath, "..", "human_index_order.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("Wrote " + outPath + " (" + filtered.Count + " entries)");
    }

    [MenuItem("Tools/VisionGraft/Prefix All Human Models With Index (2-digit)")]
    public static void Run()
    {
        var filtered = Collect();

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
                Debug.LogError("[HumanIndexPrefixer] Missing at rename time: " + path);
                continue;
            }
            if (current.name == targetName)
            {
                continue;
            }

            string error = AssetDatabase.RenameAsset(path, targetName);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[HumanIndexPrefixer] Rename failed for {path} -> {targetName}: {error}");
                continue;
            }
            renamed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[HumanIndexPrefixer] Done. Renamed {renamed}/{plan.Count}.");
    }

    private static List<GameObject> Collect()
    {
        GameObject[] all = Resources.LoadAll<GameObject>("Models/Human");
        var filtered = new List<GameObject>();
        foreach (var go in all)
        {
            if (go != null && go.name.Length > 0 && (char.IsUpper(go.name[0]) || char.IsDigit(go.name[0])))
            {
                filtered.Add(go);
            }
        }
        filtered.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
        return filtered;
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
