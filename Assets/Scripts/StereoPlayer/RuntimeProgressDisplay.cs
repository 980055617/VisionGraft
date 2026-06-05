public static class RuntimeProgressDisplay
{
    public readonly struct State
    {
        public State(float normalized, string clockText)
        {
            this.normalized = normalized;
            this.clockText = clockText;
        }

        public readonly float normalized;
        public readonly string clockText;
    }

    public static State Resolve(
        long videoFrameCount,
        long videoFrame,
        double videoTime,
        double videoLength,
        float videoFrameRate,
        float metadataFps,
        float manifestFps,
        int metadataTotalFrames,
        int manifestTotalFrames,
        int fallbackCurrentFrame)
    {
        long totalFrames = RuntimePlaybackTimeline.ResolveTotalFrames(videoFrameCount, metadataTotalFrames, manifestTotalFrames);
        float fpsFallback = RuntimePlaybackTimeline.ResolveMetadataFps(metadataFps, manifestFps);
        int currentFrame = RuntimePlaybackTimeline.ResolveCurrentFrame(videoFrame, videoTime, fpsFallback, fallbackCurrentFrame);
        double totalDuration = RuntimePlaybackTimeline.ResolveTotalDuration(videoLength, fpsFallback, totalFrames);
        float normalized = RuntimePlaybackTimeline.ResolveNormalized(videoTime, totalDuration, currentFrame, totalFrames);
        float seekFps = RuntimePlaybackTimeline.ResolveSeekFps(metadataFps, manifestFps, videoFrameRate);
        RuntimePlaybackTimeline.ResolveClockSeconds(
            videoTime,
            totalDuration,
            seekFps,
            totalFrames,
            videoFrame,
            currentFrame,
            out float curSec,
            out float totalSec);
        string clockText = RuntimePlaybackTimeline.FormatClock(curSec) + " / " + RuntimePlaybackTimeline.FormatClock(totalSec);
        return new State(normalized, clockText);
    }
}
