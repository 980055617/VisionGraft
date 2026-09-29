using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

// 2026-09-28〜29 の実験フロー監査で直した振る舞いを固定する。
//   - perf の前方シーク除外（ExperimentPerfAccumulator）と閉じていない窓の書き切り
//   - trials.csv の duration_sec（ExperimentSession.EndTrial がフラグを落とす前に経過秒を取る）
//   - CSV 5 種のヘッダと行の列数が一致すること
//   - 被験者向けの試行表記（内部の enum 名を含まない）
public class ExperimentPerfSeekJumpTests
{
    private static bool Push(ExperimentPerfAccumulator acc, float dt, long frame, double time,
        out ExperimentPerfAccumulator.Sample sample, bool playing = true)
    {
        return acc.Push(dt, frame, time, playing, false, Vector3.zero, false, false, out sample);
    }

    // 前へ飛ばしたぶん（10 秒）は「進んだ」にも「コマ落ち」にも入れず、回数だけ数える。
    // dt は 2 進数で正確な 0.125（8 fps 相当。閾値 0.125×1.5+0.1 = 0.2875）。
    [Test]
    public void Push_ForwardSeekIsCountedAsJumpNotPlayback()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        ExperimentPerfAccumulator.Sample last = default;
        int emitted = 0;
        double[] times = { 0.0, 0.125, 0.25, 10.0, 10.125, 10.25, 10.375, 10.5 };
        long[] frames = { 0, 1, 2, 80, 81, 82, 83, 84 };
        for (int i = 0; i < times.Length; i++)
        {
            if (Push(acc, 0.125f, frames[i], times[i], out ExperimentPerfAccumulator.Sample s))
            {
                emitted++;
                last = s;
            }
        }

        Assert.That(emitted, Is.EqualTo(1));
        Assert.That(last.videoSeekJumps, Is.EqualTo(1));
        Assert.That(last.videoTimeAdvancedSec, Is.EqualTo(0.75).Within(1e-9), "飛びの前 0.25 + 後 0.5");
        Assert.That(last.videoFramesAdvanced, Is.EqualTo(6));
        Assert.That(last.videoFrameSkips, Is.EqualTo(0));
    }

    // 1 フレームが伸びて（GC 0.4 s）動画がそのぶん進んだのはシークではない。閾値は刻みに比例する。
    [Test]
    public void Push_LongFrameWithMatchingAdvanceIsNotASeek()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        for (int i = 0; i < 6; i++)
        {
            Push(acc, 0.1f, i, i * 0.1, out _);
        }
        bool emitted = Push(acc, 0.4f, 9, 0.9, out ExperimentPerfAccumulator.Sample sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoSeekJumps, Is.EqualTo(0));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0.9).Within(1e-9));
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(9));
        Assert.That(sample.videoFrameSkips, Is.EqualTo(3));
        Assert.That(sample.windowSeconds, Is.EqualTo(1.0f).Within(1e-6f));
        Assert.That(sample.frames, Is.EqualTo(7));
        Assert.That(sample.maxFrameMs, Is.EqualTo(400f).Within(0.01f));
    }

    // 着地フレームが 0.3 秒詰まった 1.0 秒のシークもシーク（以前の max(0.25, 4·dt) では再生扱いになった）。
    [Test]
    public void Push_SeekLandingOnALongFrameIsStillASeek()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        for (int i = 0; i < 6; i++)
        {
            Push(acc, 0.125f, i, i * 0.125, out _);
        }
        bool emitted = Push(acc, 0.3f, 13, 1.625, out ExperimentPerfAccumulator.Sample sample);   // 0.625 → 1.625

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoSeekJumps, Is.EqualTo(1));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0.625).Within(1e-9));
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(5));
    }

    // car 動画の scrub 最小刻み（0.240 s）も、72 Hz の刻みならシーク（閾値 0.0139×1.5+0.1 ≈ 0.121）。
    [Test]
    public void Push_SmallScrubStepAtSeventyTwoHzIsASeek()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.0125f, 0, 0.0, out _);
        Push(acc, 0.0125f, 7, 0.24, out _);
        bool emitted = Push(acc, 1.0f, 8, 0.2533, out ExperimentPerfAccumulator.Sample sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoSeekJumps, Is.EqualTo(1));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0.0133).Within(1e-6));
    }

    // 巻き戻し（ループ）の後に前へ飛ばすと、数えるのは前向きの 1 回だけ。
    [Test]
    public void Push_RewindThenForwardSeek_CountsOnlyTheForwardJump()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.125f, 2165, 72.1, out _);
        Push(acc, 0.125f, 2166, 72.2, out _);
        Push(acc, 0.125f, 0, 0.0, out _);        // ループ
        Push(acc, 0.125f, 300, 10.0, out _);     // 前へ飛ばす
        Push(acc, 0.125f, 301, 10.125, out _);
        Push(acc, 0.125f, 302, 10.25, out _);
        Push(acc, 0.125f, 303, 10.375, out _);
        bool emitted = Push(acc, 0.125f, 304, 10.5, out ExperimentPerfAccumulator.Sample sample);

        Assert.That(emitted, Is.True);
        Assert.That(sample.videoSeekJumps, Is.EqualTo(1));
        Assert.That(sample.videoTimeAdvancedSec, Is.EqualTo(0.1 + 0.5).Within(1e-9));
        Assert.That(sample.videoFramesAdvanced, Is.EqualTo(1 + 4));
    }

    // 回数は窓ごとにリセットされる。
    [Test]
    public void Push_SeekJumpsResetPerWindow()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        List<ExperimentPerfAccumulator.Sample> samples = new List<ExperimentPerfAccumulator.Sample>();
        double t = 0d;
        long f = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i == 3)
            {
                t += 5.0;
                f += 40;
            }
            else
            {
                t += 0.125;
                f += 1;
            }
            if (Push(acc, 0.125f, f, t, out ExperimentPerfAccumulator.Sample s))
            {
                samples.Add(s);
            }
        }

        Assert.That(samples.Count, Is.EqualTo(2));
        Assert.That(samples[0].videoSeekJumps, Is.EqualTo(1));
        Assert.That(samples[1].videoSeekJumps, Is.EqualTo(0));
    }

    // 窓が閉じる前に TryFlush すると、その時点までの集計を返して窓を空にする。
    [Test]
    public void TryFlush_ReturnsThePartialWindowAndClearsIt()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.125f, 0, 0.0, out _);
        Push(acc, 0.125f, 1, 0.125, out _);
        Push(acc, 0.125f, 2, 0.25, out _);

        Assert.That(acc.TryFlush(out ExperimentPerfAccumulator.Sample partial), Is.True);
        Assert.That(partial.frames, Is.EqualTo(3));
        Assert.That(partial.windowSeconds, Is.EqualTo(0.375f).Within(1e-6f));
        Assert.That(partial.videoTimeAdvancedSec, Is.EqualTo(0.25).Within(1e-9));

        Assert.That(acc.TryFlush(out _), Is.False, "空の窓は返さない");
    }

    // TryFlush の video_playing_at_end は**最後に Push した**再生状態。以前は窓が閉じたときの値しか覚えず、
    // 端の窓（試行の終わり）では前の窓の値が出た（2026-09-29 の 3 回目の監査）。
    [Test]
    public void TryFlush_UsesThePlayingStateOfTheLastPush()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.5f, 0, 0.0, out _, playing: true);
        Assert.That(Push(acc, 0.5f, 15, 0.5, out ExperimentPerfAccumulator.Sample closed, playing: true), Is.True);
        Assert.That(closed.videoPlayingAtEnd, Is.True);

        Push(acc, 0.125f, 16, 0.53, out _, playing: false);
        Assert.That(acc.TryFlush(out ExperimentPerfAccumulator.Sample partial), Is.True);
        Assert.That(partial.videoPlayingAtEnd, Is.False, "端の窓は最後の Push の状態");
    }

    // 窓の長さとフレーム数が行に載る（1 フレームが長いと窓は 1 秒より伸びる）。
    [Test]
    public void Push_WindowSecondsAndFramesReflectTheActualWindow()
    {
        ExperimentPerfAccumulator acc = new ExperimentPerfAccumulator();
        Push(acc, 0.5f, 0, 0.0, out _);
        bool first = Push(acc, 0.5f, 15, 0.5, out ExperimentPerfAccumulator.Sample a);
        bool second = Push(acc, 1.7f, 66, 2.2, out ExperimentPerfAccumulator.Sample b);

        Assert.That(first, Is.True);
        Assert.That(a.windowSeconds, Is.EqualTo(1.0f).Within(1e-6f));
        Assert.That(a.frames, Is.EqualTo(2));
        Assert.That(a.fps, Is.EqualTo(2f).Within(1e-4f));

        Assert.That(second, Is.True);
        Assert.That(b.windowSeconds, Is.EqualTo(1.7f).Within(1e-6f));
        Assert.That(b.frames, Is.EqualTo(1));
        Assert.That(b.maxFrameMs, Is.EqualTo(1700f).Within(0.01f));
        Assert.That(b.videoSeekJumps, Is.EqualTo(0), "1.7 秒のフレームで 1.7 秒進んだのは再生");
    }
}

// 実際にファイルへ書いて読み戻す。時計は差し替える（Time.realtimeSinceStartup は EditMode で制御できない）。
public class ExperimentSessionCsvTests
{
    private string dir;
    private float clock;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "vg_session_test_" + Guid.NewGuid().ToString("N"));
        clock = 100f;
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // 掃除に失敗しても結果には関係ない。
        }
    }

    private ExperimentSession NewSession(ExperimentLogWriter writer)
    {
        return new ExperimentSession("P01", ExperimentGroup.A, 1, writer, () => 1.5d, () => clock);
    }

    private static List<string[]> ReadCsv(string path)
    {
        // RFC 4180 の引用符付きフィールドを切る（値のカンマを列に数えない）。
        List<string[]> rows = new List<string[]>();
        foreach (string line in File.ReadAllLines(path))
        {
            List<string> fields = new List<string>();
            System.Text.StringBuilder cur = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        cur.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    fields.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            fields.Add(cur.ToString());
            rows.Add(fields.ToArray());
        }
        return rows;
    }

    private static int Column(string[] header, string name)
    {
        int index = Array.IndexOf(header, name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"列 {name} が無い");
        return index;
    }

    // duration_sec は EndTrial がフラグを落とす前に取った経過秒（以前は常に 0 だった）。
    [Test]
    public void EndTrial_WritesElapsedSecondsCapturedBeforeClearingTheFlag()
    {
        using (ExperimentLogWriter writer = new ExperimentLogWriter(dir))
        {
            ExperimentSession session = NewSession(writer);
            session.BeginTrial(0, "bundle_human.svb");
            clock = 112.5f;
            session.EndTrial(false);
            Assert.That(session.TrialElapsedSeconds, Is.EqualTo(0f), "試行外は 0");
        }

        List<string[]> trials = ReadCsv(Path.Combine(dir, ExperimentLogWriter.TrialsFileName));
        Assert.That(trials.Count, Is.EqualTo(2));
        Assert.That(trials[1][Column(trials[0], "duration_sec")], Is.EqualTo("12.5"));
        Assert.That(trials[1][Column(trials[0], "aborted")], Is.EqualTo("0"));

        List<string[]> ops = ReadCsv(Path.Combine(dir, ExperimentLogWriter.OperationsFileName));
        string[] endRow = ops.Find(r => r[Column(ops[0], "action")] == "trial_end");
        Assert.That(endRow, Is.Not.Null);
        Assert.That(endRow[Column(ops[0], "trial_elapsed_sec")], Is.EqualTo("12.5"));
    }

    // 中断でも同じ経過秒が入り、理由が残る。
    [Test]
    public void EndTrial_Aborted_KeepsElapsedSecondsAndReason()
    {
        using (ExperimentLogWriter writer = new ExperimentLogWriter(dir))
        {
            ExperimentSession session = NewSession(writer);
            session.BeginTrial(3, "bundle_animal.svb");
            clock = 107.25f;
            session.EndTrial(true, "app_quit");
        }

        List<string[]> trials = ReadCsv(Path.Combine(dir, ExperimentLogWriter.TrialsFileName));
        Assert.That(trials[1][Column(trials[0], "duration_sec")], Is.EqualTo("7.25"));
        Assert.That(trials[1][Column(trials[0], "aborted")], Is.EqualTo("1"));
        Assert.That(trials[1][Column(trials[0], "abort_reason")], Is.EqualTo("app_quit"));
        Assert.That(trials[1][Column(trials[0], "trial_index")], Is.EqualTo("3"));
    }

    // チュートリアルの duration_sec はチュートリアルの開始からで、直前の試行の開始は使わない。
    [Test]
    public void EndTutorial_UsesTheTutorialStartNotTheTrialStart()
    {
        using (ExperimentLogWriter writer = new ExperimentLogWriter(dir))
        {
            ExperimentSession session = NewSession(writer);
            session.BeginTrial(0, "bundle_human.svb");
            clock = 110f;
            session.EndTrial(false);
            clock = 200f;
            session.BeginTutorial("bundle_tutorial.svb", 1, ExperimentDisplayMode.StereoOnly);
            Assert.That(session.TrialIndexForLog, Is.EqualTo(-1));
            clock = 205f;
            session.EndTutorial("completed=1 step=Done mode=StereoOnly");
        }

        List<string[]> ops = ReadCsv(Path.Combine(dir, ExperimentLogWriter.OperationsFileName));
        string[] endRow = ops.Find(r => r[Column(ops[0], "action")] == "tutorial_end");
        Assert.That(endRow, Is.Not.Null);
        Assert.That(endRow[Column(ops[0], "trial_index")], Is.EqualTo("-1"));
        Assert.That(endRow[Column(ops[0], "detail")], Does.Contain("duration_sec=5"));
        Assert.That(endRow[Column(ops[0], "detail")], Does.Contain("before_block=1"));
    }

    // 試行が閉じられないままチュートリアルを始めても（逆も）、開始時刻を共有しているぶん先に閉じる。
    [Test]
    public void BeginTutorial_WhileTrialInProgress_ClosesTheTrialFirst()
    {
        // どちらも防御なので LogError を出す。それ自体は期待どおり。
        UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("閉じてから続けます"));
        UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("閉じてから続けます"));

        ExperimentSession session = NewSession(null);
        session.BeginTrial(0, "bundle_human.svb");
        session.BeginTutorial("bundle_tutorial.svb", 1, ExperimentDisplayMode.Monocular);

        Assert.That(session.TrialInProgress, Is.False);
        Assert.That(session.TutorialInProgress, Is.True);

        session.BeginTrial(1, "bundle_animal.svb");
        Assert.That(session.TutorialInProgress, Is.False);
        Assert.That(session.TrialInProgress, Is.True);
    }

    // 5 ファイルとも、全行の列数がヘッダと一致する（列を足したときのずれをここで捕まえる）。
    [Test]
    public void EveryRowHasTheSameColumnCountAsItsHeader()
    {
        using (ExperimentLogWriter writer = new ExperimentLogWriter(dir))
        {
            ExperimentSession session = NewSession(writer);
            session.BeginTrial(0, "bundle_human.svb");
            session.SetTrialStartState("abc", 123L, "sharedStorage", "VisionGraft.apk;2026-09-29T12:00:00;git:abc", true, false, 1.0f, false, 72f);
            ExperimentPerfAccumulator.Sample sample = new ExperimentPerfAccumulator.Sample
            {
                windowSeconds = 1.0f, frames = 72, fps = 72f, maxFrameMs = 20f, longFrames = 1,
                videoFramesAdvanced = 30, videoFrameSkips = 2, videoSeekJumps = 1, videoTimeAdvancedSec = 0.9,
                videoPlayingAtEnd = true, controllerMovedMeters = 0.3f, triggerFrames = 4, buttonFrames = 2,
            };
            session.RecordPerf(sample, 30);
            session.RecordHeadPose(new Vector3(0.1f, 1.6f, -0.2f), Quaternion.identity);
            session.RecordInteraction(7, "random_Static", "subject=human");
            session.RecordOperation("select_track", "track=7 how=画素 u=12 v=34");
            session.RecordOperation("weird", "a,b \"quoted\"");
            session.RecordVideoLoop();
            clock = 130f;
            session.EndTrial(false);
        }

        string[] files =
        {
            ExperimentLogWriter.TrialsFileName, ExperimentLogWriter.OperationsFileName, ExperimentLogWriter.HeadPoseFileName,
            ExperimentLogWriter.InteractionsFileName, ExperimentLogWriter.PerfFileName,
        };
        foreach (string file in files)
        {
            List<string[]> rows = ReadCsv(Path.Combine(dir, file));
            Assert.That(rows.Count, Is.GreaterThan(1), $"{file} に本文が無い");
            int columns = rows[0].Length;
            for (int i = 1; i < rows.Count; i++)
            {
                Assert.That(rows[i].Length, Is.EqualTo(columns), $"{file} の {i + 1} 行目の列数がヘッダと違う");
            }
        }

        List<string[]> trials = ReadCsv(Path.Combine(dir, ExperimentLogWriter.TrialsFileName));
        Assert.That(trials[0].Length, Is.EqualTo(25));
        Assert.That(trials[1][Column(trials[0], "video_played_sec")], Is.EqualTo("0.9"));
        Assert.That(trials[1][Column(trials[0], "loop_count")], Is.EqualTo("1"));
        Assert.That(ReadCsv(Path.Combine(dir, ExperimentLogWriter.PerfFileName))[0].Length, Is.EqualTo(19));
    }
}

public class ExperimentTrialParticipantDescribeTests
{
    // 被験者が見る表記には動画名も条件名も入らない。
    [Test]
    public void DescribeForParticipant_HidesVideoAndCondition()
    {
        ExperimentTrial trial = new ExperimentTrial
        {
            trialIndex = 2, blockIndex = 0, indexInBlock = 2, video = ExperimentVideo.Car, mode = ExperimentDisplayMode.Monocular,
        };

        string text = trial.DescribeForParticipant(ExperimentPlan.TrialCount);
        Assert.That(text, Is.EqualTo("3 / 9 本目の動画"));
        Assert.That(text, Does.Not.Contain("Car"));
        Assert.That(text, Does.Not.Contain("Monocular"));
        Assert.That(trial.Describe(ExperimentPlan.TrialCount), Is.EqualTo("3/9  Car / Monocular"), "ログ用はそのまま");
    }
}

public class ExperimentTutorialAuditTests
{
    // 置換ありの見出しは 3 段階。実験者用のボタンで途中終了したときの記録には段階名が残る。
    [Test]
    public void ModelReplaced_TitleAndResultCarryTheStep()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        Assert.That(tutorial.Title, Is.EqualTo("練習 1/3"));
        Assert.That(tutorial.DescribeResult(), Is.EqualTo("completed=0 step=WatchMotion mode=ModelReplaced"));

        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.Title, Is.EqualTo("練習 2/3"));
        Assert.That(tutorial.DescribeResult(), Is.EqualTo("completed=0 step=ChangeModel mode=ModelReplaced"));
    }

    // motion_toggle の detail が無いときは ON 扱い（文面が OFF の案内にならない）。
    [Test]
    public void MotionToggleWithoutDetail_IsTreatedAsOn()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordOperation("motion_toggle", null);

        Assert.That(tutorial.Body, Does.Contain("自分から"));
        Assert.That(tutorial.Body, Does.Not.Contain("ON に戻して"));
    }

    // 全部非表示 → 1 体戻す、で文面が変わるたびに Changed が出る（パネルを作り直す合図）。
    [Test]
    public void AllModelsHidden_RaisesChangedWhenTheHintAppearsAndDisappears()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        int changed = 0;
        tutorial.Changed += () => changed++;

        tutorial.RecordOperation("model_assigned", "track=1 category=person prefab=01_Female");
        Assert.That(changed, Is.EqualTo(0), "表示が増えただけでは変わらない");

        tutorial.RecordOperation("change_model", "track=1 category=human index=-1 prefab=(none)");
        Assert.That(changed, Is.EqualTo(1), "0 体になったので案内に切り替わる");

        tutorial.RecordOperation("change_model", "track=1 category=human index=0 prefab=01_Female");
        Assert.That(changed, Is.EqualTo(2), "戻ったので元の文面へ");
    }
}
