using NUnit.Framework;

public class RuntimeProgressDisplayTests
{
    [Test]
    public void ResolveBuildsNormalizedProgressAndClockText()
    {
        RuntimeProgressDisplay.State state = RuntimeProgressDisplay.Resolve(
            videoFrameCount: 120,
            videoFrame: 30,
            videoTime: 1d,
            videoLength: 4d,
            videoFrameRate: 30f,
            metadataFps: 30f,
            manifestFps: 30f,
            metadataTotalFrames: 120,
            manifestTotalFrames: 120,
            fallbackCurrentFrame: 30);

        Assert.That(state.normalized, Is.EqualTo(0.25f).Within(0.0001f));
        Assert.That(state.clockText, Is.EqualTo("00:01 / 00:04"));
    }
}
