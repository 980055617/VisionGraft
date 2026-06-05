using NUnit.Framework;

public class AnimalAimDirectionPolicyTests
{
    [Test]
    public void ShouldUseChildPivotDirectionWhenAimChildIsRegistered()
    {
        Assert.That(AnimalAimDirectionPolicy.ShouldUseChildPivotDirection(true, false), Is.True);
    }

    [Test]
    public void ShouldUseChildPivotDirectionForLimbBonesWithoutRegisteredAimChild()
    {
        Assert.That(AnimalAimDirectionPolicy.ShouldUseChildPivotDirection(false, true), Is.True);
    }

    [Test]
    public void ShouldNotUseChildPivotDirectionForUnregisteredNonLimbBones()
    {
        Assert.That(AnimalAimDirectionPolicy.ShouldUseChildPivotDirection(false, false), Is.False);
    }
}
