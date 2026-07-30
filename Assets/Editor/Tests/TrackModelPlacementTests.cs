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

    [Test]
    public void ResolveDesiredLocalScaleUsesAnimalMinimumBBoxAxis()
    {
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            new Vector2(2f, 4f),
            1f,
            0f,
            0f,
            500f,
            1000f,
            2f,
            1f,
            1f,
            1000,
            1000,
            true,
            true));

        AssertVector(scale, Vector3.one * 1f);
    }

    // Else（および Human）は bbox の高さ基準。source/other_object_proxies.json の proxy3d.size は
    // units="same_as_depth_npz" でメートルではないため、スケール決定には使わない。
    [Test]
    public void ResolveDesiredLocalScaleUsesBBoxHeightAxisForNonAnimal()
    {
        // bboxWorldW = (2*500/1000)*(2/1) = 2 → scaleW = 2/4 = 0.5
        // bboxWorldH = (2*1000/1000)*(2/1) = 4 → scaleH = 4/2 = 2
        // 非 Animal は scaleH を採用するので、Animal 用の Min(scaleW, scaleH)=0.5 とは異なる
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            new Vector2(4f, 2f),
            1f,
            0f,
            0f,
            500f,
            1000f,
            2f,
            1f,
            1f,
            1000,
            1000,
            false,
            true));

        AssertVector(scale, Vector3.one * 2f);
    }

    [Test]
    public void ResolveDesiredLocalScaleFallsBackToModelHeightRatioWithoutFocalLengths()
    {
        // targetUniform = (targetHeight / modelHeight) * userScale = (2/10)*1.5 = 0.3
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            new Vector2(2f, 4f),
            1.5f,
            10f,
            2f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0,
            0,
            false,
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
