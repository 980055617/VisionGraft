using NUnit.Framework;
using UnityEngine;

public class AnimalVisibleJointBoundsTests
{
    [Test]
    public void TryResolveEncapsulatesVisibleFiniteJoints()
    {
        Vector3[] joints =
        {
            new Vector3(-1f, 0f, 2f),
            new Vector3(3f, 4f, -2f),
            new Vector3(100f, 100f, 100f)
        };
        byte[] vis = { 1, 1, 0 };

        bool resolved = AnimalVisibleJointBounds.TryResolve(joints, vis, 3, out Bounds bounds);

        Assert.That(resolved, Is.True);
        AssertVector(bounds.min, new Vector3(-1f, 0f, -2f));
        AssertVector(bounds.max, new Vector3(3f, 4f, 2f));
    }

    [Test]
    public void TryResolveIgnoresInvisibleAndNonFiniteJoints()
    {
        Vector3[] joints =
        {
            new Vector3(float.NaN, 0f, 0f),
            new Vector3(10f, 10f, 10f),
            new Vector3(1f, 2f, 3f)
        };
        byte[] vis = { 1, 0, 1 };

        bool resolved = AnimalVisibleJointBounds.TryResolve(joints, vis, 3, out Bounds bounds);

        Assert.That(resolved, Is.True);
        AssertVector(bounds.min, new Vector3(1f, 2f, 3f));
        AssertVector(bounds.max, new Vector3(1f, 2f, 3f));
    }

    [Test]
    public void TryResolveKeepsVisibleZeroJointLikeExistingBoundsPath()
    {
        bool resolved = AnimalVisibleJointBounds.TryResolve(
            new[] { Vector3.zero },
            new byte[] { 1 },
            1,
            out Bounds bounds);

        Assert.That(resolved, Is.True);
        AssertVector(bounds.center, Vector3.zero);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}
