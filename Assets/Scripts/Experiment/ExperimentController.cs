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

    // 試行中の「視聴を終了」を押せるようになる時刻と、押せるようにしたかどうか。
    private float trialEndAllowedAtRealtime;
    private bool trialEndEnabled;
    private const string TrialEndButtonLabel = "視聴を終了";
    // 済ませた（飛ばしたものも含む）チュートリアルの数。BeforeEachBlock の判定に使う。
    private int tutorialsCompleted;

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
        ShowSetupPanel();
    }

    private void OnDestroy()
    {
        FinishSessionIfRunning(true);
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
        Transform head = ResolveHeadTransform();
        panel?.UpdatePlacement(head, ExperimentPanel.ResolveFrontForward(head));

        if (phase == Phase.Trial)
        {
            SampleHeadPoseIfDue();
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
        tutorialsCompleted = 0;

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
            $"次の試行: {next.Describe(ExperimentPlan.TrialCount)}\n\n" +
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

        string title = trial.Describe(ExperimentPlan.TrialCount);
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
        ExperimentLog.Operation("trial_end_enabled");
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
                return "Model ボタンでモデルを替える";
        }
    }

    private string DescribeTutorialPosition()
    {
        if (session == null || !session.HasNextTrial)
        {
            return string.Empty;
        }

        ExperimentTrial next = session.NextTrial;
        return next.blockIndex == 0
            ? "最初の試行の前"
            : $"ブロック {next.blockIndex + 1}（{next.mode}）の前";
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
        ExperimentDisplayMode tutorialMode = ResolveNextTutorialMode();
        panel.Show(
            "読み込み中",
            $"チュートリアル（{tutorialMode}）\n{bundleFileName}\n\nそのままお待ちください。",
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
                // 実験者が飛ばしたいときは待機画面の「スキップ」でチュートリアル全体を飛ばす。
                break;
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

        tutorialsCompleted++;
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
        FinishSessionIfRunning(true);
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
