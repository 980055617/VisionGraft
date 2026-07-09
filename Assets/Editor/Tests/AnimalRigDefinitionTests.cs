using NUnit.Framework;

public class AnimalRigDefinitionTests
{
    [Test]
    public void LimbRulesUseCanonicalExactNames()
    {
        Assert.That(AnimalRigDefinition.LeftFrontUpper.exactNames, Does.Contain("front_l_upper"));
        Assert.That(AnimalRigDefinition.LeftFrontLower.exactNames, Does.Contain("front_l_lower"));
        Assert.That(AnimalRigDefinition.LeftFrontPaw.exactNames, Does.Contain("front_l_paw"));
        Assert.That(AnimalRigDefinition.RightFrontUpper.exactNames, Does.Contain("front_r_upper"));
        Assert.That(AnimalRigDefinition.RightFrontLower.exactNames, Does.Contain("front_r_lower"));
        Assert.That(AnimalRigDefinition.RightFrontPaw.exactNames, Does.Contain("front_r_paw"));
        Assert.That(AnimalRigDefinition.LeftRearUpper.exactNames, Does.Contain("rear_l_upper"));
        Assert.That(AnimalRigDefinition.LeftRearLower.exactNames, Does.Contain("rear_l_lower"));
        Assert.That(AnimalRigDefinition.LeftRearPaw.exactNames, Does.Contain("rear_l_paw"));
        Assert.That(AnimalRigDefinition.LeftRearToe.exactNames, Does.Contain("rear_l_toe"));
        Assert.That(AnimalRigDefinition.RightRearUpper.exactNames, Does.Contain("rear_r_upper"));
        Assert.That(AnimalRigDefinition.RightRearLower.exactNames, Does.Contain("rear_r_lower"));
        Assert.That(AnimalRigDefinition.RightRearPaw.exactNames, Does.Contain("rear_r_paw"));
        Assert.That(AnimalRigDefinition.RightRearToe.exactNames, Does.Contain("rear_r_toe"));
    }

    [Test]
    public void CoreAndTailRulesUseCanonicalExactNames()
    {
        Assert.That(AnimalRigDefinition.Spine.exactNames, Does.Contain("spine"));
        Assert.That(AnimalRigDefinition.Neck.exactNames, Does.Contain("neck"));
        Assert.That(AnimalRigDefinition.Head.exactNames, Does.Contain("head"));
        Assert.That(AnimalRigDefinition.TailBaseTokens, Does.Contain("tail_base"));
        Assert.That(AnimalRigDefinition.TailMid.exactNames, Does.Contain("tail_mid"));
        Assert.That(AnimalRigDefinition.TailTip.exactNames, Does.Contain("tail_tip"));
    }

    [Test]
    public void SpineNeckHeadRetainTokenFallbacks()
    {
        Assert.That(AnimalRigDefinition.Spine.tokens, Does.Contain("spine"));
        Assert.That(AnimalRigDefinition.Neck.tokens, Does.Contain("neck"));
        Assert.That(AnimalRigDefinition.Head.tokens, Does.Contain("head"));
    }
}
