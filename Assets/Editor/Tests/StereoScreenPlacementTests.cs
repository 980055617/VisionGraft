using NUnit.Framework;
using UnityEngine;

public class StereoScreenPlacementTests
{
    [Test]
    public void ResolvePlacementKeepsExistingHeadRelativeScreenPlacement()
    {
        Vector3 headPosition = new Vector3(1f, 2f, 3f);
        Quaternion headRotation = Quaternion.Euler(0f, 90f, 0f);
        Vector3 screenOffset = new Vector3(0.1f, 0.2f, 0.3f);

        StereoScreenPlacement.Placement placement = StereoScreenPlacement.ResolvePlacement(
            headPosition,
            headRotation,
            2f,
            screenOffset,
            0.001f);

        Vector3 expectedCenter = headPosition + headRotation * (Vector3.forward * 2f + screenOffset);
        Vector3 expectedRight = headRotation * Vector3.right * 0.001f;
        Quaternion expectedRotation = Quaternion.LookRotation((headPosition - expectedCenter).normalized, headRotation * Vector3.up);

        AssertVector(placement.center, expectedCenter);
        AssertVector(placement.leftPosition, expectedCenter - expectedRight);
        AssertVector(placement.rightPosition, expectedCenter + expectedRight);
        Assert.That(Quaternion.Angle(placement.rotation, expectedRotation), Is.LessThan(0.001f));
    }

    [Test]
    public void ResolveFitSizeKeepsExistingFovDimensions()
    {
        StereoScreenPlacement.ResolveFitSize(
            2f,
            90f,
            1920,
            1080,
            out float width,
            out float height);

        Assert.That(width, Is.EqualTo(4f).Within(0.0001f));
        Assert.That(height, Is.EqualTo(2.25f).Within(0.0001f));
    }

    [Test]
    public void ResolveForcedInFrontPoseKeepsExistingCameraRelativePose()
    {
        Vector3 cameraPosition = new Vector3(1f, 2f, 3f);
        Vector3 cameraForward = new Vector3(0f, 0f, 1f);

        StereoScreenPlacement.ForcedPose pose = StereoScreenPlacement.ResolveForcedInFrontPose(
            cameraPosition,
            cameraForward,
            1.5f);

        Vector3 expectedPosition = cameraPosition + cameraForward * 1.5f;
        Quaternion expectedRotation = Quaternion.LookRotation((cameraPosition - expectedPosition).normalized, Vector3.up);

        AssertVector(pose.position, expectedPosition);
        Assert.That(Quaternion.Angle(pose.rotation, expectedRotation), Is.LessThan(0.001f));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
