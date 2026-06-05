using UnityEngine;

public static class OtherProxyBoxFactory
{
    public static GameObject Create(uint trackId, Material material)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = $"OtherProxy_{trackId}";

        Collider collider = box.GetComponent<Collider>();
        if (collider != null)
        {
            SceneObjectWriter.DestroyObject(collider);
        }

        Renderer renderer = box.GetComponent<Renderer>();
        if (renderer != null && material != null)
        {
            SceneObjectWriter.ApplyMaterial(renderer, material);
        }

        return box;
    }
}
