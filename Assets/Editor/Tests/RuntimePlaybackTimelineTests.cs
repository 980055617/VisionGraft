using NUnit.Framework;

public class RuntimePlaybackTimelineTests
{
    [Test]
    public void ResolveTotalFramesPrefersVideoFrameCountOverMetadata()
    {
        long totalFrames = RuntimePlaybackTimeline.ResolveTotalFrames(240L, 120, 60);

        Assert.That(totalFrames, Is.EqualTo(240L));
    }


    [Test]
    public void ResolveTotalDurationFallsBackToFrameCountAndFps()
    {
        double duration = RuntimePlaybackTimeline.ResolveTotalDuration(0d, 30f, 120L);

        Assert.That(duration, Is.EqualTo(4d).Within(0.0001d));
    }


    [Test]
    public void ResolveNormalizedFallsBackToCurrentFrameWhenDurationIsUnknown()
    {
        float normalized = RuntimePlaybackTimeline.ResolveNormalized(0d, 0d, 30, 121L);

        Assert.That(normalized, Is.EqualTo(0.25f).Within(0.0001f));
    }


    [Test]
    public void ResolveTargetFrameClampsAndRoundsToFrameRange()
    {
        Assert.That(RuntimePlaybackTimeline.ResolveTargetFrame(0.5f, 121L), Is.EqualTo(60L));
        Assert.That(RuntimePlaybackTimeline.ResolveTargetFrame(2f, 121L), Is.EqualTo(120L));
        Assert.That(RuntimePlaybackTimeline.ResolveTargetFrame(-1f, 121L), Is.EqualTo(0L));
    }

    [Test]
    public void ResolveSeekTargetPrefersTimeWhenDurationIsKnownAndTimeSeekIsAvailable()
    {
        RuntimePlaybackTimeline.SeekTarget target = RuntimePlaybackTimeline.ResolveSeekTarget(
            0.25f,
            4d,
            true,
            120L);

        Assert.That(target.hasTime, Is.True);
        Assert.That(target.timeSeconds, Is.EqualTo(1d).Within(0.0001d));
        Assert.That(target.hasFrame, Is.False);
    }

    [Test]
    public void ResolveSeekTargetFallsBackToFrameWhenTimeSeekIsUnavailable()
    {
        RuntimePlaybackTimeline.SeekTarget target = RuntimePlaybackTimeline.ResolveSeekTarget(
            0.5f,
            4d,
            false,
            121L);

        Assert.That(target.hasTime, Is.False);
        Assert.That(target.hasFrame, Is.True);
        Assert.That(target.frame, Is.EqualTo(60L));
    }


    [Test]
    public void ResolveClockSecondsUsesFrameTimeWhenDurationIsUnknown()
    {
        RuntimePlaybackTimeline.ResolveClockSeconds(
            0d,
            0d,
            30f,
            120L,
            45L,
            0,
            out float currentSeconds,
            out float totalSeconds);

        Assert.That(currentSeconds, Is.EqualTo(1.5f).Within(0.0001f));
        Assert.That(totalSeconds, Is.EqualTo(4f).Within(0.0001f));
    }

    [Test]
    public void ResolveMetadataFrameClampsVideoFrameToMetadataRange()
    {
        int frame = RuntimePlaybackTimeline.ResolveMetadataFrame(
            250L,
            0d,
            30f,
            120);

        Assert.That(frame, Is.EqualTo(119));
    }

    [Test]
    public void ResolveMetadataFrameFallsBackToVideoTimeAndFps()
    {
        int frame = RuntimePlaybackTimeline.ResolveMetadataFrame(
            -1L,
            1.25d,
            24f,
            120);

        Assert.That(frame, Is.EqualTo(30));
    }

    [Test]
    public void ResolveDisplayMetadataFrameUsesCurrentFrameWhenFrameReadySyncIsOff()
    {
        int frame = RuntimePlaybackTimeline.ResolveDisplayMetadataFrame(
            42,
            10,
            false);

        Assert.That(frame, Is.EqualTo(42));
    }

    [Test]
    public void ResolveDisplayMetadataFrameUsesFrameReadyFrameWhenSyncIsOn()
    {
        int frame = RuntimePlaybackTimeline.ResolveDisplayMetadataFrame(
            42,
            10,
            true);

        Assert.That(frame, Is.EqualTo(10));
    }

    [Test]
    public void ResolveFrameSnapshotContainsCurrentAndDisplayMetadataFrame()
    {
        RuntimePlaybackTimeline.FrameSnapshot snapshot = RuntimePlaybackTimeline.ResolveFrameSnapshot(
            videoFrame: -1L,
            videoTime: 2d,
            fpsFallback: 30f,
            metadataFrameCount: 120,
            lastFrameReadyFrame: 58,
            useFrameReadySync: true);

        Assert.That(snapshot.currentFrame, Is.EqualTo(60));
        Assert.That(snapshot.displayMetadataFrame, Is.EqualTo(58));
    }

    [Test]
    public void NormalizeFrameReadyFramePreservesNegativeAsUnavailable()
    {
        Assert.That(RuntimePlaybackTimeline.NormalizeFrameReadyFrame(-1L), Is.EqualTo(-1));
    }

    [Test]
    public void NormalizeFrameReadyFrameClampsLargeFrameToIntMax()
    {
        Assert.That(RuntimePlaybackTimeline.NormalizeFrameReadyFrame(((long)int.MaxValue) + 1L), Is.EqualTo(int.MaxValue));
    }


    [Test]
    public void FormatClockClampsInvalidSeconds()
    {
        Assert.That(RuntimePlaybackTimeline.FormatClock(float.NaN), Is.EqualTo("00:00"));
        Assert.That(RuntimePlaybackTimeline.FormatClock(-1f), Is.EqualTo("00:00"));
        Assert.That(RuntimePlaybackTimeline.FormatClock(125.9f), Is.EqualTo("02:05"));
    }
}
