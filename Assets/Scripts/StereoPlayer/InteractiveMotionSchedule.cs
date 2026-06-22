public static class InteractiveMotionSchedule
{
    public readonly struct Decision
    {
        public Decision(bool shouldStart, float nextTriggerTime)
        {
            this.shouldStart = shouldStart;
            this.nextTriggerTime = nextTriggerTime;
        }

        public readonly bool shouldStart;
        public readonly float nextTriggerTime;
    }

    public static Decision ResolveRandomTrigger(
        bool enabled,
        bool isSupportedCategory,
        bool isInactive,
        float nextTriggerTime,
        float now,
        float nextIntervalSeconds)
    {
        if (!enabled || !isSupportedCategory || !isInactive)
        {
            return new Decision(false, nextTriggerTime);
        }

        if (nextTriggerTime <= 0f)
        {
            nextTriggerTime = now + nextIntervalSeconds;
        }

        if (now < nextTriggerTime)
        {
            return new Decision(false, nextTriggerTime);
        }

        return new Decision(true, now + nextIntervalSeconds);
    }
}
