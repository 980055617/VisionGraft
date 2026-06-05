using NUnit.Framework;

public class AnimalRigDefinitionTests
{
    [Test]
    public void LimbRulesKeepExistingExactNamesAndTokenFallbacks()
    {
        Assert.That(AnimalRigDefinition.LeftFrontUpper.exactNames, Does.Contain("\u30dc\u30fc\u30f3_L.001"));
        Assert.That(AnimalRigDefinition.LeftFrontUpper.exactNames, Does.Contain("DEF-front_thigh.L"));
        Assert.That(AnimalRigDefinition.LeftFrontUpper.tokens, Does.Contain("def-front_thigh.l"));

        Assert.That(AnimalRigDefinition.RightRearPaw.exactNames, Does.Contain("\u30dc\u30fc\u30f3.001_R.003"));
        Assert.That(AnimalRigDefinition.RightRearPaw.tokens, Does.Contain("def-toe.r"));
    }

    [Test]
    public void CoreRulesKeepBodyHeadAndTailFallbacks()
    {
        Assert.That(AnimalRigDefinition.Neck.exactNames, Does.Contain("DEF-spine.010"));
        Assert.That(AnimalRigDefinition.Head.tokens, Does.Contain("head"));
        Assert.That(AnimalRigDefinition.Spine.tokens, Does.Contain("spine"));
        Assert.That(AnimalRigDefinition.TailBaseTokens, Does.Contain("tail.002"));
    }
}
