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

    private static void ApplyLightingArgs()
    {
        int quality = -1;
        int extraLights = 0;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-qualityLevel") int.TryParse(args[i + 1], out quality);
            if (args[i] == "-extraLights") int.TryParse(args[i + 1], out extraLights);
        }

        if (quality >= 0 && quality < QualitySettings.names.Length && quality != QualitySettings.GetQualityLevel())
        {
            SessionState.SetInt(KeySavedQualityLevel, QualitySettings.GetQualityLevel());
            QualitySettings.SetQualityLevel(quality, true);
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

    private static void RestoreQualityLevel()
    {
        int saved = SessionState.GetInt(KeySavedQualityLevel, -1);
        if (saved < 0 || saved >= QualitySettings.names.Length) { return; }
        QualitySettings.SetQualityLevel(saved, false);
        SessionState.EraseInt(KeySavedQualityLevel);
        Debug.Log($"[LIGHTS] quality restored to {saved}({QualitySettings.names[saved]})");
    }
}
