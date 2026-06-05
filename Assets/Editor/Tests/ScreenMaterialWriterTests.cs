using NUnit.Framework;
using UnityEngine;

public class ScreenMaterialWriterTests
{
    [Test]
    public void ApplyMaterialIgnoresNullRenderer()
    {
        Assert.DoesNotThrow(() => ScreenMaterialWriter.ApplyMaterial(null, null));
    }

    [Test]
    public void ApplyTextureIgnoresNullInputs()
    {
        Assert.DoesNotThrow(() => ScreenMaterialWriter.ApplyTexture(null, "_MainTex", null));
    }

    [Test]
    public void ApplyTextureSetsExistingTextureProperty()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Assert.Inconclusive("Sprites/Default shader is not available in this Unity environment.");
        }

        Material material = new Material(shader);
        Texture2D texture = new Texture2D(1, 1);
        try
        {
            ScreenMaterialWriter.ApplyTexture(material, "_MainTex", texture);

            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(texture));
        }
        finally
        {
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(material);
        }
    }
}
