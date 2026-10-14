using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class PinholePlacementSpaceTests
{
    [Test]
    public void TryResolveProjectionIntrinsicsKeepsExistingFovFallback()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 1920,
            eye_h = 1080
        };

        bool ok = PinholePlacementSpace.TryResolveProjectionIntrinsics(
            manifest,
            true,
            90f,
            out float fx,
            out float fy,
            out float cxPixels,
            out float cyPixels);

        Assert.That(ok, Is.True);
        Assert.That(fx, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(fy, Is.EqualTo(1920f / 1080f).Within(0.0001f));
        Assert.That(cxPixels, Is.EqualTo(960f).Within(0.0001f));
        Assert.That(cyPixels, Is.EqualTo(540f).Within(0.0001f));
    }

    [Test]
    public void ReconstructCamLocalFromEyePixelKeepsExistingPrincipalPointBehavior()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 1000,
            eye_h = 500,
            cx = 250f,
            cy = 0.25f
        };

        Vector3 camLocal = PinholePlacementSpace.ReconstructCamLocalFromEyePixel(
            manifest,
            500f,
            250f,
            2f,
            2f,
            4f,
            1000,
            500);

        AssertVector(camLocal, new Vector3(0.5f, -0.25f, 2f));
    }

    [Test]
    public void EyePixelDepthToWorldAppliesCameraPose()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 100,
            eye_h = 100
        };

        Vector3 world = PinholePlacementSpace.EyePixelDepthToWorld(
            new Vector3(1f, 2f, 3f),
            Quaternion.Euler(0f, 90f, 0f),
            manifest,
            50f,
            50f,
            2f,
            1f,
            1f);

        AssertVector(world, new Vector3(3f, 2f, 3f));
    }

    [Test]
    public void ProjectCamLocalToEyePixelRoundTripsReconstruction()
    {
        ManifestData manifest = new ManifestData
        {
            eye_w = 1000,
            eye_h = 500,
            cx = 250f,
            cy = 0.25f
        };

        Vector3 camLocal = PinholePlacementSpace.ReconstructCamLocalFromEyePixel(
            manifest,
            630f,
            310f,
            2.4f,
            2f,
            4f,
            1000,
            500);

        bool ok = PinholePlacementSpace.TryProjectCamLocalToEyePixel(
            manifest,
            camLocal,
            2f,
            4f,
            out Vector2 eyePixel);

        Assert.That(ok, Is.True);
        Assert.That(eyePixel.x, Is.EqualTo(630f).Within(0.0001f));
        Assert.That(eyePixel.y, Is.EqualTo(310f).Within(0.0001f));
    }

    // ---- ⑧ の厳密解（depthRefineExactSolve、2026-10-13、Docs/tmp/depthcap_20261013/README.md の U2）----

    private const float DepthSolveFy = 2.5389298f;

    [Test]
    public void DepthSolveEqualsLinearStepWhenEveryPointIsAtTheReferenceDepth()
    {
        // 全部の点が基準点と同じ深さなら 投影高 ∝ 1/深さ が厳密に成り立つので、解は今の一手（z_ref × 比）と一致する。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { -0.30f, -0.05f, 0.10f, 0.25f };
        float[] z = { 1.0f, 1.0f, 1.0f, 1.0f };
        bool[] isBone = { true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = NoSecondFit();
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 4, 0.02f, 1.0f, 1f, fit);
        const float ratio = 1.25f;

        bool ok = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 4, 0.02f, 1.0f, fit, extent / ratio, ratio,
            out float s, out int evaluations, out float residual, out bool saturated);

        Assert.That(ok, Is.True);
        Assert.That(saturated, Is.False);
        Assert.That(s, Is.EqualTo(ratio).Within(0.0005f));
        Assert.That(Mathf.Abs(residual), Is.LessThan(0.01f));
        Assert.That(evaluations, Is.LessThanOrEqualTo(30));
    }

    [Test]
    public void DepthSolveCorrectsTheLeverOfAReferenceBehindThePoints()
    {
        // 基準点（Generic リグの root）が骨より 0.3 m 奥にあると、今の一手（z_ref × 比）は奥へ行き過ぎて目標より小さく写る（てこ）。
        // 厳密解は目標に一致し、倍率は 1 と一手の比の間に入る。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.12f, -0.18f };
        float[] z = { 0.55f, 0.60f, 0.62f, 0.66f };
        bool[] isBone = { true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = NoSecondFit();
        const float refY = -0.10f;
        const float refZ = 0.92f;
        const float ratio = 1.3f;
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 4, refY, refZ, 1f, fit);
        float target = extent / ratio;
        float linearExtent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 4, refY, refZ, ratio, fit);

        bool ok = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 4, refY, refZ, fit, target, ratio,
            out float s, out _, out float residual, out bool saturated);

        Assert.That(linearExtent, Is.LessThan(target * 0.95f));
        Assert.That(ok, Is.True);
        Assert.That(saturated, Is.False);
        Assert.That(Mathf.Abs(residual), Is.LessThan(0.01f));
        Assert.That(s, Is.GreaterThan(1f));
        Assert.That(s, Is.LessThan(ratio));
    }

    [Test]
    public void DepthSolveMoveDoesNotDependOnTheReferenceRayInTopMode()
    {
        // 上端合わせ（Top）は AABB の上端を bbox の上端へ厳密に合わせるので、最後の配置の縦の投影は奥行きの移動量だけで決まる。
        // 基準点（光線）を替えても、解いた奥行きの移動量 (s − 1) × z_ref は同じになる。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.12f, -0.18f, 0.10f };
        float[] z = { 0.50f, 0.60f, 0.64f, 0.72f, 0.80f };
        bool[] isBone = { true, true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Top,
            targetV = 40f,
            aabbCenterY = 0.02f,
            aabbCenterZ = 0.62f,
            aabbVerticalExtent = 0.24f,
            cyPixels = 360f
        };
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, 1f, fit);
        float target = extent / 1.2f;

        bool okA = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, fit, target, 1.2f, out float sA, out _, out _, out _);
        bool okB = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, 0.12f, 0.58f, fit, target, 1.2f, out float sB, out _, out _, out _);

        Assert.That(okA && okB, Is.True);
        Assert.That((sA - 1f) * 0.92f, Is.EqualTo((sB - 1f) * 0.58f).Within(0.0002f));
    }

    [Test]
    public void DepthSolveMoveDoesNotDependOnTheReferenceRayWhenTheBottomUsesThePointDepth()
    {
        // 下端合わせを一番下の点自身の深さで換算すると（bottomUsesPointDepth）、その点が bbox の下端にちょうど来るので、
        // 一番下の点が入れ替わらない限り、同じく奥行きの移動量だけで決まる。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.10f, -0.32f, 0.10f };
        float[] z = { 0.50f, 0.60f, 0.64f, 0.66f, 0.80f };
        bool[] isBone = { true, true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Bottom,
            targetV = 650f,
            aabbCenterY = 0.0f,
            aabbCenterZ = 0.64f,
            aabbVerticalExtent = 0.28f,
            cyPixels = 360f,
            bottomUsesPointDepth = true
        };
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, 1f, fit);
        float target = extent / 1.2f;

        bool okA = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, fit, target, 1.2f, out float sA, out _, out _, out _);
        bool okB = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, 0.12f, 0.58f, fit, target, 1.2f, out float sB, out _, out _, out _);

        Assert.That(okA && okB, Is.True);
        Assert.That((sA - 1f) * 0.92f, Is.EqualTo((sB - 1f) * 0.58f).Within(0.0002f));
    }

    [Test]
    public void DepthSolveFindsTheSameRootFromAnInitialGuessOnTheWrongSide()
    {
        // 一手の比が逆向き（ここでは 0.8）でも、s = 1 での符号から向きを決めて囲い込むので、同じ解になる。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.12f, -0.18f };
        float[] z = { 0.55f, 0.60f, 0.62f, 0.66f };
        bool[] isBone = { true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = NoSecondFit();
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 4, -0.10f, 0.92f, 1f, fit);
        float target = extent / 1.3f;

        PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 4, -0.10f, 0.92f, fit, target, 1.3f, out float sGood, out _, out _, out _);
        bool ok = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 4, -0.10f, 0.92f, fit, target, 0.8f, out float sWrong, out _, out float residual, out _);

        Assert.That(ok, Is.True);
        Assert.That(sWrong, Is.EqualTo(sGood).Within(0.0005f));
        Assert.That(Mathf.Abs(residual), Is.LessThan(0.01f));
    }

    [Test]
    public void DepthSolveStopsAtTheNearEyeLimitWhenTheTargetCannotBeReached()
    {
        // 目の直前まで寄せても目標に届かない（目標が極端に大きい）ときは、その端の倍率で止めて saturated を返す。点は目の前に残る。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, -0.18f };
        float[] z = { 0.55f, 0.66f };
        bool[] isBone = { true, true };
        PinholePlacementSpace.SecondFitModel fit = NoSecondFit();

        bool ok = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 2, -0.10f, 0.92f, fit, 1e9f, 1.0f, out float s, out _, out _, out bool saturated);

        Assert.That(ok, Is.True);
        Assert.That(saturated, Is.True);
        Assert.That(s, Is.LessThan(1f));
        Assert.That(0.55f + (s - 1f) * 0.92f, Is.GreaterThan(0.0019f));
    }

    [Test]
    public void DepthSolveFailsWithoutPointsInFrontOfTheEye()
    {
        // 投影できる点が無い（どれも目の後ろか目の直前）なら解かない（呼び出し側は今の一手の比のまま）。
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, -0.18f };
        float[] z = { -0.10f, 0.0015f };
        bool[] isBone = { true, true };

        bool ok = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 2, -0.10f, 0.92f, NoSecondFit(), 300f, 1.2f, out _, out _, out _, out _);

        Assert.That(ok, Is.False);
    }

    // animalBottomClippedHeadPin（2026-10-14）: カメラの面内の移動で点がちょうど目標の画素へ来る（z は変えない）。
    [Test]
    public void CameraShiftHelpersPutThePointOnTheTargetPixel()
    {
        ManifestData manifest = new ManifestData { eye_w = 1280, eye_h = 720, cx = 640f, cy = 360f };
        const float fx = 1.4281480f;
        const float fy = 2.5389298f;
        Vector3 p = new Vector3(-0.07f, 0.11f, 0.83f);
        Assert.That(PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, p, fx, fy, out Vector2 px), Is.True);
        Vector2 target = new Vector2(px.x - 51.3f, px.y + 116.7f);

        Vector3 moved = p + new Vector3(
            PinholePlacementSpace.CameraShiftXToPutPointOnColumn(manifest.eye_w, fx, p.z, px.x, target.x),
            PinholePlacementSpace.CameraShiftYToPutPointOnRow(manifest.eye_h, fy, p.z, px.y, target.y),
            0f);

        Assert.That(PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, moved, fx, fy, out Vector2 after), Is.True);
        Assert.That(after.x, Is.EqualTo(target.x).Within(0.001f));
        Assert.That(after.y, Is.EqualTo(target.y).Within(0.001f));
    }

    // HeadPoint の 2 回目の ⑦ の写し: 手で「剛体で動かす → 頭を目標の行へ縦に動かす → 縦の広がり」を計算したものと一致する。
    [Test]
    public void HeadPointSecondFitMatchesAnExplicitMoveAndShift()
    {
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.12f, -0.18f, 0.10f };
        float[] z = { 0.50f, 0.60f, 0.64f, 0.72f, 0.80f };
        bool[] isBone = { true, true, true, true, true };
        const float headY = 0.21f;
        const float headZ = 0.55f;
        const float targetV = 180f;
        const float refY = -0.10f;
        const float refZ = 0.92f;
        const float s = 1.17f;
        PinholePlacementSpace.SecondFitModel fit = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.HeadPoint,
            targetV = targetV,
            pointY = headY,
            pointZ = headZ,
            aabbCenterY = 0.02f,
            aabbCenterZ = 0.62f,
            aabbVerticalExtent = 0.24f,
            cyPixels = 360f
        };

        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 5, refY, refZ, s, fit);

        float dy = (s - 1f) * refY;
        float dz = (s - 1f) * refZ;
        float hz = headZ + dz;
        float hv = (0.5f - ((headY + dy) * DepthSolveFy / hz) * 0.5f) * 720f;
        float shiftY = -((targetV - hv) * 2f / 720f) * (hz / DepthSolveFy);
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        for (int i = 0; i < 5; i++)
        {
            float zi = z[i] + dz;
            float v = (0.5f - ((y[i] + dy + shiftY) * DepthSolveFy / zi) * 0.5f) * 720f;
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }

        Assert.That(extent, Is.EqualTo(maxV - minV).Within(0.01f));
    }

    // HeadPoint でも、解いた奥行きの移動量 (s − 1) × z_ref は基準点（光線）に依らない（移動後の頭の行が固定されるので広がりは dz だけで決まる）。
    [Test]
    public void DepthSolveMoveDoesNotDependOnTheReferenceRayInHeadPointMode()
    {
        ManifestData manifest = DepthSolveManifest();
        float[] y = { 0.20f, 0.05f, -0.12f, -0.18f, 0.10f };
        float[] z = { 0.50f, 0.60f, 0.64f, 0.72f, 0.80f };
        bool[] isBone = { true, true, true, true, true };
        PinholePlacementSpace.SecondFitModel fit = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.HeadPoint,
            targetV = 150f,
            pointY = 0.21f,
            pointZ = 0.55f,
            aabbCenterY = 0.02f,
            aabbCenterZ = 0.62f,
            aabbVerticalExtent = 0.24f,
            cyPixels = 360f
        };
        float extent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, 1f, fit);
        float target = extent / 1.2f;

        bool okA = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, -0.10f, 0.92f, fit, target, 1.2f, out float sA, out _, out _, out _);
        bool okB = PinholePlacementSpace.TrySolveDepthMultiplierForExtent(
            manifest, DepthSolveFy, y, z, isBone, 5, 0.12f, 0.58f, fit, target, 1.2f, out float sB, out _, out _, out _);

        Assert.That(okA && okB, Is.True);
        Assert.That((sA - 1f) * 0.92f, Is.EqualTo((sB - 1f) * 0.58f).Within(0.0002f));
    }

    private static ManifestData DepthSolveManifest()
    {
        return new ManifestData
        {
            eye_w = 1280,
            eye_h = 720
        };
    }

    private static PinholePlacementSpace.SecondFitModel NoSecondFit()
    {
        return new PinholePlacementSpace.SecondFitModel { mode = PinholePlacementSpace.SecondFitMode.None };
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
    }
}


// exactScaleRefine の解き方（StreamingStereoVideoPlayer.ExactScaleRefineSolver、2026-10-13）。場面なしで camera 空間の点だけを渡す。
public class ExactScaleRefineSolverTests
{
    private const float Fx = 1.4281480f;
    private const float Fy = 2.5389298f;

    private static StreamingStereoVideoPlayer.ExactScaleRefineSolver.Setup MakeSetup(Vector3 pivot, params Vector3[] points)
    {
        return new StreamingStereoVideoPlayer.ExactScaleRefineSolver.Setup
        {
            points = new List<Vector3>(points),
            pivot = pivot,
            fitMode = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.None,
            bottomUsesPointDepth = true,
            manifest = new ManifestData { eye_w = 1280, eye_h = 720 },
            fx = Fx,
            fy = Fy,
            cyPixels = 360f,
        };
    }

    // 全部の点が中心と同じ深さなら広がりは倍率に比例するので、今の一手の式（目標 ÷ 広がり）と同じ答えになる。
    [Test]
    public void FlatModelGivesTheLinearFactor()
    {
        var s = MakeSetup(new Vector3(0f, 0f, 1f), new Vector3(0f, 0.1f, 1f), new Vector3(0.05f, -0.1f, 1f));
        float e1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(s, 1f);

        var r = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s, 1.7f * e1, 0.25f);

        Assert.That(r.solved, Is.True);
        Assert.That(r.g, Is.EqualTo(1.7f).Within(0.0005f));
        Assert.That(r.extentAtG, Is.EqualTo(1.7f * e1).Within(0.05f));
    }

    // 前後に広がった点では広がりが倍率に比例しない。解いた倍率で広がりが目標に一致し、一手の式とは違う答えになる。
    [Test]
    public void DepthSpreadModelHitsTheGoalUnlikeTheLinearFactor()
    {
        var s = MakeSetup(new Vector3(0f, 0f, 0.8f), new Vector3(0f, 0.15f, 0.55f), new Vector3(0f, -0.12f, 0.95f));
        float e1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(s, 1f);
        float goal = 1.4f * e1;

        var r = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s, goal, 0.25f);

        Assert.That(r.solved, Is.True);
        Assert.That(r.clamped, Is.False);
        Assert.That(r.extentAtG, Is.EqualTo(goal).Within(0.05f));
        Assert.That(Mathf.Abs(r.g - 1.4f), Is.GreaterThan(0.02f));
    }

    // 拡大で一番近い点が 0.25 m を割るなら、その手前の倍率で止める（clamped）。
    [Test]
    public void EnlargementStopsWhereTheNearestPointReachesTheFloor()
    {
        var s = MakeSetup(new Vector3(0f, 0f, 1f), new Vector3(0f, 0.1f, 0.5f), new Vector3(0f, -0.1f, 1.2f));
        float e1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(s, 1f);

        var r = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s, 10f * e1, 0.25f);

        Assert.That(r.solved, Is.True);
        Assert.That(r.clamped, Is.True);
        Assert.That(r.upperLimit, Is.EqualTo(1.5f).Within(0.0001f));
        Assert.That(r.g, Is.EqualTo(1.5f).Within(0.0001f));
        float nearestZ = 1f + r.g * (0.5f - 1f);
        Assert.That(nearestZ, Is.EqualTo(0.25f).Within(0.0001f));
    }

    [Test]
    public void PivotInsideTheFloorIsNotSolved()
    {
        var s = MakeSetup(new Vector3(0f, 0f, 0.2f), new Vector3(0f, 0.1f, 0.2f), new Vector3(0f, -0.1f, 0.3f));

        var r = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s, 100f, 0.25f);

        Assert.That(r.solved, Is.False);
        Assert.That(r.failure, Is.EqualTo("exactPivotNear"));
    }

    // ⑦ の下端合わせを一番下の点の深さで換算すると、合わせた後の広がりは合わせる前の縦の位置に依らない（AABB の深さで換算すると依る）。
    [Test]
    public void BottomFitWithPointDepthIsIndependentOfTheStartingHeight()
    {
        Vector3[] pts = { new Vector3(0f, 0.25f, 0.62f), new Vector3(0f, -0.2f, 0.9f), new Vector3(0.1f, 0f, 0.8f) };
        Vector3 pivot = new Vector3(0f, 0f, 0.8f);
        Vector3 lift = new Vector3(0f, 0.06f, 0f);
        var a = MakeSetup(pivot, pts);
        var b = MakeSetup(pivot + lift, pts[0] + lift, pts[1] + lift, pts[2] + lift);
        foreach (bool pointDepth in new[] { true, false })
        {
            a.fitMode = b.fitMode = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.Bottom;
            a.fitTargetV = b.fitTargetV = 650f;
            a.aabbCenter = new Vector3(0f, 0f, 0.8f);
            b.aabbCenter = a.aabbCenter + lift;
            a.aabbVerticalExtent = b.aabbVerticalExtent = 0.3f;
            a.bottomUsesPointDepth = b.bottomUsesPointDepth = pointDepth;
            float ea = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(a, 1.3f);
            float eb = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(b, 1.3f);
            if (pointDepth)
            {
                Assert.That(eb, Is.EqualTo(ea).Within(0.01f));
            }
            else
            {
                Assert.That(Mathf.Abs(eb - ea), Is.GreaterThan(0.1f));
            }
        }
    }

    // ⑦ の上端合わせ（AABB の上端は中心と同じ深さ）でも、合わせた後の広がりは合わせる前の縦の位置に依らない。
    [Test]
    public void TopFitIsIndependentOfTheStartingHeight()
    {
        Vector3[] pts = { new Vector3(0f, 0.25f, 0.62f), new Vector3(0f, -0.2f, 0.9f) };
        Vector3 pivot = new Vector3(0f, 0f, 0.8f);
        Vector3 lift = new Vector3(0f, -0.04f, 0f);
        var a = MakeSetup(pivot, pts);
        var b = MakeSetup(pivot + lift, pts[0] + lift, pts[1] + lift);
        a.fitMode = b.fitMode = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.Top;
        a.fitTargetV = b.fitTargetV = 20f;
        a.aabbCenter = new Vector3(0f, 0.02f, 0.78f);
        b.aabbCenter = a.aabbCenter + lift;
        a.aabbVerticalExtent = b.aabbVerticalExtent = 0.3f;

        float ea = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(a, 0.8f);
        float eb = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(b, 0.8f);

        Assert.That(eb, Is.EqualTo(ea).Within(0.01f));
    }

    // HeadPoint（animalBottomClippedHeadPin の ⑦）: 頭を目標の行へ置いた後の広がりは、合わせる前の縦の位置に依らない。
    [Test]
    public void HeadPointFitIsIndependentOfTheStartingHeight()
    {
        Vector3[] pts = { new Vector3(0f, 0.25f, 0.62f), new Vector3(0f, -0.2f, 0.9f) };
        Vector3 head = new Vector3(0.02f, 0.22f, 0.6f);
        Vector3 pivot = new Vector3(0f, 0f, 0.8f);
        Vector3 lift = new Vector3(0f, -0.04f, 0f);
        var a = MakeSetup(pivot, pts);
        var b = MakeSetup(pivot + lift, pts[0] + lift, pts[1] + lift);
        a.fitMode = b.fitMode = StreamingStereoVideoPlayer.ExactScaleRefineSolver.FitMode.HeadPoint;
        a.fitTargetV = b.fitTargetV = 120f;
        a.fitPoint = head;
        b.fitPoint = head + lift;

        float ea = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(a, 0.8f);
        float eb = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(b, 0.8f);

        Assert.That(ea, Is.GreaterThan(0f));
        Assert.That(eb, Is.EqualTo(ea).Within(0.01f));
    }

    // 縮める向き（広がりが目標より大きい）も解ける。
    [Test]
    public void ShrinkingHitsTheGoal()
    {
        var s = MakeSetup(new Vector3(0f, 0f, 0.9f), new Vector3(0f, 0.2f, 0.7f), new Vector3(0f, -0.15f, 1.1f));
        float e1 = StreamingStereoVideoPlayer.ExactScaleRefineSolver.EvaluateExtent(s, 1f);

        var r = StreamingStereoVideoPlayer.ExactScaleRefineSolver.Solve(s, 0.45f * e1, 0.25f);

        Assert.That(r.solved, Is.True);
        Assert.That(r.g, Is.LessThan(1f));
        Assert.That(r.extentAtG, Is.EqualTo(0.45f * e1).Within(0.05f));
    }
}
