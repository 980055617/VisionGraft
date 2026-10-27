using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 画面の左右の端から出た物・人・動物を視聴者の横を通る向きへ曲げる幾何（FrameOutPassBy.Resolve、2026-10-26）。
// 目 = 原点、目の前 = +z、右 = +x、上 = +y。
public class FrameOutPassByTests
{
    private static readonly Vector3 Up = Vector3.up;
    private static readonly Vector3 Forward = Vector3.forward;

    private static FrameOutPassBy.Result Resolve(List<Vector3> points, Vector3 heading, float side, float passMin = 0.15f, float passMax = 0.30f,
        float maxTurn = 45f, bool turnPoints = false, Vector3? pivot = null)
    {
        Vector3 c = Vector3.zero;
        for (int i = 0; i < points.Count; i++)
        {
            c += points[i];
        }

        return FrameOutPassBy.Resolve(points, pivot ?? c / points.Count, heading, Vector3.right * side, Forward, Up, passMin, passMax, maxTurn, turnPoints);
    }

    [Test]
    public void HeadingStraightAtTheEye_TurnsTowardTheExitSideByTheTangentAngle()
    {
        // 目の正面 1 m の点が目へまっすぐ来る（通る距離 0）→ 出ていく側（右）へ asin(0.15) = 8.627° 回し、右を 0.15 m で通る。
        var points = new List<Vector3> { new Vector3(0f, 0f, 1f) };
        FrameOutPassBy.Result r = Resolve(points, Vector3.back, 1f);
        Assert.That(r.ok, Is.True, r.note);
        Assert.That(r.note, Is.EqualTo("toward exit"));
        Assert.That(Mathf.Abs(r.bendDegrees), Is.EqualTo(Mathf.Asin(0.15f) * Mathf.Rad2Deg).Within(0.05f));
        Assert.That(r.passMeters, Is.EqualTo(0.15f).Within(0.001f));
        Assert.That(r.heading.x, Is.GreaterThan(0f), "passes on the right = moves toward the right while it comes");
        Assert.That(r.heading.z, Is.LessThan(0f), "still comes toward the viewer");
    }

    [Test]
    public void MirroredScene_GivesMirroredHeading()
    {
        var right = new List<Vector3> { new Vector3(0.05f, 0f, 0.8f), new Vector3(0.1f, 0.1f, 0.9f) };
        var left = new List<Vector3> { new Vector3(-0.05f, 0f, 0.8f), new Vector3(-0.1f, 0.1f, 0.9f) };
        FrameOutPassBy.Result r = Resolve(right, Vector3.back, 1f);
        FrameOutPassBy.Result l = Resolve(left, Vector3.back, -1f);
        Assert.That(r.ok && l.ok, Is.True, r.note + " / " + l.note);
        Assert.That(l.bendDegrees, Is.EqualTo(-r.bendDegrees).Within(0.01f));
        Assert.That(l.passMeters, Is.EqualTo(r.passMeters).Within(0.0005f));
        Assert.That(l.heading.x, Is.EqualTo(-r.heading.x).Within(0.0005f));
        Assert.That(l.heading.z, Is.EqualTo(r.heading.z).Within(0.0005f));
    }

    [Test]
    public void SidewaysAwayFromTheEye_TurnsTowardTheViewerUntilItPassesAtTheUpperDistance()
    {
        // ボールに近い形: 右前の点が真横（右）へ遠ざかる → 目の方へ回して右を 0.30 m で通る（接線の角: 94.08°）。
        Vector3 p = new Vector3(0.33f, 0f, 0.41f);
        var points = new List<Vector3> { p };
        FrameOutPassBy.Result r = Resolve(points, Vector3.right, 1f, maxTurn: 180f);
        Assert.That(r.ok, Is.True, r.note);
        Assert.That(r.note, Is.EqualTo("toward viewer"));
        Assert.That(float.IsPositiveInfinity(r.measuredPassMeters), Is.True, "moving away never passes");
        float d = new Vector2(p.x, p.z).magnitude;
        float toEye = Mathf.Atan2(-p.x, -p.z) * Mathf.Rad2Deg;      // 目へ向かう yaw
        float expectedYaw = toEye - Mathf.Asin(0.30f / d) * Mathf.Rad2Deg;
        float yaw = Mathf.Atan2(r.heading.x, r.heading.z) * Mathf.Rad2Deg;
        Assert.That(Mathf.DeltaAngle(yaw, expectedYaw), Is.EqualTo(0f).Within(0.05f));
        Assert.That(r.passMeters, Is.EqualTo(0.30f).Within(0.001f));
        Assert.That(Mathf.Abs(r.bendDegrees), Is.EqualTo(94.08f).Within(0.1f));
    }

    [Test]
    public void TurnBeyondTheLimit_IsNotUsed()
    {
        var points = new List<Vector3> { new Vector3(0.33f, 0f, 0.41f) };
        FrameOutPassBy.Result r = Resolve(points, Vector3.right, 1f, maxTurn: 45f);
        Assert.That(r.ok, Is.False);
        Assert.That(r.note, Does.StartWith("no heading within"));
    }

    [Test]
    public void AlreadyPassingInTheBand_KeepsTheMeasuredHeading()
    {
        // テレビの女性に近い形: 左前の体が視聴者の方へ、左を 0.2 m ほどで通る向き → 曲げない。
        var points = new List<Vector3> { new Vector3(-0.17f, 0.1f, 0.30f), new Vector3(-0.15f, -0.1f, 0.32f) };
        Vector3 heading = Quaternion.AngleAxis(-162.8f, Up) * Vector3.forward;
        FrameOutPassBy.Result r = Resolve(points, heading, -1f);
        Assert.That(r.ok, Is.True, r.note);
        Assert.That(r.note, Is.EqualTo("in band"));
        Assert.That(r.bendDegrees, Is.EqualTo(0f).Within(0.001f));
        Assert.That(r.passMeters, Is.InRange(0.15f, 0.30f));
    }

    [Test]
    public void PointInsideTheMinimum_IsNotUsed()
    {
        var points = new List<Vector3> { new Vector3(-0.05f, 0f, 0.1f), new Vector3(-0.2f, 0f, 0.5f) };
        FrameOutPassBy.Result r = Resolve(points, Vector3.back, -1f);
        Assert.That(r.ok, Is.False);
        Assert.That(r.note, Is.EqualTo("inside"));
    }

    [Test]
    public void NoPoints_IsNotUsed()
    {
        FrameOutPassBy.Result r = Resolve(new List<Vector3>(), Vector3.back, 1f, pivot: Vector3.forward);
        Assert.That(r.ok, Is.False);
    }

    [Test]
    public void WheeledBox_TurnsItsNoseTheSameWayAsItsPath()
    {
        // 車に近い形: 左前（−51°）で視聴者の方を向いた箱（幅 0.3・長さ 0.74）が視聴者の方へまっすぐ来ると、左を 0.6 m 以上で通る（遠すぎる）。
        // 目の方へ曲げる。向きも同じ角だけ回すので、点を root まわりに回して測る。回した後の鼻先（−z を回したもの）が進む向きと一致すること。
        Vector3 root = new Vector3(-0.83f, 0f, 0.63f);
        var points = new List<Vector3>();
        for (int i = 0; i < 8; i++)
        {
            points.Add(root + new Vector3(((i & 1) == 0 ? -0.15f : 0.15f), ((i & 2) == 0 ? -0.13f : 0.13f), ((i & 4) == 0 ? -0.37f : 0.37f)));
        }

        Vector3 measured = Quaternion.AngleAxis(-171f, Up) * Vector3.forward;
        FrameOutPassBy.Result r = Resolve(points, measured, -1f, turnPoints: true, pivot: root);
        Assert.That(r.ok, Is.True, r.note);
        Assert.That(r.note, Is.EqualTo("toward viewer"));
        Assert.That(r.heading.x, Is.GreaterThan(Vector3.ProjectOnPlane(measured, Up).normalized.x), "turned toward the viewer (to the right)");
        Vector3 nose = Quaternion.AngleAxis(r.bendDegrees, Up) * measured;
        Assert.That(Vector3.Angle(Vector3.ProjectOnPlane(nose, Up), r.heading), Is.LessThan(0.1f), "nose and path turn the same way");
        Assert.That(r.passMeters, Is.EqualTo(0.30f).Within(0.002f));

        // 回した箱の角を、進む線に沿って動かしたとき、目にいちばん近づく距離が帯の上の端（0.30 m）になること（点を一緒に回していることの確かめ）。
        Quaternion turn = Quaternion.AngleAxis(r.bendDegrees, Up);
        float closest = float.PositiveInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 q = root + turn * (points[i] - root);
            Vector2 p2 = new Vector2(q.x, q.z);
            Vector2 h2 = new Vector2(r.heading.x, r.heading.z).normalized;
            float along = Vector2.Dot(p2, h2);
            if (along < 0f)
            {
                closest = Mathf.Min(closest, (p2 - along * h2).magnitude);
            }
        }

        Assert.That(closest, Is.EqualTo(0.30f).Within(0.002f));
    }
}
