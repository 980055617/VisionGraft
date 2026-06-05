using UnityEngine;

public static class TrackPrefabResolver
{
    public static bool HasAnyConfigured(
        GameObject fallbackPrefab,
        GameObject track0Prefab,
        GameObject track1Prefab,
        GameObject track2Prefab)
    {
        return fallbackPrefab != null || track0Prefab != null || track1Prefab != null || track2Prefab != null;
    }

    public static GameObject Resolve(
        uint trackId,
        GameObject fallbackPrefab,
        GameObject track0Prefab,
        GameObject track1Prefab,
        GameObject track2Prefab)
    {
        if (trackId == 0u && track0Prefab != null)
        {
            return track0Prefab;
        }

        if (trackId == 1u && track1Prefab != null)
        {
            return track1Prefab;
        }

        if (trackId == 2u && track2Prefab != null)
        {
            return track2Prefab;
        }

        return fallbackPrefab;
    }
}
