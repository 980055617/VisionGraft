using NUnit.Framework;
using UnityEngine;

public class RuntimeControlsPlacementTests
{
    [Test]
    public void ResolveBarPoseKeepsExistingScreenRelativePlacement()
    {
        Vector3 center = new Vector3(1f, 2f, 3f);
        Vector3 basisForward = Vector3.forward;
        Vector3 basisRight = Vector3.right;
        Vector3 basisUp = Vector3.up;
        Quaternion basisRotation = Quaternion.Euler(0f, 45f, 0f);
        Vector3 headPosition = new Vector3(1f, 2f, 0f);

        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveBarPose(
            center,
            basisForward,
            basisRight,
            basisUp,
            basisRotation,
            true,
            headPosition,
            2f,
            new Vector2(1f, 0.2f),
            0.05f,
            new Vector2(0.1f, 0.03f),
            0.02f);

        float downFromCenter = 1f + 0.05f + 0.1f - 0.03f;
        Vector3 expected = center + Vector3.right * 0.1f - Vector3.up * downFromCenter + Vector3.back * 0.02f;
        AssertVector(pose.position, expected);
        Assert.That(Quaternion.Angle(pose.rotation, basisRotation), Is.LessThan(0.001f));
    }

    [Test]
    public void ResolveBarPoseFallsBackToNegativeBasisForwardWithoutHead()
    {
        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveBarPose(
            Vector3.zero,
            Vector3.forward,
            Vector3.right,
            Vector3.up,
            Quaternion.identity,
            false,
            Vector3.zero,
            0f,
            Vector2.zero,
            0f,
            Vector2.zero,
            0.5f);

        AssertVector(pose.position, Vector3.back * 0.5f);
    }

    [Test]
    public void ResolveSettingsPoseKeepsExistingBasisRelativePlacementWithoutHead()
    {
        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveSettingsPose(
            Vector3.zero,
            Vector3.forward,
            Vector3.right,
            Vector3.up,
            Quaternion.identity,
            false,
            Vector3.zero,
            2f,
            new Vector2(0.8f, 0.6f),
            0.1f,
            new Vector2(0.2f, 0.05f),
            0.03f);

        float rightFromBar = 1f + 0.1f + 0.4f + 0.2f;
        Vector3 expected = Vector3.right * rightFromBar + Vector3.up * 0.05f + Vector3.back * 0.06f;
        AssertVector(pose.position, expected);
        Assert.That(Quaternion.Angle(pose.rotation, Quaternion.identity), Is.LessThan(0.001f));
    }

    [Test]
    public void ResolveSettingsPoseFacesHeadWithCanvasFlipWhenHeadExists()
    {
        Vector3 basisPosition = Vector3.zero;
        Vector3 headPosition = new Vector3(0f, 0f, -2f);

        RuntimeControlsPlacement.Pose pose = RuntimeControlsPlacement.ResolveSettingsPose(
            basisPosition,
            Vector3.forward,
            Vector3.right,
            Vector3.up,
            Quaternion.identity,
            true,
            headPosition,
            0f,
            Vector2.zero,
            0f,
            Vector2.zero,
            0.1f);

        Vector3 look = Vector3.ProjectOnPlane(headPosition - pose.position, Vector3.up).normalized;
        Quaternion expected = Quaternion.AngleAxis(180f, Vector3.up) * Quaternion.LookRotation(look, Vector3.up);
        Assert.That(Quaternion.Angle(pose.rotation, expected), Is.LessThan(0.001f));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
