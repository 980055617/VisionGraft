using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Animal モデルの「鼻先の方向」を、頭ボーンに付いたメッシュの頂点から実測する。
//
// なぜ要るか（2026-09-11）:
//   SMAL 側は `Head -> Mouth`（joint 16 -> 32）という明確な軸を持つのに、
//   **Unity 側のリグには口・鼻のボーンが無い**。頭の子は頭のメッシュと左右の耳だけで、
//   既定の照準は「頭メッシュの bounds 中心」へ向いており鼻先ではない。
//   その結果 `jointFrameMap` が別のものどうしを対応づけ、頭の向きが合わない
//   （Docs/smpl-retargeting.md「頭の照準」）。
//
// 何をするか:
//   頭ボーンの配下にあるメッシュの頂点を読み、**頭ボーン原点から最も遠い頂点**を鼻先とみなす。
//   方向は頭ボーンのローカル座標で出す（bindDirLocal と同じ単位）。
//
//   メッシュは Read/Write が無効だと `Mesh.vertices` が空になるので、
//   **importer を一時的に読み書き可にして読み、必ず元へ戻す。**
//
// 実行:
//   Unity.exe -batchmode -quit -projectPath <proj> \
//             -executeMethod AnimalHeadAimBaker.Bake -logFile <log> \
//             [-prefab Assets/Resources/Models/Animal/00_Dog.prefab] [-out <json>]
//   引数を省くと Assets/Resources/Models/Animal 配下の prefab をすべて処理する。
//
// 出力は JSON（prefab 名 -> 頭ローカルの方向）。runtime 側で読み込む前提。
public static class AnimalHeadAimBaker
{
    private const string DefaultFolder = "Assets/Resources/Models/Animal";
    private const string DefaultOut = "Assets/Resources/animal_head_aim.json";

    [MenuItem("Tools/VisionGraft/Bake Animal Head Aim")]
    public static void Bake()
    {
        string prefabPath = null;
        string outPath = null;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-prefab") { prefabPath = args[i + 1]; }
            if (args[i] == "-out") { outPath = args[i + 1]; }
        }
        if (string.IsNullOrEmpty(outPath)) { outPath = DefaultOut; }

        List<string> targets = new List<string>();
        if (!string.IsNullOrEmpty(prefabPath))
        {
            targets.Add(prefabPath);
        }
        else
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { DefaultFolder }))
            {
                targets.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            targets.Sort(StringComparer.Ordinal);
        }

        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        int ok = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (TryBakeOne(targets[i], out Vector3 aimLocal, out string note))
            {
                if (ok > 0) { json.Append(",\n"); }
                json.Append("  \"").Append(Path.GetFileNameWithoutExtension(targets[i])).Append("\": [")
                    .Append(aimLocal.x.ToString("F6")).Append(", ")
                    .Append(aimLocal.y.ToString("F6")).Append(", ")
                    .Append(aimLocal.z.ToString("F6")).Append("]");
                ok++;
            }
            Debug.Log("[HEADAIMBAKE] " + Path.GetFileNameWithoutExtension(targets[i]) + " " + note);
        }
        json.Append("\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, json.ToString(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log("[HEADAIMBAKE] 書き出した " + outPath + "  成功 " + ok + " / " + targets.Count);
    }

    private static bool TryBakeOne(string prefabPath, out Vector3 aimLocal, out string note)
    {
        aimLocal = Vector3.zero;
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { note = "prefab を読めない"; return false; }

        Transform head = FindHead(prefab.transform);
        if (head == null) { note = "head ボーンが見つからない"; return false; }

        // 頭の配下（頭自身も含む）にあるメッシュを集める。
        List<MeshFilter> filters = new List<MeshFilter>(head.GetComponentsInChildren<MeshFilter>(true));
        if (filters.Count == 0) { note = "頭の配下にメッシュが無い"; return false; }

        // 耳は別パーツで、鼻先より遠いことがある。名前で除く。
        filters.RemoveAll(f => f == null || f.sharedMesh == null || IsEar(f.transform));
        if (filters.Count == 0) { note = "耳を除くとメッシュが残らない"; return false; }

        HashSet<string> reimported = new HashSet<string>();
        Dictionary<string, bool> restore = new Dictionary<string, bool>();
        try
        {
            foreach (MeshFilter f in filters)
            {
                string src = AssetDatabase.GetAssetPath(f.sharedMesh);
                if (string.IsNullOrEmpty(src) || reimported.Contains(src)) { continue; }
                ModelImporter mi = AssetImporter.GetAtPath(src) as ModelImporter;
                if (mi == null || mi.isReadable) { reimported.Add(src); continue; }
                restore[src] = mi.isReadable;
                mi.isReadable = true;
                AssetDatabase.ImportAsset(src, ImportAssetOptions.ForceSynchronousImport);
                reimported.Add(src);
            }

            // 再インポート後は参照が入れ替わるので取り直す。
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            head = FindHead(prefab.transform);
            filters = new List<MeshFilter>(head.GetComponentsInChildren<MeshFilter>(true));
            filters.RemoveAll(f => f == null || f.sharedMesh == null || IsEar(f.transform));

            float best = -1f;
            Vector3 bestLocal = Vector3.zero;
            int total = 0;
            foreach (MeshFilter f in filters)
            {
                Vector3[] vs = f.sharedMesh.vertices;
                if (vs == null || vs.Length == 0) { continue; }
                total += vs.Length;
                for (int vi = 0; vi < vs.Length; vi++)
                {
                    // メッシュのローカル -> 頭ボーンのローカル
                    Vector3 inHead = head.InverseTransformPoint(f.transform.TransformPoint(vs[vi]));
                    float d = inHead.sqrMagnitude;
                    if (d > best) { best = d; bestLocal = inHead; }
                }
            }

            if (total == 0 || best <= 0f) { note = "頂点を読めなかった（Read/Write を戻した）"; return false; }

            aimLocal = bestLocal.normalized;
            note = "頂点 " + total + " 個  鼻先(headローカル)=" + aimLocal.ToString("F4") +
                "  距離=" + Mathf.Sqrt(best).ToString("F4");
            return true;
        }
        finally
        {
            foreach (KeyValuePair<string, bool> kv in restore)
            {
                ModelImporter mi = AssetImporter.GetAtPath(kv.Key) as ModelImporter;
                if (mi != null)
                {
                    mi.isReadable = kv.Value;
                    AssetDatabase.ImportAsset(kv.Key, ImportAssetOptions.ForceSynchronousImport);
                }
            }
        }
    }

    private static bool IsEar(Transform t)
    {
        string n = t.name.ToLowerInvariant();
        return n.Contains("ear") || n.StartsWith("er.") || n.Contains("_ear");
    }

    private static Transform FindHead(Transform root)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (string.Equals(t.name, "head", StringComparison.OrdinalIgnoreCase)) { return t; }
        }
        return null;
    }
}
