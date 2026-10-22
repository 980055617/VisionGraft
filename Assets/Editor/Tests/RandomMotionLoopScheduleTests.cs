using NUnit.Framework;

// 1 周に 1 回の Random モーションの時刻（2026-10-23）。
public class RandomMotionLoopScheduleTests
{
    // 30 秒・30 fps（900 フレーム）、頭と終わり 2 秒は除く → [60, 840)。
    [TestCase(0f, 60)]
    [TestCase(0.5f, 450)]
    [TestCase(0.99999f, 839)]
    public void TriggerIsUniformInsideTheMargins(float random01, int expected)
    {
        RandomMotionLoopSchedule s = new RandomMotionLoopSchedule();
        s.BeginLoop(900, 30f, 2f, 2f, random01);

        Assert.That(s.TriggerFrame, Is.EqualTo(expected));
    }

    [Test]
    public void FiresOncePerLoopAndResetsOnTheNextLoop()
    {
        RandomMotionLoopSchedule s = new RandomMotionLoopSchedule();
        Assert.That(s.Observe(500), Is.False, "周が始まる前は動かない");

        s.BeginLoop(900, 30f, 2f, 2f, 0.5f);
        Assert.That(s.LoopIndex, Is.EqualTo(0));
        Assert.That(s.Observe(0), Is.False);
        Assert.That(s.Observe(449), Is.False);
        Assert.That(s.Observe(450), Is.True);
        Assert.That(s.Observe(800), Is.True, "時刻を過ぎても動けるまで待つ");

        s.MarkFired();
        Assert.That(s.Observe(800), Is.False, "1 周に 1 回だけ");

        s.BeginLoop(900, 30f, 2f, 2f, 0f);
        Assert.That(s.LoopIndex, Is.EqualTo(1));
        Assert.That(s.Fired, Is.False);
        Assert.That(s.Observe(10), Is.False);
        Assert.That(s.Observe(60), Is.True);
    }

    // loopPointReached の直後は表示のフレームがまだ前の周の最後に残っている。それで動いてはいけない
    // （2026-10-23 のバッチで、2・3 周目が最後のフレーム 288 で発火した）。
    [Test]
    public void LastFrameOfThePreviousLoopDoesNotFireTheNewLoop()
    {
        RandomMotionLoopSchedule s = new RandomMotionLoopSchedule();
        s.BeginLoop(289, 30f, 2f, 2f, 0.5f);
        s.Observe(0);
        Assert.That(s.Observe(s.TriggerFrame), Is.True);
        s.MarkFired();

        s.BeginLoop(289, 30f, 2f, 2f, 0.5f);
        Assert.That(s.Observe(288), Is.False, "まだ前の周の最後のフレーム");
        Assert.That(s.Observe(288), Is.False);
        Assert.That(s.Observe(1), Is.False, "巻き戻った");
        Assert.That(s.Observe(s.TriggerFrame), Is.True);
    }

    // 余白より短い動画では全体から選ぶ（動かない周を作らない）。
    [Test]
    public void ShortVideoFallsBackToTheWholeLoop()
    {
        RandomMotionLoopSchedule s = new RandomMotionLoopSchedule();
        s.BeginLoop(90, 30f, 2f, 2f, 0.99999f);

        Assert.That(s.TriggerFrame, Is.InRange(0, 89));
    }
}
