using NUnit.Framework;
using UnityEngine;

// 人・動物の続きを、端に掛かって出ていくと分かった所から始める（bodyFrameOutStartWhenClipped、2026-10-29、同じ日に既定 ON、BodyFrameOut.partial.cs）。
public class BodyFrameOutStartWhenClippedTests
{
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("BodyFrameOutStartWhenClippedTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.bodyFrameOutStartWhenClipped, Is.True);
            // 一緒に入れたボールの直し（AnimalCarry.partial.cs）も既定 ON。
            Assert.That(player.animalCarryKeepsBallWhileTrackLost, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // まだ左右の端に掛かっていない（下や上から出ていく）ときの出ていく側: 箱の真ん中が左半分なら −1、右半分なら +1。
    [Test]
    public void SideFromTheBoxCenter()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutSideFromCenter(300f, 1280f), Is.EqualTo(-1));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutSideFromCenter(900f, 1280f), Is.EqualTo(1));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutSideFromCenter(640f, 1280f), Is.EqualTo(0));
        Assert.That(StreamingStereoVideoPlayer.ResolveBodyFrameOutSideFromCenter(100f, 0f), Is.EqualTo(0));
    }
}
