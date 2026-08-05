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
