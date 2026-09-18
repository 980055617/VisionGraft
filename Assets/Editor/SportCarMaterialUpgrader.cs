using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;

// Asset Store の "Sport Car" パッケージ（Assets/SportCar、2026-09-18 取り込み）のマテリアルは
// Built-in RP の Standard（fileID 46）が 64 本と Standard (Specular setup)（fileID 45、glass1）が 1 本で、
// URP のこのプロジェクトでは紫（エラーシェーダー）になる。SportBallMaterialUpgrader と同じく URP 公式の
// StandardUpgrader で Universal Render Pipeline/Lit へ変換する。玉のときは "Standard" しか登録しなかったが、
// URP パッケージ自身の一覧（UniversalRenderPipelineMaterialUpgrader.GetUpgraders）に倣って 2 種類とも登録する。
// 透明（_Mode 3: glass1 / FrontLight_Glass ×2）は upgrader が _Mode → _Surface とキーワードまで移すので手当て不要。
// 同梱の AutoPBR_*.shader（Built-in の surface shader）はどのマテリアルからも参照されていないので触らない。
// Assets/SportCar/Materials/Skybox.mat はデモシーン用なので対象外（Models/ 以下だけを舐める）。
public static class SportCarMaterialUpgrader
{
    private const string MaterialRoot = "Assets/SportCar/Models";
    private const string UrpLitShaderName = "Universal Render Pipeline/Lit";

    [MenuItem("Tools/VisionGraft/Upgrade Sport Car Materials To URP")]
    public static void Upgrade()
    {
        List<MaterialUpgrader> upgraders = new List<MaterialUpgrader>
        {
            new StandardUpgrader("Standard"),
            new StandardUpgrader("Standard (Specular setup)")
        };

        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialRoot });
        int upgraded = 0;
        int skipped = 0;
        int failed = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                continue;
            }

            string before = mat.shader != null ? mat.shader.name : "(null)";
            if (before == UrpLitShaderName)
            {
                skipped++;
                continue;
            }

            string message = string.Empty;
            bool ok = MaterialUpgrader.Upgrade(mat, upgraders, MaterialUpgrader.UpgradeFlags.LogMessageWhenNoUpgraderFound, ref message);
            string after = mat.shader != null ? mat.shader.name : "(null)";
            if (ok && after == UrpLitShaderName)
            {
                upgraded++;
                Debug.Log($"[SportCarMaterialUpgrader] {path}: '{before}' → '{after}' surface={mat.GetFloat("_Surface"):F0}");
            }
            else
            {
                failed++;
                Debug.LogWarning($"[SportCarMaterialUpgrader] 変換できず {path}: '{before}' → '{after}' {message}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[SportCarMaterialUpgrader] Done. upgraded={upgraded} skipped(URP 済み)={skipped} failed={failed} / {guids.Length}");
    }
}
