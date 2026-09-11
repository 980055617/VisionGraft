using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// 被験者実験セッション全体の進行役。ExperimentScene に 1 つだけ置く。
//
// 構成上の要点（Docs/experiment-flow.md 参照）:
//   - ExperimentScene がベースシーンとして常駐し、XR リグ・カメラ・操作パネルを保持する
//   - 試行シーン（TrialScene）は試行ごとに Additive でロードし、終わったらアンロードする
//   - ロード直後に SetActiveScene(TrialScene) する。StreamingStereoVideoPlayer が実行時に
//     生成するモデルインスタンスや UI ルートは親を持たない root オブジェクトなので、
//     アクティブシーンを切り替えておかないと ExperimentScene 側に積み上がり、
//     次の試行に前の試行のモデルが残る
//   - TrialScene に XR リグを置かないこと。プレイヤーは ViewCameraSelection で
//     シーンをまたいでカメラを探すため、ベースシーンのリグがそのまま使われる
//
// 操作チュートリアル（2026-09-11）:
//   - 試行と同じ仕組み（TrialScene を Additive ロード）で、実験の 3 本とは別の bundle を
//     置換ありモードで再生し、ExperimentTutorial が段階を進める
//   - tutorialTiming で「最初の試行の前に 1 回」「各ブロックの前に 1 回ずつ」を選ぶ
//   - Home の「チュートリアル」からはセッション無し（ログ無し）で同じものを回す
[DisallowMultipleComponent]
public sealed class ExperimentController : MonoBehaviour
{
    private enum Phase
    {
        Setup,
        Waiting,
        Loading,
        Trial,
        Tutorial,
        Finished,
    }

    [Header("Scene")]
    public string trialSceneName = "TrialScene";
    // Home からチュートリアルだけ実行したときの戻り先。
    public string homeSceneName = "HomeScene";

    [Header("Participant")]
    public string participantIdPrefix = "P";
    [Min(1)] public int participantNumber = 1;
    public ExperimentGroup group = ExperimentGroup.A;
    [Range(ExperimentPlan.MinVideoOrderPattern, ExperimentPlan.MaxVideoOrderPattern)]
    public int videoOrderPattern = 1;

    [Header("Bundles")]
    public ExperimentBundleCatalog bundleCatalog = new ExperimentBundleCatalog();

    [Header("Tutorial")]
    // 操作チュートリアルをいつ挟むか。既定は最初の試行の前に 1 回。
    public ExperimentTutorialTiming tutorialTiming = ExperimentTutorialTiming.BeforeFirstTrial;
    // 説明パネルの大きさと位置。映像とコントロールバー（画面下中央）を隠さないよう右下に置く。
    // 縦横比は ExperimentPanel の canvas（1200×900）に合わせた 4:3 にして文字を潰さない。
    public Vector2 tutorialPanelSizeMeters = new Vector2(0.64f, 0.48f);
    public Vector2 tutorialPanelOffsetMeters = new Vector2(0.8f, -0.45f);
    // bundle が無い・デコードできないときにここで諦める。試行と違いチュートリアルは
    // 無くても実験は成立するので、待ち続けずに先へ進める。
    [Min(10f)] public float tutorialLoadTimeoutSeconds = 120f;

    [Header("UI")]
    // StreamingStereoVideoPlayer の runtimeControlsPrefab / bundlePickerCanvasWithInteractionRayPrefab
    // と同じ ISDK レイ操作用 prefab を割り当てる。未設定でも素の Canvas で動く。
    public GameObject panelCanvasWithInteractionRayPrefab;
    public float panelDistanceMeters = 1.2f;
    // 試行中パネルは映像を隠さないよう下にずらす。被験者は視聴を終えたら下を見て押す。
    public float trialPanelVerticalOffsetMeters = -0.5f;

    [Header("Logging")]
    public bool logHeadPose = true;
    [Range(1f, 60f)] public float headPoseSampleHz = 15f;

    private Phase phase = Phase.Setup;
    private ExperimentSession session;
    private ExperimentPanel panel;
    private Scene baseScene;
    private Camera cachedCamera;
    private StreamingStereoVideoPlayer cachedPlayer;
    private float nextHeadPoseSampleTime;
    private float nextLogFlushTime;
    private bool trialEndRequested;

    private ExperimentTutorial tutorial;
    private bool tutorialEndRequested;
    private bool tutorialPanelDirty;
    // 済ませた（飛ばしたものも含む）チュートリアルの数。BeforeEachBlock の判定に使う。
    private int tutorialsCompleted;
    // Home の「チュートリアル」で開いた。セッションもログも作らず、終わったら Home へ戻る。
    private bool tutorialOnly;

    private static readonly Vector2 FullPanelSizeMeters = new Vector2(1.05f, 0.82f);
    private const float LogFlushIntervalSeconds = 10f;

    private void Awake()
    {
        baseScene = gameObject.scene;
        panel = new ExperimentPanel(panelCanvasWithInteractionRayPrefab, ResolveCamera)
        {
            DistanceMeters = panelDistanceMeters,
        };
    }

    private void Start()
    {
        if (HomeLaunchHandoff.ConsumeTutorialOnly())
        {
            tutorialOnly = true;
            // 練習中のモデル変更を研究者の基準ファイル（model_selection.json）へ書かせない。
            ExperimentSessionOverrides.BeginSession();
            ShowTutorialWaitingPanel();
            return;
        }

        ShowSetupPanel();
    }

    private void OnDestroy()
    {
        FinishSessionIfRunning(true);
        if (tutorialOnly)
        {
            ExperimentSessionOverrides.EndSession();
        }

        panel?.Destroy();
    }

    private void OnApplicationQuit()
    {
        FinishSessionIfRunning(true);
    }

    // Quest ではヘッドセットを外す・ホームに戻ると一時停止が来る。ここで書き出して
    // おかないと、そのまま終了された場合に進行中の試行のログが丸ごと消える。
    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            session?.FlushLogs();
        }
    }

    // 進行中の試行を中断扱いで確定させてからログを閉じる。
    private void FinishSessionIfRunning(bool aborted)
    {
        if (session == null)
        {
            return;
        }

        if (session.TrialInProgress)
        {
            session.EndTrial(aborted);
        }

        if (session.TutorialInProgress)
        {
            session.EndTutorial("aborted");
        }

        ExperimentLog.Sink = null;
        session.Dispose();
        session = null;

        // 実験を抜けたあと手動でシーンを開いたときに前の参加者の調整が残らないように。
        // ExperimentTrialHandoff.Clear() と同じ配慮。**基準ファイルは消さない。**
        ExperimentSessionOverrides.EndSession();
    }

    private void Update()
    {
        panel?.UpdatePlacement(ResolveHeadTransform());

        if (phase == Phase.Trial)
        {
            SampleHeadPoseIfDue();
            FlushLogsIfDue();
        }
        else if (phase == Phase.Tutorial)
        {
            RefreshTutorialPanelIfDirty();
            FlushLogsIfDue();
        }
    }

    // 頭部姿勢は 15Hz で溜まり続けるので、試行中も定期的に書き出す。
    private void FlushLogsIfDue()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextLogFlushTime)
        {
            return;
        }

        nextLogFlushTime = now + LogFlushIntervalSeconds;
        session?.FlushLogs();
    }

    // ── 参加者 ID ────────────────────────────────────────────────────────

    public string ParticipantId
    {
        get { return $"{participantIdPrefix}{participantNumber:00}"; }
    }

    // ── セットアップ画面 ────────────────────────────────────────────────

    private void ShowSetupPanel()
    {
        phase = Phase.Setup;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("参加者 −", () => AdjustParticipantNumber(-1)),
            ExperimentPanel.ButtonSpec.Create("参加者 ＋", () => AdjustParticipantNumber(1)),
            ExperimentPanel.ButtonSpec.Create("群 A / B", ToggleGroup),
            ExperimentPanel.ButtonSpec.Create("動画順 −", () => AdjustVideoOrderPattern(-1)),
            ExperimentPanel.ButtonSpec.Create("動画順 ＋", () => AdjustVideoOrderPattern(1)),
            ExperimentPanel.ButtonSpec.Create("セッション開始", StartSession),
        };

        panel.Show("実験セットアップ", BuildSetupBody(), specs);
    }

    private string BuildSetupBody()
    {
        return
            $"参加者 ID: {ParticipantId}\n" +
            $"{ExperimentPlan.DescribeAssignment(group, videoOrderPattern)}\n\n" +
            $"全 {ExperimentPlan.TrialCount} 試行 / チュートリアル: {DescribeTutorialTiming()}\n" +
            "割り付け表と一致していることを確認してから開始してください。";
    }

    private string DescribeTutorialTiming()
    {
        switch (tutorialTiming)
        {
            case ExperimentTutorialTiming.BeforeFirstTrial:
                return "最初の試行の前に 1 回";
            case ExperimentTutorialTiming.BeforeEachBlock:
                return "各ブロックの前に 1 回ずつ";
            default:
                return "なし";
        }
    }

    private void AdjustParticipantNumber(int delta)
    {
        participantNumber = Mathf.Max(1, participantNumber + delta);
        panel.SetBody(BuildSetupBody());
    }

    private void ToggleGroup()
    {
        group = group == ExperimentGroup.A ? ExperimentGroup.B : ExperimentGroup.A;
        panel.SetBody(BuildSetupBody());
    }

    private void AdjustVideoOrderPattern(int delta)
    {
        int next = videoOrderPattern + delta;
        // 1..6 を循環させる。端で止まると実験者が戻す手間が増えるため。
        int span = ExperimentPlan.MaxVideoOrderPattern - ExperimentPlan.MinVideoOrderPattern + 1;
        next = ((next - ExperimentPlan.MinVideoOrderPattern) % span + span) % span + ExperimentPlan.MinVideoOrderPattern;
        videoOrderPattern = next;
        panel.SetBody(BuildSetupBody());
    }

    // ── セッション進行 ──────────────────────────────────────────────────

    private void StartSession()
    {
        if (session != null)
        {
            return;
        }

        string logRoot = ExperimentLogWriter.DefaultRootDirectory;
        string sessionDir = ExperimentLogWriter.BuildSessionDirectory(logRoot, ParticipantId, System.DateTime.Now);
        ExperimentLogWriter writer = new ExperimentLogWriter(sessionDir);

        session = new ExperimentSession(
            ParticipantId,
            group,
            videoOrderPattern,
            writer,
            () => cachedPlayer != null ? cachedPlayer.CurrentVideoTimeSeconds : 0d);

        // 参加者が変わるのでモデル・向きのセッション上書きを捨てる。
        // 研究者が仕込んだ基準（model_selection.json）はそのまま読み込まれる。
        ExperimentSessionOverrides.BeginSession();
        tutorialsCompleted = 0;

        Debug.Log($"[Experiment] セッション開始: {ParticipantId} / 群 {group} / 動画順 {videoOrderPattern} / チュートリアル {tutorialTiming}");
        Debug.Log($"[Experiment] ログ出力先: {sessionDir}");

        ShowWaitingPanel();
    }

    private void ShowWaitingPanel()
    {
        phase = Phase.Waiting;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        if (!session.HasNextTrial)
        {
            ShowFinishedPanel();
            return;
        }

        ExperimentTrial next = session.NextTrial;
        if (ShouldRunTutorialBefore(next))
        {
            ShowTutorialWaitingPanel();
            return;
        }

        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"次の試行: {next.Describe(ExperimentPlan.TrialCount)}\n\n" +
            (session.CurrentTrialIndex >= 0
                ? "前の試行のアンケート記入が終わってから開始してください。\n\n"
                : "教示が済んだら開始してください。\n\n") +
            $"ログ: {session.LogDirectory}";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("この試行を開始", BeginNextTrial),
        };

        panel.Show("待機中", body, specs);
    }

    private void BeginNextTrial()
    {
        if (phase != Phase.Waiting || session == null || !session.HasNextTrial)
        {
            return;
        }

        StartCoroutine(RunTrialRoutine(session.NextTrial));
    }

    private IEnumerator RunTrialRoutine(ExperimentTrial trial)
    {
        phase = Phase.Loading;
        trialEndRequested = false;

        // このコルーチンは待機画面のボタンのクリックハンドラから始まる。パネルの
        // 作り直しはそのボタン自身の破棄を伴うので、ハンドラを抜けてから行う。
        yield return null;

        string bundleFileName = bundleCatalog.Resolve(trial.video);
        panel.Show(
            "読み込み中",
            $"{trial.Describe(ExperimentPlan.TrialCount)}\n{bundleFileName}\n\nそのままお待ちください。",
            null);

        // プレイヤーの Start() が読む。シーンをロードする前に必ず置いておくこと。
        ExperimentTrialHandoff.SetPending(
            new ExperimentTrialRequest(bundleFileName, trial.mode, trial.trialIndex, trial.video));

        AsyncOperation load = SceneManager.LoadSceneAsync(trialSceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            Debug.LogError($"[Experiment] 試行シーンをロードできません: {trialSceneName}（Build Settings に追加済みか確認）");
            ExperimentTrialHandoff.Clear();
            ShowWaitingPanel();
            yield break;
        }

        while (!load.isDone)
        {
            yield return null;
        }

        Scene trialScene = SceneManager.GetSceneByName(trialSceneName);
        if (trialScene.IsValid())
        {
            // 実行時生成オブジェクトを試行シーンに属させるため必須。
            SceneManager.SetActiveScene(trialScene);
        }

        cachedPlayer = FindPlayerInScene(trialScene);
        cachedCamera = null;
        session.BeginTrial(trial.trialIndex, bundleFileName);

        // bundle の展開と Prepare が終わって実際に再生が始まるまで待つ
        // （bundle_human.svb は 129MB あり、実機では十数秒かかる）。
        while (cachedPlayer != null && !cachedPlayer.IsVideoPlaying)
        {
            yield return null;
        }

        ShowTrialPanel(trial);

        while (!trialEndRequested)
        {
            yield return null;
        }

        yield return EndTrialRoutine(false);
    }

    private void ShowTrialPanel(ExperimentTrial trial)
    {
        phase = Phase.Trial;
        nextHeadPoseSampleTime = Time.realtimeSinceStartup;
        nextLogFlushTime = Time.realtimeSinceStartup + LogFlushIntervalSeconds;

        // 映像を隠さないよう小さく、視線の下に置く。
        panel.SizeMeters = new Vector2(0.5f, 0.22f);
        panel.OffsetMeters = new Vector2(0f, trialPanelVerticalOffsetMeters);

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("視聴を終了", RequestTrialEnd),
        };

        panel.Show(string.Empty, $"{trial.Describe(ExperimentPlan.TrialCount)}", specs);
    }

    private void RequestTrialEnd()
    {
        if (phase != Phase.Trial)
        {
            return;
        }

        ExperimentLog.Operation("trial_end_pressed");
        trialEndRequested = true;
    }

    private IEnumerator EndTrialRoutine(bool aborted)
    {
        session.EndTrial(aborted);
        yield return UnloadTrialSceneRoutine();
        ShowWaitingPanel();
    }

    // 試行・チュートリアル共通。ベースシーンをアクティブへ戻してから TrialScene を捨てる。
    private IEnumerator UnloadTrialSceneRoutine()
    {
        cachedPlayer = null;
        cachedCamera = null;

        // アンロード前にベースシーンをアクティブへ戻す。アクティブシーンを
        // アンロードすると次に生成するオブジェクトの行き先が不定になる。
        if (baseScene.IsValid())
        {
            SceneManager.SetActiveScene(baseScene);
        }

        Scene trialScene = SceneManager.GetSceneByName(trialSceneName);
        if (trialScene.IsValid() && trialScene.isLoaded)
        {
            AsyncOperation unload = SceneManager.UnloadSceneAsync(trialScene);
            while (unload != null && !unload.isDone)
            {
                yield return null;
            }
        }

        // 動画・モデルのテクスチャが試行ごとに積み上がるので明示的に解放する。
        yield return Resources.UnloadUnusedAssets();
    }

    private void ShowFinishedPanel()
    {
        phase = Phase.Finished;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"全 {ExperimentPlan.TrialCount} 試行が終了しました。\n\n" +
            $"ログ: {session.LogDirectory}\n\n" +
            "最後のアンケートを回収してください。";

        panel.Show("セッション終了", body, null);
        Debug.Log($"[Experiment] セッション終了: {session.ParticipantId} / ログ: {session.LogDirectory}");
        // 以降ログは書かないので、ここでファイルを閉じる。
        session.Dispose();
    }

    // ── 操作チュートリアル ──────────────────────────────────────────────

    // next がブロック先頭の試行で、そのブロックの前のチュートリアルがまだなら true。
    private bool ShouldRunTutorialBefore(ExperimentTrial next)
    {
        if (next.indexInBlock != 0)
        {
            return false;
        }

        switch (tutorialTiming)
        {
            case ExperimentTutorialTiming.BeforeFirstTrial:
                return next.blockIndex == 0 && tutorialsCompleted == 0;
            case ExperimentTutorialTiming.BeforeEachBlock:
                return tutorialsCompleted <= next.blockIndex;
            default:
                return false;
        }
    }

    private void ShowTutorialWaitingPanel()
    {
        phase = Phase.Waiting;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        string bundleFileName = bundleCatalog.Resolve(ExperimentVideo.Tutorial);
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("チュートリアルを開始", BeginTutorial),
        };

        string body;
        if (tutorialOnly)
        {
            body =
                "操作のチュートリアルだけを実行します（ログは残しません）。\n" +
                $"bundle: {bundleFileName}\n\n" +
                "内容: ボタンを押す / A ボタンで一時停止・再開 / Model ボタンでモデルを変える";
            specs.Add(ExperimentPanel.ButtonSpec.Create("Home へ戻る", ReturnToHome));
        }
        else
        {
            body =
                $"参加者 ID: {session.ParticipantId}\n" +
                $"次: 操作のチュートリアル（{DescribeTutorialPosition()}）\n" +
                "内容: ボタンを押す / A ボタンで一時停止・再開 / Model ボタンでモデルを変える\n\n" +
                "教示が済んだら開始してください。\n\n" +
                $"ログ: {session.LogDirectory}";
            specs.Add(ExperimentPanel.ButtonSpec.Create("スキップ", SkipTutorialFromWaiting));
        }

        panel.Show("チュートリアル", body, specs);
    }

    private string DescribeTutorialPosition()
    {
        if (session == null || !session.HasNextTrial)
        {
            return string.Empty;
        }

        return session.NextTrial.blockIndex == 0 ? "最初の試行の前" : "後半ブロックの前";
    }

    private void BeginTutorial()
    {
        if (phase != Phase.Waiting)
        {
            return;
        }

        StartCoroutine(RunTutorialRoutine());
    }

    // 実験者が待機画面で飛ばす。何を飛ばしたかは operations.csv に残す。
    private void SkipTutorialFromWaiting()
    {
        if (phase != Phase.Waiting || session == null)
        {
            return;
        }

        int beforeBlock = session.HasNextTrial ? session.NextTrial.blockIndex : -1;
        session.RecordOperation("tutorial_skipped", $"before_block={beforeBlock}");
        session.FlushLogs();
        tutorialsCompleted++;
        ShowWaitingPanel();
    }

    private IEnumerator RunTutorialRoutine()
    {
        phase = Phase.Loading;
        tutorialEndRequested = false;
        tutorialPanelDirty = false;

        // ボタンのクリックハンドラから始まるので、パネルの作り直しはハンドラを抜けてから。
        yield return null;

        string bundleFileName = bundleCatalog.Resolve(ExperimentVideo.Tutorial);
        int beforeBlock = session != null && session.HasNextTrial ? session.NextTrial.blockIndex : 0;
        panel.Show(
            "読み込み中",
            $"チュートリアル\n{bundleFileName}\n\nそのままお待ちください。",
            null);

        // チュートリアルは常に置換ありモード（Model ボタンを教えるため）。
        ExperimentTrialHandoff.SetPending(
            new ExperimentTrialRequest(bundleFileName, ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial));

        AsyncOperation load = SceneManager.LoadSceneAsync(trialSceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            Debug.LogError($"[Experiment] 試行シーンをロードできません: {trialSceneName}（Build Settings に追加済みか確認）");
            ExperimentTrialHandoff.Clear();
            OnTutorialFinished();
            yield break;
        }

        while (!load.isDone)
        {
            yield return null;
        }

        Scene trialScene = SceneManager.GetSceneByName(trialSceneName);
        if (trialScene.IsValid())
        {
            SceneManager.SetActiveScene(trialScene);
        }

        cachedPlayer = FindPlayerInScene(trialScene);
        cachedCamera = null;

        session?.BeginTutorial(bundleFileName, beforeBlock);
        tutorial = new ExperimentTutorial(session);
        tutorial.Changed += MarkTutorialPanelDirty;
        // プレイヤーの操作ログを横取りして段階を進める。セッションへはそのまま転送される。
        ExperimentLog.Sink = tutorial;

        // 再生が始まるまで待つ。試行と違い、bundle が無ければ諦めて先へ進める。
        float deadline = Time.realtimeSinceStartup + tutorialLoadTimeoutSeconds;
        while (cachedPlayer != null && !cachedPlayer.IsVideoPlaying && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (cachedPlayer == null || !cachedPlayer.IsVideoPlaying)
        {
            Debug.LogError(
                $"[Experiment] チュートリアルの再生が {tutorialLoadTimeoutSeconds:F0} 秒以内に始まりませんでした: " +
                $"{bundleFileName}（[Bundle] のエラーログを確認。共有ストレージか StreamingAssets に無いか、video.mp4 が H.264 でない）");
            ExperimentLog.Sink = null;
            session?.EndTutorial("load_failed");
            yield return UnloadTrialSceneRoutine();
            ShowTutorialLoadFailedPanel(bundleFileName);
            yield break;
        }

        ShowTutorialPanel(false);
        phase = Phase.Tutorial;
        nextLogFlushTime = Time.realtimeSinceStartup + LogFlushIntervalSeconds;

        while (!tutorialEndRequested)
        {
            yield return null;
        }

        ExperimentLog.Sink = null;
        session?.EndTutorial(tutorial.DescribeResult());
        yield return UnloadTrialSceneRoutine();
        OnTutorialFinished();
    }

    private void ShowTutorialPanel(bool keepPlacement)
    {
        panel.SizeMeters = tutorialPanelSizeMeters;
        panel.OffsetMeters = tutorialPanelOffsetMeters;

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>();
        switch (tutorial.CurrentStep)
        {
            case ExperimentTutorial.Step.PressButton:
                // 押せたこと自体が 1 つ目の課題。
                specs.Add(ExperimentPanel.ButtonSpec.Create("次へ", tutorial.CompleteCurrentStep));
                break;
            case ExperimentTutorial.Step.Done:
                specs.Add(ExperimentPanel.ButtonSpec.Create("チュートリアルを終了", RequestTutorialEnd));
                break;
            default:
                // 操作ができない参加者を詰まらせないための逃げ道。飛ばした段階はログに残る。
                specs.Add(ExperimentPanel.ButtonSpec.Create("スキップ", tutorial.SkipCurrentStep));
                break;
        }

        panel.Show(tutorial.Title, tutorial.Body, specs, keepPlacement);
    }

    private void MarkTutorialPanelDirty()
    {
        tutorialPanelDirty = true;
    }

    // 段階が進んだらパネルを作り直す。ボタンのクリックハンドラの中で作り直すと
    // 押したボタン自身を壊すので、次の Update まで遅らせる。
    private void RefreshTutorialPanelIfDirty()
    {
        if (!tutorialPanelDirty || tutorial == null)
        {
            return;
        }

        tutorialPanelDirty = false;
        ShowTutorialPanel(true);
    }

    private void RequestTutorialEnd()
    {
        if (phase != Phase.Tutorial)
        {
            return;
        }

        ExperimentLog.Operation("tutorial_end_pressed");
        tutorialEndRequested = true;
    }

    private void OnTutorialFinished()
    {
        if (tutorial != null)
        {
            tutorial.Changed -= MarkTutorialPanelDirty;
            tutorial = null;
        }

        tutorialsCompleted++;

        if (tutorialOnly)
        {
            ShowTutorialOnlyFinishedPanel();
            return;
        }

        ShowWaitingPanel();
    }

    private void ShowTutorialLoadFailedPanel(string bundleFileName)
    {
        phase = Phase.Waiting;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        string body =
            "チュートリアルの bundle を再生できませんでした。\n" +
            $"{bundleFileName}\n\n" +
            "共有ストレージか StreamingAssets に置いてあるか、video.mp4 が H.264 かを\n" +
            "確認してください（詳細はログの [Bundle] / [Video]）。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(
                tutorialOnly ? "Home へ戻る" : "チュートリアル無しで続行",
                tutorialOnly ? (System.Action)ReturnToHome : OnTutorialFinished),
        };

        panel.Show("チュートリアル読み込み失敗", body, specs);
    }

    private void ShowTutorialOnlyFinishedPanel()
    {
        phase = Phase.Finished;
        panel.SizeMeters = FullPanelSizeMeters;
        panel.OffsetMeters = Vector2.zero;

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("もう一度", ShowTutorialWaitingPanel),
            ExperimentPanel.ButtonSpec.Create("Home へ戻る", ReturnToHome),
        };

        panel.Show("チュートリアル終了", "操作のチュートリアルが終わりました。", specs);
    }

    private void ReturnToHome()
    {
        ExperimentSessionOverrides.EndSession();
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.Clear();
        Debug.Log("[Experiment] return to HomeScene");
        SceneManager.LoadScene(homeSceneName, LoadSceneMode.Single);
    }

    // ── 頭部姿勢ログ ────────────────────────────────────────────────────

    private void SampleHeadPoseIfDue()
    {
        if (!logHeadPose || session == null || !session.TrialInProgress)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now < nextHeadPoseSampleTime)
        {
            return;
        }

        nextHeadPoseSampleTime = now + 1f / Mathf.Max(1f, headPoseSampleHz);

        Transform head = ResolveHeadTransform();
        if (head == null)
        {
            return;
        }

        session.RecordHeadPose(head.position, head.rotation);
    }

    // ── 参照解決 ────────────────────────────────────────────────────────

    private Camera ResolveCamera()
    {
        if (ViewCameraSelection.IsUsable(cachedCamera))
        {
            return cachedCamera;
        }

#if UNITY_2023_1_OR_NEWER
        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
#else
        Camera[] cameras = FindObjectsOfType<Camera>();
#endif
        cachedCamera = ViewCameraSelection.Select(cameras);
        return cachedCamera;
    }

    private Transform ResolveHeadTransform()
    {
        Camera cam = ResolveCamera();
        return cam != null ? cam.transform : null;
    }

    private static StreamingStereoVideoPlayer FindPlayerInScene(Scene scene)
    {
        if (!scene.IsValid())
        {
            return null;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            StreamingStereoVideoPlayer player = roots[i].GetComponentInChildren<StreamingStereoVideoPlayer>(true);
            if (player != null)
            {
                return player;
            }
        }

        Debug.LogError($"[Experiment] {scene.name} に StreamingStereoVideoPlayer が見つかりません。");
        return null;
    }
}
