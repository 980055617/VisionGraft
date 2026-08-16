using NUnit.Framework;
using UnityEngine;

public class TrackModelPlacementTests
{
    [Test]
    public void ResolveTargetHeightMetersKeepsExistingProjectionFormula()
    {
        float height = TrackModelPlacement.ResolveTargetHeightMeters(
            270f,
            1080,
            2f,
            1.5f);

        Assert.That(height, Is.EqualTo(2f * 270f / 1080f * (2f / 1.5f)).Within(0.0001f));
    }

    // Human / Animal / Else のどれでも bbox の高さだけでスケールが決まる。
    // source/other_object_proxies.json の proxy3d.size は units="same_as_depth_npz" で
    // メートルではないため、スケール決定には使わない。
    [Test]
    public void ResolveDesiredLocalScaleUsesBBoxHeightAxis()
    {
        // bboxWorldH = (2*1000/1000)*(2/1) = 4 → scaleH = 4/2 = 2
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            2f,
            1f,
            0f,
            0f,
            1000f,
            2f,
            1f,
            1000,
            true));

        AssertVector(scale, Vector3.one * 2f);
    }

    // 回帰: Animal は以前 Min(scaleW, scaleH) で bbox 幅にも収めていたため、
    // 縦長の bbox（動物が正面を向いているカット）で高さが bbox の 1/3 まで潰れていた。
    // bundle_animal.svb の shot 5 は bbox W/H = 0.627、22_Elk1.0 は AABB W/H = 1.836 なので
    // 旧実装では 0.627/1.836 = 0.34 倍になっていた。幅は今は一切見ないので、
    // 縦長 bbox でも高さは bbox どおりになる。
    [Test]
    public void ResolveDesiredLocalScaleKeepsBBoxHeightForTallNarrowBBox()
    {
        // モデル高さ 2m、bbox 高さは上のケースと同じ 4m 相当。幅がいくら狭くても結果は変わらない。
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            0f,
            1f,
            2f,
            4f,
            1000f,
            2f,
            1f,
            1000,
            true));

        AssertVector(scale, Vector3.one * 2f);
    }

    // Humanoid では ReplaceableModel が骨格由来の身長を返す。AABB 高さ（baseHeightMeters）より
    // そちらを優先しないと、髪・靴のぶん骨格が縮んで関節位置が映像とずれる。
    [Test]
    public void ResolveDesiredLocalScalePrefersModelHeightOverBaseHeight()
    {
        // bboxWorldH = 4。modelHeightMeters = 4 なので scaleH = 1（AABB 高さ 8 を使うと 0.5 になる）
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            8f,
            1f,
            4f,
            0f,
            1000f,
            2f,
            1f,
            1000,
            true));

        AssertVector(scale, Vector3.one * 1f);
    }

    [Test]
    public void ResolveDesiredLocalScaleFallsBackToModelHeightRatioWithoutFocalLengths()
    {
        // targetUniform = (targetHeight / modelHeight) * userScale = (2/10)*1.5 = 0.3
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            4f,
            1.5f,
            10f,
            2f,
            0f,
            0f,
            0f,
            0,
            false));

        AssertVector(scale, Vector3.one * 0.3f);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
