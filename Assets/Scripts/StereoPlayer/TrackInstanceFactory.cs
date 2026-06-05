using UnityEngine;

public static class TrackInstanceFactory
{
    public static GameObject Create(GameObject prefab, uint trackId)
    {
        if (prefab == null)
        {
            return null;
        }

        GameObject instance = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        instance.name = $"Track_{trackId}";
        if (instance.GetComponent<ReplaceableModel>() == null)
        {
            instance.AddComponent<ReplaceableModel>();
        }

        return instance;
    }
}
