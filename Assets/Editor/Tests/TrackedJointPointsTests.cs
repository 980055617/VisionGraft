using NUnit.Framework;
using UnityEngine;

public class TrackedJointPointsTests
{
    [Test]
    public void TryGetRejectsInvalidJointInputs()
    {
        Assert.That(TrackedJointPoints.TryGet(null, new byte[] { 1 }, 0, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { Vector3.one }, null, 0, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { Vector3.one }, new byte[] { 1 }, -1, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { Vector3.one }, new byte[] { 1 }, 1, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { Vector3.one }, new byte[] { 0 }, 0, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { Vector3.zero }, new byte[] { 1 }, 0, out _), Is.False);
        Assert.That(TrackedJointPoints.TryGet(new[] { new Vector3(float.NaN, 1f, 1f) }, new byte[] { 1 }, 0, out _), Is.False);
    }


    [Test]
    public void TryGetReturnsVisibleFiniteNonZeroJoint()
    {
        Vector3 expected = new Vector3(1f, 2f, 3f);

        bool found = TrackedJointPoints.TryGet(new[] { expected }, new byte[] { 1 }, 0, out Vector3 actual);

        Assert.That(found, Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }
}
