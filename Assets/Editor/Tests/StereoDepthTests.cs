using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// S（2026-10-16）: 視差 → 見える奥行き Zv、目のまわりの k 倍で平面の絵が変わらないこと、F2 の枠、左右の目の撮影の窓。
public class StereoDepthTests
{
    private static readonly float W1 = 2f * Mathf.Tan(35f * Mathf.Deg2Rad);
    private const float E = 0.063f;
    private const float S0 = 0.001f;
    private const float Fx = 1.4281480f;
    private const float Fy = 2.5389298f;

    private static float Zv(float z01, float d = 1f, float e = E)
    {
        float dPx = StereoVideoDepthContract.BakedDisparityPx(1f - z01);
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(dPx, d, W1 * d, S0, e, out float z), Is.True);
        return z;
    }

    private static float ZvFromD(float dPx)
    {
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(dPx, 1f, W1, S0, E, out float z), Is.True);
        return z;
    }

    [Test]
    public void BakedDisparityFollowsTheSplatContract()
    {
        Assert.That(StereoVideoDepthContract.BakedDisparityPx(1f - 0f), Is.EqualTo(20f).Within(1e-5f));
        Assert.That(StereoVideoDepthContract.BakedDisparityPx(1f - 0.5f), Is.EqualTo(0f).Within(1e-5f));
        Assert.That(StereoVideoDepthContract.BakedDisparityPx(1f - 1f), Is.EqualTo(-20f).Within(1e-5f));
        Assert.That(StereoVideoDepthContract.BakedDisparityPx(1f - 0.416f), Is.EqualTo(3.36f).Within(1e-4f));
    }

    [Test]
    public void PerceivedDepthMatchesThePortedValues()
    {
        Assert.That(Zv(0.5f), Is.EqualTo(1.03279f).Within(2e-5f));
        Assert.That(Zv(0f), Is.EqualTo(0.76012f).Within(2e-5f));
        Assert.That(Zv(1f), Is.EqualTo(1.61049f).Within(2e-5f));
        Assert.That(Zv(0.416f), Is.EqualTo(0.97408f).Within(2e-5f));
        Assert.That(ZvFromD(3.94f), Is.EqualTo(0.9646f).Within(1e-4f));
        Assert.That(ZvFromD(6.94f), Is.EqualTo(0.9185f).Within(1e-4f));
    }

    [Test]
    public void CrossedDisparityIsNearer()
    {
        Assert.That(Zv(0.3f), Is.LessThan(Zv(0.4f)));
    }

    [Test]
    public void IpdChangesTheDepthAsPorted()
    {
        Assert.That(Zv(0.75f, 1f, 0.058f), Is.EqualTo(1.28719f).Within(2e-5f));
        Assert.That(Zv(0.75f, 1f, 0.068f), Is.EqualTo(1.23503f).Within(2e-5f));
    }

    [Test]
    public void ModelDisparityRoundTripsToTheVideoOnScreenDisparity()
    {
        foreach (float d in new[] { -20f, -7f, 0f, 3.94f, 20f })
        {
            float z = ZvFromD(d);
            float model = StereoVideoDepthContract.ModelDisparityPx(z, 1f, W1, E);
            float video = StereoVideoDepthContract.VideoOnScreenDisparityPx(d, W1, S0);
            Assert.That(model, Is.EqualTo(video).Within(1e-3f));
        }
    }

    [Test]
    public void PlateOffsetStaysInMetresAtTwoMetres()
    {
        Assert.That(Zv(0.5f, 2f), Is.EqualTo(2.06557f).Within(2e-5f));
    }

    [Test]
    public void PoleAndNonFiniteInputsAreRejected()
    {
        float w3 = W1 * 3f;
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(StereoVideoDepthContract.BakedDisparityPx(0f), 3f, w3, S0, E, out _), Is.False);      // z01 1
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(StereoVideoDepthContract.BakedDisparityPx(0.03f), 3f, w3, S0, E, out _), Is.False);   // z01 0.97
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(StereoVideoDepthContract.BakedDisparityPx(0.1f), 3f, w3, S0, E, out float z), Is.True); // z01 0.9
        Assert.That(z, Is.EqualTo(22.28f).Within(0.05f));
        Assert.That(StereoVideoDepthContract.TryPerceivedDepth(float.NaN, 1f, W1, S0, E, out _), Is.False);
    }

    [Test]
    public void PinholeProjectionIsInvariantUnderScalingAboutTheEye()
    {
        var m = new ManifestData { eye_w = 1280, eye_h = 720 };
        Vector3 p = new Vector3(0.12f, -0.07f, 0.65f);
        Assert.That(PinholePlacementSpace.TryProjectCamLocalToEyePixel(m, p, Fx, Fy, out Vector2 a), Is.True);
        foreach (float k in new[] { 0.7f, 1.53f, 1.85f })
        {
            Assert.That(PinholePlacementSpace.TryProjectCamLocalToEyePixel(m, p * k, Fx, Fy, out Vector2 b), Is.True);
            Assert.That((b - a).magnitude, Is.LessThan(1e-3f));
        }
    }

    private static StreamingStereoVideoPlayer.ExactScaleRefineSolver.Setup SolverSetup(float k, Vector3 pivot, StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode fit, params Vector3[] pts)
    {
        var list = new List<Vector3>();
        foreach (Vector3 p in pts) list.Add(p * k);
        return new StreamingStereoVideoPlayer.ExactScaleRefineSolver.Setup
        {
            points = list,
            pivot = pivot * k,
            aabbCenter = new Vector3(0f, 0.01f, 0.71f) * k,
            aabbVerticalExtent = 0.14f * k,
            fitMode = fit,
            fitTargetV = 520f,
            bottomUsesPointDepth = true,
            manifest = new ManifestData { eye_w = 1280, eye_h = 720 },
            fx = Fx,
            fy = Fy,
            cyPixels = 360f,
        };
    }

    [Test]
    public void ExactScaleSolverIsInvariantUnlessTheNearLimitBinds()
    {
        var fit = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.Bottom;
        Vector3[] pts = { new Vector3(0.02f, 0.15f, 0.62f), new Vector3(-0.03f, -0.12f, 0.80f), new Vector3(0.05f, 0.02f, 0.70f) };
        Vector3 pivot = new Vector3(0f, 0f, 0.72f);
        var s1 = SolverSetup(1f, pivot, fit, pts);
        float goal = 1.3f * StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(s1, 1f);
        var r1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s1, goal, 0.25f);
        Assert.That(r1.solved && !r1.clamped, Is.True);
        foreach (float k in new[] { 1.53f, 1.85f })
        {
            var rk = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(SolverSetup(k, pivot, fit, pts), goal, 0.25f);
            Assert.That(rk.solved && !rk.clamped, Is.True);
            Assert.That(rk.g, Is.EqualTo(r1.g).Within(1e-4f));
        }

        // 0.25 m の拡大の上限が効く設定では、k 倍で上限が変わる（不変でないことの記録）。
        var none = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.None;
        Vector3 nearPivot = new Vector3(0f, 0f, 0.5f);
        Vector3[] nearPts = { new Vector3(0f, 0.1f, 0.3f), new Vector3(0f, -0.1f, 0.6f) };
        var n1 = SolverSetup(1f, nearPivot, none, nearPts);
        float nearGoal = 1.6f * StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(n1, 1f);
        var c1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(n1, nearGoal, 0.25f);
        var c15 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(SolverSetup(1.5f, nearPivot, none, nearPts), nearGoal, 0.25f);
        Assert.That(c1.clamped, Is.True);
        Assert.That(c1.g, Is.EqualTo(1.25f).Within(1e-4f));
        Assert.That(c15.clamped, Is.False);
        Assert.That(c15.upperLimit, Is.GreaterThan(c1.upperLimit));
    }

    [Test]
    public void ExactDepthSolveIsInvariantUnderScalingAboutTheEye()
    {
        var m = new ManifestData { eye_w = 1280, eye_h = 720 };
        float[] py = { 0.15f, -0.12f, 0.02f };
        float[] pz = { 0.62f, 0.80f, 0.70f };
        bool[] bone = { true, true, true };
        PinholePlacementSpace.SecondFitModel Fit(float k) => new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Bottom, targetV = 520f, aabbCenterY = 0.01f * k, aabbCenterZ = 0.71f * k,
            aabbVerticalExtent = 0.14f * k, cyPixels = 360f, bottomUsesPointDepth = true,
        };
        float extent1 = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, py, pz, bone, 3, 0f, 0.72f, 1f, Fit(1f));
        float target = 0.8f * extent1;
        Assert.That(PinholePlacementSpace.TrySolveDepthMultiplierForExtent(m, Fy, py, pz, bone, 3, 0f, 0.72f, Fit(1f), target, 1.25f,
            out float s1, out _, out _, out bool sat1), Is.True);
        Assert.That(sat1, Is.False);
        foreach (float k in new[] { 1.53f, 1.85f })
        {
            float[] pyk = { py[0] * k, py[1] * k, py[2] * k };
            float[] pzk = { pz[0] * k, pz[1] * k, pz[2] * k };
            Assert.That(PinholePlacementSpace.TrySolveDepthMultiplierForExtent(m, Fy, pyk, pzk, bone, 3, 0f, 0.72f * k, Fit(k), target, 1.25f,
                out float sk, out _, out _, out bool satk), Is.True);
            Assert.That(satk, Is.False);
            Assert.That(sk, Is.EqualTo(s1).Within(1e-4f));
        }
    }

    private static Vector3 Path(int i) => new Vector3(0.4f * Mathf.Sin(i * 0.05f), 0.1f * Mathf.Cos(i * 0.03f), 0.7f + 0.05f * Mathf.Sin(i * 0.02f));

    // 今の 2 引数の Filter は 3 引数に 1 を渡すだけなので、今のコードどうしを比べても S の前の式からの変化は見えない。
    // eb4e034 の式（下の写し）と、2 引数・betaScale = 1 の 3 引数のビットの一致を見る。パラメータは root の位置の平滑（AnimalMotionFilter.cs:85）と
    // 向き・上の平滑の既定（AnimalFilterConfig.Default）。dt は一定と、揺らぎ・0（Max(0.0001) に丸まる）・0.1 s の詰まりを混ぜたもの。途中で Shift も通す。
    // EditMode（Mono）での一致。IL2CPP での一致は beta × 1 が厳密なことに拠る。
    [Test]
    public void OneEuroBetaScaleOneIsBitIdentical()
    {
        float[][] parameters = { new[] { 1.6f, 0.08f, 1f }, new[] { 1f, 0.15f, 1f } };
        foreach (float[] c in parameters)
        {
            foreach (bool varyingDt in new[] { false, true })
            {
                var reference = new Eb4e034OneEuroVector3(c[0], c[1], c[2]);
                var two = new RuntimeOneEuroVector3Filter(c[0], c[1], c[2]);
                var three = new RuntimeOneEuroVector3Filter(c[0], c[1], c[2]);
                for (int i = 0; i < 300; i++)
                {
                    if (i == 150)
                    {
                        Vector3 delta = new Vector3(0.013f, -0.007f, 0.021f);
                        reference.Shift(delta);
                        two.Shift(delta);
                        three.Shift(delta);
                    }

                    float dt = varyingDt ? VaryingDt(i) : 1f / 72f;
                    Vector3 expected = reference.Filter(Path(i), dt);
                    Vector3 a = two.Filter(Path(i), dt);
                    Vector3 b = three.Filter(Path(i), dt, 1f);
                    string at = $"params={c[0]},{c[1]},{c[2]} varyingDt={varyingDt} i={i}";
                    AssertSameBits(expected, a, at + " (2 args)");
                    AssertSameBits(expected, b, at + " (betaScale 1)");
                }
            }
        }
    }

    private static float VaryingDt(int i)
    {
        if (i % 17 == 5) return 0f;
        if (i % 23 == 11) return 0.1f;
        return (1f / 72f) * (1f + 0.4f * Mathf.Sin(i * 0.37f));
    }

    private static void AssertSameBits(Vector3 expected, Vector3 actual, string at)
    {
        Assert.AreEqual(Bits(expected.x), Bits(actual.x), at + " x");
        Assert.AreEqual(Bits(expected.y), Bits(actual.y), at + " y");
        Assert.AreEqual(Bits(expected.z), Bits(actual.z), at + " z");
    }

    private static int Bits(float f) => System.BitConverter.ToInt32(System.BitConverter.GetBytes(f), 0);

    // eb4e034 の Assets/Scripts/StereoPlayer/RuntimeOneEuroVector3Filter.cs をコメントを除いてそのまま写したもの（クラスの名前だけ変えた）。S の前の式の基準。
    private sealed class Eb4e034OneEuroVector3
    {
        private readonly OneEuroFilter1D x;
        private readonly OneEuroFilter1D y;
        private readonly OneEuroFilter1D z;

        public Eb4e034OneEuroVector3(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            x = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            y = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
            z = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
        }


        public Vector3 Filter(Vector3 value, float deltaTime)
        {
            return new Vector3(
                x.Filter(value.x, deltaTime),
                y.Filter(value.y, deltaTime),
                z.Filter(value.z, deltaTime));
        }


        public void Reset(Vector3 value)
        {
            x.Reset(value.x);
            y.Reset(value.y);
            z.Reset(value.z);
        }


        public void Shift(Vector3 delta)
        {
            x.Shift(delta.x);
            y.Shift(delta.y);
            z.Shift(delta.z);
        }


        public bool TryGetLag(out Vector3 lag)
        {
            lag = Vector3.zero;
            if (!x.TryGetLag(out float lx) || !y.TryGetLag(out float ly) || !z.TryGetLag(out float lz))
            {
                return false;
            }

            lag = new Vector3(lx, ly, lz);
            return true;
        }


        private sealed class LowPassFilter1D
        {
            private bool initialized;
            private float previousValue;

            public float Value => previousValue;

            public float Filter(float value, float alpha)
            {
                if (!initialized)
                {
                    initialized = true;
                    previousValue = value;
                    return value;
                }

                previousValue = alpha * value + (1f - alpha) * previousValue;
                return previousValue;
            }


            public void Reset(float value)
            {
                initialized = true;
                previousValue = value;
            }


            public void Shift(float delta)
            {
                if (initialized)
                {
                    previousValue += delta;
                }
            }
        }


        private sealed class OneEuroFilter1D
        {
            private readonly LowPassFilter1D valueFilter = new LowPassFilter1D();
            private readonly LowPassFilter1D derivativeFilter = new LowPassFilter1D();
            private readonly float minCutoff;
            private readonly float beta;
            private readonly float derivativeCutoff;
            private bool initialized;
            private float previousRawValue;

            public OneEuroFilter1D(float minCutoffHz, float betaValue, float derivativeCutoffHz)
            {
                minCutoff = Mathf.Max(0.0001f, minCutoffHz);
                beta = Mathf.Max(0f, betaValue);
                derivativeCutoff = Mathf.Max(0.0001f, derivativeCutoffHz);
            }


            public float Filter(float value, float deltaTime)
            {
                float dt = Mathf.Max(0.0001f, deltaTime);
                if (!initialized)
                {
                    initialized = true;
                    previousRawValue = value;
                    valueFilter.Reset(value);
                    derivativeFilter.Reset(0f);
                    return value;
                }

                float derivative = (value - previousRawValue) / dt;
                previousRawValue = value;
                float filteredDerivative = derivativeFilter.Filter(derivative, ComputeAlpha(derivativeCutoff, dt));
                float cutoff = minCutoff + beta * Mathf.Abs(filteredDerivative);
                return valueFilter.Filter(value, ComputeAlpha(cutoff, dt));
            }


            public void Reset(float value)
            {
                initialized = true;
                previousRawValue = value;
                valueFilter.Reset(value);
                derivativeFilter.Reset(0f);
            }


            public void Shift(float delta)
            {
                if (!initialized)
                {
                    return;
                }

                previousRawValue += delta;
                valueFilter.Shift(delta);
            }


            public bool TryGetLag(out float lag)
            {
                lag = 0f;
                if (!initialized)
                {
                    return false;
                }

                lag = valueFilter.Value - previousRawValue;
                return true;
            }


            private static float ComputeAlpha(float cutoff, float deltaTime)
            {
                float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
                return 1f / (1f + tau / Mathf.Max(0.0001f, deltaTime));
            }
        }
    }

    [Test]
    public void OneEuroWithBetaOverKFollowsScaledInputExactly()
    {
        Vector3 o = new Vector3(0.1f, 1.5f, -0.2f);
        const float k = 1.53f;
        var a = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        var b = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        for (int i = 0; i < 300; i++)
        {
            Vector3 x = o + Path(i);
            Vector3 ya = a.Filter(x, 1f / 72f);
            Vector3 yb = b.Filter(o + (x - o) * k, 1f / 72f, 1f / k);
            Assert.That((yb - (o + (ya - o) * k)).magnitude, Is.LessThan(2e-5f));
        }
    }

    [Test]
    public void OneEuroScaleAboutMidwayContinuesExactly()
    {
        Vector3 o = new Vector3(0.1f, 1.5f, -0.2f);
        const float k = 1.68f;
        var a = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        var b = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        for (int i = 0; i < 300; i++)
        {
            Vector3 x = o + Path(i);
            Vector3 ya = a.Filter(x, 1f / 72f);
            if (i < 120)
            {
                b.Filter(x, 1f / 72f);
                continue;
            }

            if (i == 120)
            {
                b.ScaleAbout(o, k);
            }

            Vector3 yb = b.Filter(o + (x - o) * k, 1f / 72f, 1f / k);
            Assert.That((yb - (o + (ya - o) * k)).magnitude, Is.LessThan(2e-5f));
        }
    }

    private const float Sw = 1.4f;
    private const float Sh = 0.7875f;
    private const float Band = 4.2f;

    private static float Area(List<Rect> rs)
    {
        float s = 0f;
        foreach (Rect r in rs) s += r.width * r.height;
        return s;
    }

    [Test]
    public void FrameBandsMinusPanelHoleAboveTheScreen()
    {
        var bands = new List<Rect>();
        StereoFrameOccluderGeometry.BuildBands(Sw, Sh, Band, bands);
        Assert.AreEqual(4, bands.Count);
        float hh = Sh * 0.5f;
        Rect hole = StereoFrameOccluderGeometry.ExtendToScreenEdge(Rect.MinMaxRect(-0.2f, hh + 0.012f, 0.2f, hh + 0.2f), Sw, Sh, 0.06f);
        Assert.That(hole.yMin, Is.EqualTo(hh).Within(1e-6f));
        var outRects = new List<Rect>();
        foreach (Rect r in bands) StereoFrameOccluderGeometry.Subtract(r, hole, outRects);
        Assert.AreEqual(6, outRects.Count);
        // Rect は（x, y, 幅, 高さ）で持ち xMax = 幅 + x なので、穴の縁で切った帯の端は丸めで 2e-7 m ほど穴へ入る（float32 の移植で確かめた。絵には効かない）。
        // 0.01 mm 内側に縮めた穴と重ならないことを見る。
        Rect holeInside = Rect.MinMaxRect(hole.xMin + 1e-5f, hole.yMin + 1e-5f, hole.xMax - 1e-5f, hole.yMax - 1e-5f);
        foreach (Rect r in outRects) Assert.That(r.Overlaps(holeInside), Is.False);
        Assert.That(Area(outRects), Is.EqualTo(Area(bands) - hole.width * hole.height).Within(1e-4f));
    }

    [Test]
    public void HoleInsideTheScreenIsDetected()
    {
        Assert.That(StereoFrameOccluderGeometry.IsInsideScreen(Rect.MinMaxRect(-Sw * 0.5f - 0.001f, -Sh * 0.5f, Sw * 0.5f - 0.001f, Sh * 0.5f), Sw, Sh), Is.True);
        Assert.That(StereoFrameOccluderGeometry.IsInsideScreen(Rect.MinMaxRect(-0.2f, Sh * 0.5f - 0.1f, 0.2f, Sh * 0.5f + 0.1f), Sw, Sh), Is.False);
    }

    [Test]
    public void ControlBarHoleSplitsTheBottomBand()
    {
        var bands = new List<Rect>();
        StereoFrameOccluderGeometry.BuildBands(Sw, Sh, Band, bands);
        float hh = Sh * 0.5f;
        Rect hole = StereoFrameOccluderGeometry.ExtendToScreenEdge(Rect.MinMaxRect(-0.3f, -hh - 0.3f, 0.3f, -hh - 0.03f), Sw, Sh, 0.06f);
        Assert.That(hole.yMax, Is.EqualTo(-hh).Within(1e-6f));
        var outRects = new List<Rect>();
        foreach (Rect r in bands) StereoFrameOccluderGeometry.Subtract(r, hole, outRects);
        Assert.AreEqual(6, outRects.Count);
        var untouched = new List<Rect>();
        StereoFrameOccluderGeometry.Subtract(bands[0], Rect.MinMaxRect(10f, 10f, 11f, 11f), untouched);
        Assert.AreEqual(1, untouched.Count);
        Assert.AreEqual(bands[0], untouched[0]);
    }

    [Test]
    public void StereoWindowPixelMatchesTheFrustumAndGivesTheModelDisparity()
    {
        const int eyeW = 1280, eyeH = 720, margin = 160;
        float h = W1 * eyeH / eyeW;
        float pitch = W1 / eyeW;
        float halfW = W1 * 0.5f + margin * pitch;
        float halfH = h * 0.5f + margin * pitch;
        int outW = eyeW + 2 * margin, outH = eyeH + 2 * margin;
        Vector3 center = new Vector3(0f, 0f, 1f);
        Vector3 eL = new Vector3(-E * 0.5f, 0f, 0f);
        Vector3 eR = new Vector3(E * 0.5f, 0f, 0f);
        Vector3 onPlane = new Vector3(0.21f, -0.13f, 1f);
        Vector2 qL = BatchPlaybackLogger.StereoCaptureMath.WindowPixel(onPlane - eL, center - eL, halfW, halfH, outW, outH);
        Vector2 qR = BatchPlaybackLogger.StereoCaptureMath.WindowPixel(onPlane - eR, center - eR, halfW, halfH, outW, outH);
        Assert.That((qL - qR).magnitude, Is.LessThan(1e-3f));
        foreach (float z in new[] { 0.6f, 0.97f, 1.4f })
        {
            Vector3 p = new Vector3(0.1f, 0.05f, z);
            Vector2 uL = BatchPlaybackLogger.StereoCaptureMath.WindowPixel(p - eL, center - eL, halfW, halfH, outW, outH);
            Vector2 uR = BatchPlaybackLogger.StereoCaptureMath.WindowPixel(p - eR, center - eR, halfW, halfH, outW, outH);
            Assert.That(uL.x - uR.x, Is.EqualTo(StereoVideoDepthContract.ModelDisparityPx(z, 1f, W1, E)).Within(1e-2f));
            Matrix4x4 proj = BatchPlaybackLogger.StereoCaptureMath.WindowFrustum(center - eL, halfW, halfH, 0.05f, 100f);
            Vector3 pc = p - eL;
            Vector4 clip = proj * new Vector4(pc.x, pc.y, -pc.z, 1f);
            Vector2 viaFrustum = new Vector2((clip.x / clip.w + 1f) * 0.5f * outW, (1f - clip.y / clip.w) * 0.5f * outH);
            Assert.That((viaFrustum - uL).magnitude, Is.LessThan(1e-2f));
        }
    }
}

// S（2026-10-16、delta P）: 実行時の状態の単位（人の根の奥行きの平滑・⑨ の差の平滑・⑧ の順序の丸め・人の Contract の代わり）。
// EditMode では Awake が走らない（manifest・vp は null、GetCurrentPlaybackFrame は 0）。動画フレームの前進は、平滑の「最後に進めたフレーム」を -1 にして作る
// （DepthRefineUniformSmoothingTests と同じ手）。
public class StereoDepthPlayerStateTests
{
    private const byte PersonCategory = 0;
    private const byte OtherCategory = 1;
    private GameObject host;
    private StreamingStereoVideoPlayer player;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("StereoDepthPlayerStateTests");
        player = host.AddComponent<StreamingStereoVideoPlayer>();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(host);
    }

    // 人の根の奥行きの平滑（HumanSmpl.partial.cs:661-691）は、確定（× m）・外す（÷ k）・作り直し（× kEntry）で状態を直せば、ずっと目のまわりに k 倍のまま。
    [Test]
    public void HumanRootDepthEmaStaysKTimesOffAcrossSettleUndoAndRelock()
    {
        var off = new HumanoidRigCache();
        var s = new HumanoidRigCache();
        Dictionary<object, int> frames = GetPrivateField<Dictionary<object, int>>("smoothingVideoFrameByKey");
        Vector3 eye = new Vector3(0.1f, 1.5f, -0.2f);
        float k = 1.1143f;
        for (int i = 0; i < 120; i++)
        {
            Vector3 x = eye + new Vector3(0.05f * Mathf.Sin(i * 0.07f), -0.2f, 0.85f + 0.1f * Mathf.Sin(i * 0.05f));
            frames[off] = -1;
            frames[s] = -1;
            Vector3 y0 = InvokeEma(off, x, eye);
            Vector3 yS = InvokeEma(s, eye + (x - eye) * k, eye);
            Assert.That((yS - (eye + (y0 - eye) * k)).magnitude, Is.LessThan(2e-5f), $"i={i}");
            if (i == 0) { InvokeScaleEma(s, 1.0167f); k *= 1.0167f; }   // 確定（同じ tick）
            if (i == 60) { InvokeScaleEma(s, 1f / k); k = 1f; }        // 外す
            if (i == 80) { InvokeScaleEma(s, 1.2f); k = 1.2f; }        // 作り直し（視聴距離）
        }
    }

    // 直さないと、同じ動画フレームの残りの tick（alpha = 0）で前の k の奥行きのまま（Hb の f0 で 1.7 %）。直しが要ることの記録。
    [Test]
    public void HumanRootDepthEmaWithoutTheRescaleKeepsTheEntryDepth()
    {
        var off = new HumanoidRigCache();
        var s = new HumanoidRigCache();
        Dictionary<object, int> frames = GetPrivateField<Dictionary<object, int>>("smoothingVideoFrameByKey");
        Vector3 x = new Vector3(0f, -0.2f, 0.85f);
        frames[off] = -1;
        frames[s] = -1;
        InvokeEma(off, x, Vector3.zero);
        InvokeEma(s, x * 1.1143f, Vector3.zero);
        Vector3 y0 = InvokeEma(off, x, Vector3.zero);
        Vector3 yS = InvokeEma(s, x * (1.1143f * 1.0167f), Vector3.zero);
        Assert.That(yS.z / y0.z, Is.EqualTo(1.1143f).Within(1e-4f));
    }

    // ⑨ の差の平滑（OtherDepth.partial.cs:335）は S の分岐で k で割った長さで持つ。状態は S なしと同じ値、目標は k 倍（k が途中で変わっても）。
    // EditMode の Time.deltaTime が 0 なら平滑は素通し（alpha = 1）になり、単位だけを確かめる。
    [Test]
    public void FollowGapStateIsHeldInOffUnitsAndTheTargetIsKTimesOff()
    {
        player.otherDepthGapSmoothingSeconds = 1.2f;
        Dictionary<uint, float> gaps = GetPrivateField<Dictionary<uint, float>>("otherDepthGapByTrack");
        float k = 1.1143f;
        for (int i = 0; i < 90; i++)
        {
            float s0 = 0.95f + 0.05f * Mathf.Sin(i * 0.1f);   // 付いていく骨格の深さ（S なし）
            float g = 0.12f + 0.03f * Mathf.Cos(i * 0.13f);   // bundle の差（popout の差）
            float t0 = s0 - InvokeGap(10, s0 - (s0 - g));      // OtherDepth.partial.cs:328 と :335（k = 1 の今の式）
            float sk = s0 * k;
            float tk = InvokeFollow(11, sk, sk - g * k, k);   // S の分岐
            Assert.That(gaps[11], Is.EqualTo(gaps[10]).Within(2e-6f), $"i={i}");
            Assert.That(tk, Is.EqualTo(t0 * k).Within(4e-6f), $"i={i}");
            if (i == 30) k *= 1.0167f;   // 確定
            if (i == 60) k = 0.9625f;    // 作り直し（k < 1 も）
        }
    }

    // ⑧ の順序の丸め（U3 が OFF のとき）は、S の複製（anchorZ と currentZ が k 倍）でも同じ比を返す（C7 の 6.6）。ボールが奥（上限）・手前（下限）の両方。
    [Test]
    public void OrderClampReturnsTheSameRatioUnderTheStereoFactor()
    {
        Dictionary<byte, string> names = GetPrivateField<Dictionary<byte, string>>("categoryNames");
        names[PersonCategory] = "person";
        names[OtherCategory] = "other";
        List<StreamingStereoVideoPlayer.MetaObj> frameObjects = GetPrivateField<List<StreamingStereoVideoPlayer.MetaObj>>("metaFrameObjects");
        player.depthRefineUniformSmoothing = false;
        player.projectedDepthOrderEpsilonMeters = 0.04f;
        player.projectedDepthScaleK = 1f;
        const float k = 1.1328f;
        foreach (float ballZ in new[] { 0.85f, 0.70f })
        {
            var person = new StreamingStereoVideoPlayer.MetaObj { trackId = 0, categoryId = PersonCategory, anchorZ = 0.80f };
            frameObjects.Clear();
            frameObjects.Add(person);
            frameObjects.Add(new StreamingStereoVideoPlayer.MetaObj { trackId = 1, categoryId = OtherCategory, anchorZ = ballZ });
            SetStereoFactor(0, 0f);
            float high0 = InvokeClamp(person, 0.80f, 1.2f);
            float low0 = InvokeClamp(person, 0.80f, 0.8f);
            SetStereoFactor(0, k);
            StreamingStereoVideoPlayer.MetaObj personS = person;
            personS.anchorZ = 0.80f * k;
            Assert.That(InvokeClamp(personS, 0.80f * k, 1.2f), Is.EqualTo(high0).Within(1e-6f), $"ball {ballZ}");
            Assert.That(InvokeClamp(personS, 0.80f * k, 0.8f), Is.EqualTo(low0).Within(1e-6f), $"ball {ballZ}");
        }
    }

    // 役の骨（AniMer の 6 点）を持たないリグ（人・前肢の役が無い鳥）は、Contract を頼んでも AabbCenter に落ちる。
    [Test]
    public void ContractFallsBackToTheAabbCentreForARigWithoutAnimalRoles()
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            cube.transform.position = new Vector3(0.1f, -0.05f, 0.9f);
            cube.transform.localScale = Vector3.one * 0.2f;
            MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("TryResolveStereoMatchDepth", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] args = { cube, Vector3.zero, Quaternion.identity, StreamingStereoVideoPlayer.StereoDepthMatchPoint.Contract, 0f, 0f };
            Assert.That((bool)method.Invoke(player, args), Is.True);
            Assert.That(args[3], Is.EqualTo(StreamingStereoVideoPlayer.StereoDepthMatchPoint.AabbCenter));
            Assert.That((float)args[4], Is.EqualTo(0.9f).Within(1e-5f));
        }
        finally
        {
            Object.DestroyImmediate(cube);
        }
    }

    private Vector3 InvokeEma(HumanoidRigCache cache, Vector3 target, Vector3 eye)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("GetSmoothedSmplRootWorld", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (Vector3)method.Invoke(player, new object[] { cache, target, eye, Vector3.forward });
    }

    private void InvokeScaleEma(HumanoidRigCache cache, float ratio)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("ScaleHumanSmplRootDepthStateForCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method.Invoke(player, new object[] { cache, ratio });
    }

    private float InvokeGap(uint trackId, float gap)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("SmoothOtherDepthGap", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (float)method.Invoke(player, new object[] { trackId, gap });
    }

    private float InvokeFollow(uint trackId, float skeletonZ, float rawTargetZ, float k)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("ResolveStereoFollowTargetZ", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (float)method.Invoke(player, new object[] { trackId, skeletonZ, rawTargetZ, k });
    }

    private float InvokeClamp(StreamingStereoVideoPlayer.MetaObj obj, float currentZ, float ratio)
    {
        MethodInfo method = typeof(StreamingStereoVideoPlayer).GetMethod("ClampRatioPreservingOtherOrder", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (float)method.Invoke(player, new object[] { obj, currentZ, ratio });
    }

    // k ≤ 0 は状態を消す。
    private void SetStereoFactor(uint trackId, float k)
    {
        var states = (IDictionary)typeof(StreamingStereoVideoPlayer).GetField("stereoDepthByTrack", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player);
        if (k <= 0f)
        {
            states.Remove(trackId);
            return;
        }

        System.Type stateType = typeof(StreamingStereoVideoPlayer).GetNestedType("StereoDepthTrackState", BindingFlags.NonPublic);
        object state = System.Activator.CreateInstance(stateType, true);
        stateType.GetField("k").SetValue(state, k);
        states[trackId] = state;
    }

    private T GetPrivateField<T>(string name) where T : class
    {
        FieldInfo field = typeof(StreamingStereoVideoPlayer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, name);
        return (T)field.GetValue(player);
    }
}
