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
        if (instances == null || prefabSources == null || lockedModelLocalScaleByTrack == null)
        {
            return null;
        }

        // prefab が null = 「この track には置かない」。既存のインスタンスは片付ける。
        // 単に非アクティブにするのではなく破棄する。ユーザーが「表示しない」を選んだ track に
        // 姿勢適用やスケールのロックが走り続ける必要はない。
        if (prefab == null)
        {
            if (instances.TryGetValue(trackId, out GameObject hidden) && hidden != null)
            {
                SceneObjectWriter.DestroyObject(hidden);
            }

            instances.Remove(trackId);
            prefabSources.Remove(trackId);
            lockedModelLocalScaleByTrack.Remove(trackId);
            return null;
        }

        if (instances.TryGetValue(trackId, out GameObject existing) && existing != null)
        {
            if (prefabSources.TryGetValue(trackId, out GameObject source) && source != prefab)
            {
                SceneObjectWriter.DestroyObject(existing);
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
