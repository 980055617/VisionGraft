using NUnit.Framework;
using UnityEngine;

public class GameObjectLifecycleWriterTests
{
    [Test]
    public void ApplyActiveSetsActiveState()
    {
        GameObject go = new GameObject("LifecycleTarget");
        try
        {
            GameObjectLifecycleWriter.ApplyActive(go, false);

            Assert.That(go.activeSelf, Is.False);

            GameObjectLifecycleWriter.ApplyActive(go, true);

            Assert.That(go.activeSelf, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void DestroyObjectIgnoresNull()
    {
        Assert.DoesNotThrow(() => GameObjectLifecycleWriter.DestroyObject(null));
    }
}
