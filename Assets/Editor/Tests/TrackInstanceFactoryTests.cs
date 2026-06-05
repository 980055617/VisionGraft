using NUnit.Framework;
using UnityEngine;

public class TrackInstanceFactoryTests
{
    [Test]
    public void CreateReturnsNullWhenPrefabIsMissing()
    {
        Assert.That(TrackInstanceFactory.Create(null, 1u), Is.Null);
    }

    [Test]
    public void CreateNamesInstanceAndAddsReplaceableModel()
    {
        GameObject prefab = new GameObject("Prefab");
        try
        {
            GameObject instance = TrackInstanceFactory.Create(prefab, 42u);
            try
            {
                Assert.That(instance, Is.Not.Null);
                Assert.That(instance.name, Is.EqualTo("Track_42"));
                Assert.That(instance.GetComponent<ReplaceableModel>(), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }
}
