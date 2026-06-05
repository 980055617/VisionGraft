using UnityEngine;

public static class ScreenMaterialWriter
{
    public static void ApplyMaterial(Renderer renderer, Material material)
    {
        if (renderer == null)
        {
            return;
        }

        renderer.material = material;
    }

    public static void ApplyTexture(Material material, string textureProperty, Texture texture)
    {
        if (material == null || texture == null || string.IsNullOrEmpty(textureProperty))
        {
            return;
        }

        if (!material.HasProperty(textureProperty))
        {
            return;
        }

        material.SetTexture(textureProperty, texture);
    }
}
