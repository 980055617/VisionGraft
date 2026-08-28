using System.Text;
using UnityEditor;
using UnityEngine;

// モデルのマテリアル設定を出力する診断。透過（ひげ・毛）の見え方を調べるのに使う。
//
//   Unity.exe -batchmode -projectPath . -executeMethod MaterialDiagnostics.DumpAnimalMaterials -quit
//
// 見るもの: シェーダ、Surface Type（0=Opaque / 1=Transparent）、Alpha Clipping、
// render queue、_BaseMap / _BaseColor のアルファ。
public static class MaterialDiagnostics
{
    // 調べたい prefab。増やすときはここに足す。
    private static readonly string[] TargetPrefabs =
    {
        "Assets/Resources/Models/Animal/39_Lynx.prefab",
        "Assets/Resources/Models/Animal/36_LabradorDog.prefab",
    };

    public static void DumpAnimalMaterials()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < TargetPrefabs.Length; i++)
        {
            string path = TargetPrefabs[i];
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                sb.AppendLine($"[MatDiag] not found: {path}");
                continue;
            }

            sb.AppendLine($"[MatDiag] ==== {path}");
            foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                sb.AppendLine($"[MatDiag]   renderer={r.name} materials={mats.Length}");
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat == null)
                    {
                        sb.AppendLine($"[MatDiag]     [{m}] null");
                        continue;
                    }

                    sb.AppendLine($"[MatDiag]     [{m}] name={mat.name} shader={(mat.shader != null ? mat.shader.name : "null")} queue={mat.renderQueue}");
                    sb.AppendLine($"[MatDiag]          surface={GetFloat(mat, "_Surface")} blend={GetFloat(mat, "_Blend")} " +
                                  $"alphaClip={GetFloat(mat, "_AlphaClip")} cutoff={GetFloat(mat, "_Cutoff")} " +
                                  $"zwrite={GetFloat(mat, "_ZWrite")} cull={GetFloat(mat, "_Cull")}");
                    sb.AppendLine($"[MatDiag]          baseColor={GetColor(mat, "_BaseColor")} baseMap={GetTex(mat, "_BaseMap")} " +
                                  $"mainTex={GetTex(mat, "_MainTex")}");
                    sb.AppendLine($"[MatDiag]          keywords={string.Join(",", mat.shaderKeywords)}");
                }
            }
        }

        Debug.Log(sb.ToString());

        // テクスチャ側の設定も出す。alphaIsTransparency が false だと透過が効かない。
        foreach (string texPath in new[]
        {
            "Assets/50+ Animated Animals/1.0/Lynx/textures/T_lynx.png",
            "Assets/50+ Animated Animals/1.0/Lynx/textures/T_lynx_alpha.png",
        })
        {
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null)
            {
                Debug.Log($"[MatDiag] texture importer not found: {texPath}");
                continue;
            }

            Debug.Log($"[MatDiag] tex={texPath} alphaSource={importer.alphaSource} " +
                      $"alphaIsTransparency={importer.alphaIsTransparency} type={importer.textureType} " +
                      $"sRGB={importer.sRGBTexture}");
        }
    }

    private static string GetFloat(Material m, string name)
    {
        return m.HasProperty(name) ? m.GetFloat(name).ToString("0.###") : "-";
    }

    private static string GetColor(Material m, string name)
    {
        return m.HasProperty(name) ? m.GetColor(name).ToString("F3") : "-";
    }

    private static string GetTex(Material m, string name)
    {
        if (!m.HasProperty(name))
        {
            return "-";
        }

        Texture t = m.GetTexture(name);
        return t != null ? t.name : "(none)";
    }
}
