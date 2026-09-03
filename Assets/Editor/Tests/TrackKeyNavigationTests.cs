using System.Collections.Generic;
using NUnit.Framework;

// キーの前後送りが 4 本の曲線の和を辿ることを固定する。
//
// ここを片方の曲線だけにすると、scale だけのキーには送りで到達できないのに
// Del は効く、という食い違いが生まれる（Del は 4 本すべてから消す）。
public class TrackKeyNavigationTests
{
    private static SortedDictionary<int, float> Keys(params int[] frames)
    {
        var d = new SortedDictionary<int, float>();
        for (int i = 0; i < frames.Length; i++)
        {
            d[frames[i]] = i;
        }

        return d;
    }


    [Test]
    public void FindNeighborsTakesUnionAcrossAllCurves()
    {
        var curves = new List<SortedDictionary<int, float>>
        {
            Keys(100, 800),   // yaw
            Keys(240),        // pitch
            Keys(1200),       // roll
            Keys(610),        // scale
        };

        TrackKeyNavigation.FindNeighbors(curves, 500, out int prev, out int next);

        // 500 の手前で一番近いのは pitch の 240、次は scale の 610。
        // どちらも yaw 以外の曲線にしかないので、和を取っていないと拾えない。
        Assert.That(prev, Is.EqualTo(240));
        Assert.That(next, Is.EqualTo(610));
    }


    [Test]
    public void FindNeighborsReachesScaleOnlyKey()
    {
        var curves = new List<SortedDictionary<int, float>>
        {
            null,
            null,
            null,
            Keys(900),
        };

        TrackKeyNavigation.FindNeighbors(curves, 0, out int prev, out int next);

        Assert.That(prev, Is.EqualTo(-1));
        Assert.That(next, Is.EqualTo(900), "scale だけのキーにも送りで到達できること");
    }


    [Test]
    public void FindNeighborsExcludesTheCurrentFrameItself()
    {
        var curves = new List<SortedDictionary<int, float>> { Keys(100, 500, 900) };

        TrackKeyNavigation.FindNeighbors(curves, 500, out int prev, out int next);

        // 500 にはもういるので、送り先には含めない。含めると押しても動かない。
        Assert.That(prev, Is.EqualTo(100));
        Assert.That(next, Is.EqualTo(900));
    }


    [Test]
    public void FindNeighborsReportsMinusOneBeyondTheEnds()
    {
        var curves = new List<SortedDictionary<int, float>> { Keys(300) };

        TrackKeyNavigation.FindNeighbors(curves, 0, out int prevAtStart, out int nextAtStart);
        Assert.That(prevAtStart, Is.EqualTo(-1));
        Assert.That(nextAtStart, Is.EqualTo(300));

        TrackKeyNavigation.FindNeighbors(curves, 1000, out int prevAtEnd, out int nextAtEnd);
        Assert.That(prevAtEnd, Is.EqualTo(300));
        Assert.That(nextAtEnd, Is.EqualTo(-1));
    }


    [Test]
    public void FindNeighborsHandlesNoKeysAtAll()
    {
        TrackKeyNavigation.FindNeighbors(
            new List<SortedDictionary<int, float>>(), 500, out int prev, out int next);

        Assert.That(prev, Is.EqualTo(-1));
        Assert.That(next, Is.EqualTo(-1));

        TrackKeyNavigation.FindNeighbors(null, 500, out int prevNull, out int nextNull);
        Assert.That(prevNull, Is.EqualTo(-1));
        Assert.That(nextNull, Is.EqualTo(-1));
    }


    [Test]
    public void FindNeighborsPicksTheNearestOnEachSideNotTheFirstSeen()
    {
        // 意図的に「遠い方を先に」並べる。曲線ごとに最初に見つけたものを
        // 採ってしまう実装だと 100 / 1200 を返して落ちる。
        var curves = new List<SortedDictionary<int, float>>
        {
            Keys(100, 1200),
            Keys(490, 510),
        };

        TrackKeyNavigation.FindNeighbors(curves, 500, out int prev, out int next);

        Assert.That(prev, Is.EqualTo(490));
        Assert.That(next, Is.EqualTo(510));
    }
}
