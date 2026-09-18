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
        Assert.That(tutorial.Title, Is.EqualTo("このブロックの説明"));
        Assert.That(tutorial.Body, Does.Contain("立体"));
    }

    [Test]
    public void ModelReplaced_SequenceIsChangeModelOnly()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);

        Assert.That(tutorial.StepCount, Is.EqualTo(1));
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));
        Assert.That(tutorial.Body, Does.Contain("Model"));

        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.DescribeResult(), Is.EqualTo("completed=1 step=Done mode=ModelReplaced"));
    }

    [Test]
    public void Monocular_StartsAtPressButton()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));
        Assert.That(tutorial.StepCount, Is.EqualTo(4));
        Assert.That(tutorial.Title, Is.EqualTo("チュートリアル 1/4"));
        Assert.That(tutorial.Body, Does.Contain("トリガー"));
    }

    [Test]
    public void CompleteCurrentStep_AdvancesFromPressButtonOnly()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.Monocular);

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));

        // 2 段階目以降は操作ログで進む。ボタンでは進まない（段階を飛ばす手段は無い）。
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
        Assert.That(tutorial.Title, Is.EqualTo("チュートリアル 3/4"));

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.Seek));
        Assert.That(tutorial.Body, Does.Contain("つまみ"));

        tutorial.RecordOperation("seek", "0.42");
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.Title, Is.EqualTo("チュートリアル 終了"));
        Assert.That(tutorial.Body, Does.Contain("視聴を終了"));
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
        tutorial.RecordInteraction(1, "random_Static", null);

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));
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
