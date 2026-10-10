using System.Collections.Generic;
using NUnit.Framework;

// 操作チュートリアルの段階送り（Assets/Scripts/Experiment/ExperimentTutorial.cs）。
// ブロックの表示条件ごとに段階の列が違う（2026-09-11 の設計）。
public class ExperimentTutorialTests
{
    private sealed class RecordingSink : IExperimentLogSink
    {
        public readonly List<string> operations = new List<string>();
        public int interactions;
        public int loops;

        public void RecordOperation(string action, string detail)
        {
            operations.Add(detail == null ? action : $"{action}:{detail}");
        }

        public void RecordInteraction(uint trackId, string kind, string detail)
        {
            interactions++;
        }

        public void RecordVideoLoop()
        {
            loops++;
        }
    }

    [Test]
    public void Monocular_SequenceIsButtonPauseResumeSeek()
    {
        ExperimentTutorial.Step[] steps = ExperimentTutorial.ResolveSequence(ExperimentDisplayMode.Monocular);

        Assert.That(steps, Is.EqualTo(new[]
        {
            ExperimentTutorial.Step.PressButton,
            ExperimentTutorial.Step.PausePlayback,
            ExperimentTutorial.Step.ResumePlayback,
            ExperimentTutorial.Step.Seek,
            ExperimentTutorial.Step.Done,
        }));
    }

    [Test]
    public void StereoOnly_HasNoOperationSteps()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.StereoOnly);

        Assert.That(tutorial.StepCount, Is.EqualTo(0));
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.Title, Is.EqualTo("次の動画の説明"));
        Assert.That(tutorial.Body, Does.Contain("立体"));
    }

    // 置換ありは「自分から動く例を見る → モデルを替える → Motion を切り替える → 掴んで回す」の 4 段階。
    // WatchMotion が先頭なのは、後ろに置くと（練習の発火間隔は 3〜6 秒）モデルを選んでいる最中に
    // 先に発火して説明が一度も出ないため（2026-09-25 の監査 F1）。掴んで回すは 2026-10-01 に追加し、
    // Motion を切った後に置く（掴もうとしている最中に発火して邪魔しないため）。
    [Test]
    public void ModelReplaced_SequenceIsWatchMotionChangeModelToggleMotionGrabRotate()
    {
        ExperimentTutorial.Step[] steps = ExperimentTutorial.ResolveSequence(ExperimentDisplayMode.ModelReplaced);

        Assert.That(steps, Is.EqualTo(new[]
        {
            ExperimentTutorial.Step.WatchMotion,
            ExperimentTutorial.Step.ChangeModel,
            ExperimentTutorial.Step.ToggleMotion,
            ExperimentTutorial.Step.GrabRotate,
            ExperimentTutorial.Step.Done,
        }));

        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        Assert.That(tutorial.StepCount, Is.EqualTo(4));
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));
        Assert.That(tutorial.Body, Does.Contain("自分から"));

        // モデルが自分から動き始めた（Random イベント）。動いている間は文面だけ変え、段階はまだ進めない。
        tutorial.RecordInteraction(1, "random_Static", "subject=human");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));
        Assert.That(tutorial.Body, Does.Contain("動いています"));

        // 動きが終わったところで「例を見せた」。判定は motion_end（動画の一時停止の解除ではない。
        // 被験者が A で戻したときの video_pause_end でも来てしまうため。2026-09-29 の 4 回目の監査）。
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));
        Assert.That(tutorial.Body, Does.Contain("Model"));

        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ToggleMotion));
        Assert.That(tutorial.Body, Does.Contain("Settings"));

        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));
        Assert.That(tutorial.Body, Does.Contain("トリガー"));

        // 掴んで回して放した（GrabRotate.partial.cs の記録と同じ形）。
        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=35 pitch=0 roll=0 frame=120");
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.DescribeResult(), Is.EqualTo("completed=1 step=Done mode=ModelReplaced"));
    }

    // 掴んで回すは**その段階を表示している間の、掴みによる回転だけ**数える。先回りで済ませると説明が
    // 一度も出ない。Model パネルのボタンで向きを戻した記録（op=grab 以外）でも進めない。
    [Test]
    public void ModelReplaced_GrabRotate_CountsOnlyAGrabWhileTheStepIsShown()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        // 1 段階目の最中にうっかり掴んで回しても、4 段階目は済ませない。
        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=10 pitch=0 roll=0 frame=30");
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));

        // 掴み以外の向きの変更では進まない。
        tutorial.RecordOperation("change_rotation", "track=1 yaw=0 op=reset");
        tutorial.RecordOperation("change_rotation", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));

        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=40 pitch=5 roll=0 frame=150");
        Assert.That(tutorial.IsDone, Is.True);
    }

    // 4/4 は左右（yaw）に回したときだけ数える。体は yaw にしか付いて回らないので、手首をひねる（roll）だけでは済ませない
    // （2026-10-10）。dyaw の無い記録（10/09 以前の形）は今までどおり数える。
    [Test]
    public void ModelReplaced_GrabRotate_NeedsAHorizontalTurn()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));
        Assert.That(tutorial.Body, Does.Contain("左右に回して"));

        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=2 pitch=0 roll=40 frame=120 dyaw=2");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate), "ひねっただけ（yaw 2°）では済まない");

        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=-33.5 pitch=0 roll=0 frame=150 dyaw=-35.5");
        Assert.That(tutorial.IsDone, Is.True);
    }

    // 2/4 は「表示しない」では済ませない。3/4 は表示中の切り替えだけ数える（2026-10-10）。
    [Test]
    public void ModelReplaced_HideDoesNotCountAsChange_AndToggleCountsOnlyWhileShown()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordOperation("model_assigned", "track=1 category=person prefab=01_Female");
        tutorial.RecordOperation("model_assigned", "track=2 category=animal prefab=36_LabradorDog");
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));

        // 2/4 の最中に Motion を切り替えても 3/4 は済ませない（説明がまだ出ていない）。
        tutorial.RecordOperation("motion_toggle", "value=0");
        tutorial.RecordOperation("motion_toggle", "value=1");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));

        // 1 体を「表示しない」にしても 2/4 は済まない。
        tutorial.RecordOperation("change_model", "track=1 category=person index=-1 prefab=(none)");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));

        tutorial.RecordOperation("change_model", "track=2 category=animal index=3 prefab=37_Lioness");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ToggleMotion));

        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));
    }

    // 掴む段階でモデルを全部「表示しない」にすると掴む対象が無い。戻し方を出し、戻したら元の文面へ。
    [Test]
    public void ModelReplaced_GrabRotate_AllModelsHidden_TellsHowToShowOneAgain()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordOperation("model_assigned", "track=1 category=person prefab=01_Female");
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        tutorial.RecordOperation("change_model", "track=1 category=human index=2 prefab=02_Female");
        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));

        int changed = 0;
        tutorial.Changed += () => changed++;

        tutorial.RecordOperation("change_model", "track=1 category=human index=-1 prefab=(none)");
        Assert.That(tutorial.Body, Does.Contain("表示しない"));
        Assert.That(changed, Is.EqualTo(1), "文面が変わるのでパネルを作り直す");

        tutorial.RecordOperation("change_model", "track=1 category=human index=0 prefab=01_Female");
        Assert.That(tutorial.Body, Does.Contain("トリガー"));
        Assert.That(changed, Is.EqualTo(2));
    }

    // Motion がどういう機能か（自分から動く・OFF なら動画どおりに動くだけ）を練習の文面で説明する。
    // 終わりの画面は、置換ありでできること 3 つ（替える・回す・切り替える）をまとめて言う（2026-10-01）。
    [Test]
    public void ModelReplaced_ToggleMotionAndDone_ExplainWhatEachFunctionDoes()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        tutorial.RecordOperation("change_model", "track=1 prefab=x");

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ToggleMotion));
        Assert.That(tutorial.Body, Does.Contain("自分から動く"));
        Assert.That(tutorial.Body, Does.Contain("OFF にすると"));
        Assert.That(tutorial.Body, Does.Contain("Screen Dist"));

        tutorial.RecordOperation("motion_toggle", "value=0");
        tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=35 pitch=0 roll=0 frame=120");
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.Body, Does.Contain("掴んで回す"));
        Assert.That(tutorial.Body, Does.Contain("Motion"));
    }

    // **その段階を表示している間の発火だけ数える。** 先回りで済ませると説明が出ないまま次へ進む。
    [Test]
    public void ModelReplaced_MotionBeforeTheStepIsShown_DoesNotCount()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        // まず 1 段階目（WatchMotion）を済ませ、2 段階目（ChangeModel）に居る状態を作る。
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));

        // ChangeModel の最中にもう一度発火しても、後ろの段階を勝手に済ませない。
        tutorial.RecordInteraction(1, "random_Dynamic", null);
        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ToggleMotion));
        Assert.That(tutorial.IsDone, Is.False);
    }

    // Motion の切り替えは**例を見た後**だけ数える。戻し方の案内（ON に戻す）で 3 段階目を
    // 消費してしまうと、切り替えの練習が一度も出ない（同監査 F3）。
    [Test]
    public void ModelReplaced_MotionTurnedOffBeforeTheExample_TellsHowToTurnItBackOnWithoutConsumingTheStep()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        int changed = 0;
        tutorial.Changed += () => changed++;

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));

        changed = 0;
        tutorial.RecordOperation("motion_toggle", "value=0");

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));
        Assert.That(tutorial.Body, Does.Contain("ON に戻して"));
        Assert.That(changed, Is.EqualTo(1), "文面が変わるのでパネルを作り直す");

        // ON に戻すと元の待ちの文面へ。ここまでの切り替えは 3 段階目には数えない。
        tutorial.RecordOperation("motion_toggle", "value=1");
        Assert.That(tutorial.Body, Does.Contain("自分から"));

        tutorial.RecordInteraction(1, "random_Dynamic", "subject=human");
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ToggleMotion), "切り替えの練習は残っている");

        tutorial.RecordOperation("motion_toggle", "value=0");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate), "切り替えの段階が済んで、掴んで回すへ");
        Assert.That(tutorial.IsDone, Is.False);
    }

    // モデルを全部「表示しない」にすると動く対象が無くなり、待っても永久に進まなかった（同監査 D1）。
    // 文面で戻し方を出す。
    [Test]
    public void ModelReplaced_AllModelsHidden_TellsHowToShowOneAgain()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        tutorial.RecordOperation("model_assigned", "track=1 category=person prefab=01_Female");
        tutorial.RecordOperation("model_assigned", "track=2 category=animal prefab=36_LabradorDog");
        Assert.That(tutorial.Body, Does.Contain("自分から"));

        tutorial.RecordOperation("change_model", "track=1 category=person index=-1 prefab=(none)");
        Assert.That(tutorial.Body, Does.Contain("自分から"), "1 体でも残っていれば待てばよい");

        tutorial.RecordOperation("change_model", "track=2 category=animal index=-1 prefab=(none)");
        Assert.That(tutorial.Body, Does.Contain("表示しない"));
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));

        // 1 体戻せば待ちの文面へ戻る。
        tutorial.RecordOperation("change_model", "track=2 category=animal index=3 prefab=36_LabradorDog");
        Assert.That(tutorial.Body, Does.Contain("自分から"));
    }

    [Test]
    public void Monocular_StartsAtPressButton()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));
        Assert.That(tutorial.StepCount, Is.EqualTo(4));
        Assert.That(tutorial.Title, Is.EqualTo("練習 1/4"));
        Assert.That(tutorial.Body, Does.Contain("トリガー"));
    }

    [Test]
    public void CompleteCurrentStep_AdvancesFromPressButtonOnly()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));

        // 2 段階目以降は操作ログで進む。ボタンでは進まない（段階を個別に飛ばす手段は無い。
        // 練習全体を終える実験者用のボタンは ExperimentController 側にあり、この状態機械には無い）。
        tutorial.CompleteCurrentStep();
        tutorial.RecordOperation("change_scale", "track=1 scale=1.2");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));
        Assert.That(tutorial.DescribeResult(), Is.EqualTo("completed=0 step=PausePlayback mode=Monocular"));
    }

    [Test]
    public void Monocular_PauseResumeSeek_ReachesDone()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);
        tutorial.CompleteCurrentStep();

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ResumePlayback));
        Assert.That(tutorial.Title, Is.EqualTo("練習 3/4"));

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.Seek));
        Assert.That(tutorial.Body, Does.Contain("つまみ"));

        tutorial.RecordOperation("seek", "0.42");
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.Title, Is.EqualTo("練習 終了"));
        Assert.That(tutorial.Body, Does.Contain("視聴を終了"));
    }

    // 被験者が A で止めたままだと Random イベントは出ない（動画が動いている間しか発火しない）。
    // 待たせずに戻し方を出し、戻したら元の文面へ（2026-09-29 の 3 回目の監査）。
    [Test]
    public void ModelReplaced_PausedByParticipant_TellsToResume()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        int changed = 0;
        tutorial.Changed += () => changed++;

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));
        Assert.That(tutorial.Body, Does.Contain("A ボタンを押して再開"));
        Assert.That(changed, Is.EqualTo(1));

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.Body, Does.Contain("自分から"));
        Assert.That(changed, Is.EqualTo(2));

        // 自動再開（パネルを閉じた・モーションが終わった）でも戻る。
        tutorial.RecordOperation("pause", null);
        tutorial.RecordOperation("resume_auto", "cause=model_panel_closed");
        Assert.That(tutorial.Body, Does.Contain("自分から"));
        Assert.That(changed, Is.EqualTo(4));

        // 同じ状態の繰り返しでは作り直さない。
        tutorial.RecordOperation("resume", null);
        Assert.That(changed, Is.EqualTo(4));
    }

    // 途中で止まった動き（Motion OFF・モデル非表示・差し替え・track が居なくなった）は「例を見た」に数えない。
    // 文面は待ちに戻る（2026-09-29 の 4 回目の監査）。
    [Test]
    public void ModelReplaced_MotionStoppedEarly_DoesNotCountAsTheExample()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        int changed = 0;
        tutorial.Changed += () => changed++;

        tutorial.RecordInteraction(1, "random_Static", null);
        Assert.That(tutorial.Body, Does.Contain("動いています"));
        Assert.That(changed, Is.EqualTo(1));

        tutorial.RecordInteraction(1, "motion_end", "reason=stopped");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion), "見ていないので済みにしない");
        Assert.That(tutorial.Body, Does.Contain("自分から"), "待ちの文面に戻る");
        Assert.That(changed, Is.EqualTo(2));

        // もう一度動いて終われば済む。
        tutorial.RecordInteraction(1, "random_Dynamic", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));
    }

    // 動きの開始（random_*）を受けていなければ motion_end だけでは進まない。
    // 被験者が A で戻したときの video_pause_end でも進まない。
    [Test]
    public void ModelReplaced_EndWithoutTheMotionStarting_DoesNotCount()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));

        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "video_pause_end", "paused_sec=1 released_by=manual_resume");
        Assert.That(
            tutorial.CurrentStep,
            Is.EqualTo(ExperimentTutorial.Step.WatchMotion),
            "被験者が A で戻しただけ。モデルはまだ動いている");
        Assert.That(tutorial.Body, Does.Contain("動いています"));
    }

    // 掴み・パネルで止まった（pause_auto）ときも「A ボタンで再開して」の案内を出す。
    [Test]
    public void ModelReplaced_PausedByPanelOrGrab_TellsToResume()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        tutorial.RecordOperation("pause_auto", "cause=grab");
        Assert.That(tutorial.Body, Does.Contain("A ボタンを押して再開"));

        tutorial.RecordOperation("resume_auto", "cause=grab_end");
        Assert.That(tutorial.Body, Does.Contain("自分から"));
    }

    [Test]
    public void ResumeWithoutPause_IsIgnored()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);
        tutorial.CompleteCurrentStep();

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ResumePlayback));
    }

    [Test]
    public void OperationsDoneEarly_AreRemembered()
    {
        // 「次へ」を押す前に止めて、再開して、シークした参加者にもう一度やらせない。
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);

        tutorial.RecordOperation("seek", "0.1");
        tutorial.RecordOperation("pause", null);
        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.IsDone, Is.True);
    }

    [Test]
    public void OperationsOutsideTheSequence_DoNotAdvance()
    {
        // C（置換あり）のチュートリアル中に止めたりシークしても段階は変わらない。
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        tutorial.RecordOperation("pause", null);
        tutorial.RecordOperation("resume", null);
        tutorial.RecordOperation("seek", "0.5");
        tutorial.RecordVideoLoop();

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.WatchMotion));

        // A（単眼）の練習中にモデルが動いたり Motion を触っても、列に無いので変わらない。
        ExperimentTutorial monocular = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);
        monocular.RecordInteraction(1, "random_Static", null);
        monocular.RecordOperation("motion_toggle", "value=0");

        Assert.That(monocular.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));
    }

    [Test]
    public void ForwardsEverythingToInnerSink()
    {
        RecordingSink sink = new RecordingSink();
        ExperimentTutorial tutorial = new ExperimentTutorial(sink, ExperimentDisplayMode.Monocular);

        tutorial.RecordOperation("pause", null);
        tutorial.RecordOperation("change_model", "track=2");
        tutorial.RecordInteraction(3, "system_frameout", "frame=10");
        tutorial.RecordVideoLoop();

        Assert.That(sink.operations, Is.EqualTo(new[] { "pause", "change_model:track=2" }));
        Assert.That(sink.interactions, Is.EqualTo(1));
        Assert.That(sink.loops, Is.EqualTo(1));
    }

    [Test]
    public void Changed_FiresOnlyWhenTheVisibleStepMoves()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);
        int changed = 0;
        tutorial.Changed += () => changed++;

        // 先回りの操作は見えている段階を変えないので通知しない。
        tutorial.RecordOperation("pause", null);
        Assert.That(changed, Is.EqualTo(0));

        // 「次へ」で PressButton → ResumePlayback（pause 済みなので 1 段飛ぶ）。
        tutorial.CompleteCurrentStep();
        Assert.That(changed, Is.EqualTo(1));
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ResumePlayback));

        tutorial.RecordOperation("resume", null);
        tutorial.RecordOperation("seek", "0.3");
        Assert.That(changed, Is.EqualTo(3));
    }
}

public class ExperimentTutorialHandoffTests
{
    [SetUp]
    public void SetUp()
    {
        HomeLaunchHandoff.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        HomeLaunchHandoff.Clear();
    }

    [Test]
    public void TutorialBundle_IsDistinctFromExperimentVideos()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();
        string tutorial = catalog.Resolve(ExperimentVideo.Tutorial);

        Assert.That(tutorial, Is.EqualTo(ExperimentBundleCatalog.DefaultTutorialBundleFileName));
        Assert.That(tutorial, Is.Not.EqualTo(catalog.Resolve(ExperimentVideo.Human)));
        Assert.That(tutorial, Is.Not.EqualTo(catalog.Resolve(ExperimentVideo.Animal)));
        Assert.That(tutorial, Is.Not.EqualTo(catalog.Resolve(ExperimentVideo.Car)));
    }

    // チュートリアルは次のブロックと同じ表示条件で再生する。単眼なら normal mode + 単眼。
    [Test]
    public void TutorialRequest_FollowsTheBlockMode()
    {
        ExperimentTrialRequest mono = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.Monocular, -1, ExperimentVideo.Tutorial,
            null, "36_LabradorDog", true);
        ExperimentTrialRequest replaced = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial,
            null, "36_LabradorDog", true);

        Assert.That(mono.StartInNormalMode, Is.True);
        Assert.That(mono.StartMonocular, Is.True);
        Assert.That(replaced.StartInNormalMode, Is.False);
        Assert.That(replaced.StartMonocular, Is.False);
        Assert.That(replaced.trialIndex, Is.EqualTo(-1));
        Assert.That(replaced.video, Is.EqualTo(ExperimentVideo.Tutorial));
        Assert.That(replaced.preferredAnimalModelName, Is.EqualTo("36_LabradorDog"));
        Assert.That(replaced.skipTrackCustomizationRestore, Is.True);
    }

    [Test]
    public void TrialRequest_HasNoFilterByDefault()
    {
        // 試行（本番）は絞り込みも既定モデルの差し替えもしない。
        ExperimentTrialRequest request = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 0, ExperimentVideo.Human);

        Assert.That(request.onlyCategory, Is.Null);
        Assert.That(request.preferredAnimalModelName, Is.Null);
        Assert.That(request.skipTrackCustomizationRestore, Is.False);
    }

    [Test]
    public void ExperimentPlan_DoesNotScheduleTheTutorialVideo()
    {
        for (int pattern = ExperimentPlan.MinVideoOrderPattern; pattern <= ExperimentPlan.MaxVideoOrderPattern; pattern++)
        {
            foreach (ExperimentTrial trial in ExperimentPlan.BuildTrials(ExperimentGroup.A, pattern))
            {
                Assert.That(trial.video, Is.Not.EqualTo(ExperimentVideo.Tutorial));
            }
        }
    }

    [Test]
    public void HomeLaunchHandoff_PickerRequestIsSingleUse()
    {
        Assert.That(HomeLaunchHandoff.ConsumeShowBundlePicker(), Is.False);

        HomeLaunchHandoff.RequestBundlePicker();
        Assert.That(HomeLaunchHandoff.PendingShowBundlePicker, Is.True);
        Assert.That(HomeLaunchHandoff.ConsumeShowBundlePicker(), Is.True);
        Assert.That(HomeLaunchHandoff.ConsumeShowBundlePicker(), Is.False);
    }
}
