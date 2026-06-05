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
            Vector3.zero,
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
            false,
            true,
            true));

        AssertVector(scale, Vector3.one * 1f);
    }

    [Test]
    public void ResolveDesiredLocalScaleUsesOtherProxyLargestAxisAndUserScale()
    {
        Vector3 scale = TrackModelPlacement.ResolveDesiredLocalScale(new TrackModelPlacement.ScaleRequest(
            Vector3.one,
            new Vector2(2f, 4f),
            new Vector3(-8f, 12f, 1f),
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
            true,
            true,
            false,
            false));

        AssertVector(scale, Vector3.one * 6f);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
