using UnityEngine;

public static class GameObjectLifecycleWriter
{
    public static void ApplyActive(GameObject target, bool active)
    {
        if (target == null)
        {
            return;
        }

        target.SetActive(active);
    }

    public static void DestroyObject(Object target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Object.Destroy(target);
        }
        else
        {
            Object.DestroyImmediate(target);
        }
    }
}
