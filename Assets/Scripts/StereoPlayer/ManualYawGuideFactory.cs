using UnityEngine;

public static class ManualYawGuideFactory
{
    public readonly struct Guide
    {
        public Guide(GameObject root, Transform shaft, Transform tip)
        {
            this.root = root;
            this.shaft = shaft;
            this.tip = tip;
        }

        public readonly GameObject root;
        public readonly Transform shaft;
        public readonly Transform tip;
    }

    public static Guide Create()
    {
        GameObject root = new GameObject("ManualYawGuide");

        Transform shaft = CreatePrimitiveChild(root.transform, PrimitiveType.Cube, "Shaft", new Color(1f, 0.1f, 0.1f, 1f));
        Transform tip = CreatePrimitiveChild(root.transform, PrimitiveType.Sphere, "Tip", new Color(1f, 0.35f, 0.35f, 1f));

        return new Guide(root, shaft, tip);
    }

    private static Transform CreatePrimitiveChild(Transform parent, PrimitiveType primitiveType, string name, Color color)
    {
        GameObject child = GameObject.CreatePrimitive(primitiveType);
        child.name = name;
        child.transform.SetParent(parent, false);
        RemoveCollider(child);
        TintMesh(child, color);
        return child.transform;
    }

    private static void RemoveCollider(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        Collider collider = target.GetComponent<Collider>();
        if (collider != null)
        {
            GameObjectLifecycleWriter.DestroyObject(collider);
        }
    }

    private static void TintMesh(GameObject target, Color color)
    {
        if (target == null)
        {
            return;
        }

        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer == null)
        {
            return;
        }

        Material material = renderer.material;
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 0.7f);
        }
    }
}
