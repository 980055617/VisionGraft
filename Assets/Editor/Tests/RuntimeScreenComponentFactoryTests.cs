using NUnit.Framework;
using UnityEngine;

public class RuntimeScreenComponentFactoryTests
{
    [Test]
    public void EnsureMeshFilterAddsFilterWhenMissing()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            MeshFilter filter = RuntimeScreenComponentFactory.EnsureMeshFilter(screen);

            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.gameObject, Is.EqualTo(screen));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    [Test]
    public void EnsureMeshRendererReusesExistingRenderer()
    {
        GameObject screen = new GameObject("Screen");
        MeshRenderer existing = screen.AddComponent<MeshRenderer>();
        try
        {
            MeshRenderer renderer = RuntimeScreenComponentFactory.EnsureMeshRenderer(screen);

            Assert.That(renderer, Is.EqualTo(existing));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }

    [Test]
    public void EnsureMeshColliderAddsColliderWhenMissing()
    {
        GameObject screen = new GameObject("Screen");
        try
        {
            MeshCollider collider = RuntimeScreenComponentFactory.EnsureMeshCollider(screen);

            Assert.That(collider, Is.Not.Null);
            Assert.That(collider.gameObject, Is.EqualTo(screen));
        }
        finally
        {
            Object.DestroyImmediate(screen);
        }
    }
}
