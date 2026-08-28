using NUnit.Framework;

public class AnimalPoseJointChainsTests
{
    // 2026-08-28: 生成側から実測ベースの対応表を受領して全面訂正した（D-007）。
    // 旧値は { 18, 13, 9, 15 } 等で、**前肢の起点が kp18（実際は「き甲」ではなく頭）**、
    // かつ**前肢・後肢とも左右が逆**だった。こちらでも 2 つの独立な方法で検証済み
    // （左右を入れ替えた候補との長さ比較 10/10、前肢の 1:2 長さ比）。
    // 経緯は Docs/smpl-retargeting.md。
    [Test]
    public void ChainsMatchTheGeneratorKeypointTable()
    {
        // 肩 -> 肘 -> 手根 -> 前足
        Assert.That(AnimalPoseJointChains.LeftFront, Is.EqualTo(new[] { 12, 8, 14, 3 }));
        Assert.That(AnimalPoseJointChains.RightFront, Is.EqualTo(new[] { 13, 9, 15, 4 }));

        // 骨盤（尾の付け根）-> 膝 -> 飛節 -> 後足。
        // kp7 は左右の後肢で共有されるハブで、股関節そのものではない。
        Assert.That(AnimalPoseJointChains.LeftRear, Is.EqualTo(new[] { 7, 10, 16, 5 }));
        Assert.That(AnimalPoseJointChains.RightRear, Is.EqualTo(new[] { 7, 11, 17, 6 }));
    }

    // 左右で同じ番号を使っていたら対応づけを間違えている。
    [Test]
    public void LeftAndRightChainsDoNotShareDistalJoints()
    {
        int[][] chains =
        {
            AnimalPoseJointChains.LeftFront, AnimalPoseJointChains.RightFront,
            AnimalPoseJointChains.LeftRear, AnimalPoseJointChains.RightRear,
        };

        for (int i = 0; i < chains.Length; i++)
        {
            for (int k = i + 1; k < chains.Length; k++)
            {
                for (int j = 1; j < 4; j++)
                {
                    Assert.That(chains[i][j], Is.Not.EqualTo(chains[k][j]),
                        $"chain {i} と {k} が index {j} で同じ keypoint を指している");
                }
            }
        }
    }
}
