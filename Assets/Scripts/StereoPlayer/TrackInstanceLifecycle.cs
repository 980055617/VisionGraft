using System.Collections.Generic;
using UnityEngine;

public static class TrackInstanceLifecycle
{
    public static GameObject GetOrCreate(
        uint trackId,
        GameObject prefab,
        Dictionary<uint, GameObject> instances,
        Dictionary<uint, GameObject> prefabSources,
        Dictionary<uint, Vector3> lockedModelLocalScaleByTrack,
        ref int selectedManualRotationTrackId)
    {
        if (prefab == null || instances == null || prefabSources == null || lockedModelLocalScaleByTrack == null)
        {
            return null;
        }

        if (instances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            if (prefabSources.TryGetValue(trackId, out GameObject source) && source != prefab)
            {
                GameObjectLifecycleWriter.DestroyObject(existing);
                instances.Remove(trackId);
                prefabSources.Remove(trackId);
                lockedModelLocalScaleByTrack.Remove(trackId);
            }
            else
            {
                SelectManualRotationTrackIfNeeded(trackId, ref selectedManualRotationTrackId);
                return existing;
            }
        }

        GameObject instance = TrackInstanceFactory.Create(prefab, trackId);
        if (instance == null)
        {
            return null;
        }

        instances[trackId] = instance;
        prefabSources[trackId] = prefab;
        SelectManualRotationTrackIfNeeded(trackId, ref selectedManualRotationTrackId);
        return instance;
    }

    private static void SelectManualRotationTrackIfNeeded(uint trackId, ref int selectedManualRotationTrackId)
    {
        if (selectedManualRotationTrackId < 0)
        {
            selectedManualRotationTrackId = (int)trackId;
        }
    }
}
