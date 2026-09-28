using NUnit.Framework;

public class ExperimentBundleCatalogTests
{
    [Test]
    public void Resolve_DefaultsMatchStreamingAssetsBundles()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();

        // 既定名は StreamingAssets に実在するファイルでなければならない。
        Assert.That(catalog.Resolve(ExperimentVideo.Human), Is.EqualTo("bundle_human.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Animal), Is.EqualTo("bundle_animal.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Car), Is.EqualTo("bundle_car.svb"));
    }

    [Test]
    public void Resolve_HonoursInspectorOverride()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog
        {
            humanBundleFileName = "custom_human.svb",
        };

        Assert.That(catalog.Resolve(ExperimentVideo.Human), Is.EqualTo("custom_human.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Animal), Is.EqualTo("bundle_animal.svb"));
    }

    // Inspector で空欄にされた場合に空文字を返すと bundle 読み込みが謎の失敗をするので、
    // 既定名にフォールバックする。
    [Test]
    public void Resolve_BlankOverrideFallsBackToDefault()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog
        {
            carBundleFileName = string.Empty,
        };

        Assert.That(catalog.Resolve(ExperimentVideo.Car), Is.EqualTo("bundle_car.svb"));
    }

    [Test]
    public void Resolve_EveryVideoHasADistinctBundle()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();

        string human = catalog.Resolve(ExperimentVideo.Human);
        string animal = catalog.Resolve(ExperimentVideo.Animal);
        string car = catalog.Resolve(ExperimentVideo.Car);

        Assert.That(human, Is.Not.EqualTo(animal));
        Assert.That(animal, Is.Not.EqualTo(car));
        Assert.That(human, Is.Not.EqualTo(car));
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
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 0, ExperimentVideo.Human);
        ExperimentTrialHandoff.SetPending(request);

        Assert.That(ExperimentTrialHandoff.Consume(), Is.SameAs(request));
    }

    // 2 度目の Consume が null になること。ここが残ると、実験終了後に手動で
    // 試行シーンを開いたときにも古い条件が適用されてしまう。
    [Test]
    public void Consume_IsSingleUse()
    {
        ExperimentTrialHandoff.SetPending(new ExperimentTrialRequest(
            "bundle_animal.svb", ExperimentDisplayMode.ModelReplaced, 1, ExperimentVideo.Animal));

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
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Human);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Human);
        ExperimentTrialRequest monocular = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Human);

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
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Human);
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Human);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Human);
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
            "bundle_human.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Human);
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 3, ExperimentVideo.Human);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Human);
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
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 6, ExperimentVideo.Human);

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
            "bundle_car.svb", ExperimentDisplayMode.ModelReplaced, 4, ExperimentVideo.Car);

        Assert.That(request.bundleFileName, Is.EqualTo("bundle_car.svb"));
        Assert.That(request.trialIndex, Is.EqualTo(4));
        Assert.That(request.video, Is.EqualTo(ExperimentVideo.Car));
        Assert.That(request.mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
    }
}

public class ExperimentTrialDescribeTests
{
    [Test]
    public void Describe_ShowsOneBasedPositionAndCondition()
    {
        ExperimentTrial trial = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1)[5];

        string text = trial.Describe(ExperimentPlan.TrialCount);

        Assert.That(text, Does.StartWith("6/9"));
        Assert.That(text, Does.Contain("Car"));
        Assert.That(text, Does.Contain("StereoOnly"));
    }
}
