public static class RuntimeClock
{
    public readonly struct TickContext
    {
        public TickContext(float now, float deltaTime, int frameCount)
        {
            this.now = now;
            this.deltaTime = deltaTime;
            this.frameCount = frameCount;
        }

        public readonly float now;
        public readonly float deltaTime;
        public readonly int frameCount;
    }

    public static float ResolveDeltaTime(float deltaTime)
    {
        return deltaTime > 0.0001f ? deltaTime : (1f / 60f);
    }

    public static TickContext ResolveTickContext(float now, float deltaTime, int frameCount)
    {
        return new TickContext(now, ResolveDeltaTime(deltaTime), frameCount);
    }

    public static float ResolveElapsed(float now, float startTime)
    {
        return now > startTime ? now - startTime : 0f;
    }

    public static float ResolveNextTime(float now, float interval)
    {
        return now + interval;
    }

    public static float ResolveTimeoutRemaining(float timeout, float unscaledDeltaTime)
    {
        return timeout - unscaledDeltaTime;
    }

    public static bool WasSeenRecently(int currentFrame, int lastSeenFrame, int maxFrameGap)
    {
        return currentFrame - lastSeenFrame <= maxFrameGap;
    }
}
