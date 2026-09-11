using System;
using UnityEngine;

// 1 参加者分のセッション。割り付け・試行の進行・ログ書き出しを保持する。
//
// 試行シーンは試行ごとにロードし直されるが、このオブジェクトは ExperimentController
// (DontDestroyOnLoad) が持ち続けるのでセッション中ずっと生存する。
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

    public ExperimentSession(
        string participantId,
        ExperimentGroup group,
        int videoOrderPattern,
        ExperimentLogWriter writer,
        Func<double> videoTimeProvider)
    {
        ParticipantId = participantId;
        Group = group;
        VideoOrderPattern = videoOrderPattern;
        Trials = ExperimentPlan.BuildTrials(group, videoOrderPattern);
        StartedAt = DateTime.Now;
        CurrentTrialIndex = -1;

        this.writer = writer;
        this.videoTimeProvider = videoTimeProvider;
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
    private int TrialIndexForLog
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
        CurrentTrialIndex = Mathf.Clamp(trialIndex, 0, Trials.Length - 1);
        currentBundleFileName = bundleFileName;
        currentLoopCount = 0;
        trialStartedAt = DateTime.Now;
        trialStartRealtime = Time.realtimeSinceStartup;
        trialInProgress = true;

        ExperimentLog.Sink = this;
        RecordOperation("trial_begin", CurrentTrial.Describe(Trials.Length));
    }

    public void EndTrial(bool aborted)
    {
        if (!trialInProgress)
        {
            return;
        }

        RecordOperation(aborted ? "trial_abort" : "trial_end", null);
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
            ExperimentCsv.FormatTimestamp(trialStartedAt),
            ExperimentCsv.FormatTimestamp(endedAt),
            ExperimentCsv.Format(TrialElapsedSeconds),
            ExperimentCsv.Format(currentLoopCount),
            ExperimentCsv.Format(aborted));

        writer?.Flush();
    }

    // 操作チュートリアルの開始。試行ではないので trials.csv には書かず、operations.csv に
    // tutorial_begin / tutorial_end を残す。sink は ExperimentController が ExperimentTutorial
    // （段階検出）を挟んで設定するので、ここでは触らない。
    public void BeginTutorial(string bundleFileName, int beforeBlockIndex)
    {
        currentBundleFileName = bundleFileName;
        currentLoopCount = 0;
        trialStartedAt = DateTime.Now;
        trialStartRealtime = Time.realtimeSinceStartup;
        tutorialInProgress = true;
        tutorialBeforeBlock = beforeBlockIndex;

        RecordOperation("tutorial_begin", $"bundle={bundleFileName} before_block={beforeBlockIndex}");
    }

    // result は ExperimentTutorial.DescribeResult() か "load_failed" / "aborted"。
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
                ? Time.realtimeSinceStartup - trialStartRealtime
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
