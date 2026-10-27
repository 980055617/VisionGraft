using NUnit.Framework;
using UnityEngine;

// 人の地面についている足を平らに置く（2026-10-27、humanFootFlatOnStance、HumanFootFlat.partial.cs）: 足の上下の角度だけを回す計算と、地面についている度合い。
public class HumanFootFlatTests
{
    private static float Elevation(Vector3 v, Vector3 up)
    {
        return Mathf.Asin(Mathf.Clamp(Vector3.Dot(v, up.normalized) / v.magnitude, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // 回した後の足の向きは目標の上下の角度になり、左右の向き（水平の成分の向き）と長さは変わらない。
    [TestCase(0f, -0.06f, 0.09f, -43.6f)]
    [TestCase(0.03f, -0.02f, 0.10f, -43.6f)]
    [TestCase(0f, -0.10f, 0.02f, -33.7f)]
    public void PitchRotationReachesTheTargetElevationAndKeepsTheHeading(float x, float y, float z, float target)
    {
        Vector3 v = new Vector3(x, y, z);
        Vector3 up = Vector3.up;
        Vector3 rotated = StreamingStereoVideoPlayer.ResolveFootPitchRotation(v, up, target) * v;
        Assert.That(Elevation(rotated, up), Is.EqualTo(target).Within(0.01f));
        Assert.That(rotated.magnitude, Is.EqualTo(v.magnitude).Within(1e-5f));
        Vector3 h0 = Vector3.ProjectOnPlane(v, up).normalized;
        Vector3 h1 = Vector3.ProjectOnPlane(rotated, up).normalized;
        Assert.That(Vector3.Dot(h0, h1), Is.GreaterThan(0.9999f));
    }

    // root が傾いていても、その上向きに対して合わせる。
    [Test]
    public void PitchRotationUsesTheGivenUp()
    {
        Vector3 up = new Vector3(0f, 0.98f, 0.2f).normalized;
        Vector3 v = new Vector3(0.01f, -0.03f, 0.1f);
        Vector3 rotated = StreamingStereoVideoPlayer.ResolveFootPitchRotation(v, up, -40f) * v;
        Assert.That(Elevation(rotated, up), Is.EqualTo(-40f).Within(0.01f));
    }

    [Test]
    public void StanceWeightIsOneOnTheGroundAndZeroWhenLifted()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveFootStanceWeight(0f, 0.8f, 0.06f), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.ResolveFootStanceWeight(0.048f, 0.8f, 0.06f), Is.EqualTo(1f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveFootStanceWeight(0.072f, 0.8f, 0.06f), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(StreamingStereoVideoPlayer.ResolveFootStanceWeight(0.2f, 0.8f, 0.06f), Is.EqualTo(0f));
    }

    // 既定 ON（2026-10-27、ユーザー「テレビ女性は結構よくなったからそれでいい」）。
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("HumanFootFlatTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.humanFootFlatOnStance, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }
}
