using UnityEngine;

// このビルドを一意に指す文字列。trials.csv の app_build 列に書く（2026-09-25、論文側の依頼 §3.3「再現性の担保」）。
//
// AndroidBuilder が APK を作るときに Assets/Resources/build_info.txt を書き出す
// （中身は 1 行: "<APK 名>;<ビルド日時>;<git の短いハッシュ>"）。Editor で走らせたときはそのファイルが
// 無いか古いので、bundleVersion と "editor" で済ませる。ファイルは gitignore 済み（ビルドごとに変わるため）。
public static class ExperimentBuildInfo
{
    public const string ResourcePath = "build_info";

    private static string cached;

    public static string Resolve()
    {
        if (cached != null)
        {
            return cached;
        }

        string fromResource = null;
        try
        {
            TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
            {
                fromResource = asset.text.Trim();
                int newline = fromResource.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0)
                {
                    fromResource = fromResource.Substring(0, newline).Trim();
                }
            }
        }
        catch
        {
            fromResource = null;
        }

        if (Application.isEditor)
        {
            // Editor では前回ビルドの build_info.txt が残っていることがあるので、その値だとは言わない。
            cached = $"{Application.version};editor";
        }
        else
        {
            cached = string.IsNullOrEmpty(fromResource) ? $"{Application.version};no-build-info" : fromResource;
        }

        return cached;
    }
}
