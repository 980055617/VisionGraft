using NUnit.Framework;
using UnityEngine;

// 人の太ももの向きの目標を外へ回す（humanThighSpreadFix、2026-10-29、同じ日に既定 ON・表の 18 は 5°、HumanThighSpread.partial.cs）。
public class HumanThighSpreadTests
{
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("HumanThighSpreadTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.humanThighSpreadFix, Is.True);
            Assert.That(player.humanThighSpreadTable, Is.EqualTo("18_Female_Meshy:5,17_Male_Meshy:5"));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 真下を向いた太もも、左右の股を結ぶ向き +X（右 → 左）: 左の太ももは +X 側へ、右は −X 側へ、どちらも 7° 傾く。前後（Z）には動かない。
    [Test]
    public void TurnsEachThighOutwardByTheAngle()
    {
        Vector3 down = Vector3.down;
        Vector3 lateral = new Vector3(0.1f, 0f, 0f);
        Vector3 left = StreamingStereoVideoPlayer.ResolveHumanThighSpread(down, lateral, 1f, 7f);
        Vector3 right = StreamingStereoVideoPlayer.ResolveHumanThighSpread(down, lateral, -1f, 7f);
        Assert.That(Vector3.Angle(left, down), Is.EqualTo(7f).Within(1e-3f));
        Assert.That(Vector3.Angle(right, down), Is.EqualTo(7f).Within(1e-3f));
        Assert.That(left.x, Is.GreaterThan(0f));
        Assert.That(right.x, Is.LessThan(0f));
        Assert.That(left.z, Is.EqualTo(0f).Within(1e-5f));
        Assert.That(right.z, Is.EqualTo(0f).Within(1e-5f));
    }

    // 体が横を向いていても（左右の股を結ぶ向きが Z）、外は股の向き。負の角度は内へ。0 は変えない。
    [Test]
    public void FollowsTheHipsAndNegativeTurnsInward()
    {
        Vector3 thigh = new Vector3(0f, -1f, 0.2f).normalized;
        Vector3 lateral = new Vector3(0f, 0f, -0.1f);
        Vector3 outward = StreamingStereoVideoPlayer.ResolveHumanThighSpread(thigh, lateral, 1f, 5f);
        Vector3 inward = StreamingStereoVideoPlayer.ResolveHumanThighSpread(thigh, lateral, 1f, -5f);
        Assert.That(Vector3.Dot(outward, lateral.normalized), Is.GreaterThan(Vector3.Dot(thigh, lateral.normalized)));
        Assert.That(Vector3.Dot(inward, lateral.normalized), Is.LessThan(Vector3.Dot(thigh, lateral.normalized)));
        Assert.That(outward.x, Is.EqualTo(0f).Within(1e-5f));
        Vector3 same = StreamingStereoVideoPlayer.ResolveHumanThighSpread(thigh, lateral, 1f, 0f);
        Assert.That(Vector3.Angle(same, thigh), Is.EqualTo(0f).Within(1e-4f));
    }
}
