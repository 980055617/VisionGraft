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

    // インタラクティブモーション（Random / frame-out）の**開始時の値**。置換あり条件は ON、
    // StereoOnly / Monocular はモデルを出さないので意味が無く OFF（2026-09-25、論文側の依頼 §1）。
    // プレイヤー側はこの値で enableInteractiveMotion を上書きする（TrialScene の serialize 値より優先）。
    // チュートリアルも同じ経路なので、置換ありブロックの練習でも ON で始まる。
    public bool InteractiveMotionEnabled
    {
        get { return mode == ExperimentDisplayMode.ModelReplaced; }
    }

    // Settings の Motion トグルを出さずに固定するか。
    // **置換あり条件では被験者が切り替えられる**（2026-09-25 のユーザー指示。同日午前に論文側の依頼で
    // 全条件を固定にしたが、置換ありだけ取り消した。切り替えは operations.csv の `motion_toggle` に残る）。
    // 単眼・ステレオはモデルが出ないので固定のまま（トグルを出さない）。
    public bool LockInteractiveMotion
    {
        get { return mode != ExperimentDisplayMode.ModelReplaced; }
    }

    // 置換ありブロックの**チュートリアルだけ**発火間隔を短くする。本番は 12 / 24 秒なので、
    // モデルを替えて終わるだけの短い練習では 1 回も出ないことがある（2026-09-25 の見立て）。
    // 「そのモードの説明の例も見せたい」（同日指示）ので、練習では必ず 1 回出るようにする。
    public const float TutorialInteractiveMotionMinIntervalSeconds = 3f;
    public const float TutorialInteractiveMotionMaxIntervalSeconds = 6f;

    public bool UseTutorialInteractiveMotionInterval
    {
        get { return video == ExperimentVideo.Tutorial && mode == ExperimentDisplayMode.ModelReplaced; }
    }
}
