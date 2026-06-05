using NUnit.Framework;
using UnityEngine;

public class AnimalRootBasisMathTests
{
    [Test]
    public void StabilizePitchRollProjectsForwardToWorldUpPlaneWhenBlendIsZero()
    {
        AnimalRootBasisMath.StabilizePitchRoll(
            Vector3.up,
            new Vector3(0f, 1f, 1f).normalized,
            Vector3.up,
            0f,
            out Vector3 forward,
            out Vector3 up);

        AssertVector(forward, Vector3.forward);
        Assert.That(Vector3.Dot(forward, up), Is.EqualTo(0f).Within(0.0001f));
        Assert.That(Vector3.Dot(up, Vector3.up), Is.GreaterThan(0.99f));
    }

    [Test]
    public void StabilizePitchRollKeepsTiltedForwardWhenBlendIsOne()
    {
        Vector3 tiltedForward = new Vector3(0f, 1f, 1f).normalized;

        AnimalRootBasisMath.StabilizePitchRoll(
            Vector3.up,
            tiltedForward,
            Vector3.up,
            1f,
            out Vector3 forward,
            out Vector3 up);

        AssertVector(forward, tiltedForward);
        Assert.That(Vector3.Dot(forward, up), Is.EqualTo(0f).Within(0.0001f));
        Assert.That(up.magnitude, Is.EqualTo(1f).Within(0.0001f));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
