using NUnit.Framework;

public class AnimalPoseJointChainsTests
{
    [Test]
    public void ChainsKeepExistingAnimalJointIndices()
    {
        Assert.That(AnimalPoseJointChains.LeftFront, Is.EqualTo(new[] { 18, 13, 9, 15 }));
        Assert.That(AnimalPoseJointChains.RightFront, Is.EqualTo(new[] { 18, 12, 8, 14 }));
        Assert.That(AnimalPoseJointChains.LeftRear, Is.EqualTo(new[] { 7, 11, 17, 6 }));
        Assert.That(AnimalPoseJointChains.RightRear, Is.EqualTo(new[] { 7, 10, 16, 5 }));
    }
}
