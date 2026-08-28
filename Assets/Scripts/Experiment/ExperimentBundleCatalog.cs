using System;

// ExperimentVideo → StreamingAssets 内の bundle ファイル名の対応。
//
// StreamingStereoVideoPlayer.bundleFileName は StreamingAssets 直下からの相対名として
// 解決される（StreamingStereoVideoPlayer.Bundle.cs の EnsureBundleAndPrepareVideo 参照）。
// 実験では bundle picker を使わずここで決めた名前を注入する。
[Serializable]
public sealed class ExperimentBundleCatalog
{
    public string humanBundleFileName = DefaultHumanBundleFileName;
    public string animalBundleFileName = DefaultAnimalBundleFileName;
    public string trainBundleFileName = DefaultTrainBundleFileName;

    // 2026-08-28: StreamingAssets を「動画ごとに最新 1 本」へ整理した。名前は据え置きで、
    // 中身が再生成版に差し替わっている（depth drift 修正 + shot 再検出）。
    //   bundle_human.svb  : 2026-08-20 ビルド
    //   bundle_animal.svb : 2026-08-27 ビルド（shots 28）
    //   bundle_train.svb  : 2026-08-19 ビルド（再生成不要と確認済み）
    // 動画の同一性は manifest.inputs.video_mp4 で見ること。ファイル名は当てにならない。
    public const string DefaultHumanBundleFileName = "bundle_human.svb";
    public const string DefaultAnimalBundleFileName = "bundle_animal.svb";
    public const string DefaultTrainBundleFileName = "bundle_train.svb";

    public string Resolve(ExperimentVideo video)
    {
        switch (video)
        {
            case ExperimentVideo.Human:
                return FallbackIfBlank(humanBundleFileName, DefaultHumanBundleFileName);
            case ExperimentVideo.Animal:
                return FallbackIfBlank(animalBundleFileName, DefaultAnimalBundleFileName);
            case ExperimentVideo.Train:
                return FallbackIfBlank(trainBundleFileName, DefaultTrainBundleFileName);
            default:
                throw new ArgumentOutOfRangeException(nameof(video), video, "未知の動画種別です。");
        }
    }

    public static string ResolveDefault(ExperimentVideo video)
    {
        return new ExperimentBundleCatalog().Resolve(video);
    }

    private static string FallbackIfBlank(string value, string fallback)
    {
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
