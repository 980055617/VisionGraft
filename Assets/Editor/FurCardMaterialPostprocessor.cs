using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// 毛・ヒゲのカードのマテリアルを alpha blend から alpha clip（cutout）へ倒す。
//
// **なぜ必要か**（2026-09-05、docs/model-materials.md）:
// 50+ Animated Animals の毛のカードは FBX 埋め込みマテリアルで、URP Lit の
// Surface = Transparent + ZWrite off + alpha clip 無しとして取り込まれる。
// alpha テクスチャは「カードの形を抜く」ためのもので不透明度ではないので、
// alpha blend で描くと一面が半透明になる。ライオンのたてがみ越しに背景の映像が
// 透けて見えていたのがこれ。
//
// **なぜ AssetPostprocessor か**: マテリアルが FBX 埋め込み（materialLocation: 1）なので
// .mat ファイルが無く、直接編集できない。取り込み時に直せば Editor・プレビュー・
// 実行時・ビルドのすべてで同じ見え方になり、実行時コストもゼロ。
//
// **対象を絞っている理由**: Transparent なら何でも倒すと、将来ガラスや水のモデルを
// 入れたときに壊れる。テクスチャ名かマテリアル名が毛・ヒゲの語を含むものだけにする。
// 現在の全 198 マテリアル中、条件に当たるのは 5 つ（Lion / Fox / Lioness / Puma / Racoon）。
public class FurCardMaterialPostprocessor : AssetPostprocessor
{
    // 毛・ヒゲのカードだと判断する語。テクスチャ名かマテリアル名に含まれていれば対象。
    // "wisker" は M_Lionesswiskers（綴りが whiskers ではない）と M_whiskers の両方に当たる。
    private static readonly string[] FurNameHints =
    {
        "alpha", "card", "fur", "mane", "hair", "wisker", "eyelash", "eyelashes",
    };

    // **`OnPostprocessMaterial` は使えない。**この pack は
    // `materialImportMode: 2`（Import via MaterialDescription）で取り込まれており、
    // その経路では `OnPostprocessMaterial` が呼ばれない（2026-09-05 実測。
    // 再取り込みしても surface=1 のまま変わらなかった）。
    // `OnPreprocessMaterialDescription` を実装すると既定のマテリアル生成ごと
    // 肩代わりすることになるので、生成後に触れる `OnPostprocessModel` を使う。
    private void OnPostprocessModel(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        HashSet<Material> done = new HashSet<Material>();
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < rs.Length; r++)
        {
            Material[] ms = rs[r].sharedMaterials;
            for (int m = 0; m < ms.Length; m++)
            {
                if (ms[m] == null || !done.Add(ms[m]) || !ShouldConvertToAlphaClip(ms[m]))
                {
                    continue;
                }

                ApplyAlphaClip(ms[m]);
                Debug.Log($"[FURFIX] 埋め込みマテリアルを cutout 化 {assetPath} (mat={ms[m].name})");
            }
        }
    }

    public static bool ShouldConvertToAlphaClip(Material material)
    {
        if (material == null || material.shader == null)
        {
            return false;
        }

        // URP Lit / Simple Lit 以外は触らない。
        if (!material.shader.name.StartsWith("Universal Render Pipeline/"))
        {
            return false;
        }

        // 既に alpha clip が立っているものは触らない。
        if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
        {
            return false;
        }

        bool isTransparent = material.HasProperty("_Surface") && material.GetFloat("_Surface") >= 0.5f;

        string names = material.name;
        Texture baseMap = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
        if (baseMap != null)
        {
            names += "|" + baseMap.name;
        }

        names = names.ToLowerInvariant();
        bool furName = false;
        for (int i = 0; i < FurNameHints.Length; i++)
        {
            if (names.Contains(FurNameHints[i]))
            {
                furName = true;
                break;
            }
        }

        if (furName)
        {
            // Transparent なら alpha が不透明度として効いてしまっている（ライオンのたてがみ）。
            if (isTransparent)
            {
                return true;
            }

            // **Opaque でも取りこぼしてはいけない。**alpha テクスチャなのに Opaque だと
            // alpha が完全に無視され、カードが白い板のまま出る（33_Hare のヒゲ、2026-09-05）。
            // ただし毛のシェル（alpha を持たない・持っていても使わない）を巻き込むと
            // 毛が消えるので、**テクスチャの alpha を実際に読んで**判定する。
            return TextureLooksLikeCutout(baseMap);
        }

        // 名前で拾えないが「cutout のつもりで設定して alpha clip を立て忘れている」もの。
        // Transparent なら _Cutoff は使われないので、既定の 0.5 から動かしてある時点で
        // 作者は閾値を調整している = cutout を意図している。
        // 27_GermanShepherd の M_GermanShepherd_Transparent_URP が該当（_Cutoff 0.123）。
        if (material.HasProperty("_Cutoff") &&
            !Mathf.Approximately(material.GetFloat("_Cutoff"), 0.5f))
        {
            return true;
        }

        return false;
    }

    // テクスチャがカットアウト用か（= 完全透明の画素がまとまってあるか）。
    //
    // 名前だけでは「毛のカード（alpha で形を抜く）」と「毛のシェル（alpha 不要）」を
    // 区別できない。実測では次のように分かれた（2026-09-05、完全透明の画素の割合）:
    //   eu_rabbit_cards_alpha_dif 99.0% / Fox_Fur 96.0% / Bear_fur_color 84.0% /
    //   foxfur_d 78.0% / T_kangaroo_alph 65.6% / Mammoth_Fur 64.2% /
    //   Red_Deer_Fur 57.3% / Donkey_Hair 48.8% / T_moose_eyelashes_alpha 21.9%
    //   → ここまでカットアウト用
    //   Donkey（21_Donkey1.0 の Fur）1.9%（98% が半透明。柔らかいグラデーションなので
    //     cutout にすると毛が消える）/ TX_HorseHair_Albedo は alpha チャンネル自体が無い
    //
    // 取り込み設定に関係なく読めるよう、**ソースファイルを直接デコード**する
    // （`Texture2D.LoadImage`）。取り込み済みテクスチャは通常 isReadable=false で
    // GetPixels できない。
    private const float CutoutTransparentFractionThreshold = 0.05f;
    private static readonly Dictionary<string, bool> CutoutCache = new Dictionary<string, bool>();

    private static bool TextureLooksLikeCutout(Texture tex)
    {
        if (tex == null)
        {
            return false;
        }

        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        if (CutoutCache.TryGetValue(path, out bool cached))
        {
            return cached;
        }

        bool result = false;
        Texture2D tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            // `Texture2D.LoadImage` は PNG / JPG しか読めない。**TGA を落とすと
            // 10_BearWITHFUR と 26_FoxWITHFUR を取りこぼす**（実測で 84% / 78% が
            // 完全透明のカットアウト用なのに、読めずに除外されていた。2026-09-05）。
            if (path.EndsWith(".tga", System.StringComparison.OrdinalIgnoreCase))
            {
                if (TryTgaTransparentFraction(bytes, out float tgaFraction))
                {
                    result = tgaFraction > CutoutTransparentFractionThreshold;
                }

                CutoutCache[path] = result;
                return result;
            }

            if (tmp.LoadImage(bytes))
            {
                Color32[] px = tmp.GetPixels32();
                if (px.Length > 0)
                {
                    // 大きいテクスチャは間引いて数える。分布を見るだけなので十分。
                    int step = Mathf.Max(1, px.Length / 200000);
                    int seen = 0;
                    int clear = 0;
                    for (int i = 0; i < px.Length; i += step)
                    {
                        seen++;
                        if (px[i].a == 0)
                        {
                            clear++;
                        }
                    }

                    result = seen > 0 &&
                             (float)clear / seen > CutoutTransparentFractionThreshold;
                }
            }
        }
        catch (IOException)
        {
            result = false;
        }
        finally
        {
            Object.DestroyImmediate(tmp);
        }

        CutoutCache[path] = result;
        return result;
    }


    // TGA の alpha だけを読んで、完全透明の画素の割合を返す。
    // 対象は 32bpp の非圧縮（type 2）と RLE（type 10）。それ以外は false。
    // ヘッダの alpha bit 数（descriptor の下位 4 bit）は当てにしない。
    // 実測した 2 ファイルはどちらも 0 だが alpha データは入っている。
    private static bool TryTgaTransparentFraction(byte[] bytes, out float fraction)
    {
        fraction = 0f;
        if (bytes == null || bytes.Length < 18)
        {
            return false;
        }

        int idLen = bytes[0];
        int colorMapType = bytes[1];
        int imageType = bytes[2];
        int bpp = bytes[16];
        if (colorMapType != 0 || bpp != 32 || (imageType != 2 && imageType != 10))
        {
            return false;
        }

        long total = (long)(bytes[12] | (bytes[13] << 8)) * (bytes[14] | (bytes[15] << 8));
        if (total <= 0)
        {
            return false;
        }

        int p = 18 + idLen;
        long seen = 0;
        long clear = 0;
        if (imageType == 2)
        {
            for (long i = 0; i < total; i++)
            {
                int off = p + (int)(i * 4);
                if (off + 3 >= bytes.Length)
                {
                    break;
                }

                seen++;
                if (bytes[off + 3] == 0)
                {
                    clear++;
                }
            }
        }
        else
        {
            long done = 0;
            while (done < total && p < bytes.Length)
            {
                int packet = bytes[p++];
                int count = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0)
                {
                    if (p + 3 >= bytes.Length)
                    {
                        break;
                    }

                    if (bytes[p + 3] == 0)
                    {
                        clear += count;
                    }

                    seen += count;
                    p += 4;
                }
                else
                {
                    for (int k = 0; k < count && p + 3 < bytes.Length; k++)
                    {
                        seen++;
                        if (bytes[p + 3] == 0)
                        {
                            clear++;
                        }

                        p += 4;
                    }
                }

                done += count;
            }
        }

        if (seen == 0)
        {
            return false;
        }

        fraction = (float)clear / seen;
        return true;
    }


    // URP Lit を alpha clip に切り替える。**プロパティだけでは効かない。**
    // シェーダキーワード・ブレンド係数・RenderType タグ・キューまで揃える必要がある。
    public static void ApplyAlphaClip(Material material)
    {
        material.SetFloat("_Surface", 0f);              // Opaque
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_ZWrite", 1f);
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        // cutoff は素材ごとに調整済みの値があれば尊重する（M_GermanShepherd… は 0.123）。
        // 既定の 0.5 のままだと毛が痩せるので、未調整のものは 0.35 にする。
        if (material.HasProperty("_Cutoff") && Mathf.Approximately(material.GetFloat("_Cutoff"), 0.5f))
        {
            material.SetFloat("_Cutoff", 0.35f);
        }

        material.EnableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        // Transparent のときに切られている深度パスを戻す。**これを忘れると
        // 深度を書かないままなので、cutout にしても前後が入れ替わって見える。**
        material.SetShaderPassEnabled("DepthOnly", true);
        material.SetShaderPassEnabled("DepthNormals", true);
    }


    // FBX 埋め込みではなく `.mat` として存在するマテリアルを直す。
    // 取り込み時には走らないので、こちらは明示的に叩く（27_GermanShepherd がこれ）。
    // **プロジェクト全体の `t:Material` を舐めてはいけない。**
    // `LoadAssetAtPath` するだけで Unity がシェーダのプロパティ同期を走らせ
    // （`_MainTex` に `_BaseMap` を写す等）、`SaveAssets` でそれが書き出されて
    // 無関係なマテリアルに差分が出る。2026-09-05 に Volleyball / TextMesh Pro の例 /
    // npc の髪・服など 12 件を巻き込んだ（alpha 関連の変更は無く、正規化の差分だけ）。
    //
    // 走査対象は Resources/Models の prefab が実際に使っているものだけにする。
    public static int FixStandaloneMaterials(IEnumerable<Material> used)
    {
        int fixedCount = 0;
        foreach (Material mat in used)
        {
            string path = mat != null ? AssetDatabase.GetAssetPath(mat) : null;
            // FBX の中のマテリアルはここでは触らない（取り込み時に postprocessor が直す）。
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".mat") ||
                !ShouldConvertToAlphaClip(mat))
            {
                continue;
            }

            ApplyAlphaClip(mat);
            EditorUtility.SetDirty(mat);
            fixedCount++;
            Debug.Log($"[FURFIX] .mat を修正 {path} (mat={mat.name})");
        }

        return fixedCount;
    }

    // 既に取り込み済みの FBX を対象だけ再取り込みする。
    // AssetPostprocessor は取り込み時にしか走らないので、導入時に一度これを回す。
    //
    // 実行:
    //   Unity.exe -batchmode -projectPath <proj> -executeMethod
    //             FurCardMaterialPostprocessor.ReimportAffectedModels -logFile <log>
    [MenuItem("Tools/VisionGraft/Reimport Fur Card Models")]
    public static void ReimportAffectedModels()
    {
        HashSet<string> paths = new HashSet<string>();
        HashSet<Material> used = new HashSet<Material>();
        GameObject[] prefabs = Resources.LoadAll<GameObject>("Models");
        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i] == null)
            {
                continue;
            }

            Renderer[] rs = prefabs[i].GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rs.Length; r++)
            {
                Material[] ms = rs[r].sharedMaterials;
                for (int m = 0; m < ms.Length; m++)
                {
                    if (ms[m] == null)
                    {
                        continue;
                    }

                    used.Add(ms[m]);
                    if (!ShouldConvertToAlphaClip(ms[m]))
                    {
                        continue;
                    }

                    string p = AssetDatabase.GetAssetPath(ms[m]);
                    if (!string.IsNullOrEmpty(p))
                    {
                        paths.Add(p);
                    }
                }
            }
        }

        foreach (string p in paths)
        {
            Debug.Log($"[FURFIX] reimport {p}");
            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
        }

        int standalone = FixStandaloneMaterials(used);
        AssetDatabase.SaveAssets();
        Debug.Log($"[FURFIX] 再取り込み {paths.Count} 件 / .mat 修正 {standalone} 件");
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }
}
