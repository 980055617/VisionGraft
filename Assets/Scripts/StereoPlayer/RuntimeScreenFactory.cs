using UnityEngine;

public static class RuntimeScreenFactory
{
    public static Transform Create(string runtimeName, GameObject prefab, Transform parent)
    {
        GameObject screenObject;
        if (prefab != null)
        {
            screenObject = Object.Instantiate(prefab, parent, false);
        }
        else
        {
            screenObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenObject.transform.SetParent(parent, false);
        }

        if (screenObject == null)
        {
            return null;
        }

        screenObject.name = runtimeName;
        return screenObject.transform;
    }
}
