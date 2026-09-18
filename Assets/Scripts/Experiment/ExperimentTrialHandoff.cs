// 試行シーンをロードする前に「どの bundle をどの条件で再生するか」を置いておく受け渡し口。
//
// 試行ごとにシーンを丸ごとロードし直す構成なので、StreamingStereoVideoPlayer の
// Inspector 値を事前に書き換えることができない（インスタンスがまだ存在しない）。
// static に置いておき、プレイヤーの Start() が起動直後に Consume して自分に適用する。
//
// 実験を行わない通常シーン（TestScene 等）では Pending が null のままなので、
// プレイヤーは従来どおり Inspector の設定で動く。
public static class ExperimentTrialHandoff
{
    public static ExperimentTrialRequest Pending { get; private set; }

    public static void SetPending(ExperimentTrialRequest request)
    {
        Pending = request;
    }

    // 適用は 1 回だけ。取り出したら消しておかないと、実験終了後に手動でシーンを開いた
    // ときにも古い試行設定が効いてしまう。
    public static ExperimentTrialRequest Consume()
    {
        ExperimentTrialRequest request = Pending;
        Pending = null;
        return request;
    }

    public static void Clear()
    {
        Pending = null;
    }
}

// 1 試行分の再生指示。ExperimentController が生成し、プレイヤーが読む。
public sealed class ExperimentTrialRequest
{
    public ExperimentTrialRequest(
        string bundleFileName,
        ExperimentDisplayMode mode,
        int trialIndex,
        ExperimentVideo video)
        : this(bundleFileName, mode, trialIndex, video, null, null, false)
    {
    }

    // onlyCategory: この category 名（"animal" など）の track だけを読む。null / 空 = 全部。
    //   チュートリアルの旧 dog クリップは犬を「person」としても誤検出しているので、
    //   "animal" にして人モデルが犬に重ならないようにする（2026-09-11 実機指摘）。
    // preferredAnimalModelName: animal の既定モデルの prefab 名（"00_Dog" など）。null = Inspector の値。
    // skipTrackCustomizationRestore: model_selection.json とセッション上書きを読まない。
    //   チュートリアルは毎回同じ見た目で始めたいので true にする。
    public ExperimentTrialRequest(
        string bundleFileName,
        ExperimentDisplayMode mode,
        int trialIndex,
        ExperimentVideo video,
        string onlyCategory,
        string preferredAnimalModelName,
        bool skipTrackCustomizationRestore)
    {
        this.bundleFileName = bundleFileName;
        this.mode = mode;
        this.trialIndex = trialIndex;
        this.video = video;
        this.onlyCategory = onlyCategory;
        this.preferredAnimalModelName = preferredAnimalModelName;
        this.skipTrackCustomizationRestore = skipTrackCustomizationRestore;
    }

    public readonly string bundleFileName;
    public readonly ExperimentDisplayMode mode;
    public readonly int trialIndex;
    public readonly ExperimentVideo video;
    public readonly string onlyCategory;
    public readonly string preferredAnimalModelName;
    public readonly bool skipTrackCustomizationRestore;

    // StereoOnly と Monocular は normal mode（source/pre_removal_stereo_video.mp4）で再生する。
    public bool StartInNormalMode
    {
        get { return mode == ExperimentDisplayMode.StereoOnly || mode == ExperimentDisplayMode.Monocular; }
    }

    // Monocular は左目映像を両目に出す（Screens.cs の ApplyStereoUvSettings）。
    public bool StartMonocular
    {
        get { return mode == ExperimentDisplayMode.Monocular; }
    }
}
