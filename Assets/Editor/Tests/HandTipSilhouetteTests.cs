using NUnit.Framework;
using UnityEngine;

// 投影の上端・下端に手先の点を足す（silhouetteHandTipPoints、2026-10-29、既定 OFF、HandTipSilhouette.partial.cs）。
public class HandTipSilhouetteTests
{
    [Test]
    public void OffByDefault()
    {
        GameObject host = new GameObject("HandTipSilhouetteTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.silhouetteHandTipPoints, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // 頭頂より上（体の上向き）のときだけ足す。下（ボールを拾う手先が足より下に来る所）・同じ高さは足さない。体が傾いていても体の上向きで測る。
    [Test]
    public void OnlyAboveTheCrown()
    {
        Vector3 crown = new Vector3(0f, 1.7f, 0f);
        Assert.That(StreamingStereoVideoPlayer.IsHandTipAboveCrown(new Vector3(0.3f, 1.9f, 0f), crown, Vector3.up), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsHandTipAboveCrown(new Vector3(0.3f, 0.05f, 0.2f), crown, Vector3.up), Is.False);
        Assert.That(StreamingStereoVideoPlayer.IsHandTipAboveCrown(new Vector3(0.3f, 1.7f, 0f), crown, Vector3.up), Is.False);
        Vector3 tilted = Quaternion.Euler(0f, 0f, 30f) * Vector3.up;
        Assert.That(StreamingStereoVideoPlayer.IsHandTipAboveCrown(crown + tilted * 0.2f + Vector3.right * 0.05f, crown, tilted), Is.True);
        Assert.That(StreamingStereoVideoPlayer.IsHandTipAboveCrown(crown + Vector3.up, crown, Vector3.zero), Is.False);
    }

    // 手の子孫で一番遠いもの（Meshy の LeftHand_End、指のあるリグなら指先の葉）を選ぶ。子孫が無ければ null。
    [Test]
    public void PicksTheFarthestDescendant()
    {
        GameObject hand = new GameObject("LeftHand");
        try
        {
            Transform near = new GameObject("Index1").transform;
            near.SetParent(hand.transform, false);
            near.localPosition = new Vector3(0.03f, 0f, 0f);
            Transform tip = new GameObject("Index3").transform;
            tip.SetParent(near, false);
            tip.localPosition = new Vector3(0.05f, 0f, 0f);
            Transform side = new GameObject("Thumb1").transform;
            side.SetParent(hand.transform, false);
            side.localPosition = new Vector3(0.02f, 0.03f, 0f);
            Assert.That(StreamingStereoVideoPlayer.FindHandTipTransform(hand.transform), Is.SameAs(tip));

            GameObject bare = new GameObject("RightHand");
            try
            {
                Assert.That(StreamingStereoVideoPlayer.FindHandTipTransform(bare.transform), Is.Null);
                Assert.That(StreamingStereoVideoPlayer.FindHandTipTransform(null), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }
        finally
        {
            Object.DestroyImmediate(hand);
        }
    }
}
