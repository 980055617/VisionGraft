using NUnit.Framework;

public class RuntimePauseInputReaderTests
{
    [Test]
    public void ResolveHotkeyPressedAcceptsEitherInputBackend()
    {
        Assert.That(RuntimePauseInputReader.ResolveHotkeyPressed(true, false), Is.True);
        Assert.That(RuntimePauseInputReader.ResolveHotkeyPressed(false, true), Is.True);
        Assert.That(RuntimePauseInputReader.ResolveHotkeyPressed(false, false), Is.False);
    }
}
