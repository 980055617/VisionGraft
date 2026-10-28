using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 動物の下端合わせで伸ばした手足の分の下端を使わない（animalSupportBottomIgnoresLimbReach、2026-10-27 夜、10/28 から既定 ON、AnimalSupportBottom.partial.cs）と、
// 大きさを区間ごとに箱へ合わせる形の B（placementFollowVideoDepthSizeMatchesBox、VideoDepthFollowFeet.partial.cs、既定 OFF のまま）。
public class AnimalSupportBottomTests
{
    private static StreamingStereoVideoPlayer.VideoDepthFollowFeetSample Sample(int frame, float top, float bottom, bool cut = false)
    {
        return new StreamingStereoVideoPlayer.VideoDepthFollowFeetSample
        {
            frame = frame, shot = 0, bottom = bottom, height = bottom - top, z01 = 0.5f, bottomCut = cut, anyCut = cut,
        };
    }

    [Test]
    public void SupportOnAndMatchOffByDefault()
    {
        GameObject host = new GameObject("AnimalSupportBottomTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.animalSupportBottomIgnoresLimbReach, Is.True);
            Assert.That(player.placementFollowVideoDepthSizeMatchesBox, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // ソファの猫: 上端 290 のまま、下端が 366 から 400 へ 20 フレームだけ下がる（前足を伸ばす）。その間は下端から 30 px 前後引き、前後は 0。
    [Test]
    public void ReachDownWithAStillTopIsTakenOff()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 100; f++)
        {
            float bottom = f >= 40 && f < 60 ? 400f : 366f;
            samples.Add(Sample(f, 290f, bottom));
        }

        float[] c = StreamingStereoVideoPlayer.ResolveAnimalSupportBottomCorrections(samples, 24, 0.10f, 0.20f, 0.03f, 3);
        for (int f = 44; f < 56; f++)
        {
            Assert.That(c[f], Is.EqualTo(34f).Within(1f), $"frame {f}");
        }

        Assert.That(c[10], Is.EqualTo(0f));
        Assert.That(c[90], Is.EqualTo(0f));
    }

    // 体ごと下へ動く（上端も同じだけ下がる）・ずっとその位置に移る（中央値が追いつく）・出っ張りが小さい（高さの 8%）は引かない。切れたフレームも引かない。
    [Test]
    public void BodyMotionAStepOrASmallReachIsKept()
    {
        var body = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var step = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var small = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var cut = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 100; f++)
        {
            bool dip = f >= 40 && f < 60;
            body.Add(Sample(f, dip ? 324f : 290f, dip ? 400f : 366f));
            step.Add(Sample(f, 290f, f >= 50 ? 420f : 366f));
            small.Add(Sample(f, 290f, dip ? 372f : 366f));
            cut.Add(Sample(f, 290f, dip ? 719f : 366f, cut: dip));
        }

        float[] cb = StreamingStereoVideoPlayer.ResolveAnimalSupportBottomCorrections(body, 24, 0.10f, 0.20f, 0.03f, 3);
        float[] cs = StreamingStereoVideoPlayer.ResolveAnimalSupportBottomCorrections(step, 24, 0.10f, 0.20f, 0.03f, 3);
        float[] cm = StreamingStereoVideoPlayer.ResolveAnimalSupportBottomCorrections(small, 24, 0.10f, 0.20f, 0.03f, 3);
        float[] cc = StreamingStereoVideoPlayer.ResolveAnimalSupportBottomCorrections(cut, 24, 0.10f, 0.20f, 0.03f, 3);
        for (int f = 0; f < 100; f++)
        {
            Assert.That(cb[f], Is.EqualTo(0f).Within(1e-4f), $"body {f}");
            Assert.That(cm[f], Is.EqualTo(0f).Within(1e-4f), $"small {f}");
            Assert.That(cc[f], Is.EqualTo(0f).Within(1e-4f), $"cut {f}");
            if (f < 47 || f > 53)
            {
                Assert.That(cs[f], Is.EqualTo(0f).Within(1e-4f), $"step {f}");
            }
        }
    }

    // B: 動いている間は箱の高さ（ならした値）、その場の区間は区切りの中央値で一定。区間の中で高さが 15% より 12 フレーム続けて外れたら区切る。
    [Test]
    public void MatchBoxHeightsAreFlatInsideAStillSegmentAndSplitOnAPostureChange()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var still = new List<bool>();
        for (int f = 0; f < 200; f++)
        {
            // 0〜59 歩いて近づく（高さ 100 → 159）、60〜199 その場。60〜119 は高さ 160 前後（±3）、120 から座って 230
            float h = f < 60 ? 100f + f : f < 120 ? 160f + 3f * Mathf.Sin(f) : 230f;
            samples.Add(Sample(f, 500f - h, 500f));
            still.Add(f >= 60);
        }

        StreamingStereoVideoPlayer.ResolveVideoDepthFollowMatchBoxHeights(samples, still.ToArray(), 0.15f, 12, 6, out float[] boxH, out int[] segment);
        Assert.That(segment[30], Is.EqualTo(-1));
        Assert.That(boxH[30], Is.EqualTo(130f).Within(1.5f));
        Assert.That(segment[80], Is.EqualTo(0));
        Assert.That(segment[170], Is.EqualTo(1));
        for (int f = 72; f < 105; f++)
        {
            Assert.That(boxH[f], Is.EqualTo(boxH[80]).Within(1e-3f), $"flat segment 0 at {f}");
        }

        for (int f = 140; f < 194; f++)
        {
            Assert.That(boxH[f], Is.EqualTo(230f).Within(1e-3f), $"flat segment 1 at {f}");
        }

        Assert.That(boxH[80], Is.EqualTo(160f).Within(3.5f));
    }

    // 形を測ってよいフレーム: 区切りに入って 12 フレーム以上、かつ直近 12 フレームの高さが区切りの中央値の ±8% 以内。立ち上がる途中（高さが伸びている間）は
    // 測らず、伸びきってから 12 フレームで測る。動いているフレームは測らない。
    [Test]
    public void MatchReadyWaitsUntilThePostureSettles()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var segment = new List<int>();
        for (int f = 0; f < 120; f++)
        {
            // 0〜19 動いている、20〜119 は 1 つの区切り: 20〜39 で高さ 300 → 420 へ伸び（立ち上がる）、40 から 420
            float h = f < 40 ? 300f + Mathf.Max(0, f - 20) * 6f : 420f;
            samples.Add(Sample(f, 500f - h, 500f));
            segment.Add(f < 20 ? -1 : 0);
        }

        bool[] ready = StreamingStereoVideoPlayer.ResolveVideoDepthFollowMatchReady(samples, segment.ToArray(), 12, 0.08f);
        Assert.That(ready[10], Is.False);
        Assert.That(ready[32], Is.False, "12 frames into the segment but still rising");
        int first = System.Array.IndexOf(ready, true);
        // 中央値 420 の ±8%（386〜454）に入るのは 35 フレーム目（390）から: 35 + 11 = 46 で 12 フレームそろう
        Assert.That(first, Is.InRange(44, 47));
        Assert.That(ready[100], Is.True);
    }
}
