using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Resources/Models 以下の prefab が実際に使っているマテリアルを棚卸しする。
//
// 目的は「見た目がおかしいモデルの原因を、推測ではなくマテリアルの実値で確かめる」こと。
// 50+ Animated Animals はマテリアルを FBX 埋め込み（materialLocation: 1）で持っており、
// .mat ファイルとしてディスク上に無いので、grep では中身が判らない。
//
// 実行:
//   Unity.exe -batchmode -projectPath <proj> -executeMethod ModelMaterialAudit.Run
//             -logFile <log> [-auditFilter Lion]
public static class ModelMaterialAudit
{
    [MenuItem("Tools/VisionGraft/Audit Model Materials")]
    public static void Run()
    {
        string filter = null;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-auditFilter")
            {
                filter = args[i + 1];
            }
        }

        GameObject[] prefabs = Resources.LoadAll<GameObject>("Models");
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"[MATAUDIT] prefab={prefabs.Length} filter={filter ?? "(なし)"}");

        HashSet<Material> seen = new HashSet<Material>();
        for (int i = 0; i < prefabs.Length; i++)
        {
            GameObject p = prefabs[i];
            if (p == null || (filter != null && p.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0))
            {
                continue;
            }

            Renderer[] rs = p.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rs.Length; r++)
            {
                Material[] ms = rs[r].sharedMaterials;
                for (int m = 0; m < ms.Length; m++)
                {
                    Material mat = ms[m];
                    if (mat == null || !seen.Add(mat))
                    {
                        continue;
                    }

                    sb.AppendLine(Describe(p.name, rs[r].name, m, mat));
                }
            }
        }

        Debug.Log(sb.ToString());
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }

    private static string Describe(string prefabName, string rendererName, int slot, Material mat)
    {
        // URP Lit の透明まわりは _Surface(0=Opaque,1=Transparent) / _Blend / _AlphaClip /
        // _Cutoff / _ZWrite に出る。値が無いプロパティは "-" にする。
        string surface = Prop(mat, "_Surface");
        string alphaClip = Prop(mat, "_AlphaClip");
        string cutoff = Prop(mat, "_Cutoff");
        string zwrite = Prop(mat, "_ZWrite");
        string cull = Prop(mat, "_Cull");
        string tex = mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null
            ? mat.GetTexture("_BaseMap").name
            : (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null
                ? mat.GetTexture("_MainTex").name
                : "(なし)");

        return $"[MATAUDIT] {prefabName} / {rendererName}[{slot}] mat={mat.name} " +
               $"shader={mat.shader.name} queue={mat.renderQueue} " +
               $"surface={surface} alphaClip={alphaClip} cutoff={cutoff} zwrite={zwrite} cull={cull} " +
               $"tex={tex}";
    }

    private static string Prop(Material mat, string name)
    {
        return mat.HasProperty(name) ? mat.GetFloat(name).ToString("0.###") : "-";
    }
}
