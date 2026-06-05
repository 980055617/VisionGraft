using NUnit.Framework;

public class SkeletonIndexPresetsTests
{
    [Test]
    public void MetrabsSmpl24KeepsExistingJointIndices()
    {
        SkeletonIndices indices = SkeletonIndexPresets.MetrabsSmpl24;

        Assert.That(indices.nose, Is.EqualTo(15));
        Assert.That(indices.leftEye, Is.EqualTo(-1));
        Assert.That(indices.rightEye, Is.EqualTo(-1));
        Assert.That(indices.leftShoulder, Is.EqualTo(16));
        Assert.That(indices.rightShoulder, Is.EqualTo(17));
        Assert.That(indices.leftElbow, Is.EqualTo(18));
        Assert.That(indices.rightElbow, Is.EqualTo(19));
        Assert.That(indices.leftWrist, Is.EqualTo(20));
        Assert.That(indices.rightWrist, Is.EqualTo(21));
        Assert.That(indices.leftHip, Is.EqualTo(1));
        Assert.That(indices.rightHip, Is.EqualTo(2));
        Assert.That(indices.leftKnee, Is.EqualTo(4));
        Assert.That(indices.rightKnee, Is.EqualTo(5));
        Assert.That(indices.leftAnkle, Is.EqualTo(7));
        Assert.That(indices.rightAnkle, Is.EqualTo(8));
        Assert.That(indices.leftFoot, Is.EqualTo(10));
        Assert.That(indices.rightFoot, Is.EqualTo(11));
    }
}
