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
    // 0 = テスト用（P00）。実際の実験は P01 から（2026-09-11）。ログは P00_... のフォルダに分かれるので、
    // 解析時に participant_id で除外できる。
    [Min(0)] public int participantNumber = 1;
    public const int TestParticipantNumber = 0;
    // 群と動画順を参加者番号から自動で決める（ExperimentPlan.ResolveAssignment。2026-09-11）。
    // OFF にするとセットアップ画面に「群 A / B」「動画順 ±」のボタンが出て手で振れる。
    public bool deriveAssignmentFromParticipantNumber = true;
    public ExperimentGroup group = ExperimentGroup.A;
    [Range(ExperimentPlan.MinVideoOrderPattern, ExperimentPlan.MaxVideoOrderPattern)]
    public int videoOrderPattern = 1;

    [Header("Bundles")]
    public ExperimentBundleCatalog bundleCatalog = new ExperimentBundleCatalog();

    [Header("Tutorial")]
    // 操作チュートリアルをいつ挟むか。既定は各ブロックの直前に 1 回ずつ（2026-09-11 の設計）。
    // 内容はブロックの表示条件で変わる（ExperimentTutorial.ResolveSequence）。
    public ExperimentTutorialTiming tutorialTiming = ExperimentTutorialTiming.BeforeEachBlock;
    // 説明パネルは**動画の画面の外側**に置く（画面に被るとレイが画面のコライダーに取られて
    // ボタンが押せない。2026-09-11 実機指摘）。位置は頭の向きではなく画面を基準に決める。
    // 下はコントロールバー、右は Model パネルが使うので既定は上。
    public ExperimentPanelSide tutorialPanelSide = ExperimentPanelSide.AboveScreen;
    // 大きさ（UI 距離 panelDistanceMeters での m）と画面端からの隙間。
    // 0.6×0.33 は ExperimentPanel.CompactLargeTextLayout（canvas 1200×660）と同じ縦横比で、文字が潰れない。
    public Vector2 tutorialPanelSizeMeters = new Vector2(0.6f, 0.33f);
    [Min(0f)] public float tutorialPanelGapMeters = 0.05f;
    // 画面基準の位置からさらにずらしたいとき（UI 距離での m）。通常は 0。
    public Vector2 tutorialPanelNudgeMeters = Vector2.zero;
    // 読む track の category（空 = 全部）。暫定 bundle（旧 dog クリップ）は person track と animal track の
    // 両方を持つが、**人モデルも出したまま**にする（2026-09-11、「人モデルは戻して」）。
    // 一度 "animal" にして人を消したのは指摘の読み違いだった。絞り込みが要る bundle が来たときだけ使う。
    public string tutorialOnlyCategory = "";
    // animal に置く既定モデルの prefab 名（空 = TrialScene の selectedAnimalIndex）。
    // 00_Dog ではなく別の犬にする指示（2026-09-11）。候補は 27_GermanShepherd / 36_LabradorDog。
    public string tutorialAnimalModelName = "36_LabradorDog";
    // bundle が無い・デコードできないときにここで諦める。試行と違いチュートリアルは
    // 無くても実験は成立するので、待ち続けずに先へ進める。
    [Min(10f)] public float tutorialLoadTimeoutSeconds = 120f;

    [Header("UI")]
    // StreamingStereoVideoPlayer の runtimeControlsPrefab / bundlePickerCanvasWithInteractionRayPrefab
    // と同じ ISDK レイ操作用 prefab を割り当てる。未設定でも素の Canvas で動く。
    public GameObject panelCanvasWithInteractionRayPrefab;
    public float panelDistanceMeters = 1.2f;
    // 試行中の「視聴を終了」パネルも画面の外側（既定は上）に置く。以前は頭の向き基準で下 0.5 m に
    // 出していたが、画面の下にはプレイヤーのコントロールバー（Pause / Model / Settings…）があり、
    // 重なって押せなかった（2026-09-11 実機指摘）。
    public ExperimentPanelSide trialPanelSide = ExperimentPanelSide.AboveScreen;
    // 0.6×0.19 は ExperimentPanel.TrialBarLayout（canvas 1200×380）と同じ縦横比で、文字が潰れない。
    public Vector2 trialPanelSizeMeters = new Vector2(0.6f, 0.19f);
    [Min(0f)] public float trialPanelGapMeters = 0.05f;
    // 試行を終えられるまでの最短時間（秒）。すぐ次へ行かせないため。パネルは最初から出しておき
    // （急に出ると押したくなる）、この時間までボタンを押せなくする。残り時間は出さない（2026-09-11）。
    // 最初は動画 1 周（human 72 s）より長い 90 s にしたが、同日「1:30 → 1:10」の指示で 70 s に緩めた。
    // テスト用の P00 には掛けない（ResolveTrialMinimumViewingSeconds）。チュートリアルにも掛けない。
    [Min(0f)] public float trialMinimumViewingSeconds = 70f;

    [Header("Logging")]
    public bool logHeadPose = true;
    [Range(1f, 60f)] public float headPoseSampleHz = 15f;
    // 1 秒ごとの描画レート・動画の進み・コントローラの動き（perf.csv、2026-09-25）。
    public bool logPerf = true;

    [Header("Trial Loading")]
    // 試行の bundle 読み込み（展開 + Prepare）がこの秒数で始まらなければ失敗として閉じる（2026-09-25）。
    // 以前は期限が無く、bundle が無い・デコードできないと「読み込み中」で永久に止まった
    // （Docs/architecture-audit-2026-09-18.md #1）。実機の読み込みは十数秒なので 60 s で十分に余裕がある。
    // プレイヤー側が失敗を報告したとき（bundle が無い・展開失敗・動画エラー）は期限を待たずに閉じる。
    [Min(10f)] public float trialLoadTimeoutSeconds = 60f;

    private Phase phase = Phase.Setup;
    private ExperimentSession session;
    private ExperimentPanel panel;
    private Scene baseScene;
    private Camera cachedCamera;
    private StreamingStereoVideoPlayer cachedPlayer;
    private float nextHeadPoseSampleTime;
    private float nextLogFlushTime;
    private bool trialEndRequested;
    // 読み込み中パネルの「中止」。
    private bool loadCancelRequested;
    private readonly ExperimentPerfAccumulator perf = new ExperimentPerfAccumulator();
    private readonly List<UnityEngine.XR.InputDevice> xrDevices = new List<UnityEngine.XR.InputDevice>();

    private ExperimentTutorial tutorial;
    private bool tutorialEndRequested;
    private bool tutorialPanelDirty;

    // 試行中の「視聴を終了」を押せるようになる時刻と、押せるようにしたかどうか。
    private float trialEndAllowedAtRealtime;
    private bool trialEndEnabled;
    private const string TrialEndButtonLabel = "視聴を終了";
    // ブロックごとに「そのブロックの前の練習を済ませたか（飛ばしたものも含む）」。
    // 以前は 1 本のカウンタで `tutorialsCompleted <= blockIndex` と判定していたので、二重押しで
    // 1 つ余計に進むと**別のブロックの練習が丸ごと消えた**（2026-09-25 の監査 F-2）。
    // ブロックごとに持てば、数がずれても他のブロックに波及しない。
    private readonly bool[] tutorialDoneForBlock = new bool[ExperimentPlan.BlockCount];
    // 実行中の練習がどのブロックの前のものか（-1 = 実行中でない）。終わったときにその枠を立てる。
    private int tutorialBlockInProgress = -1;
    // 待機画面のボタンの二重押し止め。押してからボタンを作り直すまでの間だけ立てる。
    private bool waitingActionInProgress;

    private static readonly Vector2 FullPanelSizeMeters = new Vector2(1.05f, 0.82f);
    private const float LogFlushIntervalSeconds = 10f;
    // 読み込み中パネルを画面より手前に置くときの余裕。プレイヤーの Model パネルと同じ 0.10 m。
    private const float LoadingPanelInFrontOfScreenMarginMeters = 0.10f;

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
        ShowSetupPanel();
    }

    private void OnDestroy()
    {
        FinishSessionIfRunning(true, "controller_destroyed");
        panel?.Destroy();
    }

    private void OnApplicationQuit()
    {
        FinishSessionIfRunning(true, "app_quit");
    }

    // Quest ではヘッドセットを外す・ホームに戻ると一時停止が来る。ここで書き出して
    // おかないと、そのまま終了された場合に進行中の試行のログが丸ごと消える。
    private void OnApplicationPause(bool paused)
    {
        // **ヘッドセットを外したこと自体を記録する。**以前は flush だけで、operations.csv に何も残らず、
        // 「最短視聴時間 70 秒（実時間）のうちどれだけ装着していなかったか」を後から追えなかった
        // （2026-09-25 の監査 F-6）。装着時間の記録は 70 秒の基準を実時間のままにする判断
        // （同日ユーザー指示）と対で要る。
        if (session == null)
        {
            return;
        }

        if (paused)
        {
            session.RecordOperation("app_pause", $"phase={phase} video_played_sec={ExperimentCsv.Format(session.TrialVideoPlayedSeconds)}");
            session.FlushLogs();
            return;
        }

        session.RecordOperation("app_resume", $"phase={phase}");
        session.FlushLogs();
    }

    // 進行中の試行を中断扱いで確定させてからログを閉じる。abortReason は trials.csv の abort_reason 列。
    private void FinishSessionIfRunning(bool aborted, string abortReason)
    {
        if (session == null)
        {
            return;
        }

        if (session.TrialInProgress)
        {
            session.EndTrial(aborted, abortReason);
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
        Transform head = ResolveHeadTransform();
        panel?.UpdatePlacement(head, ExperimentPanel.ResolveFrontForward(head));

        if (phase == Phase.Trial)
        {
            SampleHeadPoseIfDue();
            SamplePerfIfDue();
            FlushLogsIfDue();
            UpdateTrialEndButtonIfDue();
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

    // 局面ごとのパネルの大きさ・頭基準のずらし量・文字レイアウトをまとめて設定する。
    private void PreparePanel(Vector2 sizeMeters, Vector2 offsetMeters, ExperimentPanel.Layout layout)
    {
        panel.SizeMeters = sizeMeters;
        panel.OffsetMeters = offsetMeters;
        panel.CurrentLayout = layout;
        // 読み込み中パネルが画面より手前へ寄せた距離を、次の局面で元に戻す。
        panel.DistanceMeters = panelDistanceMeters;
    }

    // 読み込み中だけ、パネルを**画面より手前**に置く。既定の 1.2 m は画面（試行シーンでは 1.0 m）より
    // 奥にあり、角度も画面の内側に入るので、レイを画面のコライダーと ISDK サーフェスに取られて
    // 「中止」が押せなかった。押せないと 60 秒の期限まで待つしかない（2026-09-25 の監査 F-1）。
    // 見かけの大きさを変えないよう、寄せた距離の比でパネルの寸法も縮める。
    private void PrepareLoadingPanelPlacement()
    {
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        Transform head = ResolveHeadTransform();
        if (cachedPlayer == null || head == null ||
            !cachedPlayer.TryGetScreenFrame(out Vector3 center, out _, out _, out _))
        {
            return;
        }

        float screenDistance = Vector3.Distance(head.position, center);
        float wanted = screenDistance - LoadingPanelInFrontOfScreenMarginMeters;
        if (wanted < 0.35f || wanted >= panelDistanceMeters)
        {
            return;
        }

        float ratio = wanted / panelDistanceMeters;
        panel.DistanceMeters = wanted;
        panel.SizeMeters = FullPanelSizeMeters * ratio;
    }

    private void ShowSetupPanel()
    {
        phase = Phase.Setup;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        ApplyAssignmentFromParticipantNumber();

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("参加者 −", () => AdjustParticipantNumber(-1)),
            ExperimentPanel.ButtonSpec.Create("参加者 ＋", () => AdjustParticipantNumber(1)),
        };

        if (!deriveAssignmentFromParticipantNumber)
        {
            specs.Add(ExperimentPanel.ButtonSpec.Create("群 A / B", ToggleGroup));
            specs.Add(ExperimentPanel.ButtonSpec.Create("動画順 −", () => AdjustVideoOrderPattern(-1)));
            specs.Add(ExperimentPanel.ButtonSpec.Create("動画順 ＋", () => AdjustVideoOrderPattern(1)));
        }

        specs.Add(ExperimentPanel.ButtonSpec.Create("セッション開始", StartSession));
        // 間違えて入ったときの戻り道（2026-09-11 実機「被験者実験を押すと戻れない」）。
        specs.Add(ExperimentPanel.ButtonSpec.Create("Home へ戻る", ReturnToHome));

        panel.Show("実験セットアップ", BuildSetupBody(), specs);
    }

    // 参加者番号から群と動画順を決める（自動割り付けが ON のとき）。
    private void ApplyAssignmentFromParticipantNumber()
    {
        if (!deriveAssignmentFromParticipantNumber)
        {
            return;
        }

        ExperimentPlan.ResolveAssignment(participantNumber, out group, out videoOrderPattern);
    }

    private string BuildSetupBody()
    {
        string participantLine = participantNumber == TestParticipantNumber
            ? $"参加者 ID: {ParticipantId}（テスト用。実験は P01 から）\n"
            : $"参加者 ID: {ParticipantId}\n";
        string assignmentNote = deriveAssignmentFromParticipantNumber
            ? "（群と動画順は参加者番号から自動: 奇数 = A、偶数 = B、動画順は 2 人ごとに 1→6）\n"
            : string.Empty;
        return
            participantLine +
            $"{ExperimentPlan.DescribeAssignment(group, videoOrderPattern)}\n" +
            assignmentNote +
            $"\n全 {ExperimentPlan.TrialCount} 試行 / チュートリアル: {DescribeTutorialTiming()}\n" +
            "参加者 ID を確認してから開始してください。";
    }

    private string DescribeTutorialTiming()
    {
        switch (tutorialTiming)
        {
            case ExperimentTutorialTiming.BeforeFirstTrial:
                return "最初の試行の前に 1 回";
            case ExperimentTutorialTiming.BeforeEachBlock:
                return $"各ブロックの前に 1 回ずつ（{ExperimentPlan.BlockCount} 回）";
            default:
                return "なし";
        }
    }

    private void AdjustParticipantNumber(int delta)
    {
        // 0（P00）はテスト用に許す。
        participantNumber = Mathf.Max(TestParticipantNumber, participantNumber + delta);
        ApplyAssignmentFromParticipantNumber();
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
        System.Array.Clear(tutorialDoneForBlock, 0, tutorialDoneForBlock.Length);
        tutorialBlockInProgress = -1;
        waitingActionInProgress = false;

        Debug.Log($"[Experiment] セッション開始: {ParticipantId} / 群 {group} / 動画順 {videoOrderPattern} / チュートリアル {tutorialTiming}");
        Debug.Log($"[Experiment] ログ出力先: {sessionDir}");

        ShowWaitingPanel();
    }

    private void ShowWaitingPanel()
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

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
            $"次: {next.DescribeForParticipant(ExperimentPlan.TrialCount)}\n\n" +
            // ログの保存先はここに出さない（被験者が知る必要はない。2026-09-11 指示）。Debug.Log には残る。
            (session.CurrentTrialIndex >= 0
                ? "アンケートの記入が終わったら、実験者の合図で\n「この試行を開始」を押してください。"
                : "実験者の合図があったら\n「この試行を開始」を押してください。");

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("この試行を開始", BeginNextTrial),
            // 設定を間違えたまま始めたとき用。ここまでのログは残して閉じる。
            ExperimentPanel.ButtonSpec.Create("中止して Home へ", ReturnToHome),
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
        loadCancelRequested = false;

        // このコルーチンは待機画面のボタンのクリックハンドラから始まる。パネルの
        // 作り直しはそのボタン自身の破棄を伴うので、ハンドラを抜けてから行う。
        yield return null;

        string bundleFileName = bundleCatalog.Resolve(trial.video);
        ShowLoadingPanel(trial.DescribeForParticipant(ExperimentPlan.TrialCount), bundleFileName);

        // プレイヤーの Start() が読む。シーンをロードする前に必ず置いておくこと。
        ExperimentTrialHandoff.SetPending(
            new ExperimentTrialRequest(bundleFileName, trial.mode, trial.trialIndex, trial.video));

        AsyncOperation load = SceneManager.LoadSceneAsync(trialSceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            // Build Settings に TrialScene が無い等。理由を出さずに待機画面へ戻ると、実験者が
            // 「開始」を押しても一瞬で戻るだけで原因が分からない（2026-09-25 の監査 F-10）。
            Debug.LogError($"[Experiment] 試行シーンをロードできません: {trialSceneName}（Build Settings に追加済みか確認）");
            ExperimentTrialHandoff.Clear();
            ShowTrialNotStartedPanel(trial);
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
        // （bundle_human.svb は 165MB あり、実機では十数秒かかる）。
        // 期限・プレイヤー側の失敗報告・「中止」ボタンのどれかで抜ける（2026-09-25）。
        float deadline = Time.realtimeSinceStartup + trialLoadTimeoutSeconds;
        string failure = null;
        bool loadingPanelMovedInFront = false;
        while (true)
        {
            if (cachedPlayer == null)
            {
                failure = "load_failed:player_missing";
                break;
            }

            // 動画のスクリーンは Prepare が終わった時点で作られる。パネルはそれより前に置いてあるので、
            // 画面が出てきたところで一度だけ「画面より手前」に置き直す。そうしないと「中止」が
            // 画面の裏に入って押せない（2026-09-25 の監査 F-1）。
            if (!loadingPanelMovedInFront &&
                cachedPlayer.TryGetScreenFrame(out Vector3 _, out Vector3 _, out Vector3 _, out Vector2 _))
            {
                loadingPanelMovedInFront = true;
                ShowLoadingPanel(trial.DescribeForParticipant(ExperimentPlan.TrialCount), bundleFileName);
            }
            if (cachedPlayer.IsVideoPlaying)
            {
                break;
            }
            if (loadCancelRequested)
            {
                failure = "cancelled_by_button";
                break;
            }
            if (cachedPlayer.BundleLoadFailed)
            {
                failure = "load_failed:" + cachedPlayer.BundleLoadFailureMessage;
                break;
            }
            if (Time.realtimeSinceStartup >= deadline)
            {
                failure = "load_timeout";
                break;
            }
            yield return null;
        }

        if (failure != null)
        {
            yield return AbortTrialAfterLoadFailure(trial, bundleFileName, failure);
            yield break;
        }

        RecordTrialStartState();
        ShowTrialPanel(trial);

        while (!trialEndRequested)
        {
            yield return null;
        }

        yield return EndTrialRoutine(false, null);
    }

    // 読み込み中は「中止」だけ押せる。押されると読み込みを待つのをやめ、その試行を aborted で閉じる。
    // bundle のファイル名は被験者に出さない（2026-09-25 の監査）。
    private void ShowLoadingPanel(string title, string bundleFileName)
    {
        PrepareLoadingPanelPlacement();
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("中止", RequestLoadCancel),
        };
        panel.Show(
            "読み込み中",
            $"{title}\n\n動画を読み込んでいます。\nそのままお待ちください。\n（動かないときは実験者が「中止」を押す）",
            specs);
        Debug.Log($"[Experiment] 読み込み中: {title} / {bundleFileName}");
    }

    private void RequestLoadCancel()
    {
        if (phase != Phase.Loading)
        {
            return;
        }

        Debug.LogWarning("[Experiment] 読み込み中に「中止」が押されました");
        loadCancelRequested = true;
    }

    // 読み込みに失敗した試行を aborted で閉じ、シーンを捨て、実験者に次の手を選ばせる。
    private IEnumerator AbortTrialAfterLoadFailure(ExperimentTrial trial, string bundleFileName, string reason)
    {
        Debug.LogError($"[Experiment] 試行 {trial.trialIndex} を開始できません: {reason}（{bundleFileName}）");
        session.EndTrial(true, reason);
        session.FlushLogs();

        // ワーカースレッドが展開の途中なら終わるまで待つ（最大 30 s）。走ったまま次の試行が
        // キャッシュを消すと、書きかけのファイルと衝突する。
        float ioDeadline = Time.realtimeSinceStartup + 30f;
        while (cachedPlayer != null && cachedPlayer.IsBundleIoBusy && Time.realtimeSinceStartup < ioDeadline)
        {
            yield return null;
        }

        // 期限切れでも先へ進むしかないが、**黙って進むと次の試行の読み込み失敗の原因が追えない**
        // （キャッシュが壊れて失敗が連鎖しうる。2026-09-25 の監査 F-5）。必ず記録に残す。
        if (cachedPlayer != null && cachedPlayer.IsBundleIoBusy)
        {
            Debug.LogError("[Experiment] bundle の展開スレッドが 30 秒で終わりませんでした。次の試行でキャッシュが壊れている可能性があります");
            session.RecordOperation("bundle_io_abandoned", $"trial_index={trial.trialIndex} waited_sec=30");
            session.FlushLogs();
        }

        yield return UnloadTrialSceneRoutine();
        ShowTrialLoadFailedPanel(trial, bundleFileName, reason);
    }

    // シーンのロード自体ができなかったとき（Build Settings に TrialScene が無い等）。
    // この経路は session.BeginTrial の前なので**何も記録されておらず、試行番号も動いていない**。
    // 「やり直す / 飛ばす」を出すと番号がずれるので、理由を出して待機に戻す道だけ用意する
    // （2026-09-25 の監査 F-10。以前は理由を出さずに待機画面へ戻っていた）。
    private void ShowTrialNotStartedPanel(ExperimentTrial trial)
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        string body =
            $"{trial.DescribeForParticipant(ExperimentPlan.TrialCount)} を開始できませんでした。\n" +
            "試行シーンをロードできません（アプリの設定の問題）。\n\n" +
            "この試行はまだ記録していません。\n" +
            "実験者に知らせてください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("待機画面へ", ShowWaitingPanel),
            ExperimentPanel.ButtonSpec.Create("中止して Home へ", ReturnToHome),
        };

        panel.Show("開始できません", body, specs);
    }

    private void ShowTrialLoadFailedPanel(ExperimentTrial trial, string bundleFileName, string reason)
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        // bundle 名と生の理由はパネルに出さず（被験者が見る画面）、ログに残す。
        Debug.LogError($"[Experiment] 読み込み失敗パネル: trial={trial.trialIndex} bundle={bundleFileName} reason={reason}");

        string body =
            $"{trial.DescribeForParticipant(ExperimentPlan.TrialCount)} を開始できませんでした。\n" +
            $"理由: {DescribeLoadFailure(reason)}\n\n" +
            "この試行は中断（aborted）として記録しました。\n" +
            "実験者が次を選んでください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("同じ試行をやり直す", RetryFailedTrial),
            ExperimentPanel.ButtonSpec.Create("この試行を飛ばす", SkipFailedTrial),
            ExperimentPanel.ButtonSpec.Create("中止して Home へ", ReturnToHome),
        };

        panel.Show("読み込み失敗", body, specs);
    }

    private static string DescribeLoadFailure(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return "不明";
        }
        if (reason == "load_timeout")
        {
            // パネルは 1 行 30 字ほどで折り返すので短く。詳細は Debug.Log の [Bundle] / [Video] にある。
            return "時間内に再生が始まらなかった";
        }
        if (reason == "cancelled_by_button")
        {
            return "実験者が「中止」を押した";
        }
        if (reason.StartsWith("load_failed:", System.StringComparison.Ordinal))
        {
            return reason.Substring("load_failed:".Length);
        }
        return reason;
    }

    // 失敗した試行を同じ index からやり直す。trials.csv には失敗した行（aborted=1）が残り、次の行が同じ trial_index で出る。
    private void RetryFailedTrial()
    {
        // 二重押しで試行番号が 2 つ戻り、完了済みの前の試行までやり直しになる経路があった
        // （2026-09-25 の監査 F-3）。RetryCurrentTrial の戻り値も見る。
        if (phase != Phase.Waiting || session == null || waitingActionInProgress)
        {
            return;
        }

        waitingActionInProgress = true;
        // **記録は巻き戻す前に。**後だと trial_index 列が 1 つ手前を指し、試行 0 では −1 になって
        // チュートリアル行と混ざる（同監査 M-3）。
        session.RecordOperation("trial_retry", $"trial_index={session.CurrentTrialIndex}");
        if (session.RetryCurrentTrial())
        {
            ShowWaitingPanel();
        }
        else
        {
            Debug.LogWarning("[Experiment] やり直せる試行がありません（進行中か、最初の試行より前）");
            ShowWaitingPanel();
        }
        waitingActionInProgress = false;
    }

    // 失敗した試行を飛ばして次へ。EndTrial 済みなので NextTrial は既に次の試行を指している。
    private void SkipFailedTrial()
    {
        if (phase != Phase.Waiting || session == null || waitingActionInProgress)
        {
            return;
        }

        waitingActionInProgress = true;
        session.RecordOperation("trial_skipped_after_failure", $"trial_index={session.CurrentTrialIndex}");
        session.FlushLogs();
        ShowWaitingPanel();
        waitingActionInProgress = false;
    }

    // 再生が始まった時点の状態一式を記録する（bundle の SHA-256、ビルド、条件どおりの Motion 等）。
    private void RecordTrialStartState()
    {
        if (session == null || cachedPlayer == null)
        {
            return;
        }

        float displayHz = 0f;
        try
        {
            displayHz = (float)Screen.currentResolution.refreshRateRatio.value;
        }
        catch
        {
            displayHz = 0f;
        }

        session.SetTrialStartState(
            cachedPlayer.LoadedBundleSha256,
            cachedPlayer.LoadedBundleSizeBytes,
            cachedPlayer.LoadedBundleSource,
            ExperimentBuildInfo.Resolve(),
            cachedPlayer.InteractiveMotionEnabled,
            cachedPlayer.IsExperimentMonocular,
            cachedPlayer.ScreenDistanceMeters,
            cachedPlayer.HumanBoneLengthCorrectionEnabled,
            displayHz);
    }

    private void ShowTrialPanel(ExperimentTrial trial)
    {
        phase = Phase.Trial;
        nextHeadPoseSampleTime = Time.realtimeSinceStartup;
        nextLogFlushTime = Time.realtimeSinceStartup + LogFlushIntervalSeconds;
        perf.Reset();

        // 映像を隠さないよう小さく、画面の外側（既定は上）に置く。画面が取れないときの逃げは頭基準で上 0.6 m。
        // 見出し 1 行 + ボタン 1 つの縦の短いレイアウト（本文は空）。
        PreparePanel(trialPanelSizeMeters, new Vector2(0f, 0.6f), ExperimentPanel.TrialBarLayout);

        // 最短時間が過ぎるまでボタンは押せない（残り時間は出さない）。パネル自体は最初から出す。
        float minimumSeconds = ResolveTrialMinimumViewingSeconds();
        trialEndAllowedAtRealtime = Time.realtimeSinceStartup + minimumSeconds;
        trialEndEnabled = false;
        bool allowedNow = minimumSeconds <= 0f;
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(ResolveTrialEndButtonLabel(), RequestTrialEnd, allowedNow),
        };

        string title = trial.DescribeForParticipant(ExperimentPlan.TrialCount);
        if (TryResolvePanelAnchorOutsideScreen(trialPanelSide, trialPanelSizeMeters, trialPanelGapMeters, Vector2.zero, out Vector3 anchor))
        {
            panel.ShowAnchored(title, string.Empty, specs, anchor);
        }
        else
        {
            Debug.LogWarning("[Experiment] 画面の位置が取れないので、試行中のパネルを頭の向き基準で置きます");
            panel.Show(title, string.Empty, specs, false);
        }
    }

    private bool IsTrialEndAllowed()
    {
        return Time.realtimeSinceStartup >= trialEndAllowedAtRealtime;
    }

    // テスト用の P00 だけは待たずにすぐ次の動画へ移れる（2026-09-11 指示）。本番は trialMinimumViewingSeconds。
    private float ResolveTrialMinimumViewingSeconds()
    {
        if (participantNumber == TestParticipantNumber)
        {
            return 0f;
        }

        return Mathf.Max(0f, trialMinimumViewingSeconds);
    }

    // 文言は常に「視聴を終了」。残り時間は出さない（2026-09-11 指示。「しばらく見るまで押せない」ことは
    // 画面には出さず実験者が口頭で伝える）。押せない間はボタンが灰色（interactable = false）になるだけ。
    private string ResolveTrialEndButtonLabel()
    {
        return TrialEndButtonLabel;
    }

    // 最短時間が来たら押せるようにする（1 回だけ）。
    private void UpdateTrialEndButtonIfDue()
    {
        if (trialEndEnabled || !IsTrialEndAllowed())
        {
            return;
        }

        trialEndEnabled = true;
        panel.SetButtonState(0, TrialEndButtonLabel, true);
        // **押せるようになった時点で動画が実際に何秒進んでいたかを残す。**70 秒は実時間で数える判断
        // （2026-09-25 ユーザー指示）なので、一時停止・モーションの自動停止・ヘッドセット脱着の分だけ
        // 実際の視聴は短い。解析でその差を見られるようにする。
        ExperimentLog.Operation(
            "trial_end_enabled",
            session != null
                ? $"video_played_sec={ExperimentCsv.Format(session.TrialVideoPlayedSeconds)}"
                : null);
    }

    private void RequestTrialEnd()
    {
        if (phase != Phase.Trial || !IsTrialEndAllowed())
        {
            return;
        }

        ExperimentLog.Operation("trial_end_pressed");
        trialEndRequested = true;
    }

    private IEnumerator EndTrialRoutine(bool aborted, string abortReason)
    {
        // **アンロード中に phase を Trial のままにしない。**シーンの破棄と Resources.UnloadUnusedAssets は
        // 実機で 1 秒以上かかり、その間「視聴を終了」のボタンが画面に残って押せてしまう。押しても
        // 何も起きないので被験者が連打する（ログにも残らない。2026-09-25 の監査 F-4）。
        phase = Phase.Loading;
        session.EndTrial(aborted, abortReason);
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        panel.Show("お待ちください", "次の画面を準備しています。", null);

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
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"全 {ExperimentPlan.TrialCount} 試行が終了しました。\n\n" +
            "最後のアンケートを回収してください。";

        // 次の参加者に移るための戻り道。以前はボタンが無く、アプリの再起動が要った。
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("Home へ戻る", ReturnToHome),
        };

        panel.Show("セッション終了", body, specs);
        Debug.Log($"[Experiment] セッション終了: {session.ParticipantId} / ログ: {session.LogDirectory}");
        // 以降ログは書かないので、ここでファイルを閉じる。
        session.Dispose();
    }

    // ── 操作チュートリアル ──────────────────────────────────────────────

    // 被験者に見せる条件名。内部の enum 名（Monocular / StereoOnly / ModelReplaced）は出さない。
    private static string DescribeDisplayModeForParticipant(ExperimentDisplayMode mode)
    {
        switch (mode)
        {
            case ExperimentDisplayMode.Monocular:
                return "平面の動画";
            case ExperimentDisplayMode.StereoOnly:
                return "立体の動画";
            default:
                return "立体の動画 + 3D モデル";
        }
    }

    private bool IsTutorialDoneForBlock(int blockIndex)
    {
        return blockIndex >= 0 && blockIndex < tutorialDoneForBlock.Length && tutorialDoneForBlock[blockIndex];
    }

    private void MarkTutorialDoneForBlock(int blockIndex)
    {
        if (blockIndex >= 0 && blockIndex < tutorialDoneForBlock.Length)
        {
            tutorialDoneForBlock[blockIndex] = true;
        }
    }

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
                return next.blockIndex == 0 && !IsTutorialDoneForBlock(0);
            case ExperimentTutorialTiming.BeforeEachBlock:
                return !IsTutorialDoneForBlock(next.blockIndex);
            default:
                return false;
        }
    }

    private void ShowTutorialWaitingPanel()
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("チュートリアルを開始", BeginTutorial),
            ExperimentPanel.ButtonSpec.Create("スキップ", SkipTutorialFromWaiting),
            ExperimentPanel.ButtonSpec.Create("中止して Home へ", ReturnToHome),
        };

        ExperimentDisplayMode mode = ResolveNextTutorialMode();
        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"次: 操作のチュートリアル（{DescribeTutorialPosition()}）\n" +
            $"内容: {DescribeTutorialContent(mode)}\n\n" +
            "実験者の合図があったら\n「チュートリアルを開始」を押してください。";

        panel.Show("チュートリアル", body, specs);
    }

    // 次のブロックの表示条件。チュートリアルはこの条件で再生し、内容もこれで決まる。
    private ExperimentDisplayMode ResolveNextTutorialMode()
    {
        return session != null && session.HasNextTrial
            ? session.NextTrial.mode
            : ExperimentDisplayMode.Monocular;
    }

    private static string DescribeTutorialContent(ExperimentDisplayMode mode)
    {
        switch (mode)
        {
            case ExperimentDisplayMode.Monocular:
                return "ボタンを押す / A ボタンで一時停止・再開 / シークバー / 「視聴を終了」の場所";
            case ExperimentDisplayMode.StereoOnly:
                return "立体で見えることの説明のみ（操作は同じ）";
            default:
                // 2026-09-25 に 1 段階から 3 段階に増えたので文面も合わせる（監査で指摘）。
                return "モデルが自分から動く例 / Model でモデルを替える / Settings で動きの切り替え";
        }
    }

    private string DescribeTutorialPosition()
    {
        if (session == null || !session.HasNextTrial)
        {
            return string.Empty;
        }

        ExperimentTrial next = session.NextTrial;
        // 被験者も見る画面なので内部の enum 名は出さない（2026-09-25 の監査）。
        return next.blockIndex == 0
            ? "最初の試行の前"
            : $"{next.blockIndex + 1} 組目（{DescribeDisplayModeForParticipant(next.mode)}）の前";
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
        // **phase だけでは二重押しを止められない。**最後に ShowWaitingPanel() で Waiting に戻るので、
        // 同じフレームに 2 発目のクリックが来ると（VR のレイは 1 フレームに複数回飛ぶことがある）
        // もう一度ここを通り、ブロックごとの実施済みが 2 つ進んで次のブロックの練習が消えていた
        // （2026-09-25 の監査 F-2）。押した瞬間にボタンを作り直すまでの間を bool で塞ぐ。
        if (phase != Phase.Waiting || session == null || waitingActionInProgress)
        {
            return;
        }

        waitingActionInProgress = true;
        int beforeBlock = session.HasNextTrial ? session.NextTrial.blockIndex : -1;
        session.RecordOperation("tutorial_skipped", $"before_block={beforeBlock}");
        session.FlushLogs();
        MarkTutorialDoneForBlock(beforeBlock);
        ShowWaitingPanel();
        waitingActionInProgress = false;
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
        tutorialBlockInProgress = beforeBlock;
        ExperimentDisplayMode tutorialMode = ResolveNextTutorialMode();
        // 被験者が見る画面なので、内部の条件名と bundle のファイル名は出さない（2026-09-25 の監査）。
        panel.Show(
            "読み込み中",
            $"練習の準備をしています（{DescribeDisplayModeForParticipant(tutorialMode)}）。\n\nそのままお待ちください。",
            null);

        // チュートリアルは**次のブロックと同じ表示条件**で再生する（単眼なら単眼、置換ありなら置換あり）。
        // 置換ありのときは category の絞り込みと犬の既定モデルを付け、保存済みのモデル選択は
        // 読まずに毎回同じ見た目で始める。単眼・ステレオでは除去前動画を使う（bundle に要る）。
        ExperimentTrialHandoff.SetPending(
            new ExperimentTrialRequest(
                bundleFileName,
                tutorialMode,
                -1,
                ExperimentVideo.Tutorial,
                string.IsNullOrWhiteSpace(tutorialOnlyCategory) ? null : tutorialOnlyCategory.Trim(),
                string.IsNullOrWhiteSpace(tutorialAnimalModelName) ? null : tutorialAnimalModelName.Trim(),
                true));

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

        session?.BeginTutorial(bundleFileName, beforeBlock, tutorialMode);
        tutorial = new ExperimentTutorial(session, tutorialMode);
        tutorial.Changed += MarkTutorialPanelDirty;
        // プレイヤーの操作ログを横取りして段階を進める。セッションへはそのまま転送される。
        ExperimentLog.Sink = tutorial;

        // 再生が始まるまで待つ。試行と違い、bundle が無ければ諦めて先へ進める。
        // プレイヤー側が失敗を報告したら期限を待たない（2026-09-25）。
        float deadline = Time.realtimeSinceStartup + tutorialLoadTimeoutSeconds;
        while (cachedPlayer != null && !cachedPlayer.IsVideoPlaying && !cachedPlayer.BundleLoadFailed &&
               Time.realtimeSinceStartup < deadline)
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
        // 字を大きく縦を詰めたレイアウト。OffsetMeters は画面が取れないときの逃げ（頭の向き基準で上）。
        PreparePanel(tutorialPanelSizeMeters, new Vector2(0f, 0.6f), ExperimentPanel.CompactLargeTextLayout);

        List<ExperimentPanel.ButtonSpec> specs = BuildTutorialPanelButtons();

        if (keepPlacement)
        {
            panel.Show(tutorial.Title, tutorial.Body, specs, true);
            return;
        }

        if (TryResolvePanelAnchorOutsideScreen(tutorialPanelSide, tutorialPanelSizeMeters, tutorialPanelGapMeters, tutorialPanelNudgeMeters, out Vector3 anchor))
        {
            panel.ShowAnchored(tutorial.Title, tutorial.Body, specs, anchor);
        }
        else
        {
            Debug.LogWarning("[Experiment] 画面の位置が取れないので、チュートリアルのパネルを頭の向き基準で置きます");
            panel.Show(tutorial.Title, tutorial.Body, specs, false);
        }
    }

    // 画面の外側にパネルを置くための基準点。画面の中心から、画面の半分 + 隙間 + パネルの半分だけ
    // 指定した側へ出した点（画面の距離での m）。パネル自体は頭からその点への視線上 panelDistanceMeters に
    // 置かれるので、角度上の位置がこの点と一致する。パネルの寸法は UI 距離での値なので、
    // 画面の距離へ換算してから足す。チュートリアルの説明と試行中の「視聴を終了」で共用。
    private bool TryResolvePanelAnchorOutsideScreen(
        ExperimentPanelSide side,
        Vector2 panelSizeMeters,
        float gapMeters,
        Vector2 nudgeMeters,
        out Vector3 anchor)
    {
        anchor = Vector3.zero;
        if (cachedPlayer == null ||
            !cachedPlayer.TryGetScreenFrame(out Vector3 center, out Vector3 up, out Vector3 right, out Vector2 screenSize))
        {
            return false;
        }

        Transform head = ResolveHeadTransform();
        float uiDistance = Mathf.Max(0.2f, panel.DistanceMeters);
        float screenDistance = head != null ? Vector3.Distance(head.position, center) : uiDistance;
        float k = screenDistance / uiDistance;

        Vector2 halfPanel = panelSizeMeters * 0.5f * k;
        float gap = gapMeters * k;
        Vector2 nudge = nudgeMeters * k;

        anchor = center;
        switch (side)
        {
            case ExperimentPanelSide.LeftOfScreen:
                anchor -= right * (screenSize.x * 0.5f + gap + halfPanel.x);
                break;
            case ExperimentPanelSide.RightOfScreen:
                anchor += right * (screenSize.x * 0.5f + gap + halfPanel.x);
                break;
            default:
                // 上。画面の上端 + 隙間 + パネルの半分。
                anchor += up * (screenSize.y * 0.5f + gap + halfPanel.y);
                break;
        }

        anchor += right * nudge.x + up * nudge.y;
        Debug.Log(
            $"[Experiment] panel side={side} size={panelSizeMeters.x:F2}x{panelSizeMeters.y:F2}m " +
            $"screen={screenSize.x:F2}x{screenSize.y:F2}m @{screenDistance:F2}m k={k:F2} anchor={anchor:F3}");
        return true;
    }

    private List<ExperimentPanel.ButtonSpec> BuildTutorialPanelButtons()
    {
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>();
        switch (tutorial.CurrentStep)
        {
            case ExperimentTutorial.Step.PressButton:
                // 押せたこと自体が 1 つ目の課題。
                specs.Add(ExperimentPanel.ButtonSpec.Create("次へ", tutorial.CompleteCurrentStep));
                break;
            case ExperimentTutorial.Step.Done:
                // 試行中に画面の上に出るボタンと同じ文言・同じ場所。ここで「次の動画に移る操作」を覚える。
                specs.Add(ExperimentPanel.ButtonSpec.Create("視聴を終了", RequestTutorialEnd));
                break;
            default:
                // A ボタンの一時停止・再開と Model の切り替えは、実際に操作しないと進まない
                // （2026-09-11 指示。段階ごとの「スキップ」は置かない）。
                break;
        }

        // **どの段階でも押せる脱出口。** 以前は途中の段階でボタンが 0 個になり、操作が成立しない状況
        // （つまみが掴めない・モデルを全部消した・コントローラの電池切れ）で永久に進めなかった。
        // 復帰手段はアプリの強制終了だけで、その参加者のデータはそこで切れていた（2026-09-25 の監査）。
        // 実験者用と明記して出す。押されたことは tutorial_end の detail に completed=0 と段階名で残る。
        if (tutorial != null && !tutorial.IsDone)
        {
            specs.Add(ExperimentPanel.ButtonSpec.Create("実験者用: 練習を終える", RequestTutorialEnd));
        }

        return specs;
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

        MarkTutorialDoneForBlock(tutorialBlockInProgress);
        tutorialBlockInProgress = -1;
        ShowWaitingPanel();
    }

    private void ShowTutorialLoadFailedPanel(string bundleFileName)
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        string body =
            "チュートリアルの bundle を再生できませんでした。\n" +
            $"{bundleFileName}\n\n" +
            "共有ストレージか StreamingAssets に置いてあるか、video.mp4 が H.264 かを\n" +
            "確認してください（詳細はログの [Bundle] / [Video]）。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("チュートリアル無しで続行", OnTutorialFinished),
            ExperimentPanel.ButtonSpec.Create("中止して Home へ", ReturnToHome),
        };

        panel.Show("チュートリアル読み込み失敗", body, specs);
    }

    // セットアップ・待機・終了のどの画面からも戻れる。セッション中なら中断として閉じる
    // （進行中の試行があれば aborted=1 で trials.csv に残る。OnDestroy でも同じ処理が走るが、
    // シーンを抜ける前に明示しておく）。
    private void ReturnToHome()
    {
        // 二重押し止め。**同期 LoadScene は使わない。**押した瞬間にフレームが止まり、画面が固まった
        // まま数秒待たされる（HomeMenu が 2026-08-31 の実機指摘で非同期にしたのと同じ理由。
        // ここだけ同期のままだった。2026-09-25 の監査 F-9）。
        if (returningToHome)
        {
            return;
        }

        returningToHome = true;
        FinishSessionIfRunning(true, "session_abort_by_experimenter");
        ExperimentSessionOverrides.EndSession();
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.Clear();
        Debug.Log("[Experiment] return to HomeScene");

        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        panel.Show("お待ちください", "入口の画面に戻ります。", null);
        SceneManager.LoadSceneAsync(homeSceneName, LoadSceneMode.Single);
    }

    private bool returningToHome;

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

    // ── perf ログ（描画レート・動画の進み・コントローラの動き、1 秒窓）──────

    private void SamplePerfIfDue()
    {
        if (!logPerf || session == null || !session.TrialInProgress || cachedPlayer == null)
        {
            return;
        }

        bool hasPointer = RuntimeXrRayPickReader.TryReadPointerPose(
            xrDevices, out Vector3 pointerPosition, out Quaternion _, out bool triggerPressed);
        bool hasButton = RuntimePauseInputReader.TryReadPrimaryButtonPressed(xrDevices, out bool buttonPressed);

        if (perf.Push(
                Time.unscaledDeltaTime,
                cachedPlayer.CurrentVideoFrame,
                cachedPlayer.CurrentVideoTimeSeconds,
                cachedPlayer.IsVideoPlaying,
                hasPointer,
                pointerPosition,
                hasPointer && triggerPressed,
                hasButton && buttonPressed,
                out ExperimentPerfAccumulator.Sample sample))
        {
            session.RecordPerf(sample, cachedPlayer.CurrentVideoFrame);
        }
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
