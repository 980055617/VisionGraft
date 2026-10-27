using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// V（placementFollowVideoDepth、2026-10-26、既定 OFF、VideoDepthFollow.partial.cs）: f の進め方（StepVideoDepthFollowFactorMath）・巻き戻りと長い間の見分け
// （ClassifyVideoDepthFollowFrameJump）と、OFF のとき f を読む所（root の位置の平滑の beta・動作の長さ）が今と同じ値を返すこと。
public class VideoDepthFollowTests
{
    private const float Fps = 24f;
    private const float Tau = 0.3f;
    private const float EdgeTau = 0.25f;
    private const int SnapAfter = 48;
    private const int RewindFrames = 12;

    private static float Step(float current, float raw, bool first, int advanced, bool atEdge, StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode mode,
        float lockFactor, out bool snapped)
    {
        return StreamingStereoVideoPlayer.StepVideoDepthFollowFactorMath(current, raw, first, advanced, atEdge, mode, lockFactor, Fps, Tau, EdgeTau, SnapAfter,
            out snapped);
    }

    private static float Step(float current, float raw, bool first, int advanced, out bool snapped)
    {
        return Step(current, raw, first, advanced, false, StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.Follow, 1f, out snapped);
    }

    // ロックの最初の値・測りの側で戻った・長い間の後は平滑せずにその場の測り。
    [Test]
    public void FirstBackStepAndLongGapTakeTheMeasurement()
    {
        Assert.That(Step(1f, 1.33f, true, 5, out bool first), Is.EqualTo(1.33f));
        Assert.That(first, Is.True);
        Assert.That(Step(2.8f, 1.3f, false, -3, out bool back), Is.EqualTo(1.3f));
        Assert.That(back, Is.True);
        Assert.That(Step(2f, 1.2f, false, SnapAfter + 1, out bool gap), Is.EqualTo(1.2f));
        Assert.That(gap, Is.True);
    }

    // 1 動画フレームで対数を ⑧ と同じ重み（1 − e^(−1/fps/τ) = 0.1297）だけ寄せる。間が空いても（動作の音のフェードの 12 フレーム）1 フレーム分だけ。
    [Test]
    public void OneVideoFrameMovesTheLogByTheDepthSmoothingWeight()
    {
        float a = 1f - Mathf.Exp(-1f / Fps / Tau);
        float one = Step(1f, 2f, false, 1, out bool snapped);
        Assert.That(snapped, Is.False);
        Assert.That(Mathf.Log(one), Is.EqualTo(a * Mathf.Log(2f)).Within(1e-6f));
        Assert.That(Step(1f, 2f, false, 12, out _), Is.EqualTo(one).Within(1e-6f));
        Assert.That(Step(1.5f, 1.2f, false, 0, out _), Is.EqualTo(1.5f));
    }

    // NoGrow: 端のフレームは大きくしない（映像より奥へは行かない）。小さくはするが、1 % より小さく下がる測りでは動かさない（揺れで下へだけ動き続けない）。
    [Test]
    public void NoGrowAtTheEdgeShrinksButDoesNotGrow()
    {
        StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode noGrow = StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.NoGrow;
        Assert.That(Step(2f, 3f, false, 1, true, noGrow, 1.3f, out _), Is.EqualTo(2f));
        Assert.That(Step(2f, 1.5f, false, 1, true, noGrow, 1.3f, out _), Is.LessThan(2f).And.GreaterThan(1.5f));
        Assert.That(Step(2f, 1.99f, false, 1, true, noGrow, 1.3f, out _), Is.EqualTo(2f));
        Assert.That(Step(2f, 3f, false, 1, false, noGrow, 1.3f, out _), Is.GreaterThan(2f));
        Assert.That(Step(2f, 3f, false, 1, true, StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.Follow, 1.3f, out _), Is.GreaterThan(2f));
    }

    // EaseToLock: 端のフレームはこのロックの最初の f へ edgeTau で寄せる（大きくはしない）。測りがそれより小さければ測りへ。
    [Test]
    public void EaseToLockAtTheEdgeMovesTowardTheLockFactor()
    {
        StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode ease = StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.EaseToLock;
        float a = 1f - Mathf.Exp(-1f / Fps / EdgeTau);
        float next = Step(3.87f, 4.4f, false, 1, true, ease, 1.56f, out _);
        Assert.That(Mathf.Log(next), Is.EqualTo(Mathf.Log(3.87f) + a * (Mathf.Log(1.56f) - Mathf.Log(3.87f))).Within(1e-5f));
        Assert.That(Step(1.4f, 1.8f, false, 1, true, ease, 1.56f, out _), Is.EqualTo(1.4f));
        Assert.That(Step(3f, 1.2f, false, 1, true, ease, 1.56f, out _), Is.LessThan(Step(3f, 1.56f, false, 1, true, ease, 1.56f, out _)));
        float edgeFrames = 3.87f;
        for (int i = 0; i < 10; i++)
        {
            edgeFrames = Step(edgeFrames, 4.4f, false, 1, true, ease, 1.56f, out _);
        }

        // 犬の出口（端に掛かってから続きまで 10 フレーム）: 既定の 0.25 s で対数の 8 割より寄る（残りは (1 − 0.1535)^10 = 0.189）。
        Assert.That(Mathf.Log(edgeFrames / 1.56f), Is.LessThan(0.2f * Mathf.Log(3.87f / 1.56f)));
    }

    // Hold（2026-10-26 夜）: 端のフレームは大きくも小さくもしない。端でなければ測りへ寄せる。ロックの最初・長い間の後はその場の測り（ほかと同じ）。
    [Test]
    public void HoldAtTheEdgeKeepsTheFactor()
    {
        StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode hold = StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.Hold;
        Assert.That(Step(3.25f, 4.4f, false, 1, true, hold, 1.46f, out _), Is.EqualTo(3.25f));
        Assert.That(Step(3.25f, 1.2f, false, 1, true, hold, 1.46f, out _), Is.EqualTo(3.25f));
        Assert.That(Step(3.25f, 4.4f, false, 1, false, hold, 1.46f, out _), Is.GreaterThan(3.25f));
        Assert.That(Step(3.25f, 1.2f, true, 1, true, hold, 1.46f, out bool first), Is.EqualTo(1.2f));
        Assert.That(first, Is.True);
    }

    [Test]
    public void ZeroTauTakesTheGoal()
    {
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFactorMath(2f, 3f, false, 1, false, StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.Follow,
            1f, Fps, 0f, EdgeTau, SnapAfter, out _), Is.EqualTo(3f));
    }

    // 配置の前を前に通ったフレームとの比べ方: 12 フレームより大きく戻ったら rewind、48 フレームより大きく進んだら gap。小さく戻っただけ・
    // 止まっている（動作で動画が止まる）・1 フレームずつ進むのは null。
    [Test]
    public void FrameJumpsAreClassifiedFromTheLastPlacedFrame()
    {
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(0, 719, RewindFrames, SnapAfter), Is.EqualTo("rewind"));
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(103, 568, RewindFrames, SnapAfter), Is.EqualTo("rewind"));
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(700, 710, RewindFrames, SnapAfter), Is.Null);
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(400, 400, RewindFrames, SnapAfter), Is.Null);
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(413, 400, RewindFrames, SnapAfter), Is.Null);
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(449, 400, RewindFrames, SnapAfter), Is.EqualTo("gap"));
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowFrameJump(5, -1, RewindFrames, SnapAfter), Is.Null);
    }

    // 既定 OFF。状態が無ければ f を読む所は 1 を掛ける・割るだけで、今と同じ値。端の扱いの既定は NoGrow。
    [Test]
    public void OffByDefaultAndTheReadersSeeOne()
    {
        GameObject host = new GameObject("VideoDepthFollowTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepth, Is.False);
            Assert.That(player.placementFollowVideoDepthAtEdge, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowEdgeMode.NoGrow));
            Assert.That(player.placementFollowVideoDepthEdgeEaseSeconds, Is.EqualTo(EdgeTau));
            Assert.That(Invoke(player, "ResolveVideoDepthFollowFactor", 3u), Is.EqualTo(1f));
            Assert.That(Invoke(player, "ResolveStereoDepthRootFilterBetaScale", 3u), Is.EqualTo(1f));
            Assert.That(Invoke(player, "ResolveMotionLengthScale", 3u), Is.EqualTo(1f));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // その場（2026-10-27、ClassifyVideoDepthFollowStillFrames）: 足元の行が箱の高さの 8% より動かなければ、姿勢で箱が縦に伸びてもその場
    // （猫の 23〜25 秒: 下端 644 → 626、箱の高さ 288 → 400 超）。
    [Test]
    public void FeetStillWhileTheBoxGrowsInPlace()
    {
        var samples = new List<Vector3>();
        for (int f = 0; f < 48; f++)
        {
            samples.Add(new Vector3(f, 644f - 18f * Mathf.Clamp01((f - 24) / 12f), 288f + 4f * f));
        }

        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowStillFrames(samples, null, 12, 0.08f).Count, Is.EqualTo(48));
    }

    // 歩いて近づく（1 秒に 40 px、箱 200 px。犬の 8〜9 秒は 33 px）はその場にしない。
    [Test]
    public void WalkingCloserIsNotStill()
    {
        var samples = new List<Vector3>();
        for (int f = 0; f < 48; f++)
        {
            samples.Add(new Vector3(f, 592f + 40f * f / 24f, 200f));
        }

        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowStillFrames(samples, null, 12, 0.08f), Is.Empty);
    }

    // 窓は shot をまたがない（カットで下端が跳んでも両側はその場）。足元が少ない（欠け・切れの多い）フレームは決めない。
    [Test]
    public void StillWindowStaysInsideTheShotAndNeedsEnoughSamples()
    {
        var samples = new List<Vector3>();
        for (int f = 0; f < 48; f++)
        {
            samples.Add(new Vector3(f, f < 24 ? 400f : 600f, 300f));
        }

        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowStillFrames(samples, f => f < 24 ? 0 : 1, 12, 0.08f).Count, Is.EqualTo(48));
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowStillFrames(samples, null, 12, 0.08f).Contains(23), Is.False);

        var sparse = new List<Vector3> { new Vector3(0f, 400f, 300f), new Vector3(20f, 400f, 300f) };
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowStillFrames(sparse, null, 12, 0.08f), Is.Empty);
    }

    // その場の後の追いつきの時定数: 動き出した最初のフレームで長い値、追いつきの終わりで普段の値へまっすぐ戻る。範囲の外は普段の値。
    [Test]
    public void ReleaseTauRampsBackToTheNormalTau()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(1, 60, 0.3f, 1.2f), Is.EqualTo(1.2f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(31, 60, 0.3f, 1.2f), Is.EqualTo(0.75f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(60, 60, 0.3f, 1.2f), Is.EqualTo(0.315f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(0, 60, 0.3f, 1.2f), Is.EqualTo(0.3f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(61, 60, 0.3f, 1.2f), Is.EqualTo(0.3f));
        Assert.That(StreamingStereoVideoPlayer.ResolveVideoDepthFollowReleaseTau(5, 60, 0.3f, 0.2f), Is.EqualTo(0.3f));
    }

    // 大きさをゆっくり変える形の上限: 1 動画フレームあたり rate ÷ fps（対数）まで。進んだフレーム数ぶん広がる。上限 0 なら丸めない。
    [Test]
    public void SizeRateLimitClampsTheLogChangePerFrame()
    {
        float up = StreamingStereoVideoPlayer.LimitVideoDepthFollowFactorRate(2f, 4f, 0.08f, 1, 24f);
        Assert.That(Mathf.Log(up / 2f), Is.EqualTo(0.08f / 24f).Within(1e-6f));
        float down = StreamingStereoVideoPlayer.LimitVideoDepthFollowFactorRate(2f, 1f, 0.08f, 3, 24f);
        Assert.That(Mathf.Log(down / 2f), Is.EqualTo(-0.24f / 24f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.LimitVideoDepthFollowFactorRate(2f, 2.001f, 0.08f, 1, 24f), Is.EqualTo(2.001f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.LimitVideoDepthFollowFactorRate(2f, 4f, 0f, 1, 24f), Is.EqualTo(4f));
        Assert.That(StreamingStereoVideoPlayer.LimitVideoDepthFollowFactorRate(2f, 4f, 0.08f, 0, 24f), Is.EqualTo(4f));
    }

    // その場の切り替えは既定 OFF（OFF なら表を作らず、配置は今と同じ）。
    [Test]
    public void HoldWhileFeetStillIsOffByDefault()
    {
        GameObject host = new GameObject("VideoDepthFollowStillTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthHoldWhileFeetStill, Is.False);
            // ON にしたときも既定は動物だけ（人に効かせると、止まっては歩くテレビの女性が小さいまま近づいた）。
            Assert.That(player.placementFollowVideoDepthStillAnimalsOnly, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    private static float Invoke(StreamingStereoVideoPlayer player, string name, uint trackId)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, name);
        return (float)method.Invoke(player, new object[] { trackId });
    }
}
