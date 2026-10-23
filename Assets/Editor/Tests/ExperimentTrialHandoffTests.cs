using NUnit.Framework;

public class ExperimentBundleCatalogTests
{
    // 2026-10-22: 動画は 30 秒 × 6 本。2026-10-23 に届いた 4 本を Video1〜4 に（名前の順）、Video5・6 は作成中の仮名。
    [Test]
    public void Resolve_DefaultsAreTheSixVideoSlots()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();

        Assert.That(catalog.Resolve(ExperimentVideo.Video1), Is.EqualTo("bundle_balloon.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video2), Is.EqualTo("bundle_cat.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video3), Is.EqualTo("bundle_soccer.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video4), Is.EqualTo("bundle_tv.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video6), Is.EqualTo("bundle_video6.svb"));
        Assert.That(catalog.videoBundleFileNames, Is.EqualTo(ExperimentBundleCatalog.DefaultVideoBundleFileNames));
        Assert.That(catalog.videoBundleFileNames, Is.Not.SameAs(ExperimentBundleCatalog.DefaultVideoBundleFileNames),
            "インスタンスごとの配列（Inspector で書き換えても既定の表を壊さない）");
    }

    [Test]
    public void Resolve_HonoursInspectorOverride()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();
        catalog.videoBundleFileNames[0] = "custom_first.svb";

        Assert.That(catalog.Resolve(ExperimentVideo.Video1), Is.EqualTo("custom_first.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video2), Is.EqualTo("bundle_cat.svb"));
    }

    // Inspector で空欄にされた・配列を短くされた場合に空文字を返すと bundle 読み込みが謎の失敗をするので、
    // 既定名にフォールバックする。
    [Test]
    public void Resolve_BlankOrMissingOverrideFallsBackToDefault()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog
        {
            videoBundleFileNames = new[] { string.Empty, "custom_second.svb" },
        };

        Assert.That(catalog.Resolve(ExperimentVideo.Video1), Is.EqualTo("bundle_balloon.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video2), Is.EqualTo("custom_second.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Video5), Is.EqualTo("bundle_video5.svb"));

        catalog.videoBundleFileNames = null;
        Assert.That(catalog.Resolve(ExperimentVideo.Video3), Is.EqualTo("bundle_soccer.svb"));
    }

    [Test]
    public void Resolve_EveryVideoHasADistinctBundle()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();
        System.Collections.Generic.HashSet<string> seen = new System.Collections.Generic.HashSet<string>();

        foreach (ExperimentVideo video in ExperimentPlan.ResolveVideoOrder(1))
        {
            Assert.That(seen.Add(catalog.Resolve(video)), Is.True, video.ToString());
        }

        Assert.That(seen, Has.No.Member(catalog.Resolve(ExperimentVideo.Tutorial)));
    }
}

public class ExperimentTrialHandoffTests
{
    [SetUp]
    public void SetUp()
    {
        ExperimentTrialHandoff.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ExperimentTrialHandoff.Clear();
    }

    [Test]
    public void Consume_WithoutPending_ReturnsNull()
    {
        Assert.That(ExperimentTrialHandoff.Consume(), Is.Null);
    }

    [Test]
    public void Consume_ReturnsPendingRequest()
    {
        ExperimentTrialRequest request = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 0, ExperimentVideo.Video1);
        ExperimentTrialHandoff.SetPending(request);

        Assert.That(ExperimentTrialHandoff.Consume(), Is.SameAs(request));
    }

    // 2 度目の Consume が null になること。ここが残ると、実験終了後に手動で
    // 試行シーンを開いたときにも古い条件が適用されてしまう。
    [Test]
    public void Consume_IsSingleUse()
    {
        ExperimentTrialHandoff.SetPending(new ExperimentTrialRequest(
            "bundle_animal.svb", ExperimentDisplayMode.ModelReplaced, 1, ExperimentVideo.Video2));

        Assert.That(ExperimentTrialHandoff.Consume(), Is.Not.Null);
        Assert.That(ExperimentTrialHandoff.Consume(), Is.Null);
    }

    // StereoOnly と Monocular が normal mode（除去前動画）で再生されること。
    // ここが逆になると対照条件が「穴の空いた映像」になり実験が成立しない。
    // 単眼はさらに左目映像を両目に出す（StartMonocular）。
    [Test]
    public void StartInNormalMode_IsTrueForStereoOnlyAndMonocular()
    {
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Video1);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Video1);
        ExperimentTrialRequest monocular = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Video1);

        Assert.That(stereoOnly.StartInNormalMode, Is.True);
        Assert.That(stereoOnly.StartMonocular, Is.False);
        Assert.That(modelReplaced.StartInNormalMode, Is.False);
        Assert.That(modelReplaced.StartMonocular, Is.False);
        Assert.That(monocular.StartInNormalMode, Is.True);
        Assert.That(monocular.StartMonocular, Is.True);
    }

    // インタラクティブモーションは置換あり条件だけ ON（2026-09-25、論文側の依頼 §1）。
    // ここが崩れると単眼・ステレオ条件で（モデルが無いのに）モーションの状態機械が走るか、
    // 第 3 条件でモーション無しのデータが取れてしまう。
    [Test]
    public void InteractiveMotionEnabled_IsTrueOnlyForModelReplaced()
    {
        ExperimentTrialRequest monocular = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Video1);
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Video1);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Video1);
        ExperimentTrialRequest tutorialReplaced = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);

        Assert.That(monocular.InteractiveMotionEnabled, Is.False);
        Assert.That(stereoOnly.InteractiveMotionEnabled, Is.False);
        Assert.That(modelReplaced.InteractiveMotionEnabled, Is.True);
        Assert.That(tutorialReplaced.InteractiveMotionEnabled, Is.True);
    }

    // 置換あり条件だけ被験者が Settings の Motion で切り替えられる（2026-09-25 のユーザー指示で
    // 同日午前の「全条件で固定」を取り消した）。単眼・ステレオはモデルが出ないので固定のまま。
    [Test]
    public void LockInteractiveMotion_IsFalseOnlyForModelReplaced()
    {
        ExperimentTrialRequest monocular = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Video1);
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Video1);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Video1);
        ExperimentTrialRequest tutorialReplaced = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);

        Assert.That(monocular.LockInteractiveMotion, Is.True);
        Assert.That(stereoOnly.LockInteractiveMotion, Is.True);
        Assert.That(modelReplaced.LockInteractiveMotion, Is.False);
        Assert.That(tutorialReplaced.LockInteractiveMotion, Is.False);
    }

    // 練習で必ず 1 回は自発的な動きを見せたいので、置換ありのチュートリアルだけ発火間隔を短くする。
    // 本番の試行（12 / 24 秒）には掛けない。
    [Test]
    public void TutorialInteractiveMotionInterval_AppliesOnlyToTheReplacedTutorial()
    {
        ExperimentTrialRequest tutorialReplaced = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);
        ExperimentTrialRequest tutorialMonocular = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.Monocular, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);
        ExperimentTrialRequest trialReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Video1);

        Assert.That(tutorialReplaced.UseTutorialInteractiveMotionInterval, Is.True);
        Assert.That(tutorialMonocular.UseTutorialInteractiveMotionInterval, Is.False);
        Assert.That(trialReplaced.UseTutorialInteractiveMotionInterval, Is.False);
        Assert.That(
            ExperimentTrialRequest.TutorialInteractiveMotionMinIntervalSeconds,
            Is.LessThan(ExperimentTrialRequest.TutorialInteractiveMotionMaxIntervalSeconds));
    }

    [Test]
    public void Request_KeepsTrialMetadata()
    {
        ExperimentTrialRequest request = new ExperimentTrialRequest(
            "bundle_car.svb", ExperimentDisplayMode.ModelReplaced, 4, ExperimentVideo.Video3);

        Assert.That(request.bundleFileName, Is.EqualTo("bundle_car.svb"));
        Assert.That(request.trialIndex, Is.EqualTo(4));
        Assert.That(request.video, Is.EqualTo(ExperimentVideo.Video3));
        Assert.That(request.mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
    }
}

public class ExperimentTrialDescribeTests
{
    [Test]
    public void Describe_ShowsOneBasedPositionAndCondition()
    {
        // 群 A・動画順 1 の 8 本目 = 2 ブロック目（StereoOnly）の 2 本目 = Video2。
        ExperimentTrial trial = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1)[7];

        string text = trial.Describe(ExperimentPlan.TrialCount);

        Assert.That(text, Is.EqualTo("8/18  Video2 / StereoOnly"));
    }
}
