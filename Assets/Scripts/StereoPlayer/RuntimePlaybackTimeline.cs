using UnityEngine;

public static class RuntimePlaybackTimeline
{
    public readonly struct SeekTarget
    {
        public SeekTarget(bool hasTime, double timeSeconds, bool hasFrame, long frame)
        {
            this.hasTime = hasTime;
            this.timeSeconds = timeSeconds;
            this.hasFrame = hasFrame;
            this.frame = frame;
        }

        public readonly bool hasTime;
        public readonly double timeSeconds;
        public readonly bool hasFrame;
        public readonly long frame;
    }

    public readonly struct FrameSnapshot
    {
        public FrameSnapshot(int currentFrame, int displayMetadataFrame)
        {
            this.currentFrame = currentFrame;
            this.displayMetadataFrame = displayMetadataFrame;
        }

        public readonly int currentFrame;
        public readonly int displayMetadataFrame;
    }

    public static long ResolveTotalFrames(long videoFrameCount, int metaFrameCount, int manifestFrameCount)
    {
        if (videoFrameCount > 1)
        {
            return videoFrameCount;
        }

        return metaFrameCount > 1 ? metaFrameCount : (manifestFrameCount > 1 ? manifestFrameCount : 0L);
    }


    public static float ResolveMetadataFps(float metaFps, float manifestFps)
    {
        return metaFps > 0.001f ? metaFps : (manifestFps > 0.001f ? manifestFps : 0f);
    }


    public static float ResolveSeekFps(float metaFps, float manifestFps, double videoFrameRate)
    {
        float metadataFps = ResolveMetadataFps(metaFps, manifestFps);
        return metadataFps > 0.001f ? metadataFps : (videoFrameRate > 0.001d ? (float)videoFrameRate : 0f);
    }


    public static int ResolveCurrentFrame(long videoFrame, double videoTime, float fpsFallback, int fallbackCurrentFrame)
    {
        if (videoFrame >= 0)
        {
            return (int)videoFrame;
        }

        return fpsFallback > 0.001f
            ? Mathf.Max(0, Mathf.FloorToInt((float)videoTime * fpsFallback))
            : fallbackCurrentFrame;
    }

    public static int ResolveMetadataFrame(long videoFrame, double videoTime, float fpsFallback, int metadataFrameCount)
    {
        if (metadataFrameCount <= 0)
        {
            return 0;
        }

        int frame = ResolveCurrentFrame(videoFrame, videoTime, fpsFallback, 0);
        return Mathf.Clamp(frame, 0, metadataFrameCount - 1);
    }

    public static int ResolveDisplayMetadataFrame(
        int currentFrame,
        int lastFrameReadyFrame,
        bool useFrameReadySync)
    {
        return useFrameReadySync ? lastFrameReadyFrame : currentFrame;
    }

    public static FrameSnapshot ResolveFrameSnapshot(
        long videoFrame,
        double videoTime,
        float fpsFallback,
        int metadataFrameCount,
        int lastFrameReadyFrame,
        bool useFrameReadySync)
    {
        int currentFrame = ResolveMetadataFrame(videoFrame, videoTime, fpsFallback, metadataFrameCount);
        return new FrameSnapshot(
            currentFrame,
            ResolveDisplayMetadataFrame(currentFrame, lastFrameReadyFrame, useFrameReadySync));
    }

    public static int NormalizeFrameReadyFrame(long frame)
    {
        if (frame < 0L)
        {
            return -1;
        }

        return frame > int.MaxValue ? int.MaxValue : (int)frame;
    }


    public static double ResolveTotalDuration(double videoLength, float fpsFallback, long totalFrames)
    {
        if (videoLength > 0.0001d)
        {
            return videoLength;
        }

        return fpsFallback > 0.001f && totalFrames > 0 ? totalFrames / fpsFallback : videoLength;
    }


    public static float ResolveNormalized(double videoTime, double totalDuration, int currentFrame, long totalFrames)
    {
        if (totalDuration > 0.0001d)
        {
            return Mathf.Clamp01((float)(videoTime / totalDuration));
        }

        return totalFrames > 1 ? Mathf.Clamp01((float)currentFrame / (float)(totalFrames - 1)) : 0f;
    }


    public static void ResolveClockSeconds(
        double videoTime,
        double totalDuration,
        float fps,
        long totalFrames,
        long videoFrame,
        int currentFrame,
        out float currentSeconds,
        out float totalSeconds)
    {
        currentSeconds = (float)videoTime;
        totalSeconds = (float)totalDuration;
        if (totalSeconds <= 0.0001f && fps > 0.001f && totalFrames > 0)
        {
            int frameForTime = videoFrame >= 0 ? (int)videoFrame : currentFrame;
            currentSeconds = frameForTime / fps;
            totalSeconds = (float)totalFrames / fps;
        }
    }


    public static long ResolveTargetFrame(float normalized, long totalFrames)
    {
        if (totalFrames <= 1)
        {
            return 0L;
        }

        normalized = Mathf.Clamp01(normalized);
        long targetFrame = (long)Mathf.Round(normalized * (totalFrames - 1));
        return System.Math.Max(0L, System.Math.Min(targetFrame, totalFrames - 1L));
    }

    public static SeekTarget ResolveSeekTarget(
        float normalized,
        double totalDuration,
        bool canSetTime,
        long totalFrames)
    {
        normalized = Mathf.Clamp01(normalized);
        if (totalDuration > 0.0001d && canSetTime)
        {
            return new SeekTarget(true, normalized * totalDuration, false, 0L);
        }

        if (totalFrames > 1)
        {
            return new SeekTarget(false, 0d, true, ResolveTargetFrame(normalized, totalFrames));
        }

        return new SeekTarget(false, 0d, false, 0L);
    }


    public static string FormatClock(float seconds)
    {
        if (seconds < 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
        {
            seconds = 0f;
        }

        int total = Mathf.FloorToInt(seconds);
        int minutes = total / 60;
        int remainingSeconds = total % 60;
        return minutes.ToString("00") + ":" + remainingSeconds.ToString("00");
    }
}
