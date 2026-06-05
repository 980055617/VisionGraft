using NUnit.Framework;
using UnityEngine;

public class PinholePlacementSpaceTests
{
    [Test]
    public void TryResolveProjectionIntrinsicsKeepsExistingFovFallback()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 1920,
            eye_h = 1080
        };

        bool ok = PinholePlacementSpace.TryResolveProjectionIntrinsics(
            manifest,
            true,
            90f,
            out float fx,
            out float fy,
            out float cxPixels,
            out float cyPixels);

        Assert.That(ok, Is.True);
        Assert.That(fx, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(fy, Is.EqualTo(1920f / 1080f).Within(0.0001f));
        Assert.That(cxPixels, Is.EqualTo(960f).Within(0.0001f));
        Assert.That(cyPixels, Is.EqualTo(540f).Within(0.0001f));
    }

    [Test]
    public void ReconstructCamLocalFromEyePixelKeepsExistingPrincipalPointBehavior()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 1000,
            eye_h = 500,
            cx = 250f,
            cy = 0.25f
        };

        Vector3 camLocal = PinholePlacementSpace.ReconstructCamLocalFromEyePixel(
            manifest,
            500f,
            250f,
            2f,
            2f,
            4f,
            1000,
            500);

        AssertVector(camLocal, new Vector3(0.25f, -0.25f, 2f));
    }

    [Test]
    public void EyePixelDepthToWorldAppliesCameraPose()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 100,
            eye_h = 100
        };

        Vector3 world = PinholePlacementSpace.EyePixelDepthToWorld(
            new Vector3(1f, 2f, 3f),
            Quaternion.Euler(0f, 90f, 0f),
            manifest,
            50f,
            50f,
            2f,
            1f,
            1f);

        AssertVector(world, new Vector3(3f, 2f, 3f));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
