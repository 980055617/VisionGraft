using NUnit.Framework;
using UnityEngine;

// 人の足の開きをデータより狭くしない（humanLegStanceFromData、2026-10-26 夜、既定 OFF、HumanLegStance.partial.cs）: 足りない足首の間の求め方と既定値。
public class HumanLegStanceTests
{
    // テレビの女性 vf 402 の値（骨のダンプ・keypoints）: データの足首の間 0.148 m・脚 0.785 m、18 のモデルの足首の間 0.0272 m・脚 0.196 m。
    // データを脚の長さで縮めると 0.0370 m なので 0.0098 m 足りない。広いモデル（20 のゾンビ 0.0398 m）・データの足が交差（負）は 0。
    [Test]
    public void ShortfallWidensOnlyNarrowerModels()
    {
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLegStanceShortfall(0.148f, 0.785f, 0.0272f, 0.196f),
            Is.EqualTo(0.148f * 0.196f / 0.785f - 0.0272f).Within(1e-6f));
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLegStanceShortfall(0.148f, 0.785f, 0.0398f, 0.198f), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLegStanceShortfall(-0.02f, 0.785f, 0.0272f, 0.196f), Is.EqualTo(0f));
        Assert.That(StreamingStereoVideoPlayer.ResolveHumanLegStanceShortfall(0.148f, 0f, 0.0272f, 0.196f), Is.EqualTo(0f));
    }

    [Test]
    public void OffByDefault()
    {
        GameObject host = new GameObject("HumanLegStanceTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.humanLegStanceFromData, Is.False);
            Assert.That(player.humanLegStanceMaxDegrees, Is.EqualTo(8f));
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }
}
