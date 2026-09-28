using NUnit.Framework;
using UnityEngine;

// perf.csv の集計（ExperimentPerfAccumulator）と、試行のやり直し・中断理由（ExperimentSession）の検算。2026-09-25。
public class ExperimentPerfAccumulatorTests
{
    private static bool Push(ExperimentPerfAccumulator acc, float dt, long frame, double time, bool playing,
        out ExperimentPerfAccumulator.Sample sample, Vector3? pointer = null, bool trigger = false, bool button = false)
    {
        return acc.Push(dt, frame, time, playing, pointer.HasValue, pointer ?? Vector3.zero, trigger, button, out sample);
    }

    // 1 秒窓が閉じるまで false、閉じたら 1 回だけ true。
    // dt は 2 進数で正確な 0.125 にする（1/72 を 72 回足すと丸めで 1.0 に届かないことがある）。
    [Test]
    public void Push_ClosesWindowAfterOneSecond()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        int emitted = 0;
        ExperimentPerfAccumulator.Sample last = default;
        for (int i = 0; i < 8; i++)
        {
            if (Push(acc, 0.125f, i, i * 0.125, true, out ExperimentPerfAccumulator.Sample s))
            {
                emitted++;
                last = s;
            }
        }

        Assert.That(emitted, Is.EqualTo(1));
        Assert.That(last.frames, Is.EqualTo(8));
        Assert.That(last.fps, Is.EqualTo(8f).Within(0.01f));
        Assert.That(last.longFrames, Is.EqualTo(8));   // 125 ms は 20 ms 超なので全部「落ちた」扱い
        Assert.That(last.videoFramesAdvanced, Is.EqualTo(7));
        Assert.That(last.videoFrameSkips, Is.EqualTo(0));
        Assert.That(last.videoTimeAdvancedSec, Is.EqualTo(0.875).Within(1e-9));
        Assert.That(last.videoPlayingAtEnd, Is.True);
    }

    // 20 ms 超のフレームは「落ちた」と数え、最大フレーム時間も残す。
    [Test]
    public void Push_CountsLongFramesAndMaxFrameTime()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        ExperimentPerfAccumulator.Sample sample = default;
        bool emitted = false;
        Push(acc, 0.014f, 0, 0.0, true, out _);
        Push(acc, 0.030f, 1, 0.03, true, out _);   // 落ちた
        Push(acc, 0.014f, 1, 0.044, true, out _);
        Push(acc, 0.050f, 3, 0.094, true, out _);  // 落ちた + 動画フレームが 1 つ飛んだ
        emitted |= Push(acc, 0.900f, 4, 0.994, true, out sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.longFrames, Is.EqualTo(3));
        Assert.That(sample.maxFrameMs, Is.EqualTo(900f).Within(0.01f));
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(4));
        Assert.That(sample.videoFrameSkips, Is.EqualTo(1));
    }

    // ループ（2166 → 0）やシークで戻ったぶんは「進んだ」に数えない。
    [Test]
    public void Push_IgnoresVideoRewind()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.25f, 2165, 72.1, true, out _);
        Push(acc, 0.25f, 2166, 72.2, true, out _);
        Push(acc, 0.25f, 0, 0.0, true, out _);
        bool emitted = Push(acc, 0.25f, 1, 0.033, true, out ExperimentPerfAccumulator.Sample sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(2));
        Assert.That(sample.videoFrameSkips, Is.EqualTo(0));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0.1 + 0.033).Within(1e-6));
    }

    // 一時停止中は動画の時刻もフレームも進まないので 0 のまま、playing は false。
    [Test]
    public void Push_PausedVideoAdvancesNothing()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        for (int i = 0; i < 3; i++)
        {
            Push(acc, 0.25f, 100, 3.333, false, out _);
        }
        bool emitted = Push(acc, 0.25f, 100, 3.333, false, out ExperimentPerfAccumulator.Sample sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(0));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0d).Within(1e-9));
        Assert.That(sample.videoPlayingAtEnd, Is.False);
    }

    // コントローラの移動量は姿勢が取れた連続フレームの間だけ足し、ボタンは押していたフレーム数。
    [Test]
    public void Push_AccumulatesControllerMovementAndButtons()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        const float dt = 0.125f;   // 8 回で 1.0 s ちょうど
        Push(acc, dt, 0, 0.0, true, out _, new Vector3(0f, 0f, 0f), true, false);
        Push(acc, dt, 0, 0.0, true, out _, new Vector3(0.1f, 0f, 0f), true, true);   // +0.1
        Push(acc, dt, 0, 0.0, true, out _, null, false, false);                       // 姿勢が取れないフレーム
        Push(acc, dt, 0, 0.0, true, out _, new Vector3(0.5f, 0f, 0f), false, false);  // 前回が無いので足さない
        Push(acc, dt, 0, 0.0, true, out _, new Vector3(0.5f, 0.2f, 0f), false, true); // +0.2
        Push(acc, dt, 0, 0.0, true, out _, null, false, false);
        Push(acc, dt, 0, 0.0, true, out _, null, false, false);
        bool emitted = Push(acc, dt, 0, 0.0, true, out ExperimentPerfAccumulator.Sample sample, new Vector3(1f, 0f, 0f), false, false);

        Assert.That(emitted, Is.True);
        Assert.That(sample.controllerMovedMeters, Is.EqualTo(0.1f + 0.2f).Within(1e-5f));
        Assert.That(sample.triggerFrames, Is.EqualTo(2));
        Assert.That(sample.buttonFrames, Is.EqualTo(2));
    }

    // 窓が閉じたら次の窓は 0 から。動画の前回値は引き継ぐ（窓をまたいだ進みを落とさない）。
    [Test]
    public void Push_ResetsWindowButKeepsVideoContinuity()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.5f, 10, 0.333, true, out _);
        Push(acc, 0.5f, 25, 0.833, true, out _);
        Push(acc, 0.5f, 40, 1.333, true, out _);
        bool emitted = Push(acc, 0.5f, 55, 1.833, true, out ExperimentPerfAccumulator.Sample second);

        Assert.That(emitted, Is.True);
        Assert.That(second.frames, Is.EqualTo(2));
        Assert.That(second.videoFramesAdvanced, Is.EqualTo(30));
        Assert.That(second.videoTimeAdvancedSec, Is.EqualTo(1.0).Within(1e-6));
    }
}

public class ExperimentSessionTrialFlowTests
{
    [TearDown]
    public void TearDown()
    {
        ExperimentLog.Sink = null;
    }

    private static ExperimentSession NewSession()
    {
        // writer null = ファイルは書かない（列の組み立てだけ走る）。
        return new ExperimentSession("P00", ExperimentGroup.A, 1, null, () => 0d);
    }

    // 読み込みに失敗した試行を EndTrial(aborted) で閉じ、RetryCurrentTrial で巻き戻すと同じ試行が次になる。
    [Test]
    public void RetryCurrentTrial_MakesTheSameTrialNextAgain()
    {
        ExperimentSession session = NewSession();
        ExperimentTrial first = session.NextTrial;
        session.BeginTrial(first.trialIndex, "bundle_human.svb");
        session.EndTrial(true, "load_timeout");

        Assert.That(session.NextTrial.trialIndex, Is.EqualTo(first.trialIndex + 1));
        Assert.That(session.RetryCurrentTrial(), Is.True);
        Assert.That(session.NextTrial.trialIndex, Is.EqualTo(first.trialIndex));
        Assert.That(session.TrialInProgress, Is.False);
    }

    // 試行中は巻き戻せない（先に閉じる）。まだ 1 本も始めていないときも何もしない。
    [Test]
    public void RetryCurrentTrial_RefusesWhileInProgressOrBeforeFirstTrial()
    {
        ExperimentSession session = NewSession();
        Assert.That(session.RetryCurrentTrial(), Is.False);

        session.BeginTrial(0, "bundle_human.svb");
        Assert.That(session.RetryCurrentTrial(), Is.False);
        session.EndTrial(false);
    }

    // 開始状態と perf を入れても writer 無しで例外にならず、動画の再生時間が積み上がる。
    [Test]
    public void SetTrialStartStateAndRecordPerf_AccumulateVideoPlayedSeconds()
    {
        ExperimentSession session = NewSession();
        session.BeginTrial(0, "bundle_human.svb");
        Assert.That(session.TrialStateRecorded, Is.False);

        session.SetTrialStartState("abc", 123L, "sharedStorage", "VisionGraft.apk;2026-09-25 12:00:00;deadbeef", true, false, 1.0f, true, 72f);
        Assert.That(session.TrialStateRecorded, Is.True);

        ExperimentPerfAccumulator.Sample sample = new ExperimentPerfAccumulator.Sample { videoTimeAdvancedSec = 0.9, fps = 72f };
        session.RecordPerf(sample, 30);
        session.RecordPerf(sample, 60);
        Assert.That(session.TrialVideoPlayedSeconds, Is.EqualTo(1.8).Within(1e-9));

        Assert.DoesNotThrow(() => session.EndTrial(false));
    }
}
