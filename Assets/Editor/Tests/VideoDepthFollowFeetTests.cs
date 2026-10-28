using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// V の合わせる先の奥行きを足元から決める（placementFollowVideoDepthFromFeet、2026-10-27、10/28 から既定 ON、VideoDepthFollowFeet.partial.cs）:
// shot ごとの床の直線（近さ = 1 − z01 を箱の下端の行に当てる）と、フレームごとの z01（足元の行をならす・下が切れたら前の値）。
public class VideoDepthFollowFeetTests
{
    private static StreamingStereoVideoPlayer.VideoDepthFollowFeetSample Sample(int frame, float bottom, float z01, bool bottomCut = false, bool anyCut = false)
    {
        return new StreamingStereoVideoPlayer.VideoDepthFollowFeetSample
        {
            frame = frame, shot = 0, bottom = bottom, z01 = z01, bottomCut = bottomCut, anyCut = anyCut || bottomCut,
        };
    }

    // 歩いて近づく 200 フレームは床の直線（近さ 0.002 × 行 − 0.5）の上、その場の 60 フレーム（行 690 のまま）は深度マップが奥へ流れる（近さ 0.88 → 0.30）。
    private static List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample> WalkThenDriftInPlace()
    {
        var list = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 200; f++)
        {
            float b = 500f + f;
            float noise = 0.01f * Mathf.Sin(f * 1.7f);
            list.Add(Sample(f, b, 1f - (0.002f * b - 0.5f + noise)));
        }

        for (int k = 0; k < 60; k++)
        {
            float nearness = Mathf.Lerp(0.88f, 0.30f, k / 59f);
            list.Add(Sample(200 + k, 690f, 1f - nearness));
        }

        return list;
    }

    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("VideoDepthFollowFeetTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthFromFeet, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 直線は歩きの所で決まり、その場の流れは外れとして落ちる。その場のフレームの z01 は床の直線の値のまま一定（1 − (0.002 × 690 − 0.5) = 0.12）。
    [Test]
    public void FitsTheFloorLineAndIgnoresAnInPlaceDrift()
    {
        List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample> samples = WalkThenDriftInPlace();
        StreamingStereoVideoPlayer.VideoDepthFollowFeetFit fit = StreamingStereoVideoPlayer.FitVideoDepthFollowFeet(samples, 48, 40f, 0.5f);
        Assert.That(fit.mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Line));
        Assert.That(fit.slope, Is.EqualTo(0.002f).Within(0.0001f));
        Assert.That(fit.intercept, Is.EqualTo(-0.5f).Within(0.05f));
        float[] z01 = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetZ01(samples, fit, 6);
        for (int i = 210; i < 260; i++)
        {
            Assert.That(z01[i], Is.EqualTo(0.12f).Within(0.02f), $"sample {i}");
        }
    }

    // 足元の行がほとんど動かない shot は直線が決まらないので、z01 の中央値の一定値。
    [Test]
    public void NarrowRowsGiveTheMedianConstant()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 100; f++)
        {
            samples.Add(Sample(f, 640f + (f % 7) - 3f, f < 50 ? 0.2f : 0.6f + 0.001f * f));
        }

        StreamingStereoVideoPlayer.VideoDepthFollowFeetFit fit = StreamingStereoVideoPlayer.FitVideoDepthFollowFeet(samples, 48, 40f, 0.5f);
        Assert.That(fit.mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Constant));
        float[] z01 = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetZ01(samples, fit, 6);
        Assert.That(z01[0], Is.EqualTo(z01[99]));
        Assert.That(z01[0], Is.EqualTo(0.6f + 0.001f * 50).Within(1e-4f));
    }

    // フレームが足りない shot・足元が下（手前）ほど遠くなる shot（床の直線に当たらない）は使わない。
    [Test]
    public void TooFewFramesOrAWrongSlopeIsOff()
    {
        var few = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 30; f++)
        {
            few.Add(Sample(f, 500f + 5f * f, 1f - (0.002f * (500f + 5f * f) - 0.5f)));
        }

        Assert.That(StreamingStereoVideoPlayer.FitVideoDepthFollowFeet(few, 48, 40f, 0.5f).mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Off));

        var wrong = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 100; f++)
        {
            float b = 500f + 2f * f;
            wrong.Add(Sample(f, b, 0.002f * b - 0.5f));
        }

        StreamingStereoVideoPlayer.VideoDepthFollowFeetFit fit = StreamingStereoVideoPlayer.FitVideoDepthFollowFeet(wrong, 48, 40f, 0.5f);
        Assert.That(fit.mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Off));
        Assert.That(fit.reason, Is.EqualTo("slope"));
    }

    // 下が切れたフレームは足元が読めないので、その前の切れていないフレームの値。shot の頭から切れていれば最初の切れていないフレームの値。
    [Test]
    public void BottomCutFramesHoldTheNeighbouringValue()
    {
        var fit = new StreamingStereoVideoPlayer.VideoDepthFollowFeetFit
        {
            mode = StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Line, slope = 0.002f, intercept = -0.5f,
        };
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>
        {
            Sample(0, 719f, 0.9f, bottomCut: true),
            Sample(1, 600f, 0.3f),
            Sample(2, 600f, 0.3f),
            Sample(3, 719f, 0.9f, bottomCut: true),
            Sample(4, 719f, 0.9f, bottomCut: true),
        };

        float[] z01 = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetZ01(samples, fit, 0);
        float expected = 1f - (0.002f * 600f - 0.5f);
        Assert.That(z01[0], Is.EqualTo(expected).Within(1e-5f));
        Assert.That(z01[3], Is.EqualTo(expected).Within(1e-5f));
        Assert.That(z01[4], Is.EqualTo(expected).Within(1e-5f));
    }

    // 3D の大きさも足元の位置から決める（placementFollowVideoDepthSizeFromFeet、2026-10-27 夜、10/28 から既定 ON）。
    [Test]
    public void SizeFromFeetOnByDefault()
    {
        GameObject host = new GameObject("VideoDepthFollowFeetTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthSizeFromFeet, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 下端が 6 フレームごとに 6 px ずつ段で動くと、中央値だけの行は段のまま（隣のフレームとの差が 6 px）。もう一度平均すると坂になる（1.5 px 以下）。
    // 動かない所は平均しても同じ値。
    [Test]
    public void MeanPassTurnsMedianStepsIntoARamp()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 120; f++)
        {
            samples.Add(Sample(f, f < 60 ? 500f + 6f * (f / 6) : 560f, 0.5f));
        }

        float[] median = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetRows(samples, 2, 0);
        float[] smooth = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetRows(samples, 2, 6);
        float maxMedianStep = 0f;
        float maxSmoothStep = 0f;
        for (int i = 8; i < 52; i++)
        {
            maxMedianStep = Mathf.Max(maxMedianStep, Mathf.Abs(median[i + 1] - median[i]));
            maxSmoothStep = Mathf.Max(maxSmoothStep, Mathf.Abs(smooth[i + 1] - smooth[i]));
        }

        Assert.That(maxMedianStep, Is.EqualTo(6f).Within(1e-4f));
        Assert.That(maxSmoothStep, Is.LessThanOrEqualTo(1.5f));
        Assert.That(smooth[100], Is.EqualTo(560f).Within(1e-4f));
    }

    // 歩いて近づくフレームは log(箱の高さ) = 0.005 × 行 + 1.0 の上、途中でその場で座る 40 フレーム（行 600）は箱が 1.45 倍に縦に伸びる（姿勢の外れ）。
    // 直線は歩きの所で決まる。足元の行がほとんど動かない shot は Off。
    // 注: 頑健な当てはめ（FitRobustLine）は最小二乗から始めるので、外れの塊が行の範囲の端（いちばん手前・奥）にあると引かれる（10/27 の最初の版の
    // このテストは座る 60 フレームを行 690 の端に置き、傾き 0.0064 になった）。実際の猫の座る所（行 624）は歩く行の範囲（366〜644）の内側。
    [Test]
    public void SizeFitFollowsTheBoxLineAndIgnoresASittingPose()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        int frame = 0;
        for (int k = 0; k < 110; k++, frame++)
        {
            float b = 490f + k;
            StreamingStereoVideoPlayer.VideoDepthFollowFeetSample s = Sample(frame, b, 0.5f);
            s.height = Mathf.Exp(0.005f * b + 1.0f + 0.02f * Mathf.Sin(frame * 1.3f));
            samples.Add(s);
        }

        for (int k = 0; k < 40; k++, frame++)
        {
            StreamingStereoVideoPlayer.VideoDepthFollowFeetSample s = Sample(frame, 600f, 0.5f);
            s.height = 1.45f * Mathf.Exp(0.005f * 600f + 1.0f);
            samples.Add(s);
        }

        for (int k = 0; k < 100; k++, frame++)
        {
            float b = 600f + k;
            StreamingStereoVideoPlayer.VideoDepthFollowFeetSample s = Sample(frame, b, 0.5f);
            s.height = Mathf.Exp(0.005f * b + 1.0f + 0.02f * Mathf.Sin(frame * 1.3f));
            samples.Add(s);
        }

        float[] rows = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetRows(samples, 6, 6);
        StreamingStereoVideoPlayer.VideoDepthFollowFeetFit fit = StreamingStereoVideoPlayer.FitVideoDepthFollowFeetSize(samples, rows, 48, 40f, 0.5f);
        Assert.That(fit.mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Line));
        Assert.That(fit.slope, Is.EqualTo(0.005f).Within(0.0005f));
        Assert.That(Mathf.Exp(fit.slope * 600f + fit.intercept), Is.EqualTo(Mathf.Exp(0.005f * 600f + 1.0f)).Within(0.05f * Mathf.Exp(0.005f * 600f + 1.0f)));

        var narrow = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        for (int f = 0; f < 100; f++)
        {
            StreamingStereoVideoPlayer.VideoDepthFollowFeetSample s = Sample(f, 640f + (f % 7) - 3f, 0.5f);
            s.height = 300f + f;
            narrow.Add(s);
        }

        float[] narrowRows = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetRows(narrow, 6, 6);
        StreamingStereoVideoPlayer.VideoDepthFollowFeetFit narrowFit = StreamingStereoVideoPlayer.FitVideoDepthFollowFeetSize(narrow, narrowRows, 48, 40f, 0.5f);
        Assert.That(narrowFit.mode, Is.EqualTo(StreamingStereoVideoPlayer.VideoDepthFollowFeetMode.Off));
        Assert.That(narrowFit.reason, Is.EqualTo("narrow"));
    }

    // 並べた見積もりに候補を足したときの中央値（偶数個は上側）。要素が無ければ false。
    [Test]
    public void MedianWithCandidate()
    {
        var sorted = new List<float> { 1f, 3f, 5f };
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(sorted, false, 0f, out float m0), Is.True);
        Assert.That(m0, Is.EqualTo(3f));
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(sorted, true, 4f, out float m1), Is.True);
        Assert.That(m1, Is.EqualTo(4f));
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(sorted, true, 0f, out float m2), Is.True);
        Assert.That(m2, Is.EqualTo(3f));
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(new List<float>(), true, 2f, out float m3), Is.True);
        Assert.That(m3, Is.EqualTo(2f));
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(new List<float>(), false, 2f, out _), Is.False);
        Assert.That(StreamingStereoVideoPlayer.TryMedianWithCandidate(null, true, 2f, out float m4), Is.True);
        Assert.That(m4, Is.EqualTo(2f));
    }

    // 定数 c̄ はその場・中央値が無い・進んでいないなら動かない。ほかは 1 フレーム分（rate ÷ fps、対数）まで中央値へ寄る（何フレーム進んでも 1 フレーム分）。
    [Test]
    public void FeetSizeConstFreezesWhileStillAndLimitsTheRate()
    {
        float step = Mathf.Exp(0.24f / 24f);
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 2f, true, false, 1, 0.24f, 24f), Is.EqualTo(step).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 2f, true, false, 5, 0.24f, 24f), Is.EqualTo(step).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 0.5f, true, false, 1, 0.24f, 24f), Is.EqualTo(1f / step).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 1.001f, true, false, 1, 0.24f, 24f), Is.EqualTo(1.001f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 2f, true, true, 1, 0.24f, 24f), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 2f, false, false, 1, 0.24f, 24f), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.StepVideoDepthFollowFeetSizeConst(1f, 2f, true, false, 0, 0.24f, 24f), Is.EqualTo(1f));
    }
}
