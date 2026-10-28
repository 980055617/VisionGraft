using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// その場の判定に体の大きさも使う（placementFollowVideoDepthStillUsesBodyScale、2026-10-28、同じ日から既定 ON、VideoDepthFollowStill.partial.cs）と、
// その場の間は足元の行を止めて出た後に混ぜて戻す（ResolveVideoDepthFollowFeetHeldRows、VideoDepthFollowFeet.partial.cs）。
public class VideoDepthFollowBodyScaleStillTests
{
    private static StreamingStereoVideoPlayer.VideoDepthFollowFeetSample Sample(int frame, float bottom)
    {
        return new StreamingStereoVideoPlayer.VideoDepthFollowFeetSample { frame = frame, shot = 0, bottom = bottom, height = 100f, z01 = 0.3f };
    }

    [Test]
    public void OnByDefault()
    {
        GameObject host = new GameObject("VideoDepthFollowBodyScaleStillTests");
        try
        {
            StreamingStereoVideoPlayer player = host.AddComponent<StreamingStereoVideoPlayer>();
            Assert.That(player.placementFollowVideoDepthStillUsesBodyScale, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(host);
        }
    }

    // その場で前を低くする 100 フレーム（体の大きさは ±1% の揺れと 1 フレームの外れ）→ 近づく 50 フレーム（1 フレームに +1%）→ その場の 50 フレーム。
    // その場の 2 つは窓が近づく所に掛からない所が全部入り、近づく所は入らない。外れの 1 フレームは中央値で消える。
    [Test]
    public void BodyScaleStillFindsInPlaceAndSkipsAnApproach()
    {
        var samples = new List<Vector2>();
        float baseLog = -Mathf.Log(3.4f);
        for (int f = 0; f < 200; f++)
        {
            float v = baseLog + 0.01f * Mathf.Sin(f * 0.9f);
            if (f >= 100 && f < 150)
            {
                v = baseLog + 0.01f * (f - 100);
            }
            else if (f >= 150)
            {
                v = baseLog + 0.5f + 0.01f * Mathf.Sin(f * 0.9f);
            }

            if (f == 40)
            {
                v += 0.3f;
            }

            samples.Add(new Vector2(f, v));
        }

        var still = new HashSet<int>(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowBodyScaleStillFrames(samples, null, 12, 3, 0.06f, 12));
        for (int f = 0; f <= 87; f++)
        {
            Assert.That(still.Contains(f), Is.True, $"frame {f} should be in place");
        }

        for (int f = 106; f <= 143; f++)
        {
            Assert.That(still.Contains(f), Is.False, $"frame {f} is the approach");
        }

        for (int f = 162; f < 200; f++)
        {
            Assert.That(still.Contains(f), Is.True, $"frame {f} should be in place");
        }
    }

    // 続きが minRun より短いものは捨てる（歩いている途中に当てはめが 0.3 秒止まる所）。
    [Test]
    public void BodyScaleStillDropsShortRuns()
    {
        var samples = new List<Vector2>();
        for (int f = 0; f < 30; f++)
        {
            samples.Add(new Vector2(f, -1f));
        }

        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowBodyScaleStillFrames(samples, null, 12, 3, 0.06f, 12).Count, Is.EqualTo(30));
        Assert.That(StreamingStereoVideoPlayer.ClassifyVideoDepthFollowBodyScaleStillFrames(samples, null, 12, 3, 0.06f, 31).Count, Is.EqualTo(0));
    }

    private static List<Vector3> Rows(int count, System.Func<int, float> bottom, float height = 60f)
    {
        var rows = new List<Vector3>();
        for (int f = 0; f < count; f++)
        {
            rows.Add(new Vector3(f, bottom(f), height));
        }

        return rows;
    }

    // 寝た猫が伸びをして前足を下へ伸ばす（切れ目の間だけ下端が 35 px 下がる）: 両端の足元の行が同じなので切れ目をつなぐ。
    [Test]
    public void BridgeJoinsAShortGapOnTheSameRow()
    {
        var still = new HashSet<int>();
        for (int f = 0; f <= 40; f++)
        {
            still.Add(f);
        }

        for (int f = 60; f <= 100; f++)
        {
            still.Add(f);
        }

        List<Vector3> rows = Rows(101, f => f > 40 && f < 60 ? 401f : 366f + (f % 3));
        int added = StreamingStereoVideoPlayer.BridgeVideoDepthFollowStillGaps(still, rows, null, 30, 6, 0.08f);
        Assert.That(added, Is.EqualTo(19));
        for (int f = 0; f <= 100; f++)
        {
            Assert.That(still.Contains(f), Is.True);
        }
    }

    // 両端の足元の行が違う（近づいてから止まった）・切れ目が長い・切れ目の多くのフレームが無い（下で切れた）ときはつながない。
    [Test]
    public void BridgeKeepsGapsWhereTheRowMovedOrThatAreLongOrMissing()
    {
        System.Func<HashSet<int>> twoRuns = () =>
        {
            var s = new HashSet<int>();
            for (int f = 0; f <= 40; f++)
            {
                s.Add(f);
            }

            for (int f = 60; f <= 100; f++)
            {
                s.Add(f);
            }

            return s;
        };

        HashSet<int> moved = twoRuns();
        Assert.That(StreamingStereoVideoPlayer.BridgeVideoDepthFollowStillGaps(moved, Rows(101, f => f < 50 ? 625f : 680f), null, 30, 6, 0.08f), Is.EqualTo(0));
        HashSet<int> longGap = twoRuns();
        Assert.That(StreamingStereoVideoPlayer.BridgeVideoDepthFollowStillGaps(longGap, Rows(101, f => 366f), null, 18, 6, 0.08f), Is.EqualTo(0));
        HashSet<int> missing = twoRuns();
        List<Vector3> rows = Rows(101, f => 366f);
        rows.RemoveAll(r => r.x > 41 && r.x < 58);
        Assert.That(StreamingStereoVideoPlayer.BridgeVideoDepthFollowStillGaps(missing, rows, null, 30, 6, 0.08f), Is.EqualTo(0));
    }

    // その場の間（30〜79）に前足が前へ出て行が 600 → 650 へ動いても、表の行は入った 600 のまま。出た後は 12 フレーム止め、その後ずれ（−50）を 24 フレームで 0 へ。
    [Test]
    public void HeldRowsFreezeInsideARunAndBlendBackAfterTheHold()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var rows = new float[140];
        var still = new bool[140];
        for (int f = 0; f < 140; f++)
        {
            samples.Add(Sample(f, 0f));
            rows[f] = f < 50 ? 600f : 650f;
            still[f] = f >= 30 && f <= 79;
        }

        float[] held = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetHeldRows(samples, rows, still, 12, 24);
        for (int f = 0; f < 30; f++)
        {
            Assert.That(held[f], Is.EqualTo(600f));
        }

        // 区間の後 1〜12 フレーム（80〜91）は止めたまま、13 フレーム目（92）はずれを全部残すので同じ値。
        for (int f = 30; f <= 92; f++)
        {
            Assert.That(held[f], Is.EqualTo(600f), $"frame {f}");
        }

        // 103 は age 24: 650 − 50 × (1 − (24 − 13) / 24)。116（age 37）からは今の行。
        Assert.That(held[103], Is.EqualTo(650f - 50f * (1f - 11f / 24f)).Within(0.01f));
        for (int f = 116; f < 140; f++)
        {
            Assert.That(held[f], Is.EqualTo(650f));
        }
    }

    // 出た後に本当に近づく（止めた後 1 フレームに +2 px）分は、ずれを戻している間もそのまま通る（犬が 10.58 秒にその場を出てすぐ近づく所）。
    [Test]
    public void HeldRowsPassAnApproachRightAfterTheHold()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var rows = new float[110];
        var still = new bool[110];
        for (int f = 0; f < 110; f++)
        {
            samples.Add(Sample(f, 0f));
            rows[f] = f <= 61 ? 600f : 600f + 2f * (f - 61);
            still[f] = f <= 49;
        }

        float[] held = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetHeldRows(samples, rows, still, 12, 24);
        Assert.That(held[62], Is.EqualTo(600f));
        for (int f = 63; f < 110; f++)
        {
            Assert.That(held[f] - held[f - 1], Is.GreaterThan(1.9f), $"frame {f}");
            Assert.That(rows[f] - held[f], Is.InRange(0f, 2.001f), $"frame {f}");
        }

        Assert.That(held[100], Is.EqualTo(rows[100]));
    }

    // 戻している途中に次のその場に入ったら、その値で止める（跳ばない）。NaN の行はそのまま。
    [Test]
    public void HeldRowsReEnterDuringTheBlendWithoutAJump()
    {
        var samples = new List<StreamingStereoVideoPlayer.VideoDepthFollowFeetSample>();
        var rows = new float[120];
        var still = new bool[120];
        for (int f = 0; f < 120; f++)
        {
            samples.Add(Sample(f, 0f));
            rows[f] = f < 20 ? 600f : 660f;
            still[f] = (f >= 10 && f <= 29) || f >= 60;
        }

        rows[5] = float.NaN;
        float[] held = StreamingStereoVideoPlayer.ResolveVideoDepthFollowFeetHeldRows(samples, rows, still, 12, 24);
        Assert.That(float.IsNaN(held[5]), Is.True);
        // 区間の最後 29 の後、42（age 13）からずれ −60 を戻す。59 は age 30: 660 − 60 × (1 − 17 / 24)、60 は age 31 の 660 − 60 × (1 − 18 / 24) = 645 で止める。
        Assert.That(held[59], Is.EqualTo(660f - 60f * (1f - 17f / 24f)).Within(0.01f));
        for (int f = 60; f < 120; f++)
        {
            Assert.That(held[f], Is.EqualTo(645f).Within(0.01f), $"frame {f}");
        }
    }
}
