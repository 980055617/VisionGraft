using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor の Build / Build And Run でも Assets/Resources/build_info.txt を書く（2026-10-26）。
//
// これまで build_info.txt を書くのは AndroidBuilder（バッチ・メニューのビルド）だけで、Editor の Build And Run は前回のバッチのビルドの
// ファイルをそのまま APK に入れていた。10/26 18:21 にユーザーが Build And Run で作った版（作業ツリーの既定のまま）は、
// 見る用の版 f のスタンプ "VisionGraft_preview_20261026f.apk;...;git:e43dbf8-dirty-previewf" を持っていた。trials.csv の app_build
// （ExperimentBuildInfo）はこの 1 行なので、その版で実験すると別の版を見たことになる。
// AndroidBuilder が書いた直後のビルドだけはそのまま使う（WrittenByAndroidBuilder）。それ以外は「出力の名前;日時;git:<短いハッシュ>[-dirty]-editor」。
public sealed class BuildInfoStampForEditorBuilds : IPreprocessBuildWithReport
{
    // AndroidBuilder.WriteBuildInfo が書いた直後に true。次のビルドの前処理で読んで戻す。
    internal static bool WrittenByAndroidBuilder;

    public int callbackOrder => -10;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (WrittenByAndroidBuilder)
        {
            WrittenByAndroidBuilder = false;
            return;
        }

        try
        {
            string output = report != null ? Path.GetFileName(report.summary.outputPath) : "unknown";
            string line = $"{(string.IsNullOrEmpty(output) ? "unknown" : output)};{DateTime.Now:yyyy-MM-ddTHH:mm:ss};{ResolveGitStamp()}-editor";
            string dir = Path.Combine(Application.dataPath, "Resources");
            Directory.CreateDirectory(dir);
            string fileName = ExperimentBuildInfo.ResourcePath + ".txt";
            File.WriteAllText(Path.Combine(dir, fileName), line + "\n");
            AssetDatabase.ImportAsset("Assets/Resources/" + fileName, ImportAssetOptions.ForceSynchronousImport);
            UnityEngine.Debug.Log($"[BUILD] build_info (editor build): {line}");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[BUILD] build_info.txt を書けませんでした（editor build）: {ex.Message}");
        }
    }

    // "git:<短いハッシュ>" に、作業ツリーに変更があれば "-dirty" を付ける。git が使えなければ "git:unknown"。
    private static string ResolveGitStamp()
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        string head = RunGit(root, "rev-parse --short HEAD");
        if (string.IsNullOrEmpty(head))
        {
            return "git:unknown";
        }

        string status = RunGit(root, "status --porcelain --untracked-files=no");
        return "git:" + head.Trim() + (string.IsNullOrWhiteSpace(status) ? string.Empty : "-dirty");
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (Process process = Process.Start(info))
            {
                if (process == null)
                {
                    return null;
                }

                string text = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(10000) || process.ExitCode != 0)
                {
                    return null;
                }

                return text;
            }
        }
        catch
        {
            return null;
        }
    }
}
