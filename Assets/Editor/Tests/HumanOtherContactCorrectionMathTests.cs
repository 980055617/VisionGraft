using NUnit.Framework;
using UnityEngine;

public class HumanOtherContactCorrectionMathTests
{
    [Test]
    public void ResolveContactWeightFadesBetweenFullContactAndRelease()
    {
        Assert.That(
            HumanOtherContactCorrectionMath.ResolveContactWeight(
                20f,
                20f,
                1.25f,
                2f),
            Is.EqualTo(1f));
        Assert.That(
            HumanOtherContactCorrectionMath.ResolveContactWeight(
                40f,
                20f,
                1.25f,
                2f),
            Is.EqualTo(0f));

        float faded = HumanOtherContactCorrectionMath.ResolveContactWeight(
            32.5f,
            20f,
            1.25f,
            2f);
        Assert.That(faded, Is.InRange(0f, 1f));
    }

    [Test]
    public void MappedSegmentContactPreservesSourceSideWhenSegmentRotates()
    {
        bool ok = HumanOtherContactCorrectionMath.TryResolveMappedSegmentContact(
            new Vector2(5f, -5f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(100f, 100f),
            new Vector2(100f, 120f),
            10f,
            out Vector2 target,
            out float segmentParameter);

        Assert.That(ok, Is.True);
        Assert.That(segmentParameter, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(target.x, Is.EqualTo(110f).Within(0.0001f));
        Assert.That(target.y, Is.EqualTo(110f).Within(0.0001f));
    }

    [Test]
    public void MappedSegmentContactPreservesDirectionBeyondEndpoint()
    {
        bool ok = HumanOtherContactCorrectionMath.TryResolveMappedSegmentContact(
            new Vector2(15f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(100f, 100f),
            new Vector2(100f, 120f),
            10f,
            out Vector2 target,
            out float segmentParameter);

        Assert.That(ok, Is.True);
        Assert.That(segmentParameter, Is.EqualTo(1f));
        Assert.That(target.x, Is.EqualTo(100f).Within(0.0001f));
        Assert.That(target.y, Is.EqualTo(130f).Within(0.0001f));
    }
}
