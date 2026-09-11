using System;

// ExperimentVideo → bundle ファイル名の対応。
//
// 2026-08-28: 解決先が変わった。StreamingStereoVideoPlayer.bundleFileName は
//   1. 共有ストレージ（/storage/emulated/0/VisionGraft など）に同名ファイルがあればそれ
//   2. 無ければ StreamingAssets 直下
// の順で解決される（Bundle.cs の TryResolveBundleInSharedStorage）。
// 実機は adb push した .svb を読むので、APK に 340MB 焼かなくて済む。
// エディタ・バッチには共有ストレージが無いので必ず StreamingAssets 側になる。
//
// 実験では bundle picker を使わずここで決めた名前を注入する。
[Serializable]
public sealed class ExperimentBundleCatalog
{
    public string humanBundleFileName = DefaultHumanBundleFileName;
    public string animalBundleFileName = DefaultAnimalBundleFileName;
    public string trainBundleFileName = DefaultTrainBundleFileName;
    // 操作チュートリアル用。実験の 3 本とは別のクリップで、置換ありモードで再生する
    // （Model ボタンを教えるため）。人か動物が 1 体は写っていること、video.mp4 が
    // H.264 であること（Quest は mp4v を再生できない。ADR 0003）が条件。
    public string tutorialBundleFileName = DefaultTutorialBundleFileName;

    // 2026-08-28: StreamingAssets を「動画ごとに最新 1 本」へ整理した。名前は据え置きで、
    // 中身が再生成版に差し替わっていく。
    // 動画の同一性は manifest.inputs.video_mp4 で見ること。ファイル名は当てにならない。
    //
    // 2026-09-05 現在の中身（D-006 で 3 本とも除去前ステレオ動画を差し替えた）:
    //   bundle_human.svb  : bundle_shots_driftfix_preremovalfix
    //                       ← 背景ドリフト補正あり。**inpaintfix 系を入れないこと**。
    //                       一度間違えた版が配布され、補正が消えるところだった
    //   bundle_animal.svb : bundle_shots_depthdriftfix_shotsfix_preremovalfix（shots 28）
    //   bundle_train.svb  : bundle_shots_inpaintfix_zquantfix_preremovalfix
    //                       ← quant_pos_scale = 0.0001（D-008）
    //
    // **StreamingAssets は APK に丸ごと焼かれる。** 検証用の古い bundle を置きっぱなしに
    // しないこと（2026-09-05 に 737MB → 380MB まで戻した）。
    public const string DefaultHumanBundleFileName = "bundle_human.svb";
    public const string DefaultAnimalBundleFileName = "bundle_animal.svb";
    public const string DefaultTrainBundleFileName = "bundle_train.svb";
    // 2026-09-11 時点の中身: 旧 bundle.svb（01_dog クリップ、289 フレーム）の video.mp4 を
    // H.264 に再エンコードした暫定版。生成側が正式なチュートリアル用 bundle を出したら差し替える。
    public const string DefaultTutorialBundleFileName = "bundle_tutorial.svb";

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
            case ExperimentVideo.Tutorial:
                return FallbackIfBlank(tutorialBundleFileName, DefaultTutorialBundleFileName);
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
