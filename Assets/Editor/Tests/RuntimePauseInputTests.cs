using NUnit.Framework;

public class RuntimePauseInputTests
{
    [Test]
    public void ResolveTogglesImmediatelyForHotkey()
    {
        RuntimePauseInput.Decision decision = RuntimePauseInput.Resolve(
            hotkeyPressed: true,
            hasPrimaryButton: false,
            primaryButtonPressed: false,
            previousPrimaryButtonPressed: true);

        Assert.That(decision.togglePause, Is.True);
        Assert.That(decision.previousPrimaryButtonPressed, Is.True);
    }

    [Test]
    public void ResolveTogglesOnPrimaryButtonRisingEdge()
    {
        RuntimePauseInput.Decision decision = RuntimePauseInput.Resolve(
            hotkeyPressed: false,
            hasPrimaryButton: true,
            primaryButtonPressed: true,
            previousPrimaryButtonPressed: false);

        Assert.That(decision.togglePause, Is.True);
        Assert.That(decision.previousPrimaryButtonPressed, Is.True);
    }

    [Test]
    public void ResolveResetsPreviousButtonWhenNoDeviceCanReadPrimaryButton()
    {
        RuntimePauseInput.Decision decision = RuntimePauseInput.Resolve(
            hotkeyPressed: false,
            hasPrimaryButton: false,
            primaryButtonPressed: false,
            previousPrimaryButtonPressed: true);

        Assert.That(decision.togglePause, Is.False);
        Assert.That(decision.previousPrimaryButtonPressed, Is.False);
    }
}
