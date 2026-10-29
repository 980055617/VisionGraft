using NUnit.Framework;
using UnityEngine;

// 出口で近づく人・動物の直し（placementFollowVideoDepthEdgeFollowsSmplDepth・bodyFrameOutLeaveViewWithTrack、2026-10-29、既定 OFF、ExitApproach.partial.cs）。
public class ExitApproachTests
{
    [Test]
    public void OffByDefault()
    {
        GameObject host = new GameObject("ExitApproachTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthEdgeFollowsSmplDepth, Is.False);
            Assert.That(player.bodyFrameOutLeaveViewWithTrack, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 近づいた分（tz の比）だけ手前へ。映像の奥行きの方が手前ならそのまま、近づいていない・値が無いときもそのまま、下限より手前にはしない。
    [Test]
    public void EdgeDepthFollowsTheApproachOnly()
    {
        float d = StreamingStereoVideoPlayer.ResolveVideoDepthEdgeApproachDepth(1.0f, 0.92f, 3.0f, 2.4f, 0.35f);
        Assert.That(d, Is.EqualTo(0.92f * 0.8f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthEdgeApproachDepth(0.5f, 0.92f, 3.0f, 2.4f, 0.35f), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthEdgeApproachDepth(1.0f, 0.92f, 3.0f, 3.2f, 0.35f), Is.EqualTo(1.0f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthEdgeApproachDepth(1.0f, 0f, 3.0f, 2.4f, 0.35f), Is.EqualTo(1.0f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthEdgeApproachDepth(1.0f, 0.92f, 3.0f, 0.3f, 0.35f), Is.EqualTo(0.35f).Within(1e-6f));
    }

    // 点が動画の枠（|x| ≤ tanH·z、|y| ≤ tanV·z、z ≥ 0.05）を抜けるまでの距離: 中から横へ抜ける、外にいて離れていく（0）、外から入って後で抜ける、
    // 下の面から抜ける、ずっと中（+∞）、枠を通らない（0）。
    [Test]
    public void LeaveDistanceThroughTheVideoFrame()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(0f, 0f, 1f), Vector3.right, 1f, 0.5f), Is.EqualTo(1f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(0f, -0.6f, 1f), Vector3.back, 1f, 0.5f), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(-2f, 0f, 1f), Vector3.right, 1f, 0.5f), Is.EqualTo(3f).Within(1e-5f));
        // 下へ: y = -0.1 - s が -0.5·1 を下回るのは s = 0.4
        Assert.That(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(0f, -0.1f, 1f), Vector3.down, 1f, 0.5f), Is.EqualTo(0.4f).Within(1e-5f));
        Assert.That(float.IsPositiveInfinity(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(0f, 0f, 1f), Vector3.forward, 1f, 0.5f)), Is.True);
        Assert.That(StreamingStereoVideoPlayer.ResolveLeaveViewDistanceForPoint(new Vector3(0f, 3f, 1f), Vector3.right, 1f, 0.5f), Is.EqualTo(0f).Within(1e-6f));
    }

    // 消えるまでに枠を越える速さ。今より遅くはしない、0.3〜2.5 × 高さに収める。
    [Test]
    public void LeaveViewSpeedIsNeverSlowerAndClamped()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutLeaveViewSpeed(0.2f, 0.5f, 1.0f, 0.31f), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutLeaveViewSpeed(0.2f, 2.0f, 1.0f, 0.31f), Is.EqualTo(2.5f * 0.31f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutLeaveViewSpeed(0.5f, 0.2f, 1.0f, 1.0f), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutLeaveViewSpeed(0.2f, 0f, 1.0f, 0.31f), Is.EqualTo(0.2f).Within(1e-6f));
    }
}
