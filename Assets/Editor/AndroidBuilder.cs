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
        string buildStamp = null;
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

            // git の短いハッシュなど。trials.csv の app_build に入る（ExperimentBuildInfo）。
            if (args[i] == "-buildStamp" && i + 1 < args.Length)
            {
                buildStamp = args[i + 1];
            }
        }

        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Directory.GetCurrentDirectory(), "Build", "VisionGraft.apk");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        WriteBuildInfo(outPath, buildStamp);

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

    // Assets/Resources/build_info.txt に「APK 名;ビルド日時;スタンプ」を 1 行書く。
    // 実機の trials.csv の app_build 列がこれになり、どの参加者がどのビルドを見たかを一意にできる（2026-09-25）。
    // gitignore 済み（ビルドごとに変わる）。Editor 実行時は ExperimentBuildInfo がこれを使わず "editor" と書く。
    private static void WriteBuildInfo(string apkPath, string stamp)
    {
        try
        {
            string dir = Path.Combine(Application.dataPath, "Resources");
            Directory.CreateDirectory(dir);
            string fileName = ExperimentBuildInfo.ResourcePath + ".txt";
            string path = Path.Combine(dir, fileName);
            string line =
                $"{Path.GetFileName(apkPath)};{DateTime.Now:yyyy-MM-dd HH:mm:ss};{(string.IsNullOrEmpty(stamp) ? "nostamp" : stamp)}";
            File.WriteAllText(path, line + "\n");
            AssetDatabase.ImportAsset("Assets/Resources/" + fileName, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[BUILD] build_info: {line}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BUILD] build_info.txt を書けませんでした: {ex.Message}");
        }
    }
}
