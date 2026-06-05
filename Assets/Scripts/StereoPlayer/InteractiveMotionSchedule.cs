public static class InteractiveMotionSchedule
{
    public enum Action
    {
        None,
        Wait,
        Start,
        Stop
    }

    public enum FrameOutAction
    {
        None,
        StartThenApply,
        Apply
    }

    public readonly struct Decision
    {
        public Decision(Action action, float nextTriggerTime)
        {
            this.action = action;
            this.nextTriggerTime = nextTriggerTime;
        }

        public readonly Action action;
        public readonly float nextTriggerTime;
    }

    public static Decision Resolve(
        bool enabled,
        bool isSupportedCategory,
        bool active,
        float nextTriggerTime,
        float startTime,
        float duration,
        float now,
        float nextIntervalSeconds)
    {
        if (!enabled || !isSupportedCategory)
        {
            return new Decision(Action.None, nextTriggerTime);
        }

        if (nextTriggerTime <= 0f)
        {
            nextTriggerTime = now + nextIntervalSeconds;
        }

        if (active)
        {
            if (now - startTime > duration)
            {
                return new Decision(Action.Stop, now + nextIntervalSeconds);
            }

            return new Decision(Action.Wait, nextTriggerTime);
        }

        if (now < nextTriggerTime)
        {
            return new Decision(Action.Wait, nextTriggerTime);
        }

        return new Decision(Action.Start, nextTriggerTime);
    }

    public static bool ShouldStopFrameOut(
        bool isNullState,
        bool isAnimal,
        bool startedFromFrameOut,
        int frameInFrame,
        int frameOutStartFrame,
        int currentFrame,
        float elapsedSeconds,
        float durationSeconds)
    {
        if (isNullState)
        {
            return true;
        }

        if (isAnimal && startedFromFrameOut && frameInFrame > frameOutStartFrame)
        {
            return currentFrame > frameInFrame;
        }

        return elapsedSeconds > durationSeconds;
    }

    public static FrameOutAction ResolveFrameOutAction(
        bool enabled,
        bool hasInstance,
        bool hasState,
        bool hasLastTrackedTransform,
        bool isSupportedCategory,
        bool active,
        bool startedFromFrameOut,
        bool isReplacement)
    {
        if (!enabled || !hasInstance || !hasState || !hasLastTrackedTransform || !isSupportedCategory)
        {
            return FrameOutAction.None;
        }

        if (!active || !startedFromFrameOut)
        {
            return FrameOutAction.StartThenApply;
        }

        return isReplacement ? FrameOutAction.Apply : FrameOutAction.None;
    }
}
