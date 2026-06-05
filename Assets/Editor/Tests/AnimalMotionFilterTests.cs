using NUnit.Framework;
using UnityEngine;

public class AnimalMotionFilterTests
{
    private AnimalFilterConfig defaultConfig;

    [SetUp]
    public void SetUp()
    {
        defaultConfig = AnimalFilterConfig.Default;
    }

    // --- FilterRootPosition ---

    [Test]
    public void FilterRootPosition_FirstCall_ReturnsInputUnchanged()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 target = new Vector3(3f, 1f, 2f);

            Vector3 result = filter.FilterRootPosition(go.transform, target, 0.016f);

            Assert.That(result, Is.EqualTo(target));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void FilterRootPosition_SecondCall_SmoothsTowardTarget()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            filter.FilterRootPosition(go.transform, Vector3.zero, 0.016f);

            Vector3 result = filter.FilterRootPosition(go.transform, new Vector3(10f, 0f, 0f), 0.016f);

            Assert.That(result.x, Is.GreaterThan(0f).And.LessThan(10f));
        }
        finally { Object.DestroyImmediate(go); }
    }

    // --- FilterLimbTarget ---

    [Test]
    public void FilterLimbTarget_FirstCall_ReturnsInputUnchanged()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 target = new Vector3(1f, 2f, 3f);

            Vector3 result = filter.FilterLimbTarget(go.transform, target, 2.0f, 0.08f, 0.016f);

            Assert.That(result, Is.EqualTo(target));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void FilterLimbTarget_SecondCall_SmoothsTowardTarget()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            filter.FilterLimbTarget(go.transform, Vector3.zero, 2.0f, 0.08f, 0.016f);

            Vector3 result = filter.FilterLimbTarget(go.transform, new Vector3(0f, 10f, 0f), 2.0f, 0.08f, 0.016f);

            Assert.That(result.y, Is.GreaterThan(0f).And.LessThan(10f));
        }
        finally { Object.DestroyImmediate(go); }
    }

    // --- WasSeenRecently / MarkRootSeen ---

    [Test]
    public void WasSeenRecently_FreshState_ReturnsFalse()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);

            Assert.That(filter.WasSeenRecently(go.transform, 100), Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void WasSeenRecently_AfterMarkSeen_SameFrame_ReturnsTrue()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            filter.MarkRootSeen(go.transform, 100);

            Assert.That(filter.WasSeenRecently(go.transform, 100), Is.True);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void WasSeenRecently_AfterMarkSeen_TwoFramesLater_ReturnsFalse()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            filter.MarkRootSeen(go.transform, 100);

            Assert.That(filter.WasSeenRecently(go.transform, 102), Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // --- TryGetPrevRootForward / SetRootForward ---

    [Test]
    public void TryGetPrevRootForward_FreshState_ReturnsFalse()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);

            Assert.That(filter.TryGetPrevRootForward(go.transform, out _), Is.False);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void TryGetPrevRootForward_AfterSetRootForward_ReturnsForward()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 forward = new Vector3(0f, 0f, 1f);
            filter.SetRootForward(go.transform, forward);

            bool found = filter.TryGetPrevRootForward(go.transform, out Vector3 result);

            Assert.That(found, Is.True);
            Assert.That(result, Is.EqualTo(forward));
        }
        finally { Object.DestroyImmediate(go); }
    }

    // --- ResetRootBasis ---

    [Test]
    public void ResetRootBasis_SetsPrevForwardToPlanarForward()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 planarForward = new Vector3(1f, 0f, 0f);

            filter.ResetRootBasis(go.transform, planarForward, Vector3.up, Vector3.up);

            filter.TryGetPrevRootForward(go.transform, out Vector3 stored);
            Assert.That(stored, Is.EqualTo(planarForward));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void FilterRootForward_AfterReset_SameVectorAsReset_ReturnsSameVector()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 planarForward = new Vector3(0f, 0f, 1f);
            filter.ResetRootBasis(go.transform, planarForward, Vector3.up, Vector3.up);

            Vector3 result = filter.FilterRootForward(go.transform, planarForward, 0.016f);

            Assert.That(result.x, Is.EqualTo(planarForward.x).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(planarForward.y).Within(0.0001f));
            Assert.That(result.z, Is.EqualTo(planarForward.z).Within(0.0001f));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void FilterRootForward_AfterReset_DifferentVector_ReturnsBetween()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            filter.ResetRootBasis(go.transform, Vector3.forward, Vector3.up, Vector3.up);

            Vector3 result = filter.FilterRootForward(go.transform, Vector3.right, 0.016f);

            // result should be between forward (0,0,1) and right (1,0,0) — not purely either
            Assert.That(result.x, Is.GreaterThan(0f).And.LessThan(1f));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void FilterRootForward_NoReset_FirstCall_ReturnsInputUnchanged()
    {
        var go = new GameObject();
        try
        {
            var filter = new AnimalMotionFilter(defaultConfig);
            Vector3 forward = new Vector3(1f, 0f, 0f);

            Vector3 result = filter.FilterRootForward(go.transform, forward, 0.016f);

            Assert.That(result, Is.EqualTo(forward));
        }
        finally { Object.DestroyImmediate(go); }
    }
}
