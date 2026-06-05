using UnityEngine;

public static class RuntimeUiRootFactory
{
    public static GameObject Create(string name, GameObject prefab)
    {
        GameObject root = prefab != null
            ? Object.Instantiate(prefab)
            : new GameObject(name);

        if (root != null)
        {
            root.name = name;
        }

        return root;
    }
}
