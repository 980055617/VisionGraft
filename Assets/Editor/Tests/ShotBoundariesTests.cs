using NUnit.Framework;

public class ShotBoundariesTests
{
    // bundle_animal.svb の shots の先頭部分（実データ）
    private const string AnimalBundleJson =
        "{\"num_frames\":2120,\"shots\":[[0,258],[258,338],[338,427],[427,668]]}";

    [Test]
    public void FromManifestJson_ReadsShotStartFrames()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(AnimalBundleJson);

        Assert.That(shots.HasShots, Is.True);
        Assert.That(shots.Count, Is.EqualTo(4));
        Assert.That(shots.GetStartFrame(0), Is.EqualTo(0));
        Assert.That(shots.GetStartFrame(1), Is.EqualTo(258));
        Assert.That(shots.GetStartFrame(2), Is.EqualTo(338));
        Assert.That(shots.GetStartFrame(3), Is.EqualTo(427));
    }

    [Test]
    public void ResolveShotIndex_FrameInsideShot_ReturnsThatShot()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(AnimalBundleJson);

        Assert.That(shots.ResolveShotIndex(0), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(257), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(258), Is.EqualTo(1));
        Assert.That(shots.ResolveShotIndex(337), Is.EqualTo(1));
        Assert.That(shots.ResolveShotIndex(338), Is.EqualTo(2));
        Assert.That(shots.ResolveShotIndex(500), Is.EqualTo(3));
    }

    [Test]
    public void ResolveShotIndex_FrameBeyondLastShotStart_ReturnsLastShot()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(AnimalBundleJson);

        Assert.That(shots.ResolveShotIndex(99999), Is.EqualTo(3));
    }

    [Test]
    public void ResolveShotIndex_NegativeFrame_ReturnsFirstShot()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(AnimalBundleJson);

        Assert.That(shots.ResolveShotIndex(-1), Is.EqualTo(0));
    }

    // 先頭 shot が 0 始まりでない bundle でも、それより前のフレームは先頭 shot 扱いにする。
    [Test]
    public void ResolveShotIndex_FrameBeforeFirstShotStart_ReturnsFirstShot()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson("{\"shots\":[[10,20],[20,30]]}");

        Assert.That(shots.ResolveShotIndex(5), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(10), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(20), Is.EqualTo(1));
    }

    // 旧 bundle (bundle.svb / bundle_old.svb) には shots がない。全編 1 shot 扱いにして
    // 従来どおりスケールをロックしっぱなしにする。
    [Test]
    public void FromManifestJson_NoShotsKey_ReturnsEmptyAndAlwaysShotZero()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson("{\"num_frames\":289}");

        Assert.That(shots.HasShots, Is.False);
        Assert.That(shots.Count, Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(0), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(288), Is.EqualTo(0));
    }

    [Test]
    public void FromManifestJson_SingleShotCoveringWholeVideo_NeverChangesShotIndex()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson("{\"shots\":[[0,2167]]}");

        Assert.That(shots.Count, Is.EqualTo(1));
        Assert.That(shots.ResolveShotIndex(0), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(2166), Is.EqualTo(0));
    }

    [Test]
    public void FromManifestJson_EmptyOrReversedRanges_AreIgnored()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(
            "{\"shots\":[[0,100],[100,100],[300,200],[400,500]]}");

        Assert.That(shots.Count, Is.EqualTo(2));
        Assert.That(shots.GetStartFrame(0), Is.EqualTo(0));
        Assert.That(shots.GetStartFrame(1), Is.EqualTo(400));
    }

    [Test]
    public void FromManifestJson_UnsortedShots_AreSortedByStartFrame()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(
            "{\"shots\":[[338,427],[0,258],[258,338]]}");

        Assert.That(shots.GetStartFrame(0), Is.EqualTo(0));
        Assert.That(shots.GetStartFrame(1), Is.EqualTo(258));
        Assert.That(shots.GetStartFrame(2), Is.EqualTo(338));
        Assert.That(shots.ResolveShotIndex(300), Is.EqualTo(1));
    }

    [Test]
    public void FromManifestJson_MalformedEntries_AreIgnored()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(
            "{\"shots\":[[0,258],[258],\"nonsense\",[\"a\",\"b\"],null,[338,427]]}");

        Assert.That(shots.Count, Is.EqualTo(2));
        Assert.That(shots.GetStartFrame(0), Is.EqualTo(0));
        Assert.That(shots.GetStartFrame(1), Is.EqualTo(338));
    }

    [Test]
    public void FromManifestJson_ShotsNotAnArray_ReturnsEmpty()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson("{\"shots\":123}");

        Assert.That(shots.HasShots, Is.False);
    }

    [Test]
    public void FromManifestJson_InvalidJson_ReturnsEmpty()
    {
        Assert.That(ShotBoundaries.FromManifestJson(null).HasShots, Is.False);
        Assert.That(ShotBoundaries.FromManifestJson(string.Empty).HasShots, Is.False);
        Assert.That(ShotBoundaries.FromManifestJson("not json").HasShots, Is.False);
    }

    [Test]
    public void GetStartFrame_OutOfRangeIndex_ReturnsZero()
    {
        ShotBoundaries shots = ShotBoundaries.FromManifestJson(AnimalBundleJson);

        Assert.That(shots.GetStartFrame(-1), Is.EqualTo(0));
        Assert.That(shots.GetStartFrame(4), Is.EqualTo(0));
    }

    [Test]
    public void Empty_ResolvesEveryFrameToShotZero()
    {
        Assert.That(ShotBoundaries.Empty.HasShots, Is.False);
        Assert.That(ShotBoundaries.Empty.ResolveShotIndex(0), Is.EqualTo(0));
        Assert.That(ShotBoundaries.Empty.ResolveShotIndex(1234), Is.EqualTo(0));
    }
}
