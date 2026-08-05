using NUnit.Framework;

public class ExperimentBundleCatalogTests
{
    [Test]
    public void Resolve_DefaultsMatchStreamingAssetsBundles()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();

        Assert.That(catalog.Resolve(ExperimentVideo.Human), Is.EqualTo("bundle_human.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Animal), Is.EqualTo("bundle_animal.svb"));
        Assert.That(catalog.Resolve(ExperimentVideo.Train), Is.EqualTo("bundle_train.svb"));
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
            trainBundleFileName = string.Empty,
        };

        Assert.That(catalog.Resolve(ExperimentVideo.Train), Is.EqualTo("bundle_train.svb"));
    }

    [Test]
    public void Resolve_EveryVideoHasADistinctBundle()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();

        string human = catalog.Resolve(ExperimentVideo.Human);
        string animal = catalog.Resolve(ExperimentVideo.Animal);
        string train = catalog.Resolve(ExperimentVideo.Train);

        Assert.That(human, Is.Not.EqualTo(animal));
        Assert.That(animal, Is.Not.EqualTo(train));
        Assert.That(human, Is.Not.EqualTo(train));
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

    // StereoOnly 条件が normal mode（除去前動画）で再生されること。
    // ここが逆になると対照条件が「穴の空いた映像」になり実験が成立しない。
    [Test]
    public void StartInNormalMode_IsTrueOnlyForStereoOnly()
    {
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.StereoOnly, 0, ExperimentVideo.Human);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_human.svb", ExperimentDisplayMode.ModelReplaced, 3, ExperimentVideo.Human);

        Assert.That(stereoOnly.StartInNormalMode, Is.True);
        Assert.That(modelReplaced.StartInNormalMode, Is.False);
    }

    [Test]
    public void Request_KeepsTrialMetadata()
    {
        ExperimentTrialRequest request = new ExperimentTrialRequest(
            "bundle_train.svb", ExperimentDisplayMode.ModelReplaced, 4, ExperimentVideo.Train);

        Assert.That(request.bundleFileName, Is.EqualTo("bundle_train.svb"));
        Assert.That(request.trialIndex, Is.EqualTo(4));
        Assert.That(request.video, Is.EqualTo(ExperimentVideo.Train));
        Assert.That(request.mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
    }
}

public class ExperimentTrialDescribeTests
{
    [Test]
    public void Describe_ShowsOneBasedPositionAndCondition()
    {
        ExperimentTrial trial = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1)[2];

        string text = trial.Describe(ExperimentPlan.TrialCount);

        Assert.That(text, Does.StartWith("3/6"));
        Assert.That(text, Does.Contain("Train"));
        Assert.That(text, Does.Contain("StereoOnly"));
    }
}
