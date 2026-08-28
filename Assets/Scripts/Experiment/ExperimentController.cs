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
[DisallowMultipleComponent]
public sealed class ExperimentController : MonoBehaviour
{
    private enum Phase
    {
        Setup,
        Waiting,
        Loading,
        Trial,
        Finished,
    }

    [Header("Scene")]
    public string trialSceneName = "TrialScene";

    [Header("Participant")]
    public string participantIdPrefix = "P";
    [Min(1)] public int participantNumber = 1;
    public ExperimentGroup group = ExperimentGroup.A;
    [Range(ExperimentPlan.MinVideoOrderPattern, ExperimentPlan.MaxVideoOrderPattern)]
    public int videoOrderPattern = 1;

    [Header("Bundles")]
    public ExperimentBundleCatalog bundleCatalog = new ExperimentBundleCatalog();

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
        panel.SizeMeters = new Vector2(1.05f, 0.82f);
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
            $"全 {ExperimentPlan.TrialCount} 試行\n" +
            "割り付け表と一致していることを確認してから開始してください。";
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

        Debug.Log($"[Experiment] セッション開始: {ParticipantId} / 群 {group} / 動画順 {videoOrderPattern}");
        Debug.Log($"[Experiment] ログ出力先: {sessionDir}");

        ShowWaitingPanel();
    }

    private void ShowWaitingPanel()
    {
        phase = Phase.Waiting;
        panel.SizeMeters = new Vector2(1.05f, 0.82f);
        panel.OffsetMeters = Vector2.zero;

        if (!session.HasNextTrial)
        {
            ShowFinishedPanel();
            return;
        }

        ExperimentTrial next = session.NextTrial;
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
        // （bundle_human.svb は 155MB あり、実機では十数秒かかる）。
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

        ShowWaitingPanel();
    }

    private void ShowFinishedPanel()
    {
        phase = Phase.Finished;
        panel.SizeMeters = new Vector2(1.05f, 0.82f);
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
