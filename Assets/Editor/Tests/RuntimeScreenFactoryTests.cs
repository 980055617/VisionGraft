using NUnit.Framework;
using UnityEngine;

public class RuntimeScreenFactoryTests
{
    [Test]
    public void CreateUsesFallbackQuadWhenPrefabIsMissing()
    {
        GameObject parent = new GameObject("Parent");
        try
        {
            Transform screen = RuntimeScreenFactory.Create("RuntimeScreen", null, parent.transform);

            Assert.That(screen, Is.Not.Null);
            Assert.That(screen.name, Is.EqualTo("RuntimeScreen"));
            Assert.That(screen.parent, Is.EqualTo(parent.transform));
        }
        finally
        {
            Object.DestroyImmediate(parent);
        }
    }

    [Test]
    public void CreateInstantiatesPrefabWhenProvided()
    {
        GameObject parent = new GameObject("Parent");
        GameObject prefab = new GameObject("Prefab");
        try
        {
            Transform screen = RuntimeScreenFactory.Create("RuntimeScreen", prefab, parent.transform);
            try
            {
                Assert.That(screen, Is.Not.Null);
                Assert.That(screen.name, Is.EqualTo("RuntimeScreen"));
                Assert.That(screen.parent, Is.EqualTo(parent.transform));
            }
            finally
            {
                Object.DestroyImmediate(screen.gameObject);
            }
        }
        finally
        {
            Object.DestroyImmediate(parent);
            Object.DestroyImmediate(prefab);
        }
    }
}
