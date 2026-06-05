using NUnit.Framework;
using UnityEngine;

public class AnimalAimChildSelectorTests
{
    [Test]
    public void SelectPrefersRegisteredAimChildBeforeFallbackChild()
    {
        GameObject registered = new GameObject("RegisteredAimChild");
        GameObject fallback = new GameObject("FallbackFirstChild");
        try
        {
            Transform selected = AnimalAimChildSelector.Select(registered.transform, fallback.transform);

            Assert.That(selected, Is.EqualTo(registered.transform));
        }
        finally
        {
            Object.DestroyImmediate(registered);
            Object.DestroyImmediate(fallback);
        }
    }

    [Test]
    public void SelectUsesFallbackChildWhenRegisteredAimChildIsMissing()
    {
        GameObject fallback = new GameObject("FallbackFirstChild");
        try
        {
            Transform selected = AnimalAimChildSelector.Select(null, fallback.transform);

            Assert.That(selected, Is.EqualTo(fallback.transform));
        }
        finally
        {
            Object.DestroyImmediate(fallback);
        }
    }

    [Test]
    public void SelectReturnsNullWhenNoCandidateExists()
    {
        Assert.That(AnimalAimChildSelector.Select(null, null), Is.Null);
    }
}
