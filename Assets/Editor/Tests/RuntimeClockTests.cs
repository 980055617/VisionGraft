using NUnit.Framework;

public class RuntimeClockTests
{
    [Test]
    public void ResolveDeltaTimeUsesProvidedDeltaWhenPositive()
    {
        Assert.That(RuntimeClock.ResolveDeltaTime(0.02f), Is.EqualTo(0.02f).Within(0.0001f));
    }

    [Test]
    public void ResolveDeltaTimeFallsBackToSixtyFpsWhenDeltaIsTooSmall()
    {
        Assert.That(RuntimeClock.ResolveDeltaTime(0f), Is.EqualTo(1f / 60f).Within(0.0001f));
    }

    [Test]
    public void ResolveElapsedClampsNegativeElapsedToZero()
    {
        Assert.That(RuntimeClock.ResolveElapsed(1f, 2f), Is.EqualTo(0f).Within(0.0001f));
    }

    [Test]
    public void ResolveNextTimeAddsIntervalToCurrentTime()
    {
        Assert.That(RuntimeClock.ResolveNextTime(3f, 1.5f), Is.EqualTo(4.5f).Within(0.0001f));
    }

    [Test]
    public void ResolveTimeoutRemainingSubtractsUnscaledDeltaTime()
    {
        Assert.That(RuntimeClock.ResolveTimeoutRemaining(0.75f, 0.2f), Is.EqualTo(0.55f).Within(0.0001f));
    }

    [Test]
    public void WasSeenRecentlyReturnsTrueWithinFrameGap()
    {
        Assert.That(RuntimeClock.WasSeenRecently(10, 9, 1), Is.True);
    }

    [Test]
    public void WasSeenRecentlyReturnsFalseOutsideFrameGap()
    {
        Assert.That(RuntimeClock.WasSeenRecently(10, 8, 1), Is.False);
    }

    [Test]
    public void ResolveTickContextCarriesNowFrameAndNormalizedDeltaTime()
    {
        RuntimeClock.TickContext context = RuntimeClock.ResolveTickContext(
            now: 2.5f,
            deltaTime: 0f,
            frameCount: 42);

        Assert.That(context.now, Is.EqualTo(2.5f).Within(0.0001f));
        Assert.That(context.deltaTime, Is.EqualTo(1f / 60f).Within(0.0001f));
        Assert.That(context.frameCount, Is.EqualTo(42));
    }
}
