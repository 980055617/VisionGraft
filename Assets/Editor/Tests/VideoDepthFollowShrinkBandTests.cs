using NUnit.Framework;
using UnityEngine;

// 縮める向きの遊び（placementFollowVideoDepthFeetSizeShrinkBand、2026-10-28 夕、10/28 夜から既定 0.15、VideoDepthFollowFeet.partial.cs・VideoDepthFollow.partial.cs）。
public class VideoDepthFollowShrinkBandTests
{
    [Test]
    public void BandIs015ByDefault()
    {
        GameObject host = new GameObject("VideoDepthFollowShrinkBandTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthFeetSizeShrinkBand, Is.EqualTo(0.15f));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 猫が下がる 17.0 → 18.25 秒（目標 3.75 → 3.34、−11%）: 帯 0.15 の中なので今の f のまま。帯 0 は目標のまま。
    [Test]
    public void SmallShrinkKeepsTheCurrentSize()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3.75f, 3.34f, 0.15f), Is.EqualTo(3.75f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3.75f, 3.34f, 0f), Is.EqualTo(3.34f));
    }

    // 帯より大きく縮む目標は帯の端（目標 × 1.15）まで。大きくする向きは目標のまま（モデルは今より手前に来ない）。
    [Test]
    public void LargeShrinkStopsAtTheBandEdgeAndGrowthFollowsTheTarget()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(4f, 2f, 0.15f), Is.EqualTo(2.3f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3.3f, 4.3f, 0.15f), Is.EqualTo(4.3f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3.3f, 3.3f, 0.15f), Is.EqualTo(3.3f));
    }

    // 値が無い・負のときは目標をそのまま返す。
    [Test]
    public void InvalidValuesPassTheTargetThrough()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(0f, 2f, 0.15f), Is.EqualTo(2f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3f, -1f, 0.15f), Is.EqualTo(-1f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowShrinkBandGoal(3f, 2f, -0.1f), Is.EqualTo(2f));
    }
}
