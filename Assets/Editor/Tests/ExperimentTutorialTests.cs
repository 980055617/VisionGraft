using System.Collections.Generic;
using NUnit.Framework;

// 操作チュートリアルの段階送り（Assets/Scripts/Experiment/ExperimentTutorial.cs）。
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
    public void StartsAtPressButton()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));
        Assert.That(tutorial.IsDone, Is.False);
        Assert.That(tutorial.Title, Does.Contain("1/4"));
    }

    [Test]
    public void CompleteCurrentStep_AdvancesFromPressButtonOnly()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));

        // 2 段階目以降は操作ログで進む。ボタンでは進まない。
        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));
    }

    [Test]
    public void PauseThenResumeThenChangeModel_ReachesDone()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);
        tutorial.CompleteCurrentStep();

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ResumePlayback));

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel));

        tutorial.RecordOperation("change_model", "track=1 prefab=x");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.Done));
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.DescribeResult(), Does.StartWith("completed=1 skipped=0"));
    }

    [Test]
    public void ResumeWithoutPause_IsIgnored()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);
        tutorial.CompleteCurrentStep();

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ResumePlayback));
    }

    [Test]
    public void OperationsDoneEarly_AreRemembered()
    {
        // 「次へ」を押す前にモデルを変え、止めて、再開した参加者にもう一度やらせない。
        ExperimentTutorial tutorial = new ExperimentTutorial(null);

        tutorial.RecordOperation("change_model", null);
        tutorial.RecordOperation("pause", null);
        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PressButton));

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.Done));
    }

    [Test]
    public void UnrelatedOperations_DoNotAdvance()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);
        tutorial.CompleteCurrentStep();

        tutorial.RecordOperation("seek", "0.5");
        tutorial.RecordOperation("change_scale", "track=1 scale=1.2");
        tutorial.RecordVideoLoop();
        tutorial.RecordInteraction(1, "random_Static", null);

        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.PausePlayback));
    }

    [Test]
    public void Skip_AdvancesEachStepAndCountsIt()
    {
        RecordingSink sink = new RecordingSink();
        ExperimentTutorial tutorial = new ExperimentTutorial(sink);

        tutorial.SkipCurrentStep();
        tutorial.SkipCurrentStep();
        tutorial.SkipCurrentStep();
        tutorial.SkipCurrentStep();
        Assert.That(tutorial.IsDone, Is.True);
        Assert.That(tutorial.SkippedCount, Is.EqualTo(4));

        // Done で押しても何も起きない。
        tutorial.SkipCurrentStep();
        Assert.That(tutorial.SkippedCount, Is.EqualTo(4));

        Assert.That(sink.operations, Is.EqualTo(new[]
        {
            "tutorial_step_skipped:PressButton",
            "tutorial_step_skipped:PausePlayback",
            "tutorial_step_skipped:ResumePlayback",
            "tutorial_step_skipped:ChangeModel",
        }));
        Assert.That(tutorial.DescribeResult(), Does.StartWith("completed=1 skipped=4"));
    }

    [Test]
    public void ForwardsEverythingToInnerSink()
    {
        RecordingSink sink = new RecordingSink();
        ExperimentTutorial tutorial = new ExperimentTutorial(sink);

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
        ExperimentTutorial tutorial = new ExperimentTutorial(null);
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
        tutorial.RecordOperation("change_model", null);
        Assert.That(changed, Is.EqualTo(3));
    }

    [Test]
    public void BodyMentionsTheControlForEachStep()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null);
        Assert.That(tutorial.Body, Does.Contain("トリガー"));

        tutorial.CompleteCurrentStep();
        Assert.That(tutorial.Body, Does.Contain("A ボタン"));

        tutorial.RecordOperation("pause", null);
        Assert.That(tutorial.Body, Does.Contain("A ボタン"));

        tutorial.RecordOperation("resume", null);
        Assert.That(tutorial.Body, Does.Contain("Model"));

        tutorial.RecordOperation("change_model", null);
        Assert.That(tutorial.Title, Does.Contain("終了"));
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
        Assert.That(tutorial, Is.Not.EqualTo(catalog.Resolve(ExperimentVideo.Train)));
    }

    [Test]
    public void TutorialRequest_StartsInModelReplacedMode()
    {
        ExperimentTrialRequest request = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial);

        Assert.That(request.StartInNormalMode, Is.False);
        Assert.That(request.trialIndex, Is.EqualTo(-1));
        Assert.That(request.video, Is.EqualTo(ExperimentVideo.Tutorial));
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
    public void ConsumeTutorialOnly_IsSingleUse()
    {
        Assert.That(HomeLaunchHandoff.ConsumeTutorialOnly(), Is.False);

        HomeLaunchHandoff.RequestTutorialOnly();
        Assert.That(HomeLaunchHandoff.PendingTutorialOnly, Is.True);
        Assert.That(HomeLaunchHandoff.ConsumeTutorialOnly(), Is.True);
        Assert.That(HomeLaunchHandoff.ConsumeTutorialOnly(), Is.False);
    }

    [Test]
    public void Clear_DropsBothRequests()
    {
        HomeLaunchHandoff.RequestBundlePicker();
        HomeLaunchHandoff.RequestTutorialOnly();

        HomeLaunchHandoff.Clear();

        Assert.That(HomeLaunchHandoff.PendingShowBundlePicker, Is.False);
        Assert.That(HomeLaunchHandoff.PendingTutorialOnly, Is.False);
    }
}
