using NUnit.Framework;
using UnityEngine;

public class RuntimeUiRootFactoryTests
{
    [Test]
    public void CreateInstantiatesPrefabWhenProvided()
    {
        GameObject prefab = new GameObject("Prefab");
        try
        {
            GameObject root = RuntimeUiRootFactory.Create("RuntimeRoot", prefab);
            try
            {
                Assert.That(root, Is.Not.Null);
                Assert.That(root.name, Is.EqualTo("RuntimeRoot"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void CreateBuildsEmptyRootWhenPrefabIsMissing()
    {
        GameObject root = RuntimeUiRootFactory.Create("RuntimeRoot", null);
        try
        {
            Assert.That(root, Is.Not.Null);
            Assert.That(root.name, Is.EqualTo("RuntimeRoot"));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }
}
