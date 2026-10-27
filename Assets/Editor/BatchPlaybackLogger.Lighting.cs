using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 照明を実験と同じにして撮るための引数（2026-10-16）。
//   -qualityLevel N   品質を切り替える。選べるのはビルド先で有効な品質だけ（QualitySettings.names）。
//                     ビルド先が Android の間は Mobile（Mobile_RPAsset: LDR・硬い影・追加ライトの影なし）しか無い
//                     （PC は Android から除外、ProjectSettings/QualitySettings.asset の excludedTargetPlatforms）ので、
//                     それまでの撮影も Quest と同じ Mobile で描いていた（10/16 に [LIGHTS] で確認。-qualityLevel 1 は効かない）。
//   -extraLights 1    シーンの Directional Light を同じ設定でもう 1 灯足す。実験は ExperimentScene に TrialScene を
//                     Additive で読むので（ExperimentController.cs:774）、同じ設定の光が 2 灯になる。TestScene は 1 灯。
// どちらも play mode に入った最初の Tick で当てる。品質は終わるときに元へ戻す（ProjectSettings に残さない）。
public static partial class BatchPlaybackLogger
{
    private const string KeySavedQualityLevel = "BatchPlaybackLogger.SavedQualityLevel";
    private const string KeySavedSkinWeights = "BatchPlaybackLogger.SavedSkinWeights";
    private const string KeySavedSkinWeightsLevel = "BatchPlaybackLogger.SavedSkinWeightsLevel";

    private static void ApplyLightingArgs()
    {
        int quality = -1;
        int extraLights = 0;
        int skinWeights = -1;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-qualityLevel") int.TryParse(args[i + 1], out quality);
            if (args[i] == "-extraLights") int.TryParse(args[i + 1], out extraLights);
            // -skinWeights 1|2|4|255（2026-10-23）: 今の品質の skinWeights（1 頂点を動かす骨の数の上限）をこの走りだけ変える。
            // Mobile は 2 本。SkinnedMeshRenderer.quality = Bone4 はこの上限を超えない（Docs/tmp/smal_rig_20261023 の皮膚の確かめ）ので、
            // 「全体を 4 本にしたら」の絵はこれで撮る。終わるときに元へ戻す（ProjectSettings に残さない）。
            if (args[i] == "-skinWeights") int.TryParse(args[i + 1], out skinWeights);
        }

        if (quality >= 0 && quality < QualitySettings.names.Length && quality != QualitySettings.GetQualityLevel())
        {
            SessionState.SetInt(KeySavedQualityLevel, QualitySettings.GetQualityLevel());
            QualitySettings.SetQualityLevel(quality, true);
        }

        if ((skinWeights == 1 || skinWeights == 2 || skinWeights == 4 || skinWeights == 255) && (int)QualitySettings.skinWeights != skinWeights)
        {
            SessionState.SetInt(KeySavedSkinWeights, (int)QualitySettings.skinWeights);
            SessionState.SetInt(KeySavedSkinWeightsLevel, QualitySettings.GetQualityLevel());
            QualitySettings.skinWeights = (SkinWeights)skinWeights;
            // 戻すのは play mode を抜ける直前（品質の段がまだ同じとき）。抜けた後の Editor は別の段（PC）なので、そこで書くと
            // PC の skinWeights を書き換えて ProjectSettings に保存してしまう（2026-10-23 に PC が 4 → 2 になった。git restore で戻した）。
            EditorApplication.playModeStateChanged -= RestoreSkinWeightsOnExit;
            EditorApplication.playModeStateChanged += RestoreSkinWeightsOnExit;
            Debug.Log($"[LIGHTS] skinWeights → {QualitySettings.skinWeights}（この走りだけ。品質の段 {QualitySettings.GetQualityLevel()}）");
        }

        Light source = null;
        foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.InstanceID))
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled) { source = l; break; }
        }
        for (int i = 0; i < extraLights && source != null; i++)
        {
            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            copy.name = source.gameObject.name + " (batch copy " + (i + 1) + ")";
        }

        var sb = new System.Text.StringBuilder();
        foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.InstanceID))
        {
            sb.Append($" [{l.name} {l.type} active={l.isActiveAndEnabled} intensity={l.intensity:F2} shadows={l.shadows} euler={l.transform.eulerAngles}]");
        }
        int level = QualitySettings.GetQualityLevel();
        RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
        Debug.Log($"[LIGHTS] quality={level}({QualitySettings.names[level]}) rp={(rp != null ? rp.name : "builtin")} " +
                  $"extraLights={extraLights}{sb}");
    }

    // [SHADOWPARAMS]（2026-10-25）: 直前の Camera.Render で URP が使った主ライトの影の値を読む（Editor だけ）。
    //   _ShadowBias.x = -depthBias x texel（URP ShadowUtils.cs:414-416。ソフトの影が無い品質では kernel を掛けない）
    //   _MainLightWorldToShadow[0] の 1 行目の長さ = 1 m あたりの影の地図の uv → texel = 1 / (長さ x 解像度)
    //   _MainLightShadowParams.zw = 距離の 2 乗に対する消え方（全部付くのは -w/z まで、消えるのは (1-w)/z）
    private static void LogShadowParams(string where)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
        var urp = rp as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
        Vector4 sb = Shader.GetGlobalVector("_ShadowBias");
        Vector4 sp = Shader.GetGlobalVector("_MainLightShadowParams");
        float texelFromMatrix = -1f;
        try
        {
            Matrix4x4[] w2s = Shader.GetGlobalMatrixArray("_MainLightWorldToShadow");
            if (w2s != null && w2s.Length > 0 && urp != null)
            {
                Vector3 row0 = new Vector3(w2s[0].m00, w2s[0].m01, w2s[0].m02);
                if (row0.magnitude > 1e-9f) { texelFromMatrix = 1f / (row0.magnitude * urp.mainLightShadowmapResolution); }
            }
        }
        catch (Exception) { texelFromMatrix = -2f; }

        float texelFromBias = urp != null && urp.shadowDepthBias > 1e-6f ? -sb.x / urp.shadowDepthBias : -1f;
        float fullUntil = sp.z > 1e-9f ? Mathf.Sqrt(Mathf.Max(0f, -sp.w / sp.z)) : -1f;
        float endAt = sp.z > 1e-9f ? Mathf.Sqrt(Mathf.Max(0f, (1f - sp.w) / sp.z)) : -1f;
        var lights = new System.Text.StringBuilder();
        foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.InstanceID))
        {
            if (l.type == LightType.Directional) { lights.Append($" {l.name}:{l.shadows}/{(l.isActiveAndEnabled ? "on" : "off")}"); }
        }

        Debug.Log(string.Format(inv,
            "[SHADOWPARAMS] {0} asset={1} dist={2:F2} casc={3} res={4} bias={5:F2}/{6:F2} softSupported={7} | _ShadowBias=({8:F5},{9:F5}) " +
            "texel(bias)={10:F2}cm texel(matrix)={11:F2}cm shadowFull<={12:F2}m shadowEnd={13:F2}m strength={14:F2} lights:{15}",
            where, rp != null ? rp.name : "builtin", urp != null ? urp.shadowDistance : -1f, urp != null ? urp.shadowCascadeCount : -1,
            urp != null ? urp.mainLightShadowmapResolution : -1, urp != null ? urp.shadowDepthBias : -1f, urp != null ? urp.shadowNormalBias : -1f,
            urp != null && urp.supportsSoftShadows, sb.x, sb.y, 100f * texelFromBias, 100f * texelFromMatrix, fullUntil, endAt, sp.x, lights));
    }

    private static void RestoreSkinWeightsOnExit(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.ExitingPlayMode)
        {
            return;
        }

        EditorApplication.playModeStateChanged -= RestoreSkinWeightsOnExit;
        RestoreSkinWeights("exiting playmode");
    }

    // 変えたときと同じ品質の段のときだけ戻す。違う段なら書かない（ほかの段の値を書き換えて保存してしまうため）。
    private static void RestoreSkinWeights(string when)
    {
        int savedSkin = SessionState.GetInt(KeySavedSkinWeights, -1);
        if (savedSkin < 0)
        {
            return;
        }

        int level = SessionState.GetInt(KeySavedSkinWeightsLevel, -1);
        SessionState.EraseInt(KeySavedSkinWeights);
        SessionState.EraseInt(KeySavedSkinWeightsLevel);
        if (level != QualitySettings.GetQualityLevel())
        {
            Debug.LogWarning($"[LIGHTS] skinWeights not restored ({when}): quality level is {QualitySettings.GetQualityLevel()}, changed at {level}");
            return;
        }

        QualitySettings.skinWeights = (SkinWeights)savedSkin;
        Debug.Log($"[LIGHTS] skinWeights restored to {QualitySettings.skinWeights} ({when}, level {level})");
    }

    private static void RestoreQualityLevel()
    {
        RestoreSkinWeights("after playmode");

        int saved = SessionState.GetInt(KeySavedQualityLevel, -1);
        if (saved < 0 || saved >= QualitySettings.names.Length) { return; }
        QualitySettings.SetQualityLevel(saved, false);
        SessionState.EraseInt(KeySavedQualityLevel);
        Debug.Log($"[LIGHTS] quality restored to {saved}({QualitySettings.names[saved]})");
    }
}
