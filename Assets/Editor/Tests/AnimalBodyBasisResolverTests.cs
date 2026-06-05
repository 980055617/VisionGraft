using NUnit.Framework;
using UnityEngine;

public class AnimalBodyBasisResolverTests
{
    [Test]
    public void TryResolveFromJointsUsesPelvisToWithersAsForward()
    {
        Vector3[] joints = CreateJoints();
        byte[] vis = CreateVisible();
        joints[7] = Vector3.zero;
        joints[18] = Vector3.forward;

        bool resolved = AnimalBodyBasisResolver.TryResolveFromJoints(joints, vis, Vector3.up, out Vector3 forward, out Vector3 up, out _);

        Assert.That(resolved, Is.True);
        AssertVector(forward, Vector3.forward);
        AssertVector(up, Vector3.up);
    }

    [Test]
    public void TryResolveFromJointsFlipsForwardTowardHeadRoot()
    {
        Vector3[] joints = CreateJoints();
        byte[] vis = CreateVisible();
        joints[7] = Vector3.zero;
        joints[18] = Vector3.forward;
        joints[24] = Vector3.back;

        bool resolved = AnimalBodyBasisResolver.TryResolveFromJoints(joints, vis, Vector3.up, out Vector3 forward, out _, out Vector3 facingHint);

        Assert.That(resolved, Is.True);
        AssertVector(forward, Vector3.back);
        AssertVector(facingHint, new Vector3(0f, 0f, -2f).normalized);
    }

    [Test]
    public void TryResolveFromJointsUsesShoulderAxisToChooseUpClosestToPreferredUp()
    {
        Vector3[] joints = CreateJoints();
        byte[] vis = CreateVisible();
        joints[7] = Vector3.zero;
        joints[18] = Vector3.forward;
        joints[12] = Vector3.left;
        joints[13] = Vector3.right;

        bool resolved = AnimalBodyBasisResolver.TryResolveFromJoints(joints, vis, Vector3.up, out _, out Vector3 up, out _);

        Assert.That(resolved, Is.True);
        Assert.That(Vector3.Dot(up, Vector3.up), Is.GreaterThan(0.99f));
    }

    [Test]
    public void TryResolveFromControlUsesForwardHintBeforeWithers()
    {
        AnimalControlWorldData control = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = Vector3.zero,
            hasForwardHint = true,
            forwardHintWorld = Vector3.forward,
            hasWithers = true,
            withersWorld = Vector3.right,
            hasUpHint = true,
            upHintWorld = Vector3.up
        };

        bool resolved = AnimalBodyBasisResolver.TryResolveFromControl(control, out Vector3 forward, out Vector3 up, out _);

        Assert.That(resolved, Is.True);
        AssertVector(forward, Vector3.forward);
        Assert.That(Vector3.Dot(forward, up), Is.EqualTo(0f).Within(0.0001f));
        Assert.That(up.magnitude, Is.EqualTo(1f).Within(0.0001f));
    }

    [Test]
    public void TryResolveFromControlFlipsForwardWhenHeadFacingOpposesIt()
    {
        AnimalControlWorldData control = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = Vector3.zero,
            hasWithers = true,
            withersWorld = Vector3.forward,
            hasHeadRoot = true,
            headRootWorld = Vector3.zero,
            hasHeadTip = true,
            headTipWorld = Vector3.back,
            hasUpHint = true,
            upHintWorld = Vector3.up
        };

        bool resolved = AnimalBodyBasisResolver.TryResolveFromControl(control, out Vector3 forward, out _, out Vector3 facingHint);

        Assert.That(resolved, Is.True);
        AssertVector(forward, Vector3.back);
        AssertVector(facingHint, Vector3.back);
    }

    [Test]
    public void TryResolveFromControlFallsBackToWorldUpWhenUpHintIsMissing()
    {
        AnimalControlWorldData control = new AnimalControlWorldData
        {
            hasRoot = true,
            rootWorld = Vector3.zero,
            hasWithers = true,
            withersWorld = Vector3.forward
        };

        bool resolved = AnimalBodyBasisResolver.TryResolveFromControl(control, out Vector3 forward, out Vector3 up, out _);

        Assert.That(resolved, Is.True);
        AssertVector(forward, Vector3.forward);
        AssertVector(up, Vector3.up);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }

    private static Vector3[] CreateJoints()
    {
        return new Vector3[25];
    }

    private static byte[] CreateVisible()
    {
        byte[] vis = new byte[25];
        for (int i = 0; i < vis.Length; i++)
        {
            vis[i] = 1;
        }

        return vis;
    }
}
