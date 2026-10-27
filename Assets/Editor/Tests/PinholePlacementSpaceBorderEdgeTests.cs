using NUnit.Framework;
using UnityEngine;

// bottomFitBorderEdgesAsBounds（2026-10-25）の ⑧ の厳密解の写し（PinholePlacementSpace.EvaluateExtentAfterDepthMove の HoldReferenceY と lowestNotAboveEdge）。
// 置き場所: Assets/Editor/Tests/（PinholePlacementSpaceTests と同じアセンブリ）。
public class PinholePlacementSpaceBorderEdgeTests
{
    private const float Fy = 2.5389298f;

    private static ManifestData Manifest()
    {
        return new ManifestData { eye_w = 1280, eye_h = 720, cx = 640f, cy = 360f, fx_norm = 1.4281480f, fy_norm = Fy };
    }

    // 点の深さがばらけているので、縦に動かすと広がりが変わる（どの移動を選んだかが広がりに出る）。
    private static readonly float[] Y = { 0.17f, 0.06f, -0.04f, -0.15f, -0.19f };
    private static readonly float[] Z = { 0.36f, 0.41f, 0.40f, 0.44f, 0.37f };
    private static readonly bool[] IsBone = { true, true, true, true, true };

    [Test]
    public void HoldingTheReferenceAtItsMovedHeightIsTheSameAsNoSecondFit()
    {
        // ⑧ の平行移動で基準点の高さは s × refY になる。保つ高さがちょうどその値なら 2 回目の ⑦ は動かさない。
        ManifestData m = Manifest();
        const float refY = -0.02f, refZ = 0.40f, s = 0.83f;
        var none = new PinholePlacementSpace.SecondFitModel { mode = PinholePlacementSpace.SecondFitMode.None };
        var hold = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.HoldReferenceY,
            heldReferenceY = refY * s,
            aabbCenterY = 0f, aabbCenterZ = 0.40f, aabbVerticalExtent = 0.18f, cyPixels = 360f
        };
        float a = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, refY, refZ, s, none);
        float b = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, refY, refZ, s, hold);
        Assert.That(b, Is.EqualTo(a).Within(1e-3f));
    }

    [Test]
    public void TopFitThatWouldLiftTheLowestPointIntoThePictureUsesTheEdgeFit()
    {
        // 上端合わせの目標が高すぎて一番下の点が縁（719）より上へ上がるなら、一番下の点を縁へ置く移動（Bottom、目標 719）と同じになる。
        ManifestData m = Manifest();
        var top = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Top, targetV = -300f,
            aabbCenterY = 0f, aabbCenterZ = 0.40f, aabbVerticalExtent = 0.18f, cyPixels = 360f, bottomUsesPointDepth = true,
            lowestNotAboveEdge = true, edgeTargetV = 719f
        };
        var edge = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Bottom, targetV = 719f,
            aabbCenterY = 0f, aabbCenterZ = 0.40f, aabbVerticalExtent = 0.18f, cyPixels = 360f, bottomUsesPointDepth = true
        };
        var topUnclamped = top;
        topUnclamped.lowestNotAboveEdge = false;
        float clamped = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, 0f, 0.40f, 1f, top);
        float edgeExtent = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, 0f, 0.40f, 1f, edge);
        float unclamped = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, 0f, 0.40f, 1f, topUnclamped);
        Assert.That(clamped, Is.EqualTo(edgeExtent).Within(1e-3f));
        Assert.That(Mathf.Abs(unclamped - clamped), Is.GreaterThan(0.5f));
    }

    [Test]
    public void TopFitThatKeepsTheLowestPointBelowTheEdgeIsUnchanged()
    {
        // 上端合わせで一番下の点が縁より下（画の外）にあるなら、範囲の判定は何も変えない。
        ManifestData m = Manifest();
        var top = new PinholePlacementSpace.SecondFitModel
        {
            mode = PinholePlacementSpace.SecondFitMode.Top, targetV = 200f,
            aabbCenterY = 0f, aabbCenterZ = 0.40f, aabbVerticalExtent = 0.18f, cyPixels = 360f, bottomUsesPointDepth = true,
            lowestNotAboveEdge = true, edgeTargetV = 719f
        };
        var topUnclamped = top;
        topUnclamped.lowestNotAboveEdge = false;
        float a = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, 0f, 0.40f, 1f, top);
        float b = PinholePlacementSpace.EvaluateExtentAfterDepthMove(m, Fy, Y, Z, IsBone, 5, 0f, 0.40f, 1f, topUnclamped);
        Assert.That(a, Is.EqualTo(b).Within(1e-4f));
    }
}
