using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 動画ごと・track ごとのモデル選択と手動 yaw を復元／保存する。
    // 設計と経緯は docs/model-selection-persistence.md。

    private string trackCustomizationVideoKey;

    // 復元した prefab 名を index に直せるようになるまで預けておく置き場。
    // track のカテゴリが確定するのは ResolveTrackPrefab の時点なので、そこで解決する。
    private readonly Dictionary<uint, string> pendingModelNameByTrack = new Dictionary<uint, string>();

    private string ResolveTrackCustomizationVideoKey()
    {
        // **bundleFileName ではない。** 再生成で .svb の名前が変わっても
        // manifest.inputs.video_mp4 は同じなので、仕込んだ設定が残る。
        return manifest != null && manifest.inputs != null ? manifest.inputs.video_mp4 : null;
    }


    // manifest と meta を読んだ直後、最初のインスタンス生成より前に呼ぶ。
    // 既存の解決順（selectedModelIndexByTrack -> trackModelIndices -> selectedHumanIndex...）
    // には触らず、selectedModelIndexByTrack と manualYawKeyframesByTrack を埋めるだけ。
    private void RestoreTrackCustomization()
    {
        trackCustomizationVideoKey = null;
        pendingModelNameByTrack.Clear();
        if (!rememberTrackCustomization)
        {
            return;
        }

        string key = ResolveTrackCustomizationVideoKey();
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning("[Customization] manifest.inputs.video_mp4 が無いので復元しません。");
            return;
        }

        trackCustomizationVideoKey = key;

        // ① 基準を読み、② セッション上書きを重ねる。
        VideoCustomization merged = new VideoCustomization();
        merged.OverlayWith(TrackCustomizationStore.Get(key));
        merged.OverlayWith(ExperimentSessionOverrides.Get(key));
        if (merged.tracks.Count == 0)
        {
            return;
        }

        int restoredModels = 0;
        int restoredYaw = 0;
        int frames = manifest != null ? manifest.num_frames : 0;

        foreach (KeyValuePair<uint, TrackCustomization> kv in merged.tracks)
        {
            TrackCustomization entry = kv.Value;
            if (entry == null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(entry.modelPrefabName))
            {
                // ここでは index に解決できない。track のカテゴリが分かるのは
                // ResolveTrackPrefab の時点なので、名前のまま預けておいて遅延解決する。
                pendingModelNameByTrack[kv.Key] = entry.modelPrefabName;
                restoredModels++;
            }

            if (entry.yawKeyframes != null && entry.yawKeyframes.Count > 0)
            {
                // yaw キーフレームはフレーム番号に紐づくので、フレーム数が変わったら別の場面に当たる。
                int storedFrames = ResolveStoredNumFrames(key);
                if (storedFrames > 0 && frames > 0 && storedFrames != frames)
                {
                    Debug.LogWarning(
                        $"[Customization] numFrames 不一致 ({storedFrames} != {frames}) のため yaw を破棄します: " +
                        $"track={kv.Key}");
                }
                else
                {
                    manualYawKeyframesByTrack[kv.Key] = new SortedDictionary<int, float>(entry.yawKeyframes);
                    restoredYaw++;
                }
            }
        }

        Debug.Log(
            $"[Customization] restored video={key} models={restoredModels} yawTracks={restoredYaw} " +
            $"session={(ExperimentSessionOverrides.Active ? "experiment(読むだけ)" : "normal(書き込み可)")}");
    }


    private int ResolveStoredNumFrames(string key)
    {
        VideoCustomization session = ExperimentSessionOverrides.Get(key);
        if (session != null && session.numFrames > 0)
        {
            return session.numFrames;
        }

        VideoCustomization baseline = TrackCustomizationStore.Get(key);
        return baseline != null ? baseline.numFrames : 0;
    }


    // 復元した prefab 名を index に直す。カテゴリが確定する ResolveTrackPrefab から呼ぶ。
    // 名前が見つからなければ何もしない（＝既定のまま）。1 track につき 1 回だけ試す。
    private void ResolvePendingModelSelection(uint trackId, GameObject[] prefabs)
    {
        if (pendingModelNameByTrack.Count == 0 ||
            !pendingModelNameByTrack.TryGetValue(trackId, out string prefabName))
        {
            return;
        }

        pendingModelNameByTrack.Remove(trackId);
        if (prefabs == null || string.IsNullOrEmpty(prefabName))
        {
            return;
        }

        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i] != null && prefabs[i].name == prefabName)
            {
                selectedModelIndexByTrack[trackId] = i;
                Debug.Log($"[Customization] applied track={trackId} model={prefabName} index={i}");
                return;
            }
        }

        // モデルの追加・削除・改名で名前が消えることがある。既定に戻して知らせる。
        Debug.LogWarning($"[Customization] prefab が見つからないので既定に戻します: track={trackId} name={prefabName}");
    }


    // ---- 保存 ----

    // 実験中は基準ファイルに書かず、セッション上書きへ入れる。
    private VideoCustomization ResolveWritableCustomization()
    {
        if (!rememberTrackCustomization || string.IsNullOrEmpty(trackCustomizationVideoKey))
        {
            return null;
        }

        int frames = manifest != null ? manifest.num_frames : 0;
        return ExperimentSessionOverrides.Active
            ? ExperimentSessionOverrides.GetOrCreate(trackCustomizationVideoKey, frames)
            : TrackCustomizationStore.GetOrCreate(trackCustomizationVideoKey, frames);
    }


    private void PersistModelSelection(uint trackId, string prefabName)
    {
        VideoCustomization target = ResolveWritableCustomization();
        if (target == null || string.IsNullOrEmpty(prefabName))
        {
            return;
        }

        target.GetOrCreate(trackId).modelPrefabName = prefabName;
        if (!ExperimentSessionOverrides.Active)
        {
            TrackCustomizationStore.Save();
        }
    }


    private void PersistManualYaw(uint trackId)
    {
        VideoCustomization target = ResolveWritableCustomization();
        if (target == null)
        {
            return;
        }

        TrackCustomization entry = target.GetOrCreate(trackId);
        if (manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) &&
            keys != null && keys.Count > 0)
        {
            entry.yawKeyframes = new SortedDictionary<int, float>(keys);
        }
        else
        {
            entry.yawKeyframes = null;
        }

        if (!ExperimentSessionOverrides.Active)
        {
            TrackCustomizationStore.Save();
        }
    }
}
