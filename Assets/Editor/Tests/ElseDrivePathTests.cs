using NUnit.Framework;
using UnityEngine;

// Else（車）の「走って近づく → U ターン → 走って戻る」経路の幾何。
// その場回転を使わずに、必ず出発位置・出発時の向きへ戻ることを保証する。
public class ElseDrivePathTests
{
    private static readonly Vector3 Up = Vector3.up;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance, string label)
    {
        Assert.That(Vector3.Distance(expected, actual), Is.LessThan(tolerance), $"{label}: expected {expected} got {actual}");
    }

    [Test]
    public void StartsAtOriginWithHeadingAndReturnsToOriginWithSameHeading_FacingViewer()
    {
        Vector3 origin = new Vector3(0.3f, -0.2f, 0.9f);
        Vector3 heading = Vector3.forward;                 // 視聴者の方を向いている
        Vector3 stop = origin + Vector3.forward * 0.5f;
        ElseDrivePath.Path path = ElseDrivePath.Build(origin, heading, stop, Up, 0.05f);

        ElseDrivePath.Sample first = path.Evaluate(0f);
        ElseDrivePath.Sample last = path.Evaluate(path.TotalLength);
        AssertClose(origin, first.position, 0.001f, "start position");
        AssertClose(origin, last.position, 0.001f, "end position");
        Assert.That(Vector3.Dot(first.tangent, heading), Is.GreaterThan(0.999f), "start heading");
        Assert.That(Vector3.Dot(last.tangent, heading), Is.GreaterThan(0.999f), "end heading");
        Assert.That(path.TotalLength, Is.GreaterThan(1.0f));
    }

    [Test]
    public void StartsAndEndsWithHeading_FacingAwayFromViewer()
    {
        Vector3 origin = Vector3.zero;
        Vector3 heading = Vector3.back;                    // 視聴者に背を向けている → 出発時に U ターンが要る
        Vector3 stop = Vector3.forward * 0.5f;
        ElseDrivePath.Path path = ElseDrivePath.Build(origin, heading, stop, Up, 0.05f);

        ElseDrivePath.Sample first = path.Evaluate(0f);
        ElseDrivePath.Sample last = path.Evaluate(path.TotalLength);
        AssertClose(origin, first.position, 0.001f, "start position");
        AssertClose(origin, last.position, 0.001f, "end position");
        Assert.That(Vector3.Dot(first.tangent, heading), Is.GreaterThan(0.999f), "start heading");
        Assert.That(Vector3.Dot(last.tangent, heading), Is.GreaterThan(0.999f), "end heading");
    }

    [Test]
    public void StartsAndEndsWithHeading_Sideways()
    {
        Vector3 origin = Vector3.zero;
        Vector3 heading = Vector3.right;                   // 画面を横切っている車
        Vector3 stop = Vector3.forward * 0.4f;
        ElseDrivePath.Path path = ElseDrivePath.Build(origin, heading, stop, Up, 0.04f);

        ElseDrivePath.Sample first = path.Evaluate(0f);
        ElseDrivePath.Sample last = path.Evaluate(path.TotalLength);
        Assert.That(Vector3.Dot(first.tangent, heading), Is.GreaterThan(0.999f), "start heading");
        Assert.That(Vector3.Dot(last.tangent, heading), Is.GreaterThan(0.999f), "end heading");
        AssertClose(origin, last.position, 0.001f, "end position");
    }

    // 停止点で必ず折り返す: 経路の最も視聴者寄りの点は停止点の付近（U ターンの半径ぶん先）で、それより先へは行かない。
    [Test]
    public void ReachesStopPointAndTurnsThere()
    {
        Vector3 origin = Vector3.zero;
        Vector3 stop = Vector3.forward * 0.5f;
        float radius = 0.05f;
        ElseDrivePath.Path path = ElseDrivePath.Build(origin, Vector3.forward, stop, Up, radius);

        float maxZ = float.MinValue;
        bool sawUTurn = false;
        for (float d = 0f; d <= path.TotalLength; d += 0.005f)
        {
            ElseDrivePath.Sample s = path.Evaluate(d);
            maxZ = Mathf.Max(maxZ, s.position.z);
            sawUTurn |= s.segment == ElseDrivePath.SegmentUTurn;
        }
        Assert.That(sawUTurn, Is.True, "u_turn segment");
        Assert.That(maxZ, Is.EqualTo(0.5f + radius).Within(0.005f), "farthest point is stop + turn radius");
        Assert.That(path.segmentLengths[ElseDrivePath.SegmentUTurn], Is.EqualTo(Mathf.PI * radius).Within(0.01f), "u_turn length");
    }

    // 距離は単調で、Evaluate は端で止まる。
    [Test]
    public void EvaluateClampsAtEnds()
    {
        ElseDrivePath.Path path = ElseDrivePath.Build(Vector3.zero, Vector3.forward, Vector3.forward * 0.4f, Up, 0.05f);
        AssertClose(path.Evaluate(-1f).position, path.Evaluate(0f).position, 0.0001f, "below zero");
        AssertClose(path.Evaluate(path.TotalLength + 1f).position, path.Evaluate(path.TotalLength).position, 0.0001f, "beyond end");
        float previous = -1f;
        for (int i = 0; i < path.cumulative.Count; i++)
        {
            Assert.That(path.cumulative[i], Is.GreaterThanOrEqualTo(previous));
            previous = path.cumulative[i];
        }
    }

    // 高さは出発位置のまま（地面の上を走る）。
    [Test]
    public void KeepsHeightOfOrigin()
    {
        Vector3 origin = new Vector3(0f, -0.35f, 0.8f);
        Vector3 stop = new Vector3(0.1f, 0.2f, 0.3f);      // 停止点の高さが違っても無視する
        ElseDrivePath.Path path = ElseDrivePath.Build(origin, Vector3.right, stop, Up, 0.05f);
        for (float d = 0f; d <= path.TotalLength; d += 0.01f)
        {
            Assert.That(path.Evaluate(d).position.y, Is.EqualTo(origin.y).Within(0.0001f));
        }
    }

    [Test]
    public void MinimumDriveDistanceScalesWithTurnRadius()
    {
        Assert.That(ElseDrivePath.MinimumDriveDistance(0.01f), Is.EqualTo(0.05f));
        Assert.That(ElseDrivePath.MinimumDriveDistance(0.05f), Is.EqualTo(0.12f).Within(0.0001f));
    }
}
