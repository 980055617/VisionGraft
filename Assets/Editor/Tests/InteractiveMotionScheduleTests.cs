using NUnit.Framework;

public class InteractiveMotionScheduleTests
{
    [Test]
    public void ResolveRandomTriggerInitializesNextTriggerWhenScheduleHasNotStarted()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: true,
            isSupportedCategory: true,
            isInactive: true,
            isRandomEventAlreadyActive: false,
            nextTriggerTime: 0f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.shouldStart, Is.False);
        Assert.That(decision.nextTriggerTime, Is.EqualTo(13f).Within(0.0001f));
    }

    [Test]
    public void ResolveRandomTriggerStartsWhenTriggerTimeHasArrived()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: true,
            isSupportedCategory: true,
            isInactive: true,
            isRandomEventAlreadyActive: false,
            nextTriggerTime: 9f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.shouldStart, Is.True);
        Assert.That(decision.nextTriggerTime, Is.EqualTo(13f).Within(0.0001f));
    }

    [Test]
    public void ResolveRandomTriggerWaitsWhenNotInactive()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: true,
            isSupportedCategory: true,
            isInactive: false,
            isRandomEventAlreadyActive: false,
            nextTriggerTime: 9f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.shouldStart, Is.False);
        Assert.That(decision.nextTriggerTime, Is.EqualTo(9f).Within(0.0001f));
    }

    [Test]
    public void ResolveRandomTriggerIgnoresDisabledOrUnsupportedCategory()
    {
        Assert.That(InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: false,
            isSupportedCategory: true,
            isInactive: true,
            isRandomEventAlreadyActive: false,
            nextTriggerTime: 0f,
            now: 10f,
            nextIntervalSeconds: 3f).shouldStart, Is.False);

        Assert.That(InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: true,
            isSupportedCategory: false,
            isInactive: true,
            isRandomEventAlreadyActive: false,
            nextTriggerTime: 0f,
            now: 10f,
            nextIntervalSeconds: 3f).shouldStart, Is.False);
    }

    [Test]
    public void ResolveRandomTriggerWaitsAndReschedulesWhenAnotherRandomEventIsActive()
    {
        InteractiveMotionSchedule.Decision decision = InteractiveMotionSchedule.ResolveRandomTrigger(
            enabled: true,
            isSupportedCategory: true,
            isInactive: true,
            isRandomEventAlreadyActive: true,
            nextTriggerTime: 9f,
            now: 10f,
            nextIntervalSeconds: 3f);

        Assert.That(decision.shouldStart, Is.False);
        Assert.That(decision.nextTriggerTime, Is.EqualTo(13f).Within(0.0001f));
    }
}
