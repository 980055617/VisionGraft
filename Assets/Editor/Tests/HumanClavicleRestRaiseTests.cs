using NUnit.Framework;
using UnityEngine;

// 人の鎖骨の基準を上げる（humanClavicleRestRaiseDeg、2026-10-29、同じ日に既定 20、HumanClavicle.partial.cs）。
public class HumanClavicleRestRaiseTests
{
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("HumanClavicleRestRaiseTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.humanClavicleRestRaiseDeg, Is.EqualTo(20f));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 水平な鎖骨（外の端が +X）を 20° 上げると、外の端は水平から 20° 上を向く。0 なら変えない。
    [Test]
    public void RaisesTheOuterEndTowardUp()
    {
        Quaternion bind = Quaternion.identity;
        Vector3 child = new Vector3(0.15f, 0f, 0f);
        Quaternion raised = StreamingStereoVideoPlayer.ResolveHumanClavicleRestRaise(bind, child, 20f);
        Vector3 d = (raised * child).normalized;
        Assert.That(Mathf.Asin(d.y) * Mathf.Rad2Deg, Is.EqualTo(20f).Within(1e-3f));
        Assert.That(d.z, Is.EqualTo(0f).Within(1e-5f));
        Quaternion same = StreamingStereoVideoPlayer.ResolveHumanClavicleRestRaise(bind, child, 0f);
        Assert.That(Quaternion.Angle(same, bind), Is.EqualTo(0f).Within(1e-4f));
    }

    // 骨の軸がどちらを向いていても（bind に回転があっても）、外の端の仰角が同じだけ増える。左右（−X）も同じ。
    [Test]
    public void SameRaiseForARotatedBindAndTheOtherSide()
    {
        Quaternion bind = Quaternion.Euler(10f, 70f, -35f);
        Vector3 child = Quaternion.Inverse(bind) * new Vector3(-0.15f, -0.02f, 0.01f);
        Vector3 before = (bind * child).normalized;
        Quaternion raised = StreamingStereoVideoPlayer.ResolveHumanClavicleRestRaise(bind, child, 20f);
        Vector3 after = (raised * child).normalized;
        float elevBefore = Mathf.Asin(before.y) * Mathf.Rad2Deg;
        float elevAfter = Mathf.Asin(after.y) * Mathf.Rad2Deg;
        Assert.That(elevAfter - elevBefore, Is.EqualTo(20f).Within(0.05f));
        // 水平面の向きは変えない
        float yawBefore = Mathf.Atan2(before.z, before.x) * Mathf.Rad2Deg;
        float yawAfter = Mathf.Atan2(after.z, after.x) * Mathf.Rad2Deg;
        Assert.That(Mathf.DeltaAngle(yawBefore, yawAfter), Is.EqualTo(0f).Within(0.05f));
    }
}
