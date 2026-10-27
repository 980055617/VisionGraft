using NUnit.Framework;
using UnityEngine;

// 男性の口パク（2026-10-27、humanLipSyncHardcoded、HumanLipSync.partial.cs）: 動画の時刻から口の開きを決める計算。
public class HumanLipSyncTests
{
    private static StreamingStereoVideoPlayer.HumanLipSyncLaugh[] OneLaugh()
    {
        return new[]
        {
            new StreamingStereoVideoPlayer.HumanLipSyncLaugh
            {
                from = 9.05f, to = 10.15f, baseDeg = 4f, peakDeg = 5f,
                syllables = new[] { 9.23f, 9.31f, 9.36f, 9.44f },
            },
        };
    }

    // 笑いの区間の外は閉じている。
    [TestCase(0f)]
    [TestCase(9.05f)]
    [TestCase(10.15f)]
    [TestCase(20f)]
    public void ClosedOutsideTheLaugh(float t)
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLipSyncDeg(OneLaugh(), t), Is.EqualTo(0f));
    }

    // 声の区切りの真ん中は土台 + 山、区切りの外（区間の中）は土台だけ。区切りの間も閉じない。
    [Test]
    public void OpensWiderOnASyllableAndStaysOpenBetween()
    {
        float onSyllable = StreamingStereoVideoPlayer.ResolveHumanLipSyncDeg(OneLaugh(), 9.27f);
        float between = StreamingStereoVideoPlayer.ResolveHumanLipSyncDeg(OneLaugh(), 9.80f);
        Assert.That(onSyllable, Is.EqualTo(9f).Within(1e-4f));
        Assert.That(between, Is.EqualTo(4f).Within(1e-4f));
    }

    // 区間の始まりはなめらかに開く（いきなり土台にならない）。
    [Test]
    public void RampsInAtTheStart()
    {
        float early = StreamingStereoVideoPlayer.ResolveHumanLipSyncDeg(OneLaugh(), 9.08f);
        Assert.That(early, Is.GreaterThan(0f));
        Assert.That(early, Is.LessThan(2f));
    }

    // 開きの上限。
    [Test]
    public void ClampedToTheMaximum()
    {
        var laughs = new[]
        {
            new StreamingStereoVideoPlayer.HumanLipSyncLaugh { from = 0f, to = 2f, baseDeg = 30f, peakDeg = 30f, syllables = new[] { 0.5f, 1.5f } },
        };
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLipSyncDeg(laughs, 1f), Is.EqualTo(16f).Within(1e-4f));
    }

    // あごの骨の名前から開く角度に掛ける割合（Jaw = 1、Jaw_NN = NN%）。ほかの名前は対象外。
    [TestCase("Jaw", 1f)]
    [TestCase("Jaw_05", 0.05f)]
    [TestCase("Jaw_98", 0.98f)]
    public void ParsesTheJawFraction(string name, float expected)
    {
        Assert.That(StreamingStereoVideoPlayer.TryParseJawFraction(name, out float fraction), Is.True);
        Assert.That(fraction, Is.EqualTo(expected).Within(1e-6f));
    }

    [TestCase("JawTip")]
    [TestCase("Jaw_00")]
    [TestCase("Jaw_100")]
    [TestCase("headfront")]
    [TestCase("Jaw_ab")]
    public void IgnoresOtherBones(string name)
    {
        Assert.That(StreamingStereoVideoPlayer.TryParseJawFraction(name, out _), Is.False);
    }

    // 既定 ON（ユーザーの依頼。口の骨があるモデルにだけ効く）。
    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("HumanLipSyncTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.humanLipSyncHardcoded, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }
}
