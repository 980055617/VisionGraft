using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Diagnostic: reproduces StreamingStereoVideoPlayer's LoadPrefabsFromResources +
// SortByPriority ordering exactly, so the actual runtime index -> model name mapping can be
// read without opening Play Mode. Used to match user-reported "index N is broken" reports to
// real prefab names before renaming everything with index prefixes.
public static class AnimalIndexDumper
{
    private static readonly string[] AnimalModelPriorityOrder =
    {
        "0_Dog", "1_Wolf", "2_WildBoar", "3_Buffalo", "4_Lion", "5_Horse",
    };

    [MenuItem("Tools/VisionGraft/Dump Current Animal Index Order")]
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

        filtered.Sort((a, b) =>
        {
            int indexA = System.Array.IndexOf(AnimalModelPriorityOrder, a.name);
            int indexB = System.Array.IndexOf(AnimalModelPriorityOrder, b.name);
            if (indexA < 0) indexA = AnimalModelPriorityOrder.Length;
            if (indexB < 0) indexB = AnimalModelPriorityOrder.Length;
            return indexA != indexB
                ? indexA.CompareTo(indexB)
                : string.Compare(a.name, b.name, System.StringComparison.Ordinal);
        });

        var sb = new StringBuilder();
        for (int i = 0; i < filtered.Count; i++)
        {
            string path = AssetDatabase.GetAssetPath(filtered[i]);
            sb.AppendLine($"{i}\t{filtered[i].name}\t{path}");
        }

        string outPath = Path.Combine(Application.dataPath, "..", "animal_index_order.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("Wrote " + outPath + " (" + filtered.Count + " entries)");
    }
}
