using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Android（Quest）向けの APK をコマンドラインからビルドする。
//
// これまで実機用のビルドは Editor の Build And Run から手作業でやっていて、
// バッチから叩く口が無かった（2026-09-06 に追加）。
//
// **シーン構成・各種設定は触らない。**`EditorBuildSettings` に登録されている
// 有効なシーンをその順番のまま使う。先頭が入口シーン（HomeScene）である前提は
// docs/model-selection-persistence.md のとおり。
//
// 実行:
//   Unity.exe -batchmode -quit -projectPath <proj> \
//             -executeMethod AndroidBuilder.BuildApk -logFile <log> \
//             [-apkOut <path>] [-development]
//
// 実機へ入れるときは **`adb install -r`**（上書き）を使うこと。
// アンインストールを挟むと `model_selection.json`（モデル選択・回転キー）が消える。
public static class AndroidBuilder
{
    [MenuItem("Tools/VisionGraft/Build Android APK")]
    public static void BuildApk()
    {
        string outPath = null;
        bool development = false;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-apkOut" && i + 1 < args.Length)
            {
                outPath = args[i + 1];
            }

            if (args[i] == "-development")
            {
                development = true;
            }
        }

        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Directory.GetCurrentDirectory(), "Build", "VisionGraft.apk");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));

        List<string> scenes = new List<string>();
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (s.enabled)
            {
                scenes.Add(s.path);
            }
        }

        if (scenes.Count == 0)
        {
            Debug.LogError("[BUILD] 有効なシーンが 1 つもありません。");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[BUILD] scenes={scenes.Count} 先頭={scenes[0]} out={outPath} development={development}");

        BuildPlayerOptions opts = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = outPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = development
                ? (BuildOptions.Development | BuildOptions.AllowDebugging)
                : BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary summary = report.summary;
        Debug.Log($"[BUILD] result={summary.result} errors={summary.totalErrors} " +
                  $"warnings={summary.totalWarnings} size={summary.totalSize / (1024 * 1024)}MB " +
                  $"time={summary.totalTime}");

        if (summary.result != BuildResult.Succeeded)
        {
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage m in step.messages)
                {
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                    {
                        Debug.LogError($"[BUILD] {step.name}: {m.content}");
                    }
                }
            }

            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }
}
