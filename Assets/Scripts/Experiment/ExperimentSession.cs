using System;
using UnityEngine;

// 1 参加者分のセッション。割り付け・試行の進行・ログ書き出しを保持する。
//
// 試行シーンは試行ごとにロードし直されるが、このオブジェクトは ExperimentScene（Additive の
// ベースシーン）に常駐する ExperimentController が持ち続けるのでセッション中ずっと生存する。
public sealed class ExperimentSession : IExperimentLogSink, IDisposable
{
    private readonly ExperimentLogWriter writer;
    private readonly Func<double> videoTimeProvider;

    private DateTime trialStartedAt;
    private float trialStartRealtime;
    private string currentBundleFileName;
    private int currentLoopCount;
    private bool trialInProgress;
    private bool tutorialInProgress;
    private int tutorialBeforeBlock;

    // 試行開始時の状態一式（trials.csv の bundle_sha256 〜 display_hz 列。2026-09-25）。
    // 再生が始まった時点で ExperimentController が SetTrialStartState で入れる。読み込みに失敗した試行は空のまま。
    private string trialBundleSha256;
    private long trialBundleBytes;
    private string trialBundleSource;
    private string trialAppBuild;
    private bool trialMotionEnabled;
    private bool trialMonocular;
    private float trialScreenDistStart;
    private bool trialBoneLengthCorrection;
    private float trialDisplayHz;
    private bool trialStateRecorded;
    // 動画が実際に進んだ秒数の合計（perf.csv の video_time_advanced_sec の和）。
    // Motion で動画が止まる第 3 条件では duration_sec（試行の経過時間）と分かれる。
    private double trialVideoPlayedSeconds;

    // realtimeProvider: 経過秒の時計（null なら Time.realtimeSinceStartup）。テストが差し替える。
    public ExperimentSession(
        string participantId,
        ExperimentGroup group,
        int videoOrderPattern,
        ExperimentLogWriter writer,
        Func<double> videoTimeProvider,
        Func<float> realtimeProvider = null)
    {
        ParticipantId = participantId;
        Group = group;
        VideoOrderPattern = videoOrderPattern;
        Trials = ExperimentPlan.BuildTrials(group, videoOrderPattern);
        StartedAt = DateTime.Now;
        CurrentTrialIndex = -1;

        this.writer = writer;
        this.videoTimeProvider = videoTimeProvider;
        this.realtimeProvider = realtimeProvider ?? (() => Time.realtimeSinceStartup);
    }

    private readonly Func<float> realtimeProvider;

    private float Realtime
    {
        get { return realtimeProvider(); }
    }

    public string ParticipantId { get; }
    public ExperimentGroup Group { get; }
    public int VideoOrderPattern { get; }
    public ExperimentTrial[] Trials { get; }
    public DateTime StartedAt { get; }

    // 進行中の試行番号。-1 = まだ 1 本目を始めていない。
    public int CurrentTrialIndex { get; private set; }

    public bool TrialInProgress
    {
        get { return trialInProgress; }
    }

    public bool TutorialInProgress
    {
        get { return tutorialInProgress; }
    }

    // operations.csv / interactions.csv の trial_index 列。チュートリアル中は試行ではないので -1
    // （後半ブロックの前のチュートリアルでも、直前の試行番号を書かない）。
    public int TrialIndexForLog
    {
        get { return tutorialInProgress ? -1 : CurrentTrialIndex; }
    }

    public bool HasNextTrial
    {
        get { return CurrentTrialIndex + 1 < Trials.Length; }
    }

    public ExperimentTrial NextTrial
    {
        get { return Trials[Mathf.Clamp(CurrentTrialIndex + 1, 0, Trials.Length - 1)]; }
    }

    public ExperimentTrial CurrentTrial
    {
        get { return Trials[Mathf.Clamp(CurrentTrialIndex, 0, Trials.Length - 1)]; }
    }

    public string LogDirectory
    {
        get { return writer != null ? writer.SessionDirectory : string.Empty; }
    }

    public void BeginTrial(int trialIndex, string bundleFileName)
    {
        // 試行とチュートリアルは開始時刻（trialStartRealtime）を共有する。両方が同時に進行中になると
        // duration_sec が静かに誤るので、片方が残っていたら先に閉じる（進行上は起きないはず。防御）。
        if (tutorialInProgress)
        {
            Debug.LogError("[Experiment] チュートリアルが閉じられないまま試行を始めようとしました。閉じてから続けます");
            EndTutorial("aborted:trial_started");
        }

        CurrentTrialIndex = Mathf.Clamp(trialIndex, 0, Trials.Length - 1);
        currentBundleFileName = bundleFileName;
        currentLoopCount = 0;
        trialStartedAt = DateTime.Now;
        trialStartRealtime = Realtime;
        trialInProgress = true;

        trialBundleSha256 = null;
        trialBundleBytes = 0;
        trialBundleSource = null;
        trialAppBuild = null;
        trialMotionEnabled = false;
        trialMonocular = false;
        trialScreenDistStart = 0f;
        trialBoneLengthCorrection = false;
        trialDisplayHz = 0f;
        trialStateRecorded = false;
        trialVideoPlayedSeconds = 0d;

        ExperimentLog.Sink = this;
        RecordOperation("trial_begin", CurrentTrial.Describe(Trials.Length));
        // 読み込み中（実機で 5〜十数秒）に落ちると trial_begin すら残らない。周期 flush は再生開始後に
        // しか走らないので、ここで書き切る（2026-09-29 の監査）。
        writer?.Flush();
    }

    // 再生が始まった時点の状態一式。operations.csv に `trial_state` 1 行で残し、trials.csv の列にも入れる。
    // 「条件どおりの状態で試行が始まった」ことを後から確認するための行（2026-09-25、論文側の依頼 §3.3）。
    public void SetTrialStartState(
        string bundleSha256,
        long bundleBytes,
        string bundleSource,
        string appBuild,
        bool motionEnabled,
        bool monocular,
        float screenDistanceMeters,
        bool boneLengthCorrection,
        float displayHz)
    {
        if (!trialInProgress)
        {
            return;
        }

        trialBundleSha256 = bundleSha256;
        trialBundleBytes = bundleBytes;
        trialBundleSource = bundleSource;
        trialAppBuild = appBuild;
        trialMotionEnabled = motionEnabled;
        trialMonocular = monocular;
        trialScreenDistStart = screenDistanceMeters;
        trialBoneLengthCorrection = boneLengthCorrection;
        trialDisplayHz = displayHz;
        trialStateRecorded = true;

        RecordOperation(
            "trial_state",
            $"mode={CurrentTrial.mode} bundle={currentBundleFileName} sha256={bundleSha256} bytes={ExperimentCsv.Format(bundleBytes)} " +
            $"source={bundleSource} app_build={appBuild} motion={ExperimentCsv.Format(motionEnabled)} " +
            $"monocular={ExperimentCsv.Format(monocular)} screen_dist={ExperimentCsv.Format(screenDistanceMeters)} " +
            $"bone_length_correction={ExperimentCsv.Format(boneLengthCorrection)} display_hz={ExperimentCsv.Format(displayHz)}");
        // 視聴開始の刻（解析の起点）。周期 flush まで 10 秒あるので、ここでも書き切る。
        writer?.Flush();
    }

    public bool TrialStateRecorded
    {
        get { return trialStateRecorded; }
    }

    public double TrialVideoPlayedSeconds
    {
        get { return trialVideoPlayedSeconds; }
    }

    // abortReason: `load_timeout` / `load_failed:<理由>` / `cancelled_by_button` / `session_abort_by_experimenter` / `app_quit` 等。
    // 正常終了は空。
    public void EndTrial(bool aborted, string abortReason = null)
    {
        if (!trialInProgress)
        {
            return;
        }

        // **経過秒はフラグを落とす前に取る。** TrialElapsedSeconds は trialInProgress が false だと 0 を返すので、
        // 下の AppendRow で読むと duration_sec が必ず 0 になる（2026-09-25 の監査で発覚。初版からの取り違え。
        // 実機ログ P00_20260911_171627 が証拠: operations.csv の trial_end は 8.9138 秒、trials.csv は 0）。
        float elapsedSeconds = TrialElapsedSeconds;

        RecordOperation(aborted ? "trial_abort" : "trial_end", aborted ? abortReason : null);
        trialInProgress = false;
        ExperimentLog.Sink = null;

        DateTime endedAt = DateTime.Now;
        ExperimentTrial trial = CurrentTrial;

        writer?.AppendRow(
            ExperimentLogWriter.TrialsFileName,
            ParticipantId,
            Group.ToString(),
            ExperimentCsv.Format(VideoOrderPattern),
            ExperimentCsv.Format(trial.trialIndex),
            ExperimentCsv.Format(trial.blockIndex),
            ExperimentCsv.Format(trial.indexInBlock),
            trial.video.ToString(),
            trial.mode.ToString(),
            currentBundleFileName,
            trialBundleSha256,
            trialStateRecorded ? ExperimentCsv.Format(trialBundleBytes) : string.Empty,
            trialBundleSource,
            trialAppBuild,
            trialStateRecorded ? ExperimentCsv.Format(trialMotionEnabled) : string.Empty,
            trialStateRecorded ? ExperimentCsv.Format(trialMonocular) : string.Empty,
            trialStateRecorded ? ExperimentCsv.Format(trialScreenDistStart) : string.Empty,
            trialStateRecorded ? ExperimentCsv.Format(trialBoneLengthCorrection) : string.Empty,
            trialStateRecorded ? ExperimentCsv.Format(trialDisplayHz) : string.Empty,
            ExperimentCsv.FormatTimestamp(trialStartedAt),
            ExperimentCsv.FormatTimestamp(endedAt),
            ExperimentCsv.Format(elapsedSeconds),
            ExperimentCsv.Format(trialVideoPlayedSeconds),
            ExperimentCsv.Format(currentLoopCount),
            ExperimentCsv.Format(aborted),
            aborted ? (abortReason ?? string.Empty) : string.Empty);

        writer?.Flush();
    }

    // 読み込みに失敗した試行を「もう一度同じ試行から」始めるための巻き戻し。
    // 失敗した試行の行（aborted=1）は trials.csv に残ったまま、次の試行が同じ index になる。
    // 試行中には呼ばない（進行中の試行を先に EndTrial で閉じる）。
    public bool RetryCurrentTrial()
    {
        if (trialInProgress || CurrentTrialIndex < 0)
        {
            return false;
        }

        CurrentTrialIndex--;
        return true;
    }

    // perf.csv の 1 行（1 秒窓）。動画が進んだ秒数を試行の合計にも足す。
    public void RecordPerf(ExperimentPerfAccumulator.Sample sample, long videoFrame)
    {
        if (!trialInProgress)
        {
            return;
        }

        trialVideoPlayedSeconds += sample.videoTimeAdvancedSec;

        writer?.AppendRow(
            ExperimentLogWriter.PerfFileName,
            ParticipantId,
            ExperimentCsv.Format(CurrentTrialIndex),
            ExperimentCsv.FormatTimestamp(DateTime.Now),
            ExperimentCsv.Format(TrialElapsedSeconds),
            ExperimentCsv.Format(CurrentVideoTimeSeconds),
            ExperimentCsv.Format(videoFrame),
            ExperimentCsv.Format(sample.windowSeconds),
            ExperimentCsv.Format(sample.frames),
            ExperimentCsv.Format(sample.fps),
            ExperimentCsv.Format(sample.maxFrameMs),
            ExperimentCsv.Format(sample.longFrames),
            ExperimentCsv.Format(sample.videoFramesAdvanced),
            ExperimentCsv.Format(sample.videoFrameSkips),
            ExperimentCsv.Format(sample.videoSeekJumps),
            ExperimentCsv.Format(sample.videoTimeAdvancedSec),
            ExperimentCsv.Format(sample.videoPlayingAtEnd),
            ExperimentCsv.Format(sample.controllerMovedMeters),
            ExperimentCsv.Format(sample.triggerFrames),
            ExperimentCsv.Format(sample.buttonFrames));
    }

    // 操作チュートリアルの開始。試行ではないので trials.csv には書かず、operations.csv に
    // tutorial_begin / tutorial_end を残す。sink は ExperimentController が ExperimentTutorial
    // （段階検出）を挟んで設定するので、ここでは触らない。
    public void BeginTutorial(string bundleFileName, int beforeBlockIndex, ExperimentDisplayMode mode)
    {
        if (trialInProgress)
        {
            Debug.LogError("[Experiment] 試行が閉じられないままチュートリアルを始めようとしました。閉じてから続けます");
            EndTrial(true, "tutorial_started");
        }

        currentBundleFileName = bundleFileName;
        currentLoopCount = 0;
        trialStartedAt = DateTime.Now;
        trialStartRealtime = Realtime;
        tutorialInProgress = true;
        tutorialBeforeBlock = beforeBlockIndex;

        RecordOperation("tutorial_begin", $"bundle={bundleFileName} before_block={beforeBlockIndex} mode={mode}");
        writer?.Flush();
    }

    // result は ExperimentTutorial.DescribeResult()（completed=… step=… mode=…）か、読み込みの失敗理由
    // （load_timeout / load_failed:<理由> / cancelled_by_button）か "aborted"。
    public void EndTutorial(string result)
    {
        if (!tutorialInProgress)
        {
            return;
        }

        RecordOperation(
            "tutorial_end",
            $"{result} duration_sec={ExperimentCsv.Format(TrialElapsedSeconds)} before_block={tutorialBeforeBlock}");
        tutorialInProgress = false;
        writer?.Flush();
    }

    // 試行中はその試行の、チュートリアル中はチュートリアルの開始からの経過秒。
    public float TrialElapsedSeconds
    {
        get
        {
            return trialInProgress || tutorialInProgress
                ? Realtime - trialStartRealtime
                : 0f;
        }
    }

    private double CurrentVideoTimeSeconds
    {
        get
        {
            if (videoTimeProvider == null)
            {
                return 0d;
            }

            try
            {
                return videoTimeProvider();
            }
            catch
            {
                return 0d;
            }
        }
    }

    public void RecordOperation(string action, string detail)
    {
        writer?.AppendRow(
            ExperimentLogWriter.OperationsFileName,
            ParticipantId,
            ExperimentCsv.Format(TrialIndexForLog),
            ExperimentCsv.FormatTimestamp(DateTime.Now),
            ExperimentCsv.Format(TrialElapsedSeconds),
            ExperimentCsv.Format(CurrentVideoTimeSeconds),
            action,
            detail);
    }

    public void RecordInteraction(uint trackId, string kind, string detail)
    {
        writer?.AppendRow(
            ExperimentLogWriter.InteractionsFileName,
            ParticipantId,
            ExperimentCsv.Format(TrialIndexForLog),
            ExperimentCsv.FormatTimestamp(DateTime.Now),
            ExperimentCsv.Format(TrialElapsedSeconds),
            ExperimentCsv.Format(CurrentVideoTimeSeconds),
            ExperimentCsv.Format((int)trackId),
            kind,
            detail);
    }

    public void RecordVideoLoop()
    {
        currentLoopCount++;
        RecordOperation("video_loop", ExperimentCsv.Format(currentLoopCount));
    }

    // 試行の途中でヘッドセットを外された／アプリが落とされたときに、
    // バッファに溜まったままの行を失わないようにする。
    public void FlushLogs()
    {
        writer?.Flush();
    }

    public void RecordHeadPose(Vector3 position, Quaternion rotation)
    {
        if (!trialInProgress)
        {
            return;
        }

        writer?.AppendRow(
            ExperimentLogWriter.HeadPoseFileName,
            ParticipantId,
            ExperimentCsv.Format(CurrentTrialIndex),
            ExperimentCsv.FormatTimestamp(DateTime.Now),
            ExperimentCsv.Format(TrialElapsedSeconds),
            ExperimentCsv.Format(CurrentVideoTimeSeconds),
            ExperimentCsv.Format(position.x),
            ExperimentCsv.Format(position.y),
            ExperimentCsv.Format(position.z),
            ExperimentCsv.Format(rotation.x),
            ExperimentCsv.Format(rotation.y),
            ExperimentCsv.Format(rotation.z),
            ExperimentCsv.Format(rotation.w));
    }

    public void Dispose()
    {
        if (ReferenceEquals(ExperimentLog.Sink, this))
        {
            ExperimentLog.Sink = null;
        }

        writer?.Dispose();
    }
}
