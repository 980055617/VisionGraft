using System.IO;
using UnityEditor;
using UnityEngine;

// 39_Lynx のひげ・耳毛（cards）が薄く透けて見える問題を直す。
//
//   Unity.exe -batchmode -projectPath . -executeMethod LynxWhiskerMaterialFix.Apply -quit
//
// 実測した元の設定（Assets/Editor/MaterialDiagnostics.cs、2026-08-28）:
//
//   M_lynx_cards  surface=1(Transparent) queue=3000 zwrite=0
//                 baseColor alpha=0.207        <- これだけで 21% の不透明度
//                 keywords=_ALPHAPREMULTIPLY_ON,_SURFACE_TYPE_TRANSPARENT
//
// ひげ・毛の板ポリゴンはブレンド透過ではなく**アルファクリップ**にするのが定石。
// ブレンドだと板同士の描画順が破綻して、背後の板が透けたり消えたりする。
//
// マテリアルは FBX 埋め込み（materialLocation=1）なので、.mat を作って
// ModelImporter.AddRemap で差し替える。FBX 自体は書き換えない。
public static class LynxWhiskerMaterialFix
{
    private const string FbxPath = "Assets/50+ Animated Animals/1.0/Lynx/Lynx.fbx";
    private const string CardsMaterialName = "M_lynx_cards";
    private const string OutputMaterialPath = "Assets/50+ Animated Animals/1.0/Lynx/M_lynx_cards.mat";
    private const string AlphaTexturePath = "Assets/50+ Animated Animals/1.0/Lynx/textures/T_lynx_alpha.png";
    private const string PrefabPath = "Assets/Resources/Models/Animal/39_Lynx.prefab";

    // 板が消えすぎず、隙間も残る程度。実機で見て詰める。
    private const float AlphaCutoff = 0.35f;

    public static void Apply()
    {
        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[LynxFix] ModelImporter not found: {FbxPath}");
            EditorApplication.Exit(1);
            return;
        }

        // 元の埋め込みマテリアルを複製して土台にする。テクスチャ割り当てや UV 設定を
        // 引き継げるので、ゼロから作るより安全。
        Material source = null;
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
        {
            if (asset is Material mat && mat.name == CardsMaterialName)
            {
                source = mat;
                break;
            }
        }

        // 2 回目以降は remap 済みで埋め込みマテリアルが無い。作った .mat を土台にする。
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(OutputMaterialPath);
        Material fixedMaterial;
        if (source != null)
        {
            fixedMaterial = Object.Instantiate(source);
            fixedMaterial.name = CardsMaterialName;
            ConfigureAsAlphaClip(fixedMaterial);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputMaterialPath));
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(OutputMaterialPath);
            }

            AssetDatabase.CreateAsset(fixedMaterial, OutputMaterialPath);
        }
        else if (existing != null)
        {
            fixedMaterial = existing;
            ConfigureAsAlphaClip(fixedMaterial);
            EditorUtility.SetDirty(fixedMaterial);
            Debug.Log("[LynxFix] 既に remap 済み。既存の .mat を設定し直します。");
        }
        else
        {
            Debug.LogError($"[LynxFix] 埋め込みも .mat も見つかりません: {CardsMaterialName}");
            EditorApplication.Exit(1);
            return;
        }

        // FBX の該当マテリアルをこの .mat に差し替える。
        importer.AddRemap(
            new AssetImporter.SourceAssetIdentifier(typeof(Material), CardsMaterialName),
            fixedMaterial);
        importer.SaveAndReimport();

        FixAlphaTextureImport();
        RepointPrefabMaterial(fixedMaterial);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"[LynxFix] remapped {CardsMaterialName} -> {OutputMaterialPath} " +
            $"(surface=Opaque alphaClip=1 cutoff={AlphaCutoff} baseColorAlpha=1)");
    }


    private static void ConfigureAsAlphaClip(Material m)
    {
        // URP/Lit の Surface Type と Alpha Clipping。プロパティとキーワードの両方を
        // 揃えないと効かない（インスペクタは片方だけ書いても表示が追従しない）。
        SetIfPresent(m, "_Surface", 0f);          // Opaque
        SetIfPresent(m, "_Blend", 0f);
        SetIfPresent(m, "_AlphaClip", 1f);
        SetIfPresent(m, "_Cutoff", AlphaCutoff);
        SetIfPresent(m, "_ZWrite", 1f);
        SetIfPresent(m, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        SetIfPresent(m, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);

        // **baseColor のアルファが 0.207 だった。** これが薄さの主因。
        if (m.HasProperty("_BaseColor"))
        {
            Color c = m.GetColor("_BaseColor");
            c.a = 1f;
            m.SetColor("_BaseColor", c);
        }
        if (m.HasProperty("_Color"))
        {
            Color c = m.GetColor("_Color");
            c.a = 1f;
            m.SetColor("_Color", c);
        }

        m.EnableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.DisableKeyword("_ALPHABLEND_ON");

        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;   // 2450
    }


    private static void SetIfPresent(Material m, string name, float value)
    {
        if (m.HasProperty(name))
        {
            m.SetFloat(name, value);
        }
    }


    // **remap だけでは足りない。**
    // 39_Lynx.prefab は FBX とは別のプレハブで、埋め込みマテリアルを fileID で直接
    // 参照していた。remap で埋め込みが消えると参照が切れて **null** になり、
    // ひげが描画されなくなる（2026-08-28 に実際に壊した）。プレハブ側も差し替える。
    private static void RepointPrefabMaterial(Material fixedMaterial)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[LynxFix] prefab not found: {PrefabPath}");
            return;
        }

        int repointed = 0;
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                // null（参照が切れたスロット）と、旧名のままのものを差し替える。
                if (mats[i] == null || mats[i].name == CardsMaterialName)
                {
                    mats[i] = fixedMaterial;
                    changed = true;
                    repointed++;
                }
            }

            if (changed)
            {
                r.sharedMaterials = mats;
                EditorUtility.SetDirty(r);
            }
        }

        if (repointed > 0)
        {
            PrefabUtility.SavePrefabAsset(prefab);
        }

        Debug.Log($"[LynxFix] prefab material slots repointed: {repointed}");
    }


    private static void FixAlphaTextureImport()
    {
        var tex = AssetImporter.GetAtPath(AlphaTexturePath) as TextureImporter;
        if (tex == null)
        {
            Debug.LogWarning($"[LynxFix] texture importer not found: {AlphaTexturePath}");
            return;
        }

        if (tex.alphaIsTransparency)
        {
            return;
        }

        // アルファを透過として扱う。false のままだと縮小時にアルファ境界の色がにじむ。
        tex.alphaIsTransparency = true;
        tex.SaveAndReimport();
        Debug.Log($"[LynxFix] alphaIsTransparency=true: {AlphaTexturePath}");
    }
}
