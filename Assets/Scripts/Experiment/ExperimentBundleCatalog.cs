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
    // 2026-10-22: 30 秒 × 6 本の新しい実験。要素 i が ExperimentVideo.Video(i+1)。
    // 2026-10-23: 届いた 4 本を名前の順に割り当てた（ユーザー「好きなようにでいいよ」。どの割り当てでも動画順の方格で相殺される）:
    //   Video1 = bundle_balloon（風船）、Video2 = bundle_cat（猫）、Video3 = bundle_soccer（人とボール）、Video4 = bundle_tv（廊下の女性）。
    //   Video5・Video6 は作成中の 2 本（仮名のまま。届いたら StreamingAssets に置く名前に合わせて、ここと ExperimentScene の Inspector を書き換える）。
    // 要素が足りない・空のときは既定名に落ちる（Inspector で配列の長さを変えても壊れない）。
    //
    // 旧実験（3 本 × 3 条件）の bundle_human / animal / car.svb は
    // Build/archive/experiment-v2_20261022/ にコピーし、sha256 を控えた（タグ experiment-v2-final）。
    public string[] videoBundleFileNames = (string[])DefaultVideoBundleFileNames.Clone();
    // 操作チュートリアル用。実験の 6 本とは別のクリップで、置換ありモードで再生する
    // （Model ボタンを教えるため）。人か動物が 1 体は写っていること、video.mp4 が
    // H.264 であること（Quest は mp4v を再生できない。ADR 0003）が条件。
    public string tutorialBundleFileName = DefaultTutorialBundleFileName;

    // **StreamingAssets は APK に丸ごと焼かれる。** 検証用の古い bundle を置きっぱなしに
    // しないこと（2026-09-05 に 737MB → 380MB まで戻した）。
    // 動画の同一性は manifest.inputs.video_mp4 で見ること。ファイル名は当てにならない。
    public static readonly string[] DefaultVideoBundleFileNames =
    {
        "bundle_balloon.svb",
        "bundle_cat.svb",
        "bundle_soccer.svb",
        "bundle_tv.svb",
        "bundle_video5.svb",
        "bundle_video6.svb",
    };

    // 2026-09-11 時点の中身: 旧 bundle.svb（01_dog クリップ、289 フレーム）の video.mp4 を
    // H.264 に再エンコードした暫定版。生成側が正式なチュートリアル用 bundle を出したら差し替える。
    public const string DefaultTutorialBundleFileName = "bundle_tutorial.svb";

    public string Resolve(ExperimentVideo video)
    {
        if (video == ExperimentVideo.Tutorial)
        {
            return FallbackIfBlank(tutorialBundleFileName, DefaultTutorialBundleFileName);
        }

        int index = (int)video - (int)ExperimentVideo.Video1;
        if (index < 0 || index >= DefaultVideoBundleFileNames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(video), video, "未知の動画種別です。");
        }

        string configured = videoBundleFileNames != null && index < videoBundleFileNames.Length
            ? videoBundleFileNames[index]
            : null;
        return FallbackIfBlank(configured, DefaultVideoBundleFileNames[index]);
    }

    private static string FallbackIfBlank(string value, string fallback)
    {
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
