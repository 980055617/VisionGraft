using NUnit.Framework;

public class AnimalSmalFkPolicyTests
{
    [Test]
    public void KeepsSmalTailJointsInBindPose()
    {
        for (int joint = 25; joint <= 31; joint++)
        {
            Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(joint), Is.True);
        }
    }

    [Test]
    public void AppliesTrackedPoseToBodyAndLimbJoints()
    {
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(7), Is.False);
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(15), Is.False);
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(24), Is.False);
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(32), Is.False);
    }
}
