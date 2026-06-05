using NUnit.Framework;
using UnityEngine;

public class TrackPrefabResolverTests
{
    [Test]
    public void ResolvePrefersTrackSpecificPrefabBeforeFallback()
    {
        GameObject fallback = new GameObject("Fallback");
        GameObject track1 = new GameObject("Track1");
        try
        {
            GameObject resolved = TrackPrefabResolver.Resolve(
                1u,
                fallback,
                null,
                track1,
                null);

            Assert.That(resolved, Is.EqualTo(track1));
        }
        finally
        {
            Object.DestroyImmediate(fallback);
            Object.DestroyImmediate(track1);
        }
    }

    [Test]
    public void ResolveUsesFallbackWhenTrackSpecificPrefabIsMissing()
    {
        GameObject fallback = new GameObject("Fallback");
        try
        {
            GameObject resolved = TrackPrefabResolver.Resolve(
                3u,
                fallback,
                null,
                null,
                null);

            Assert.That(resolved, Is.EqualTo(fallback));
        }
        finally
        {
            Object.DestroyImmediate(fallback);
        }
    }

    [Test]
    public void HasAnyConfiguredDetectsFallbackOrTrackPrefab()
    {
        GameObject track2 = new GameObject("Track2");
        try
        {
            Assert.That(TrackPrefabResolver.HasAnyConfigured(null, null, null, null), Is.False);
            Assert.That(TrackPrefabResolver.HasAnyConfigured(null, null, null, track2), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(track2);
        }
    }
}
