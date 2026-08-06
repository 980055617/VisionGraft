using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Turns the Renderpeople rigged FBX files under Assets/RP_Character/ into Resources/Models/Human
// prefabs, the same shape as the existing npc_casual_set_00 based entries. The FBX metas ship
// with animationType=Humanoid / avatarSetup=CreateFromThisModel, so instantiating and saving is
// enough - no rig work is needed here.
//
// Index prefixes continue the existing 00_..13_ range (14/15/16), so every selectedHumanIndex,
// trackModelIndices entry and experiment log recorded so far keeps pointing at the same model.
// See HumanIndexPrefixer for the renumbering rule these names have to satisfy.
public static class RenderpeopleHumanPrefabBuilder
{
    private const string HumanResourceDir = "Assets/Resources/Models/Human";

    private static readonly (string fbxPath, string prefabName)[] Entries =
    {
        ("Assets/RP_Character/rp_carla_rigged_001/rp_carla_rigged_001_u3d.fbx",     "14_Female_Carla"),
        ("Assets/RP_Character/rp_claudia_rigged_002/rp_claudia_rigged_002_u3d.fbx", "15_Female_Claudia"),
        ("Assets/RP_Character/rp_eric_rigged_001/rp_eric_rigged_001_u3d.fbx",       "16_Male_Eric"),
    };

    [MenuItem("Tools/VisionGraft/Build Renderpeople Human Prefabs")]
    public static void Run()
    {
        AssetDatabase.Refresh();

        int built = 0;
        foreach (var (fbxPath, prefabName) in Entries)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
            {
                Debug.LogError($"[RenderpeopleHuman] FBX not found: {fbxPath}");
                continue;
            }

            string prefabPath = $"{HumanResourceDir}/{prefabName}.prefab";
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            if (instance == null)
            {
                Debug.LogError($"[RenderpeopleHuman] Could not instantiate {fbxPath}");
                continue;
            }

            instance.name = prefabName;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
            Object.DestroyImmediate(instance);

            if (!success || saved == null)
            {
                Debug.LogError($"[RenderpeopleHuman] SaveAsPrefabAsset failed: {prefabPath}");
                continue;
            }

            built++;
            Debug.Log($"[RenderpeopleHuman] Built {prefabPath}\n{DescribeRig(saved)}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[RenderpeopleHuman] Done. Built {built}/{Entries.Length}.");

        DumpHumanIndexOrder();
    }

    // Mirrors LoadPrefabsFromResources() in StreamingStereoVideoPlayer.Core.partial.cs so the
    // dump shows the exact index each model will get at runtime.
    [MenuItem("Tools/VisionGraft/Dump Human Index Order (runtime rule)")]
    public static void DumpHumanIndexOrder()
    {
        GameObject[] all = Resources.LoadAll<GameObject>("Models/Human");
        var indexed = new List<GameObject>(all.Length);
        foreach (var go in all)
        {
            if (go != null && go.name.Length >= 3 &&
                char.IsDigit(go.name[0]) && char.IsDigit(go.name[1]) && go.name[2] == '_')
            {
                indexed.Add(go);
            }
        }
        indexed.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        var sb = new StringBuilder();
        bool contiguous = true;
        for (int i = 0; i < indexed.Count; i++)
        {
            string name = indexed[i].name;
            bool matches = name.StartsWith($"{i:D2}_", System.StringComparison.Ordinal);
            contiguous &= matches;
            sb.AppendLine($"{i}\t{name}\t{(matches ? "ok" : "INDEX MISMATCH")}\t{AssetDatabase.GetAssetPath(indexed[i])}");
        }

        string outPath = Path.Combine(Application.dataPath, "..", "human_index_order.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[RenderpeopleHuman] {indexed.Count} human models, contiguous={contiguous}\n{sb}");
    }

    private static string DescribeRig(GameObject prefab)
    {
        var animator = prefab.GetComponentInChildren<Animator>();
        string avatarInfo = animator == null || animator.avatar == null
            ? "avatar=MISSING"
            : $"avatar={animator.avatar.name} isHuman={animator.avatar.isHuman} isValid={animator.avatar.isValid}";

        var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>();
        string sizeInfo = "bounds=none";
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].sharedMesh != null ? renderers[0].sharedMesh.bounds : new Bounds();
            for (int i = 1; i < renderers.Length; i++)
            {
                if (renderers[i].sharedMesh != null)
                {
                    b.Encapsulate(renderers[i].sharedMesh.bounds);
                }
            }
            sizeInfo = $"meshBoundsSize={b.size} (height={b.size.y:F3}m)";
        }

        string materialInfo = "materials=";
        foreach (var r in renderers)
        {
            foreach (var m in r.sharedMaterials)
            {
                materialInfo += m == null ? "NULL " : $"{m.name}({(m.shader != null ? m.shader.name : "no shader")}) ";
            }
        }

        return $"  {avatarInfo}\n  {sizeInfo}\n  {materialInfo}";
    }
}
