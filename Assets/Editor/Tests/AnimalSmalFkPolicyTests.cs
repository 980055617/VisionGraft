using NUnit.Framework;

public class AnimalSmalFkPolicyTests
{
    // Tail1 / Tail2（SMAL joint 25 / 26）は 2026-07-16 に SmalRestDirByJoint へ
    // rest 方向を登録して body_pose を当てるようにした。bind pose のまま残すのは
    // Tail3 以降（27〜31）だけ。テストの期待値が変更前のままだったので更新した（2026-08-28）。
    [Test]
    public void KeepsBindPoseOnlyForTailJointsBeyondTail2()
    {
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(25), Is.False, "Tail1 は body_pose 駆動");
        Assert.That(AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(26), Is.False, "Tail2 は body_pose 駆動");

        for (int joint = 27; joint <= 31; joint++)
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
