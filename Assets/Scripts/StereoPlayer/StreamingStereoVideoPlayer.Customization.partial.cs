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

    // yaw スライダーは 1 回のドラッグで何十回も値変更を飛ばす。そのたびに
    // File.WriteAllText するとムダな書き込みが積み上がるので、**メモリだけ即時更新し、
    // ファイルへの書き出しは操作が止まってから 1 回**にする。
    private bool trackCustomizationSaveRequested;
    private float trackCustomizationSaveDueTime;
    private const float TrackCustomizationSaveDelaySeconds = 0.75f;

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
        int restoredScale = 0;
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

            bool hasKeyframes =
                (entry.yawKeyframes != null && entry.yawKeyframes.Count > 0) ||
                (entry.scaleKeyframes != null && entry.scaleKeyframes.Count > 0);
            if (hasKeyframes)
            {
                // キーフレームはフレーム番号に紐づくので、フレーム数が変わったら別の場面に当たる。
                int storedFrames = ResolveStoredNumFrames(key);
                if (storedFrames > 0 && frames > 0 && storedFrames != frames)
                {
                    Debug.LogWarning(
                        $"[Customization] numFrames 不一致 ({storedFrames} != {frames}) のため yaw/scale を破棄します: " +
                        $"track={kv.Key}");
                }
                else
                {
                    if (entry.yawKeyframes != null && entry.yawKeyframes.Count > 0)
                    {
                        manualYawKeyframesByTrack[kv.Key] = new SortedDictionary<int, float>(entry.yawKeyframes);
                        restoredYaw++;
                    }
                    if (entry.scaleKeyframes != null && entry.scaleKeyframes.Count > 0)
                    {
                        manualScaleKeyframesByTrack[kv.Key] = new SortedDictionary<int, float>(entry.scaleKeyframes);
                        restoredScale++;
                    }
                }
            }
        }

        Debug.Log(
            $"[Customization] restored video={key} models={restoredModels} yawTracks={restoredYaw} " +
            $"scaleTracks={restoredScale} " +
            $"session={(ExperimentSessionOverrides.Active ? "experiment(読むだけ)" : "normal(書き込み可)")}");
    }


    // batchManualYawSpec / batchManualScaleSpec をキーフレーム辞書に流し込む。
    //
    // 書式は 2 通り:
    //   "track:値"        … フレーム 0 に 1 個だけ打つ。キーが 1 個なので全フレームその値
    //   "track:frame:値"  … 指定フレームに打つ。複数並べればキーフレーム間の補間を確認できる
    //
    // 例: "1:0:1.0,1:900:3.0" は track 1 を f=0 で等倍、f=900 で 3 倍にし、間を補間する。
    private void ApplyBatchManualOverrideSpecs()
    {
        ApplyBatchSpec(batchManualYawSpec, nameof(batchManualYawSpec), manualYawKeyframesByTrack);
        ApplyBatchSpec(batchManualScaleSpec, nameof(batchManualScaleSpec), manualScaleKeyframesByTrack);
    }


    private static void ApplyBatchSpec(
        string spec, string specName, Dictionary<uint, SortedDictionary<int, float>> target)
    {
        if (string.IsNullOrEmpty(spec))
        {
            return;
        }

        int applied = 0;
        var touched = new HashSet<uint>();
        foreach (string part in spec.Split(','))
        {
            string[] kv = part.Split(':');
            if ((kv.Length != 2 && kv.Length != 3) ||
                !uint.TryParse(kv[0].Trim(), out uint trackId) ||
                !float.TryParse(kv[kv.Length - 1].Trim(), out float value))
            {
                Debug.LogWarning($"[Customization] {specName} を解釈できません: '{part}'");
                continue;
            }

            int frame = 0;
            if (kv.Length == 3 && !int.TryParse(kv[1].Trim(), out frame))
            {
                Debug.LogWarning($"[Customization] {specName} のフレーム番号を解釈できません: '{part}'");
                continue;
            }

            // 同じ track に複数キーを打てるように、その track の初出のときだけ作り直す。
            if (touched.Add(trackId) ||
                !target.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
            {
                keys = new SortedDictionary<int, float>();
                target[trackId] = keys;
            }

            keys[frame] = value;
            applied++;
        }

        Debug.Log($"[Customization] {specName}='{spec}' applied={applied}");
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
        RequestTrackCustomizationSave();
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

        RequestTrackCustomizationSave();
    }


    private void PersistManualScale(uint trackId)
    {
        VideoCustomization target = ResolveWritableCustomization();
        if (target == null)
        {
            return;
        }

        TrackCustomization entry = target.GetOrCreate(trackId);
        if (manualScaleKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) &&
            keys != null && keys.Count > 0)
        {
            entry.scaleKeyframes = new SortedDictionary<int, float>(keys);
        }
        else
        {
            entry.scaleKeyframes = null;
        }

        RequestTrackCustomizationSave();
    }


    // 実験中は基準ファイルを書き換えないので、要求そのものを立てない。
    private void RequestTrackCustomizationSave()
    {
        if (ExperimentSessionOverrides.Active)
        {
            return;
        }

        trackCustomizationSaveRequested = true;
        trackCustomizationSaveDueTime = Time.unscaledTime + TrackCustomizationSaveDelaySeconds;
    }


    // Update から毎フレーム呼ぶ。操作が止まってから 1 回だけ書く。
    private void FlushTrackCustomizationSaveIfDue()
    {
        if (!trackCustomizationSaveRequested || Time.unscaledTime < trackCustomizationSaveDueTime)
        {
            return;
        }

        trackCustomizationSaveRequested = false;
        TrackCustomizationStore.Save();
    }


    // アプリが閉じられる・バックグラウンドに回るときは待たずに書く。
    // VR アプリはヘッドセットを外した時点で止まることがあるので、取りこぼさないようにする。
    private void FlushTrackCustomizationSaveNow()
    {
        if (!trackCustomizationSaveRequested)
        {
            return;
        }

        trackCustomizationSaveRequested = false;
        TrackCustomizationStore.Save();
    }
}
