using NUnit.Framework;

public class AnimalDefSpineFallbackPolicyTests
{
    [Test]
    public void ReplacesCanonicalSpineForTailLessDefSpineRig()
    {
        Assert.That(
            AnimalDefSpineFallbackPolicy.ShouldReplaceCanonicalSpineWithDefSpineChain(
                hasTailBase: false,
                defSpineBoneCount: 8),
            Is.True);
    }

    [Test]
    public void KeepsCanonicalSpineWhenTailBoneExists()
    {
        Assert.That(
            AnimalDefSpineFallbackPolicy.ShouldReplaceCanonicalSpineWithDefSpineChain(
                hasTailBase: true,
                defSpineBoneCount: 8),
            Is.False);
    }
}
