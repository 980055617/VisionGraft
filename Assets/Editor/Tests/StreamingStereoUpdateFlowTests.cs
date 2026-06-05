using NUnit.Framework;

public class StreamingStereoUpdateFlowTests
{
    [Test]
    public void ResolveSkipsRuntimeTickWhileBundlePickerIsActive()
    {
        StreamingStereoUpdateFlow.Decision decision = StreamingStereoUpdateFlow.Resolve(bundlePickerActive: true);

        Assert.That(decision.updateBundlePickerPlacement, Is.True);
        Assert.That(decision.updateRuntimePlayback, Is.False);
    }

    [Test]
    public void ResolveRunsRuntimeTickWhenBundlePickerIsInactive()
    {
        StreamingStereoUpdateFlow.Decision decision = StreamingStereoUpdateFlow.Resolve(bundlePickerActive: false);

        Assert.That(decision.updateBundlePickerPlacement, Is.False);
        Assert.That(decision.updateRuntimePlayback, Is.True);
    }
}
