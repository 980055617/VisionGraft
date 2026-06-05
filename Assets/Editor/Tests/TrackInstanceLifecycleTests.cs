using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class TrackInstanceLifecycleTests
{
    [Test]
    public void GetOrCreateReusesExistingTrackInstanceAndSelectsManualRotationTrack()
    {
        GameObject prefab = new GameObject("Prefab");
        GameObject existing = new GameObject("Existing");
        Dictionary<uint, GameObject> instances = new Dictionary<uint, GameObject>
        {
            [7u] = existing
        };
        Dictionary<uint, GameObject> prefabSources = new Dictionary<uint, GameObject>
        {
            [7u] = prefab
        };
        Dictionary<uint, Vector3> lockedScales = new Dictionary<uint, Vector3>();
        int selectedTrack = -1;

        try
        {
            GameObject result = TrackInstanceLifecycle.GetOrCreate(
                7u,
                prefab,
                instances,
                prefabSources,
                lockedScales,
                ref selectedTrack);

            Assert.That(result, Is.SameAs(existing));
            Assert.That(instances[7u], Is.SameAs(existing));
            Assert.That(selectedTrack, Is.EqualTo(7));
        }
        finally
        {
            Object.DestroyImmediate(existing);
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void GetOrCreateReplacesTrackInstanceWhenPrefabSourceChangesAndClearsLockedScale()
    {
        GameObject oldPrefab = new GameObject("OldPrefab");
        GameObject newPrefab = new GameObject("NewPrefab");
        GameObject existing = new GameObject("Existing");
        Dictionary<uint, GameObject> instances = new Dictionary<uint, GameObject>
        {
            [2u] = existing
        };
        Dictionary<uint, GameObject> prefabSources = new Dictionary<uint, GameObject>
        {
            [2u] = oldPrefab
        };
        Dictionary<uint, Vector3> lockedScales = new Dictionary<uint, Vector3>
        {
            [2u] = new Vector3(2f, 2f, 2f)
        };
        int selectedTrack = 3;

        try
        {
            GameObject result = TrackInstanceLifecycle.GetOrCreate(
                2u,
                newPrefab,
                instances,
                prefabSources,
                lockedScales,
                ref selectedTrack);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.SameAs(existing));
            Assert.That(result.name, Is.EqualTo("Track_2"));
            Assert.That(instances[2u], Is.SameAs(result));
            Assert.That(prefabSources[2u], Is.SameAs(newPrefab));
            Assert.That(lockedScales.ContainsKey(2u), Is.False);
            Assert.That(selectedTrack, Is.EqualTo(3));
        }
        finally
        {
            if (instances.TryGetValue(2u, out GameObject current) && current != null)
            {
                Object.DestroyImmediate(current);
            }
            Object.DestroyImmediate(newPrefab);
            Object.DestroyImmediate(oldPrefab);
        }
    }
}
