using NUnit.Framework;

public class InteractiveMotionScheduleTests
{
    [Test]
    public void ResolveInitializesNextTriggerWhenScheduleHasNotStarted()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.Resolve(
            enabled: true,
            isSupportedCategory: true,
            active: false,
            nextTriggerTime: 0f,
            startTime: 0f,
            duration: 5f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.action, Is.EqualTo(InteractiveMotionSchedule.Action.Wait));
        Assert.That(decision.nextTriggerTime, Is.EqualTo(13f).Within(0.0001f));
    }

    [Test]
    public void ResolveStartsWhenTriggerTimeHasArrived()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.Resolve(
            enabled: true,
            isSupportedCategory: true,
            active: false,
            nextTriggerTime: 9f,
            startTime: 0f,
            duration: 5f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.action, Is.EqualTo(InteractiveMotionSchedule.Action.Start));
        Assert.That(decision.nextTriggerTime, Is.EqualTo(9f).Within(0.0001f));
    }

    [Test]
    public void ResolveStopsActiveMotionAfterDurationAndSchedulesNextTrigger()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.Resolve(
            enabled: true,
            isSupportedCategory: true,
            active: true,
            nextTriggerTime: 9f,
            startTime: 10f,
            duration: 5f,
            now: 16f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.action, Is.EqualTo(InteractiveMotionSchedule.Action.Stop));
        Assert.That(decision.nextTriggerTime, Is.EqualTo(19f).Within(0.0001f));
    }

    [Test]
    public void ShouldStopFrameOutAnimalWaitsForFrameIn()
    {
        Assert.That(InteractiveMotionSchedule.ShouldStopFrameOut(
            isNullState: false,
            isAnimal: true,
            startedFromFrameOut: true,
            frameInFrame: 20,
            frameOutStartFrame: 10,
            currentFrame: 20,
            elapsedSeconds: 100f,
            durationSeconds: 5f), Is.False);

        Assert.That(InteractiveMotionSchedule.ShouldStopFrameOut(
            isNullState: false,
            isAnimal: true,
            startedFromFrameOut: true,
            frameInFrame: 20,
            frameOutStartFrame: 10,
            currentFrame: 21,
            elapsedSeconds: 0f,
            durationSeconds: 5f), Is.True);
    }

    [Test]
    public void ResolveFrameOutActionStartsAndAppliesInactiveSupportedTrack()
    {
        Assert.That(InteractiveMotionSchedule.ResolveFrameOutAction(
            enabled: true,
            hasInstance: true,
            hasState: true,
            hasLastTrackedTransform: true,
            isSupportedCategory: true,
            active: false,
            startedFromFrameOut: false,
            isReplacement: false), Is.EqualTo(InteractiveMotionSchedule.FrameOutAction.StartThenApply));
    }

    [Test]
    public void ResolveFrameOutActionAppliesActiveReplacementTrack()
    {
        Assert.That(InteractiveMotionSchedule.ResolveFrameOutAction(
            enabled: true,
            hasInstance: true,
            hasState: true,
            hasLastTrackedTransform: true,
            isSupportedCategory: true,
            active: true,
            startedFromFrameOut: true,
            isReplacement: true), Is.EqualTo(InteractiveMotionSchedule.FrameOutAction.Apply));
    }

    [Test]
    public void ResolveFrameOutActionIgnoresUnsupportedOrOverlayTrack()
    {
        Assert.That(InteractiveMotionSchedule.ResolveFrameOutAction(
            enabled: true,
            hasInstance: true,
            hasState: true,
            hasLastTrackedTransform: true,
            isSupportedCategory: false,
            active: false,
            startedFromFrameOut: false,
            isReplacement: false), Is.EqualTo(InteractiveMotionSchedule.FrameOutAction.None));

        Assert.That(InteractiveMotionSchedule.ResolveFrameOutAction(
            enabled: true,
            hasInstance: true,
            hasState: true,
            hasLastTrackedTransform: true,
            isSupportedCategory: true,
            active: true,
            startedFromFrameOut: true,
            isReplacement: false), Is.EqualTo(InteractiveMotionSchedule.FrameOutAction.None));
    }
}
