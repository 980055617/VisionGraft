using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 不透明の cutout マテリアル（毛・ヒゲのカード、人の髪・服）の alpha のブレンドを One/OneMinusSrcAlpha にする（2026-10-13）。
//
// **なぜ必要か**（Docs/model-materials.md の 10/13「原因の確定」）:
// 実機の画質 Mobile の URP 設定（Mobile_RPAsset）は MSAA 4x。MSAA 中の URP は不透明のパスで _AlphaToMaskAvailable を 1 にし
// （DrawObjectsPass）、cutout（_ALPHATEST_ON）のマテリアルは alpha-to-coverage 用に尖らせた alpha（1 未満）を書き出す
// （ShaderVariablesFunctions.hlsl の AlphaClip / OutputAlpha）。既定の alpha のブレンド One/Zero のままだと、それが下の体の
// alpha 1 を上書きする。アプリはパススルーを underlay で敷いているので、alpha が 1 未満の画素から部屋が透ける。
// 毛のテクスチャは半透明の段階が多く、選択画面の 11・17・27・41（10_BearWITHFUR・16_Deer1・26_FoxWITHFUR・40_Mammoth）で
// 毛の内側まで透けていた。MSAA の無い PC・Editor では起きない。
// alpha だけ One/OneMinusSrcAlpha にすると、下に不透明な体があれば a + 1 × (1 − a) = 1 で不透明のまま残る。
// 体からはみ出した毛のふちは部屋の上で a のまま（ふちの柔らかさとして残る）。色のブレンド（One/Zero）は変えない。
//
// **なぜマテリアルに書かず実行時か**: URP の ShaderGUI.ValidateMaterial（Inspector で値を変えたとき。Unity の資料では
// 読み込み時も）は、不透明の alpha のブレンドを One/Zero に計算し直す（BaseShaderGUI.SetupMaterialBlendModeInternal）。
// .mat や取り込み時の処理で書いても Editor で消える。プレイヤーには ShaderGUI が無いので、読み込んだ後に書けば残る。
//
// **Editor では書き換えない**: Editor は MSAA 1x で起きない。共有マテリアル（.mat のアセット）を Play 中に書き換えると
// 保存されて git の差分になる。
public static class PassthroughCutoutAlpha
{
    private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
    private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
    private static readonly int SrcBlendAlphaId = Shader.PropertyToID("_SrcBlendAlpha");
    private static readonly int DstBlendAlphaId = Shader.PropertyToID("_DstBlendAlpha");

    // 不透明の cutout で、alpha のブレンドをプロパティで持つシェーダ（URP Lit など）か。
    public static bool IsOpaqueCutout(Material material)
    {
        if (material == null ||
            !material.HasProperty(SurfaceId) || !material.HasProperty(AlphaClipId) ||
            !material.HasProperty(SrcBlendAlphaId) || !material.HasProperty(DstBlendAlphaId))
        {
            return false;
        }

        return material.GetFloat(SurfaceId) < 0.5f && material.GetFloat(AlphaClipId) >= 0.5f;
    }

    // 不透明の cutout なら alpha のブレンドを書き換えて true。
    public static bool Apply(Material material)
    {
        if (!IsOpaqueCutout(material))
        {
            return false;
        }

        material.SetFloat(SrcBlendAlphaId, (float)BlendMode.One);
        material.SetFloat(DstBlendAlphaId, (float)BlendMode.OneMinusSrcAlpha);
        return true;
    }

    // prefab の共有マテリアルに当てる。instance（トラック・選択画面のプレビュー）は prefab のマテリアルを共有しているので、
    // ここで 1 回当てれば全部に効く。書き換えた数を返す。done はカテゴリをまたいで共有されるマテリアルを 2 回数えないため。
    public static int ApplyToPrefabs(GameObject[] prefabs, string category, HashSet<Material> done)
    {
        if (Application.isEditor || prefabs == null)
        {
            return 0;
        }

        int changed = 0;
        for (int p = 0; p < prefabs.Length; p++)
        {
            if (prefabs[p] == null)
            {
                continue;
            }

            Renderer[] renderers = prefabs[p].GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Material[] materials = renderers[r].sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null || !done.Add(material) || !Apply(material))
                    {
                        continue;
                    }

                    changed++;
                    Debug.Log($"[CUTOUTALPHA] {category} {prefabs[p].name} mat={material.name} shader={material.shader.name}");
                }
            }
        }

        return changed;
    }
}
