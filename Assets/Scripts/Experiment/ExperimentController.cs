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
//     次のブロックと同じ表示条件で再生し、ExperimentTutorial が段階を進める
//   - tutorialTiming で「最初の試行の前に 1 回」「各ブロックの前に 1 回ずつ（既定）」を選ぶ
//   - 被験者実験の中でだけ走る。Home からの単独起動は 2026-09-11 に外した
//
// 動画の画面が動いたとき（Reset View の再センタリング、Screen Dist）は、画面基準で置いたパネルを
// 置き直す（ReanchorPanelIfScreenMoved）。置き直す手段が無いと、再センタリングで画面だけが動いて
// パネルが画面の裏に取り残され、「視聴を終了」が押せなくなる（2026-09-29 の監査）。
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
    // セットアップ・待機・終了・失敗パネルの「Home へ戻る」「中止して Home へ」の戻り先。
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
    // 0 にすると読み込み中パネルの寸法計算（wanted / panelDistanceMeters）が NaN になる。
    [Min(0.2f)] public float panelDistanceMeters = 1.2f;
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
    // 実験者が試行中に「実験者用: 中止」を 2 度押しした。70 秒の待ちに関係なく効く。
    // 以前は試行中に押せるボタンが「視聴を終了」（70 秒は押せない）だけで、被験者が体調を崩しても
    // ヘッドセットを外すかアプリを落とすしかなく、その試行は trials.csv に行が残らなかった
    // （2026-09-29 の 4 回目の監査）。
    private bool trialAbortRequested;
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
    // 以前は 1 本のカウンタで `tutorialsCompleted <= blockIndex` と判定していたので、押下が 1 つ余計に
    // 数えられると**別のブロックの練習が丸ごと消えた**（2026-09-25 の監査）。ブロックごとに持てば冪等。
    // 二重押しそのものは ExperimentPanel が塞ぐ（旧ボタンのリスナー解除 + 非アクティブ化、作り直し直後の押下抑止）。
    private readonly bool[] tutorialDoneForBlock = new bool[ExperimentPlan.BlockCount];
    // 実行中の練習がどのブロックの前のものか（-1 = 実行中でない）。終わったときにその枠を立てる。
    private int tutorialBlockInProgress = -1;
    // セッション開始時に決めた最短視聴時間（秒）。P00 は 0。参加者番号は試行中に変わらないが、
    // 判定のたびに生のフィールドを読むより、セッションと一緒に固定するほうが安全。
    private float sessionMinimumViewingSeconds;

    // 画面基準で置いたパネルの基準（画面の中心と右方向）。画面が動いたら置き直す（ReanchorPanelIfScreenMoved）。
    private enum AnchoredPanelKind { None, Loading, Trial, Tutorial }
    private AnchoredPanelKind anchoredPanelKind = AnchoredPanelKind.None;
    private Vector3 anchoredScreenCenter;
    private Vector3 anchoredScreenRight;
    private const float ScreenMoveReanchorMeters = 0.03f;
    private const float ScreenMoveReanchorDegrees = 1f;

    // 読み込みの期限（実時間）。ヘッドセットを外していた時間は数えない（OnApplicationPause で延ばす）。
    private float loadDeadlineRealtime;
    private float appPausedAtRealtime = -1f;
    // 休止から戻った最初のフレームの dt は停止時間を含みうるので perf の窓に入れない（SamplePerfIfDue が 1 回読み飛ばす）。
    private bool skipNextPerfPush;

    // 次の参加者の番号。Home へ戻るとこのシーンごと捨てられるので static に覚える。以前は戻るたびに
    // シーンの値（1）に戻り、実験者が毎回 ＋ を押し直していた。セッションを完走したら +1、途中で戻ったら同じ番号
    // （同じ参加者でやり直すことが多い）。テスト用の P00 を完走したら P01（2026-09-29 の 3 回目の監査）。
    private static int rememberedParticipantNumber = -1;
    private bool sessionCompleted;

    // 実験者用のボタンで途中終了した練習のブロック（-1 = 無い）。待機画面で「やり直すか飛ばすか」を出す。
    private int tutorialEscapedForBlock = -1;

    // panel_reanchored の記録は動きが止まってから 1 行（Screen Dist のスライダー中は 3 cm ごとに置き直すので、
    // そのたびに書くと 1 秒に 30 行になる。2026-09-29 の 3 回目の監査）。置き直し自体は即時。
    private bool reanchorLogPending;
    private float reanchorLogChangedAt;
    private int reanchorLogCount;
    private float reanchorLogMovedTotal;
    private float reanchorLogTurnedMax;
    private AnchoredPanelKind reanchorLogKind;
    private const float ReanchorLogSettleSeconds = 0.5f;

    private static readonly Vector2 FullPanelSizeMeters = new Vector2(1.05f, 0.82f);
    private const float LogFlushIntervalSeconds = 10f;
    // 読み込み中パネルを画面より手前に置くときの余裕。プレイヤーの Model パネルと同じ 0.10 m。
    private const float LoadingPanelInFrontOfScreenMarginMeters = 0.10f;
    // 読み込み中パネルをどこまで近づけるか。
    private const float LoadingPanelMinDistanceMeters = 0.35f;
    // 展開のワーカースレッドを待つ上限（秒）。
    private const float BundleIoWaitSeconds = 30f;

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
        if (rememberedParticipantNumber >= 0)
        {
            participantNumber = rememberedParticipantNumber;
        }
        ShowSetupPanel();
    }

    private void OnDestroy()
    {
        FinishSessionIfRunning(true, "controller_destroyed");
        // セッションが無いまま抜けるとき（セットアップ画面で終了・終了画面のまま終了）も閉じる。
        // 閉じないと、同じプロセスで自由視聴に入ったときにモデルの調整が基準ファイルへ保存されない
        // （2026-09-29 の 4 回目の監査）。
        ExperimentSessionOverrides.EndSession();
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
        if (paused)
        {
            appPausedAtRealtime = Time.realtimeSinceStartup;
            // 書きかけの perf の窓（1 秒未満）は捨てずに書き切る。休止から戻った最初のフレームの dt は
            // 停止時間を含みうるので、その 1 回だけ Push を読み飛ばす（skipNextPerfPush）。
            // 以前は戻ったときに perf.Reset() していたが、Reset は Push より前なので復帰フレームの dt は
            // 除けておらず、休止前の窓だけが消えていた（2026-09-29 の 3 回目の監査）。
            FlushPerfWindow();
        }
        else if (appPausedAtRealtime >= 0f)
        {
            // 読み込みの期限は装着していない時間ぶん延ばす。展開スレッドは休止中も進むが Prepare と再生は
            // 主スレッド再開後なので、延ばさないと戻した瞬間に load_timeout で試行が捨てられる（2026-09-29 の監査）。
            float pausedFor = Mathf.Max(0f, Time.realtimeSinceStartup - appPausedAtRealtime);
            loadDeadlineRealtime += pausedFor;
            appPausedAtRealtime = -1f;
            // 試行中に外されたときだけ。試行外で立てると、次の試行の最初のフレームを 1 つ捨てる。
            skipNextPerfPush = phase == Phase.Trial;
        }

        if (session == null)
        {
            return;
        }

        if (paused)
        {
            // video_played_sec は試行中だけ意味がある（試行外は前の試行の値が残る）。
            string detail = phase == Phase.Trial
                ? $"phase={phase} video_played_sec={ExperimentCsv.Format(session.TrialVideoPlayedSeconds)}"
                : $"phase={phase}";
            session.RecordOperation("app_pause", detail);
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

        // 走っている Random モーションを止めて video_pause_begin を end と対にする（試行の正常終了と同じ扱い）。
        if (cachedPlayer != null)
        {
            cachedPlayer.StopAllInteractiveMotionForExperimentEnd();
        }

        if (session.TrialInProgress)
        {
            session.EndTrial(aborted, abortReason);
        }

        if (session.TutorialInProgress)
        {
            session.EndTutorial("aborted");
        }

        // **セッションの終わり方を 1 行残す。**以前は待機画面から「中止して Home へ」を押すと
        // trials.csv にも operations.csv にも何も出ず、「9 本完走した」「実験者が途中で止めた」
        // 「アプリが落ちた」を区別できなかった（2026-09-29 の 4 回目の監査）。
        session.RecordOperation("session_end", $"reason={abortReason} trials_done={session.CurrentTrialIndex + 1}");
        session.FlushLogs();

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
        ReanchorPanelIfScreenMoved();
        FlushPendingReanchorLog();
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
        if (wanted >= panelDistanceMeters)
        {
            return;
        }

        // 頭が画面のすぐそばにあっても、諦めて 1.2 m（画面の裏）に置くよりは近くに置く。
        wanted = Mathf.Max(LoadingPanelMinDistanceMeters, wanted);
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
        // この画面は実験者が見る（群・動画順・条件の並びを確認する）ので内部名のままでよい。
        return
            participantLine +
            $"{ExperimentPlan.DescribeAssignment(group, videoOrderPattern)}\n" +
            assignmentNote +
            $"\n動画 {ExperimentPlan.TrialCount} 本 / 練習: {DescribeTutorialTiming()}\n" +
            "参加者 ID を確認してから開始してください。";
    }

    private string DescribeTutorialTiming()
    {
        switch (tutorialTiming)
        {
            case ExperimentTutorialTiming.BeforeFirstTrial:
                return "最初の動画の前に 1 回";
            case ExperimentTutorialTiming.BeforeEachBlock:
                return $"3 本ごとの前に 1 回（計 {ExperimentPlan.BlockCount} 回）";
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
        ExperimentLogWriter writer;
        try
        {
            writer = new ExperimentLogWriter(sessionDir);
        }
        catch (System.Exception ex)
        {
            // 保存先が作れない（ストレージ満杯・権限）。以前は例外でハンドラが落ちるだけで画面が変わらず、
            // 実験者には「押しても反応しない」ように見えた（2026-09-29 の 4 回目の監査）。
            Debug.LogError($"[Experiment] ログの保存先を作れません: {sessionDir} | {ex.Message}");
            ShowLogDirectoryFailedPanel();
            return;
        }

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
        tutorialEscapedForBlock = -1;
        sessionCompleted = false;
        sessionMinimumViewingSeconds = ResolveTrialMinimumViewingSeconds();

        Debug.Log($"[Experiment] セッション開始: {ParticipantId} / 群 {group} / 動画順 {videoOrderPattern} / チュートリアル {tutorialTiming}");
        Debug.Log($"[Experiment] ログ出力先: {sessionDir}");

        // **セッション単位の情報を 1 行残す。**以前は群・動画順・ビルドが trials.csv の行にしか無く、
        // 全試行が読み込み失敗したセッションではどのビルドで走ったのかも追えなかった（2026-09-29 の 4 回目の監査）。
        session.RecordOperation(
            "session_begin",
            $"group={group} pattern={videoOrderPattern} trials={ExperimentPlan.TrialCount} " +
            $"app_build={ExperimentBuildInfo.Resolve()} tutorial={tutorialTiming} " +
            $"min_viewing_sec={ExperimentCsv.Format(sessionMinimumViewingSeconds)}");
        session.FlushLogs();

        ShowWaitingPanel();
    }

    // ログの保存先を作れなかった。セッションは始まっていないので、押せる道を残して実験者に知らせる。
    private void ShowLogDirectoryFailedPanel()
    {
        phase = Phase.Setup;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("セットアップへ戻る", ShowSetupPanel),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        panel.Show(
            "記録を保存できません",
            "記録の保存先を作れませんでした。\n" +
            "端末の空き容量を確認してください。\n\n" +
            "このままでは記録が残らないので\n" +
            "実験者に知らせてください。",
            specs);
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

        // 被験者向けの語は「動画」（「試行」「ブロック」は出さない。2026-09-29 の 3 回目の監査）。
        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"次: {next.DescribeForParticipant(ExperimentPlan.TrialCount)}\n\n" +
            // ログの保存先はここに出さない（被験者が知る必要はない。2026-09-11 指示）。Debug.Log には残る。
            (session.CurrentTrialIndex >= 0
                ? "アンケートの記入が終わったら、実験者の合図で\n「この動画を開始」を押してください。"
                : "実験者の合図があったら\n「この動画を開始」を押してください。");

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("この動画を開始", BeginNextTrial),
            // 設定を間違えたまま始めたとき用。ここまでのログは残して閉じる。2 度押しで実行。
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
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
        trialAbortRequested = false;
        loadCancelRequested = false;

        // このコルーチンは待機画面のボタンのクリックハンドラから始まる。パネルの
        // 作り直しはそのボタン自身の破棄を伴うので、ハンドラを抜けてから行う。
        yield return null;

        string bundleFileName = bundleCatalog.Resolve(trial.video);
        string loadingTitle = trial.DescribeForParticipant(ExperimentPlan.TrialCount);
        Debug.Log($"[Experiment] 読み込み: {trial.Describe(ExperimentPlan.TrialCount)} / {bundleFileName}");
        ShowLoadingPanel(loadingTitle);

        LoadOutcome outcome = new LoadOutcome();
        yield return LoadTrialSceneRoutine(
            new ExperimentTrialRequest(bundleFileName, trial.mode, trial.trialIndex, trial.video),
            outcome);
        if (outcome.sceneMissing)
        {
            // Build Settings に TrialScene が無い等。理由を出さずに待機画面へ戻ると、実験者が
            // 「開始」を押しても一瞬で戻るだけで原因が分からない（2026-09-25 の監査 F-10）。
            ShowTrialNotStartedPanel(trial);
            yield break;
        }

        session.BeginTrial(trial.trialIndex, bundleFileName);
        yield return WaitForPlaybackStartRoutine(trialLoadTimeoutSeconds, loadingTitle, outcome);
        if (outcome.failure != null)
        {
            yield return AbortTrialAfterLoadFailure(trial, bundleFileName, outcome.failure);
            yield break;
        }

        RecordTrialStartState();
        ShowTrialPanel(trial);

        while (!trialEndRequested && !trialAbortRequested)
        {
            yield return null;
        }

        yield return trialAbortRequested
            ? EndTrialRoutine(true, "aborted_by_experimenter")
            : EndTrialRoutine(false, null);
    }

    // シーンのロードと再生開始待ちの結果。コルーチンは値を返せないので入れ物で受ける。
    private sealed class LoadOutcome
    {
        // Build Settings に TrialScene が無い等でロード自体ができなかった。
        public bool sceneMissing;
        // null = 再生が始まった。それ以外は trials.csv の abort_reason / tutorial_end の result と同じ語彙
        // （load_timeout / load_failed:<理由> / cancelled_by_button）。
        public string failure;
    }

    // TrialScene を Additive でロードしてプレイヤーを掴む。試行とチュートリアルで共通
    // （以前は 2 か所に同じ 25 行があり、失敗の扱いがずれていた。2026-09-29 の監査）。
    private IEnumerator LoadTrialSceneRoutine(ExperimentTrialRequest request, LoadOutcome outcome)
    {
        // プレイヤーの Start() が読む。シーンをロードする前に必ず置いておくこと。
        ExperimentTrialHandoff.SetPending(request);

        AsyncOperation load = SceneManager.LoadSceneAsync(trialSceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            Debug.LogError($"[Experiment] 試行シーンをロードできません: {trialSceneName}（Build Settings に追加済みか確認）");
            ExperimentTrialHandoff.Clear();
            outcome.sceneMissing = true;
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
        if (cachedPlayer == null)
        {
            // 次に開いたシーンが前の試行の指示で再生を始めないように捨てる。
            ExperimentTrialHandoff.Clear();
        }
    }

    // bundle の展開と Prepare が終わって実際に再生が始まるまで待つ（bundle_human.svb は 165MB あり、
    // 実機では十数秒かかる）。期限・プレイヤー側の失敗報告・「中止」ボタンのどれかで抜ける（2026-09-25）。
    // 期限はヘッドセットを外していた時間ぶん延びる（OnApplicationPause）。
    private IEnumerator WaitForPlaybackStartRoutine(float timeoutSeconds, string loadingTitle, LoadOutcome outcome)
    {
        loadDeadlineRealtime = Time.realtimeSinceStartup + timeoutSeconds;
        bool loadingPanelMovedInFront = false;
        while (true)
        {
            if (cachedPlayer == null)
            {
                outcome.failure = "load_failed:player_missing";
                break;
            }

            // 動画のスクリーンは Prepare が終わった時点で作られる。パネルはそれより前に置いてあるので、
            // 画面が出てきたところで一度だけ「画面より手前」に置き直す。そうしないと「中止」が
            // 画面の裏に入って押せない（2026-09-25 の監査 F-1）。通常は再生開始と同じフレームなので
            // すぐ試行パネルに置き換わる。効くのは Prepare 後に再生が始まらない場合。
            if (!loadingPanelMovedInFront &&
                cachedPlayer.TryGetScreenFrame(out Vector3 _, out Vector3 _, out Vector3 _, out Vector2 _))
            {
                loadingPanelMovedInFront = true;
                ShowLoadingPanel(loadingTitle);
                RememberAnchoredPanel(AnchoredPanelKind.Loading);
            }
            // 「一度でも再生が始まった」で抜ける。IsVideoPlaying だけで待つと、Play の直後に被験者が
            // A を押した場合（読み込み中に押しがち）に読み込み中のまま期限切れになり、その試行が
            // aborted で捨てられた（2026-09-29 の 4 回目の監査）。
            if (cachedPlayer.IsVideoPlaying || cachedPlayer.HasPlaybackStarted)
            {
                break;
            }
            if (loadCancelRequested)
            {
                outcome.failure = "cancelled_by_button";
                break;
            }
            if (cachedPlayer.BundleLoadFailed)
            {
                outcome.failure = "load_failed:" + cachedPlayer.BundleLoadFailureMessage;
                break;
            }
            if (Time.realtimeSinceStartup >= loadDeadlineRealtime)
            {
                outcome.failure = "load_timeout";
                break;
            }
            yield return null;
        }

        ForgetAnchoredPanel();
    }

    // ワーカースレッドが展開の途中なら終わるまで待つ（最大 BundleIoWaitSeconds）。走ったまま次の試行が
    // キャッシュを消すと、書きかけのファイルと衝突する。期限切れでも先へ進むしかないが、**黙って進むと
    // 次の試行の読み込み失敗の原因が追えない**（失敗が連鎖しうる。2026-09-25 の監査 F-5）ので必ず記録に残す。
    private IEnumerator WaitForBundleIoRoutine(string context)
    {
        float ioDeadline = Time.realtimeSinceStartup + BundleIoWaitSeconds;
        while (cachedPlayer != null && cachedPlayer.IsBundleIoBusy && Time.realtimeSinceStartup < ioDeadline)
        {
            yield return null;
        }

        if (cachedPlayer != null && cachedPlayer.IsBundleIoBusy)
        {
            Debug.LogError($"[Experiment] bundle の展開スレッドが {BundleIoWaitSeconds:0} 秒で終わりませんでした。次の試行でキャッシュが壊れている可能性があります");
            session?.RecordOperation("bundle_io_abandoned", $"{context} waited_sec={BundleIoWaitSeconds:0}");
            session?.FlushLogs();
        }
    }

    // 読み込み中は「中止」だけ押せる。押されると読み込みを待つのをやめ、その試行（練習）を閉じる。
    // bundle のファイル名は被験者に出さない（2026-09-25 の監査）。
    private void ShowLoadingPanel(string title)
    {
        PrepareLoadingPanelPlacement();
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            // 実験者用。2 度押しで実行（被験者が待ちきれずに押しても 1 回では効かない）。
            ExperimentPanel.ButtonSpec.CreateConfirm("中止", RequestLoadCancel),
        };
        panel.Show(
            "読み込み中",
            $"{title}\n\n動画を読み込んでいます。\n何も押さずにお待ちください。\n（進まないときは実験者が「中止」を 2 回押す）",
            specs);
    }

    // ボタンの無い「お待ちください」。シーンのアンロードなど、押せるものが無い間に出す。
    // 画面（試行シーン）がまだある間は読み込み中パネルと同じく画面より手前に置く。1.2 m の既定だと
    // アンロードが終わるまでの 1 秒ほど、1.0 m の画面の裏に両眼視差だけ奥のパネルが描かれる（2026-09-29 の 3 回目の監査）。
    private void ShowPleaseWaitPanel(string body)
    {
        PrepareLoadingPanelPlacement();
        panel.Show("お待ちください", body, null);
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
        // 後片付け（展開待ち + アンロード）の間、押しても効かない「中止」を残さない。
        ShowPleaseWaitPanel("次の画面を準備しています。");

        yield return WaitForBundleIoRoutine($"trial_index={trial.trialIndex}");
        yield return UnloadTrialSceneRoutine();
        ShowTrialLoadFailedPanel(trial, reason);
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
            "アプリの設定に問題があり、動画の画面を開けません。\n\n" +
            "この動画はまだ記録していません。\n" +
            "実験者に知らせてください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("待機画面へ", ShowWaitingPanel),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        panel.Show("開始できません", body, specs);
    }

    private void ShowTrialLoadFailedPanel(ExperimentTrial trial, string reason)
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        // 生の理由はパネルに出さず（被験者が見る画面）、ログに残す。bundle 名は AbortTrialAfterLoadFailure が出している。
        Debug.LogError($"[Experiment] 読み込み失敗パネル: trial={trial.trialIndex} reason={reason}");

        string body =
            $"{trial.DescribeForParticipant(ExperimentPlan.TrialCount)} を開始できませんでした。\n" +
            $"理由: {DescribeLoadFailure(reason)}\n\n" +
            "この動画は「中断」として記録しました。\n" +
            "実験者が次を選んでください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("同じ動画をやり直す", RetryFailedTrial),
            ExperimentPanel.ButtonSpec.Create("この動画を飛ばす", SkipFailedTrial),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        panel.Show("読み込み失敗", body, specs);
    }

    // 失敗理由の語彙（load_timeout / cancelled_by_button / load_failed:<理由>）を被験者が見てもよい定型文にする。
    // load_failed の後ろは bundle のファイル名やフルパス、Unity のエラー文がそのまま入る（Bundle.cs / Core）ので
    // 出さない（2026-09-29 の 3 回目の監査）。生の文字列は呼び出し側が Debug.LogError に残す。
    private static string DescribeLoadFailure(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return "不明（ログを確認）";
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
            return "動画のファイルを読み込めなかった（ログを確認）";
        }
        return "不明（ログを確認）";
    }

    // 失敗した試行を同じ index からやり直す。trials.csv には失敗した行（aborted=1）が残り、次の行が同じ trial_index で出る。
    private void RetryFailedTrial()
    {
        if (phase != Phase.Waiting || session == null)
        {
            return;
        }

        // **記録は巻き戻す前に。**後だと trial_index 列が 1 つ手前を指し、試行 0 では −1 になって
        // チュートリアル行と混ざる（2026-09-25 の監査 M-3）。待機中は周期 flush が無いので明示する。
        session.RecordOperation("trial_retry", $"trial_index={session.CurrentTrialIndex}");
        session.FlushLogs();
        // 失敗パネルに来る時点で試行は閉じているので、戻り値が false になる経路は無い（防御だけ）。
        if (!session.RetryCurrentTrial())
        {
            Debug.LogWarning("[Experiment] やり直せる試行がありません（進行中か、最初の試行より前）");
        }
        ShowWaitingPanel();
    }

    // 失敗した試行を飛ばして次へ。EndTrial 済みなので NextTrial は既に次の試行を指している。
    private void SkipFailedTrial()
    {
        if (phase != Phase.Waiting || session == null)
        {
            return;
        }

        session.RecordOperation("trial_skipped_after_failure", $"trial_index={session.CurrentTrialIndex}");
        session.FlushLogs();
        ShowWaitingPanel();
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
        float minimumSeconds = sessionMinimumViewingSeconds;
        trialEndAllowedAtRealtime = Time.realtimeSinceStartup + minimumSeconds;
        trialEndEnabled = false;
        bool allowedNow = minimumSeconds <= 0f;
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(TrialEndButtonLabel, RequestTrialEnd, allowedNow),
            // 実験者用。2 度押しで効く（1 度目は色が変わるだけ）。最短視聴時間の待ちには縛られない。
            ExperimentPanel.ButtonSpec.CreateConfirm("実験者用: 中止", RequestTrialAbortByExperimenter),
        };

        string title = trial.DescribeForParticipant(ExperimentPlan.TrialCount);
        if (TryResolvePanelAnchorOutsideScreen(trialPanelSide, trialPanelSizeMeters, trialPanelGapMeters, Vector2.zero, out Vector3 anchor))
        {
            panel.ShowAnchored(title, string.Empty, specs, anchor);
        }
        else
        {
            Debug.LogWarning("[Experiment] 画面の位置が取れないので、試行中のパネルを正面基準で置きます");
            panel.Show(title, string.Empty, specs, false);
        }
        RememberAnchoredPanel(AnchoredPanelKind.Trial);
    }

    // 画面基準で置いたパネルの基準を覚える。画面が動いたら ReanchorPanelIfScreenMoved が置き直す。
    private void RememberAnchoredPanel(AnchoredPanelKind kind)
    {
        anchoredPanelKind = kind;
        if (cachedPlayer != null &&
            cachedPlayer.TryGetScreenFrame(out Vector3 center, out _, out Vector3 right, out _))
        {
            anchoredScreenCenter = center;
            anchoredScreenRight = right;
        }
        else
        {
            anchoredPanelKind = AnchoredPanelKind.None;
        }
    }

    private void ForgetAnchoredPanel()
    {
        anchoredPanelKind = AnchoredPanelKind.None;
    }

    // 動画の画面が動いていたら（Reset View の再センタリング、Screen Dist）、画面基準のパネルを置き直す。
    // パネルは表示時に世界座標で固定されるので、画面だけ動くと相対位置がずれ、再センタリングでは
    // 画面の裏に入って押せなくなる（2026-09-29 の監査。試行を終える手段が無くなる）。
    private void ReanchorPanelIfScreenMoved()
    {
        if (anchoredPanelKind == AnchoredPanelKind.None || cachedPlayer == null || panel == null ||
            !cachedPlayer.TryGetScreenFrame(out Vector3 center, out _, out Vector3 right, out _))
        {
            return;
        }

        float moved = Vector3.Distance(center, anchoredScreenCenter);
        float turned = Vector3.Angle(right, anchoredScreenRight);
        if (moved < ScreenMoveReanchorMeters && turned < ScreenMoveReanchorDegrees)
        {
            return;
        }

        anchoredScreenCenter = center;
        anchoredScreenRight = right;

        Vector3 anchor;
        switch (anchoredPanelKind)
        {
            case AnchoredPanelKind.Trial:
                panel.Reanchor(
                    TryResolvePanelAnchorOutsideScreen(trialPanelSide, trialPanelSizeMeters, trialPanelGapMeters, Vector2.zero, out anchor, false)
                        ? anchor
                        : (Vector3?)null);
                break;
            case AnchoredPanelKind.Tutorial:
                panel.Reanchor(
                    TryResolvePanelAnchorOutsideScreen(tutorialPanelSide, tutorialPanelSizeMeters, tutorialPanelGapMeters, tutorialPanelNudgeMeters, out anchor, false)
                        ? anchor
                        : (Vector3?)null);
                break;
            default:
                // 読み込み中パネルは正面基準（画面より手前）。距離と寸法を取り直して置き直す。
                PrepareLoadingPanelPlacement();
                panel.Reanchor(null);
                break;
        }

        // 記録は動きが止まってから 1 行にまとめる（Screen Dist のスライダー中は毎 2〜3 フレーム来る）。
        // 種類が変わったら、前の種類のぶんを先に書いてから数え直す（捨てない）。
        if (reanchorLogPending && reanchorLogKind != anchoredPanelKind)
        {
            FlushPendingReanchorLog(true);
        }
        if (!reanchorLogPending || reanchorLogKind != anchoredPanelKind)
        {
            reanchorLogCount = 0;
            reanchorLogMovedTotal = 0f;
            reanchorLogTurnedMax = 0f;
        }
        reanchorLogPending = true;
        reanchorLogKind = anchoredPanelKind;
        reanchorLogChangedAt = Time.unscaledTime;
        reanchorLogCount++;
        reanchorLogMovedTotal += moved;
        reanchorLogTurnedMax = Mathf.Max(reanchorLogTurnedMax, turned);
    }

    // force: 試行・練習を閉じる直前に呼ぶ。沈静を待たずに書き切る。待つと trial_end / tutorial_end の
    // **後**に出て、練習では trial_index が −1 でなく直前の試行番号になる（2026-09-29 の 4 回目の監査）。
    private void FlushPendingReanchorLog(bool force = false)
    {
        if (!reanchorLogPending || (!force && Time.unscaledTime - reanchorLogChangedAt < ReanchorLogSettleSeconds))
        {
            return;
        }

        reanchorLogPending = false;
        Debug.Log(
            $"[Experiment] 画面が動いたのでパネルを置き直しました: kind={reanchorLogKind} times={reanchorLogCount} " +
            $"moved={reanchorLogMovedTotal:F3}m turned_max={reanchorLogTurnedMax:F1}deg");
        session?.RecordOperation(
            "panel_reanchored",
            $"kind={reanchorLogKind} times={reanchorLogCount} screen_moved_m={ExperimentCsv.Format(reanchorLogMovedTotal)} " +
            $"screen_turned_deg={ExperimentCsv.Format(reanchorLogTurnedMax)}");
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
        // trialEndRequested の判定は、同じフレームに 2 本のレイから来たときに trial_end_pressed を 2 行残さないため。
        if (phase != Phase.Trial || trialEndRequested || !IsTrialEndAllowed())
        {
            return;
        }

        ExperimentLog.Operation("trial_end_pressed");
        trialEndRequested = true;
    }

    // 実験者が試行を途中で打ち切る（被験者の体調・機器の不調）。trials.csv には
    // aborted=1 / abort_reason=aborted_by_experimenter の行が残る。
    private void RequestTrialAbortByExperimenter()
    {
        if (phase != Phase.Trial || trialEndRequested || trialAbortRequested)
        {
            return;
        }

        Debug.LogWarning("[Experiment] 試行中に「実験者用: 中止」が押されました");
        ExperimentLog.Operation("trial_abort_pressed");
        trialAbortRequested = true;
    }

    private IEnumerator EndTrialRoutine(bool aborted, string abortReason)
    {
        // **アンロード中に phase を Trial のままにしない。**シーンの破棄と Resources.UnloadUnusedAssets は
        // 実機で 1 秒以上かかり、その間「視聴を終了」のボタンが画面に残って押せてしまう。押しても
        // 何も起きないので被験者が連打する（ログにも残らない。2026-09-25 の監査 F-4）。
        phase = Phase.Loading;
        // 走っている Random モーションを止め、video_pause_begin を end と対にしてから閉じる。
        // アンロード任せだと Sink が外れた後に消えて begin だけが残る。
        if (cachedPlayer != null)
        {
            cachedPlayer.StopAllInteractiveMotionForExperimentEnd();
        }
        // perf の閉じていない窓（1 秒未満）を書き切る。捨てると video_played_sec が最大 1 秒少なくなる。
        FlushPerfWindow();
        FlushPendingReanchorLog(true);
        session.EndTrial(aborted, abortReason);
        ForgetAnchoredPanel();
        ShowPleaseWaitPanel("次の画面を準備しています。");

        yield return UnloadTrialSceneRoutine();
        ShowWaitingPanel();
    }

    private void FlushPerfWindow()
    {
        if (!logPerf || session == null || !session.TrialInProgress || cachedPlayer == null)
        {
            return;
        }

        if (perf.TryFlush(out ExperimentPerfAccumulator.Sample sample))
        {
            session.RecordPerf(sample, cachedPlayer.CurrentVideoFrame);
        }
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
            $"全 {ExperimentPlan.TrialCount} 本の動画が終わりました。\n" +
            "お疲れさまでした。\n\n" +
            "（実験者: 最後のアンケートを回収してください）";

        // 次の参加者に移るための戻り道。以前はボタンが無く、アプリの再起動が要った。
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("Home へ戻る", ReturnToHome),
        };

        panel.Show("実験終了", body, specs);
        sessionCompleted = true;
        session.RecordOperation("session_end", $"reason=completed trials_done={ExperimentPlan.TrialCount}");
        session.FlushLogs();
        Debug.Log($"[Experiment] セッション終了: {session.ParticipantId} / ログ: {session.LogDirectory}");
        // 以降ログは書かないので、ここでファイルを閉じて手放す（閉じたあとの書き込みは黙って捨てられるだけだが、
        // 参照を残す意味も無い）。セッション上書きは「Home へ戻る」で片付く。
        ExperimentLog.Sink = null;
        session.Dispose();
        session = null;
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
            ExperimentPanel.ButtonSpec.Create("練習を開始", BeginTutorial),
            // 実験者用。2 度押しで実行（1 度目は「もう一度押す」に変わる）。
            ExperimentPanel.ButtonSpec.CreateConfirm("練習を飛ばす", SkipTutorialFromWaiting),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        ExperimentDisplayMode mode = ResolveNextTutorialMode();
        int beforeBlock = session.HasNextTrial ? session.NextTrial.blockIndex : -1;
        string escapedNote = beforeBlock >= 0 && tutorialEscapedForBlock == beforeBlock
            ? "前の練習は途中で終わりました。\nもう一度行うか、実験者が「練習を飛ばす」を選んでください。\n"
            : string.Empty;
        string body =
            $"参加者 ID: {session.ParticipantId}\n" +
            $"次: 操作の練習 ― {DescribeTutorialPosition()}\n" +
            $"内容: {DescribeTutorialContent(mode)}\n\n" +
            escapedNote +
            (session.CurrentTrialIndex >= 0
                ? "アンケートの記入が終わったら、実験者の合図で\n「練習を開始」を押してください。"
                : "実験者の合図があったら\n「練習を開始」を押してください。");

        panel.Show("練習", body, specs);
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
                return "ボタンを押す / A ボタンで一時停止・再開 / つまみで動画の位置を変える / 「視聴を終了」の場所";
            case ExperimentDisplayMode.StereoOnly:
                return "立体で見えることの説明のみ（操作は同じ）";
            default:
                // 2026-09-25 に 1 段階から 3 段階に増えたので文面も合わせる（監査で指摘）。
                return "モデルが自分から動く例を見る / Model でモデルを替える / Settings で動きの切り替え";
        }
    }

    private string DescribeTutorialPosition()
    {
        if (session == null || !session.HasNextTrial)
        {
            return string.Empty;
        }

        ExperimentTrial next = session.NextTrial;
        // 被験者も見る画面なので内部の enum 名は出さない（2026-09-25 の監査）。「ブロック」「組」も出さない。
        return next.blockIndex == 0
            ? "最初の動画の前"
            : $"次の 3 本（{DescribeDisplayModeForParticipant(next.mode)}）の前";
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
        MarkTutorialDoneForBlock(beforeBlock);
        ShowWaitingPanel();
    }

    private IEnumerator RunTutorialRoutine()
    {
        phase = Phase.Loading;
        tutorialEndRequested = false;
        tutorialPanelDirty = false;
        loadCancelRequested = false;

        // ボタンのクリックハンドラから始まるので、パネルの作り直しはハンドラを抜けてから。
        yield return null;

        string bundleFileName = bundleCatalog.Resolve(ExperimentVideo.Tutorial);
        int beforeBlock = session.HasNextTrial ? session.NextTrial.blockIndex : 0;
        tutorialBlockInProgress = beforeBlock;
        ExperimentDisplayMode tutorialMode = ResolveNextTutorialMode();
        // 被験者が見る画面なので、内部の条件名と bundle のファイル名は出さない（2026-09-25 の監査）。
        string loadingTitle = $"練習（{DescribeDisplayModeForParticipant(tutorialMode)}）";
        Debug.Log($"[Experiment] 読み込み: 練習 before_block={beforeBlock} mode={tutorialMode} / {bundleFileName}");
        ShowLoadingPanel(loadingTitle);

        // チュートリアルは**次のブロックと同じ表示条件**で再生する（単眼なら単眼、置換ありなら置換あり）。
        // 置換ありのときは category の絞り込みと犬の既定モデルを付け、保存済みのモデル選択は
        // 読まずに毎回同じ見た目で始める。単眼・ステレオでは除去前動画を使う（bundle に要る）。
        LoadOutcome outcome = new LoadOutcome();
        yield return LoadTrialSceneRoutine(
            new ExperimentTrialRequest(
                bundleFileName,
                tutorialMode,
                -1,
                ExperimentVideo.Tutorial,
                string.IsNullOrWhiteSpace(tutorialOnlyCategory) ? null : tutorialOnlyCategory.Trim(),
                string.IsNullOrWhiteSpace(tutorialAnimalModelName) ? null : tutorialAnimalModelName.Trim(),
                true),
            outcome);
        if (outcome.sceneMissing)
        {
            // 記録も無いまま「済み」にはしない（試行側の ShowTrialNotStartedPanel と同じ扱い）。
            tutorialBlockInProgress = -1;
            ShowTutorialNotStartedPanel();
            yield break;
        }

        session.BeginTutorial(bundleFileName, beforeBlock, tutorialMode);
        tutorial = new ExperimentTutorial(session, tutorialMode);
        tutorial.Changed += MarkTutorialPanelDirty;
        // プレイヤーの操作ログを横取りして段階を進める。セッションへはそのまま転送される。
        ExperimentLog.Sink = tutorial;

        // 再生が始まるまで待つ。試行と違い、bundle が無ければ諦めて先へ進める。
        yield return WaitForPlaybackStartRoutine(tutorialLoadTimeoutSeconds, loadingTitle, outcome);
        if (outcome.failure != null)
        {
            Debug.LogError($"[Experiment] 練習を開始できません: {outcome.failure}（{bundleFileName}）");
            ExperimentLog.Sink = null;
            session.EndTutorial(outcome.failure);
            tutorial.Changed -= MarkTutorialPanelDirty;
            tutorial = null;
            ShowPleaseWaitPanel("次の画面を準備しています。");
            yield return WaitForBundleIoRoutine($"tutorial before_block={beforeBlock}");
            yield return UnloadTrialSceneRoutine();
            ShowTutorialLoadFailedPanel(outcome.failure);
            yield break;
        }

        ShowTutorialPanel(false);
        phase = Phase.Tutorial;
        nextLogFlushTime = Time.realtimeSinceStartup + LogFlushIntervalSeconds;

        while (!tutorialEndRequested)
        {
            yield return null;
        }

        // アンロード中に phase を Tutorial のままにしない（試行側の EndTrialRoutine と同じ理由）。
        phase = Phase.Loading;
        // **結果は StopAll の前に読む。**StopAll は motion_end を出し、それが練習の段階を 1 つ進めるので、
        // 後で読むと tutorial_end の step= が実際に脱出した段階の 1 つ先になる（2026-09-29 の 4 回目の監査）。
        bool completed = tutorial.IsDone;
        string tutorialResult = tutorial.DescribeResult();
        if (cachedPlayer != null)
        {
            cachedPlayer.StopAllInteractiveMotionForExperimentEnd();
        }
        ExperimentLog.Sink = null;
        FlushPendingReanchorLog(true);
        session.EndTutorial(tutorialResult);
        ForgetAnchoredPanel();
        ShowPleaseWaitPanel("次の画面を準備しています。");
        yield return UnloadTrialSceneRoutine();
        // 実験者用のボタンで途中で終えた練習は「済み」にしない。待機画面に戻り、やり直すか飛ばすかを
        // 実験者が選ぶ。以前は無条件で済みにしていたので、被験者が「次へ」のつもりで押すと
        // その練習はやり直せなかった（2026-09-29 の 3 回目の監査）。
        if (!completed)
        {
            tutorialEscapedForBlock = tutorialBlockInProgress;
        }
        OnTutorialFinished(completed);
    }

    // 練習のシーンがロードできなかったとき。記録も番号も動いていないので、待機へ戻す道だけ用意する。
    private void ShowTutorialNotStartedPanel()
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);

        string body =
            "練習を開始できませんでした。\n" +
            "アプリの設定に問題があり、動画の画面を開けません。\n\n" +
            "実験者に知らせてください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("待機画面へ", ShowWaitingPanel),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        panel.Show("開始できません", body, specs);
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
            Debug.LogWarning("[Experiment] 画面の位置が取れないので、チュートリアルのパネルを正面基準で置きます");
            panel.Show(tutorial.Title, tutorial.Body, specs, false);
        }
        RememberAnchoredPanel(AnchoredPanelKind.Tutorial);
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
        out Vector3 anchor,
        bool verbose = true)
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
        // 置き直し（Screen Dist のスライダー中は毎 2〜3 フレーム）からは出さない。
        if (verbose)
        {
            Debug.Log(
                $"[Experiment] panel side={side} size={panelSizeMeters.x:F2}x{panelSizeMeters.y:F2}m " +
                $"screen={screenSize.x:F2}x{screenSize.y:F2}m @{screenDistance:F2}m k={k:F2} anchor={anchor:F3}");
        }
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
        // 文言はボタン幅（330 px、40 px フォント）に 1 行で収まる長さにする。長いと 2 行になり、
        // 日本語のフォールバックフォントの行高では 2 行目が切れうる（2026-09-29 の監査）。
        // 2 度押しで実行。途中の段階はこのボタンしか無いので、被験者が「次へ」のつもりで 1 回押しても効かない
        // （2026-09-29 の 3 回目の監査）。押し切ったときはその練習を「済み」にせず待機画面へ戻す（RunTutorialRoutine）。
        if (tutorial != null && !tutorial.IsDone)
        {
            specs.Add(ExperimentPanel.ButtonSpec.CreateConfirm("実験者用: 終了", RequestTutorialEnd));
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
        if (phase != Phase.Tutorial || tutorialEndRequested)
        {
            return;
        }

        ExperimentLog.Operation("tutorial_end_pressed");
        tutorialEndRequested = true;
    }

    // markDone: そのブロックの練習を済みにする（完走した・実験者が「練習なしで続行」を選んだ）。
    // 途中終了なら false で、待機画面はもう一度練習の案内を出す。
    private void OnTutorialFinished(bool markDone)
    {
        if (tutorial != null)
        {
            tutorial.Changed -= MarkTutorialPanelDirty;
            tutorial = null;
        }

        if (markDone)
        {
            MarkTutorialDoneForBlock(tutorialBlockInProgress);
        }
        tutorialBlockInProgress = -1;
        ShowWaitingPanel();
    }

    // 練習の動画が読み込めなかった。被験者が見る画面なので bundle 名と技術用語は出さない（ログには残る）。
    private void ShowTutorialLoadFailedPanel(string reason)
    {
        phase = Phase.Waiting;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        Debug.LogError($"[Experiment] 練習の読み込み失敗パネル: reason={reason}");

        string body =
            "練習用の動画を読み込めませんでした。\n" +
            $"理由: {DescribeLoadFailure(reason)}\n\n" +
            "実験者が次を選んでください。";

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            // もう一度読み込む道を残す（実験者が誤って「中止」を押したときのため。試行側の「やり直す」と同じ）。
            // 文言はボタン幅（330 px、32 px フォント）に 1 行で収まる長さにする。
            ExperimentPanel.ButtonSpec.Create("練習をやり直す", RetryTutorialAfterFailure),
            ExperimentPanel.ButtonSpec.Create("練習なしで続行", () => OnTutorialFinished(true)),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };

        panel.Show("練習を読み込めません", body, specs);
    }

    // 練習の読み込みに失敗したあと、同じブロックの練習をもう一度試す。
    private void RetryTutorialAfterFailure()
    {
        if (phase != Phase.Waiting || session == null)
        {
            return;
        }

        tutorialBlockInProgress = -1;
        session.RecordOperation(
            "tutorial_retry",
            $"before_block={(session.HasNextTrial ? session.NextTrial.blockIndex : -1)}");
        session.FlushLogs();
        ShowWaitingPanel();
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

        // 試行やチュートリアルを動かしている最中（読み込みのコルーチンが走っている間も含む）は戻らない。
        // 「この試行を開始」と同じフレームに Home が押されると、Home を Single でロードした上に
        // コルーチンが TrialScene を Additive で載せ、Home の上で動画が再生され始める（2026-09-29 の監査）。
        if (phase == Phase.Loading || phase == Phase.Trial || phase == Phase.Tutorial)
        {
            Debug.LogWarning($"[Experiment] phase={phase} の間は Home へ戻れません");
            return;
        }

        returningToHome = true;
        bool hadSession = session != null;
        FinishSessionIfRunning(true, "session_abort_by_experimenter");
        if (!hadSession)
        {
            // FinishSessionIfRunning が呼ばない分（セットアップ画面から戻るとき）。
            ExperimentSessionOverrides.EndSession();
        }
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.Clear();
        ForgetAnchoredPanel();
        Debug.Log("[Experiment] return to HomeScene");

        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        panel.Show("お待ちください", "入口の画面に戻ります。", null);
        AsyncOperation load = SceneManager.LoadSceneAsync(homeSceneName, LoadSceneMode.Single);
        if (load != null)
        {
            // 次に「被験者実験」へ入ったときの参加者番号。完走したら次の人、途中なら同じ人。
            // **戻れたと分かってから進める。**戻れないまま進めると画面の番号と次回の番号がずれる。
            rememberedParticipantNumber = sessionCompleted ? participantNumber + 1 : participantNumber;
            Debug.Log($"[Experiment] next participant number = {rememberedParticipantNumber}");
        }
        else
        {
            // Build Settings に HomeScene が無い等。ボタンの無いパネルのまま止まると何もできないので、
            // セットアップ画面に戻して押せるものを残す（2026-09-29 の 3 回目の監査）。
            Debug.LogError($"[Experiment] Home シーンをロードできません: {homeSceneName}（Build Settings に追加済みか確認）");
            returningToHome = false;
            ShowSetupPanel();
        }
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

        // 休止から戻った最初のフレーム。dt に停止時間が乗りうるので窓に入れない（OnApplicationPause）。
        if (skipNextPerfPush)
        {
            skipNextPerfPush = false;
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

        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
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
