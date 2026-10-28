using NUnit.Framework;
using UnityEngine;

// 左右の端に掛かっている間から視聴者の横を通る続きを始める（elseFrameOutCoastFromEdge、2026-10-28 夕、10/28 夜から既定 ON、ElseFrameOut.partial.cs）の
// 出口の先読み（ResolveFrameOutLeavesSoon）。
public class ElseCoastFromEdgeTests
{
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("ElseCoastFromEdgeTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.elseFrameOutCoastFromEdge, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    private static System.Func<int, bool> PresentUntil(int lastPresent, int comesBackAt = -1)
    {
        return f => f <= lastPresent || (comesBackAt >= 0 && f >= comesBackAt);
    }

    // car2 の 1 台目: f202 で端に掛かって外へ動き、f217 まで映って f218 に消え、戻らない → 始める（窓 36 フレーム・戻り 72 フレーム）。
    [Test]
    public void StartsWhenTheTrackLeavesWithinTheWindowAndDoesNotComeBack()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(217), 202, 720, 36, 72), Is.True);
    }

    // まだ 36 フレームより長く映り続ける（端をかすめただけ）・すぐ戻る（練習の犬は 24 フレームで戻る）なら始めない。
    [Test]
    public void SkipsAGrazeAndAQuickReturn()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(300), 202, 720, 36, 72), Is.False);
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(217, 242), 202, 720, 36, 72), Is.False);
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(217, 300), 202, 720, 36, 72), Is.True);
    }

    // 進んだ距離: 上げる間が無い（消えた所で始める今の続き）なら経過フレームそのもの。36 フレームで 0.25 倍から 1 倍へ上げると、36 フレーム目までに
    // 0.25 × 36 + 0.75 × 36 / 2 = 22.5 フレーム分、その後は 1 フレームに 1 ずつ。
    [Test]
    public void TravelRampsFromTheApproachSpeedToTheExitSpeed()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(10, 0, 0.25f), Is.EqualTo(10f));
        Assert.That(StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(10, 36, 1f), Is.EqualTo(10f));
        Assert.That(StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(0, 36, 0.25f), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(36, 36, 0.25f), Is.EqualTo(22.5f).Within(1e-4f));
        Assert.That(StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(46, 36, 0.25f), Is.EqualTo(32.5f).Within(1e-4f));
        // 速さは連続: 上げ終わりの直前と直後の 1 フレームの進みはどちらも約 1。
        float before = StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(36, 36, 0.25f) - StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(35, 36, 0.25f);
        float after = StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(37, 36, 0.25f) - StreamingStereoVideoPlayer.ResolveElseCoastTravelFrames(36, 36, 0.25f);
        Assert.That(before, Is.EqualTo(1f).Within(0.03f));
        Assert.That(after, Is.EqualTo(1f).Within(1e-4f));
    }

    // 動画の終わりで打ち切る: 終わりまで映り続けるなら始めない、消えた後に終わりまで戻らなければ始める。
    [Test]
    public void StopsAtTheEndOfTheVideo()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(1000), 700, 720, 36, 72), Is.False);
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(PresentUntil(715), 700, 720, 36, 72), Is.True);
        Assert.That(StreamingStereoVideoPlayer.ResolveFrameOutLeavesSoon(null, 700, 720, 36, 72), Is.False);
    }
}
