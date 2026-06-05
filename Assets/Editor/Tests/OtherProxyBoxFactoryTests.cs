using NUnit.Framework;
using UnityEngine;

public class OtherProxyBoxFactoryTests
{
    [Test]
    public void CreateNamesBoxRemovesColliderAndAppliesMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Assert.Inconclusive("Sprites/Default shader is not available in this Unity environment.");
        }

        Material material = new Material(shader);
        try
        {
            GameObject box = OtherProxyBoxFactory.Create(7u, material);
            try
            {
                Assert.That(box.name, Is.EqualTo("OtherProxy_7"));
                Assert.That(box.GetComponent<Collider>(), Is.Null);
                Assert.That(box.GetComponent<Renderer>().sharedMaterial, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(box);
            }
        }
        finally
        {
            Object.DestroyImmediate(material);
        }
    }
}
