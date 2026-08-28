using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 動画ごと・track ごとのユーザー設定（モデル選択と手動 yaw）。
//
// 設計は docs/model-selection-persistence.md。要点だけ:
//
//  - キーは **manifest.inputs.video_mp4**。.svb のファイル名ではない（再生成で変わるため）
//  - モデルは **prefab 名**で持つ。index は Resources のソート順と
//    AnimalModelPriorityOrder に依存し、モデルを 1 つ足すとずれる
//  - yaw キーフレームはフレーム番号に紐づくので numFrames を添えて整合を見る
public sealed class TrackCustomization
{
    public string modelPrefabName;
    public SortedDictionary<int, float> yawKeyframes;

    public bool IsEmpty
    {
        get { return string.IsNullOrEmpty(modelPrefabName) && (yawKeyframes == null || yawKeyframes.Count == 0); }
    }

    public TrackCustomization Clone()
    {
        var copy = new TrackCustomization { modelPrefabName = modelPrefabName };
        if (yawKeyframes != null && yawKeyframes.Count > 0)
        {
            copy.yawKeyframes = new SortedDictionary<int, float>(yawKeyframes);
        }

        return copy;
    }
}


public sealed class VideoCustomization
{
    public int numFrames;
    public readonly Dictionary<uint, TrackCustomization> tracks = new Dictionary<uint, TrackCustomization>();

    public TrackCustomization GetOrCreate(uint trackId)
    {
        if (!tracks.TryGetValue(trackId, out TrackCustomization t) || t == null)
        {
            t = new TrackCustomization();
            tracks[trackId] = t;
        }

        return t;
    }

    // other を自分の上に重ねる（other が勝つ）。基準ファイルにセッション上書きを乗せるのに使う。
    public void OverlayWith(VideoCustomization other)
    {
        if (other == null)
        {
            return;
        }

        foreach (KeyValuePair<uint, TrackCustomization> kv in other.tracks)
        {
            if (kv.Value == null || kv.Value.IsEmpty)
            {
                continue;
            }

            TrackCustomization dst = GetOrCreate(kv.Key);
            if (!string.IsNullOrEmpty(kv.Value.modelPrefabName))
            {
                dst.modelPrefabName = kv.Value.modelPrefabName;
            }
            if (kv.Value.yawKeyframes != null && kv.Value.yawKeyframes.Count > 0)
            {
                dst.yawKeyframes = new SortedDictionary<int, float>(kv.Value.yawKeyframes);
            }
        }
    }
}


// persistentDataPath 上の 1 ファイル。研究者が通常利用で仕込んだ「基準」を保持する。
// 被験者実験中は **読むだけで書かない**（書き込み先はセッション上書き側）。
public static class TrackCustomizationStore
{
    public const string FileName = "model_selection.json";

    private static Dictionary<string, VideoCustomization> cache;

    public static string FilePath
    {
        get { return Path.Combine(Application.persistentDataPath, FileName); }
    }

    public static VideoCustomization Get(string videoKey)
    {
        if (string.IsNullOrEmpty(videoKey))
        {
            return null;
        }

        EnsureLoaded();
        return cache.TryGetValue(videoKey, out VideoCustomization v) ? v : null;
    }

    public static VideoCustomization GetOrCreate(string videoKey, int numFrames)
    {
        EnsureLoaded();
        if (!cache.TryGetValue(videoKey, out VideoCustomization v) || v == null)
        {
            v = new VideoCustomization();
            cache[videoKey] = v;
        }

        v.numFrames = numFrames;
        return v;
    }

    public static void Save()
    {
        EnsureLoaded();
        try
        {
            File.WriteAllText(FilePath, Serialize(cache), Encoding.UTF8);
            Debug.Log($"[Customization] saved: {FilePath}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Customization] save failed: {ex.Message}");
        }
    }

    // テスト・手動リセット用。実験の participant 切り替えでは呼ばない
    // （基準ファイルは消さない。docs/model-selection-persistence.md）。
    public static void Reload()
    {
        cache = null;
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (cache != null)
        {
            return;
        }

        cache = new Dictionary<string, VideoCustomization>();
        string path = FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> root)
            {
                foreach (KeyValuePair<string, object> kv in root)
                {
                    if (TryParseVideo(kv.Value, out VideoCustomization v))
                    {
                        cache[kv.Key] = v;
                    }
                }
            }

            Debug.Log($"[Customization] loaded {cache.Count} video(s) from {path}");
        }
        catch (System.Exception ex)
        {
            // 壊れていても再生は続ける。既定で動くだけ。
            cache = new Dictionary<string, VideoCustomization>();
            Debug.LogError($"[Customization] load failed ({ex.Message}). 既定値で続行します: {path}");
        }
    }

    private static bool TryParseVideo(object node, out VideoCustomization video)
    {
        video = null;
        if (!(node is Dictionary<string, object> obj))
        {
            return false;
        }

        video = new VideoCustomization { numFrames = ToInt(obj, "numFrames", 0) };
        if (!(obj.TryGetValue("tracks", out object tracksNode) && tracksNode is Dictionary<string, object> tracks))
        {
            return true;
        }

        foreach (KeyValuePair<string, object> kv in tracks)
        {
            if (!uint.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint trackId) ||
                !(kv.Value is Dictionary<string, object> t))
            {
                continue;
            }

            TrackCustomization entry = video.GetOrCreate(trackId);
            if (t.TryGetValue("model", out object model) && model is string modelName)
            {
                entry.modelPrefabName = modelName;
            }

            if (t.TryGetValue("yaw", out object yawNode) && yawNode is Dictionary<string, object> yaw)
            {
                var keys = new SortedDictionary<int, float>();
                foreach (KeyValuePair<string, object> y in yaw)
                {
                    if (int.TryParse(y.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
                    {
                        keys[frame] = ToFloat(y.Value, 0f);
                    }
                }

                if (keys.Count > 0)
                {
                    entry.yawKeyframes = keys;
                }
            }
        }

        return true;
    }

    private static int ToInt(Dictionary<string, object> obj, string key, int fallback)
    {
        return obj.TryGetValue(key, out object v) ? Mathf.RoundToInt(ToFloat(v, fallback)) : fallback;
    }

    private static float ToFloat(object v, float fallback)
    {
        if (v is double d) return (float)d;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is float f) return f;
        if (v is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return parsed;
        return fallback;
    }

    // MiniJson は Parse しか持たないので書き出しは手書き。構造が固定なので十分。
    private static string Serialize(Dictionary<string, VideoCustomization> data)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        bool firstVideo = true;
        foreach (KeyValuePair<string, VideoCustomization> video in data)
        {
            if (video.Value == null || video.Value.tracks.Count == 0)
            {
                continue;
            }

            if (!firstVideo)
            {
                sb.Append(",\n");
            }
            firstVideo = false;

            sb.Append("  ").Append(Quote(video.Key)).Append(": {\n");
            sb.Append("    \"numFrames\": ").Append(video.Value.numFrames).Append(",\n");
            sb.Append("    \"tracks\": {\n");
            bool firstTrack = true;
            foreach (KeyValuePair<uint, TrackCustomization> track in video.Value.tracks)
            {
                if (track.Value == null || track.Value.IsEmpty)
                {
                    continue;
                }

                if (!firstTrack)
                {
                    sb.Append(",\n");
                }
                firstTrack = false;

                sb.Append("      ").Append(Quote(track.Key.ToString(CultureInfo.InvariantCulture))).Append(": {");
                bool needComma = false;
                if (!string.IsNullOrEmpty(track.Value.modelPrefabName))
                {
                    sb.Append("\"model\": ").Append(Quote(track.Value.modelPrefabName));
                    needComma = true;
                }

                if (track.Value.yawKeyframes != null && track.Value.yawKeyframes.Count > 0)
                {
                    if (needComma)
                    {
                        sb.Append(", ");
                    }
                    sb.Append("\"yaw\": {");
                    bool firstKey = true;
                    foreach (KeyValuePair<int, float> k in track.Value.yawKeyframes)
                    {
                        if (!firstKey)
                        {
                            sb.Append(", ");
                        }
                        firstKey = false;
                        sb.Append(Quote(k.Key.ToString(CultureInfo.InvariantCulture)))
                          .Append(": ")
                          .Append(k.Value.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    sb.Append("}");
                }

                sb.Append("}");
            }

            sb.Append("\n    }\n  }");
        }

        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
