using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Resources/Models 配下の prefab 名を一覧にして Resources へ書き出す。
//
// **なぜ要るか。**
// `Resources.LoadAll<GameObject>("Models/Animal")` は同期で、prefab だけでなく
// mesh / texture まで全部読む。実機で 75 prefab を読むのに **2304ms** かかっており
// （2026-09-04 実測: Human 867ms / Animal 1246ms / Else 195ms）、その間 Update が
// 一切回らない。VR では頭の向きだけコンポジタが再投影するので、
// 「コントローラーだけ空中で固まる」という見え方になる。
//
// 1 つずつ `Resources.LoadAsync` すればフレームを跨げるが、そのためには
// **読む前に名前を知っている必要がある**。Resources には一覧 API が無いので、
// ビルド時にここで作る。
//
// **staleness を心配しなくていいようにビルド前に必ず作り直す。** モデルを増やしても
// 手作業は要らない。エディタで試すときは Tools メニューから作れる。
// 万一これが無くても runtime は Resources.LoadAll に落ちるだけで、遅いが壊れはしない。
public sealed class ModelResourceIndexGenerator : IPreprocessBuildWithReport
{
    public const string IndexResourcePath = "Models/model_index";
    private const string IndexAssetPath = "Assets/Resources/Models/model_index.txt";
    private static readonly string[] Categories = { "Human", "Animal", "Else" };

    public int callbackOrder => 0;


    public void OnPreprocessBuild(BuildReport report)
    {
        Generate();
    }


    [MenuItem("Tools/VisionGraft/モデル一覧を作り直す")]
    public static void Generate()
    {
        var lines = new List<string>();
        for (int i = 0; i < Categories.Length; i++)
        {
            string category = Categories[i];
            string dir = "Assets/Resources/Models/" + category;
            if (!Directory.Exists(dir))
            {
                Debug.LogWarning($"[ModelIndex] {dir} がありません");
                continue;
            }

            // Resources.LoadAll と同じく再帰で拾い、同じ名前規則で絞る。
            // 規則がずれると runtime と一覧の中身が食い違う。
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { dir });
            var forCategory = new List<string>();
            for (int g = 0; g < guids.Length; g++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[g]);
                string name = Path.GetFileNameWithoutExtension(assetPath);
                if (!IsIndexedPrefabName(name))
                {
                    continue;
                }

                // "Assets/Resources/" を落として Resources.Load 用のパスにする。
                string resourcePath = assetPath
                    .Replace("Assets/Resources/", string.Empty)
                    .Replace(".prefab", string.Empty);
                forCategory.Add(resourcePath);
            }

            forCategory.Sort(string.CompareOrdinal);
            lines.AddRange(forCategory);
            Debug.Log($"[ModelIndex] {category}: {forCategory.Count} prefab");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(IndexAssetPath));
        File.WriteAllLines(IndexAssetPath, lines);
        AssetDatabase.ImportAsset(IndexAssetPath);
        Debug.Log($"[ModelIndex] {IndexAssetPath} に {lines.Count} 行を書きました");
    }


    // StreamingStereoVideoPlayer.IsIndexedPrefabName と同じ規則。
    // 2 桁ゼロ埋め番号 + "_" で始まるものだけを使う（Sources/ の素材を除くため）。
    private static bool IsIndexedPrefabName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               name.Length >= 3 &&
               char.IsDigit(name[0]) &&
               char.IsDigit(name[1]) &&
               name[2] == '_';
    }
}
