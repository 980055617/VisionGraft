using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class HumanSourcePose2D
{
    public readonly string keypointFormat;
    public readonly Rect sourceBox;
    public readonly Vector2[] keypoints;

    public HumanSourcePose2D(
        string keypointFormat,
        Rect sourceBox,
        Vector2[] keypoints)
    {
        this.keypointFormat = keypointFormat;
        this.sourceBox = sourceBox;
        this.keypoints = keypoints;
    }
}

public static class HumanSourcePoseSidecar
{
    public const string Hmr2OpenPose25Extra19 = "hmr2_openpose25_extra19";

    public static bool TryLoad(
        string path,
        out Dictionary<int, Dictionary<uint, HumanSourcePose2D>> posesByFrame,
        out string error)
    {
        posesByFrame =
            new Dictionary<int, Dictionary<uint, HumanSourcePose2D>>();
        error = null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var textReader = new StreamReader(stream))
            {
                posesByFrame = Load(textReader);
            }
            return posesByFrame.Count > 0;
        }
        catch (Exception ex)
        {
            posesByFrame =
                new Dictionary<int, Dictionary<uint, HumanSourcePose2D>>();
            error = ex.Message;
            return false;
        }
    }

    public static Dictionary<int, Dictionary<uint, HumanSourcePose2D>> Load(
        TextReader textReader)
    {
        var result =
            new Dictionary<int, Dictionary<uint, HumanSourcePose2D>>();
        if (textReader == null)
        {
            return result;
        }

        string keypointFormat = null;
        using (var reader = new JsonTextReader(textReader))
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                string propertyName =
                    Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
                if (propertyName == "meta")
                {
                    if (reader.Read() && reader.TokenType == JsonToken.StartObject)
                    {
                        JObject meta = JObject.Load(reader);
                        keypointFormat =
                            meta["keypoints"]?["format"]?.Value<string>();
                    }
                }
                else if (propertyName == "frames")
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                    {
                        continue;
                    }

                    while (reader.Read() &&
                           reader.TokenType != JsonToken.EndArray)
                    {
                        if (reader.TokenType != JsonToken.StartObject)
                        {
                            reader.Skip();
                            continue;
                        }

                        JObject frame = JObject.Load(reader);
                        AddFrame(result, frame, keypointFormat);
                    }
                }
                else if (reader.Read())
                {
                    reader.Skip();
                }
            }
        }

        return result;
    }

    private static void AddFrame(
        Dictionary<int, Dictionary<uint, HumanSourcePose2D>> result,
        JObject frame,
        string keypointFormat)
    {
        int frameIndex = frame["frame_index"]?.Value<int>() ?? -1;
        JArray objects = frame["objects"] as JArray;
        if (frameIndex < 0 || objects == null)
        {
            return;
        }

        for (int i = 0; i < objects.Count; i++)
        {
            JObject obj = objects[i] as JObject;
            uint? trackId = obj?["trackId"]?.Value<uint?>();
            JArray box = obj?["box_xywh"] as JArray;
            JArray rawKeypoints = obj?["predKeypoints2d"] as JArray;
            if (!trackId.HasValue ||
                box == null ||
                box.Count < 4 ||
                rawKeypoints == null ||
                rawKeypoints.Count == 0)
            {
                continue;
            }

            float x = box[0].Value<float>();
            float y = box[1].Value<float>();
            float width = box[2].Value<float>();
            float height = box[3].Value<float>();
            float cropSize = Mathf.Max(width, height);
            if (width <= 0f || height <= 0f || cropSize <= 0f)
            {
                continue;
            }

            Vector2 center = new Vector2(
                x + width * 0.5f,
                y + height * 0.5f);
            var keypoints = new Vector2[rawKeypoints.Count];
            bool valid = true;
            for (int keypointIndex = 0;
                 keypointIndex < rawKeypoints.Count;
                 keypointIndex++)
            {
                JArray rawPoint = rawKeypoints[keypointIndex] as JArray;
                if (rawPoint == null || rawPoint.Count < 2)
                {
                    valid = false;
                    break;
                }

                keypoints[keypointIndex] = center + new Vector2(
                    rawPoint[0].Value<float>() * cropSize,
                    rawPoint[1].Value<float>() * cropSize);
            }
            if (!valid)
            {
                continue;
            }

            if (!result.TryGetValue(
                    frameIndex,
                    out Dictionary<uint, HumanSourcePose2D> posesByTrack))
            {
                posesByTrack = new Dictionary<uint, HumanSourcePose2D>();
                result[frameIndex] = posesByTrack;
            }

            posesByTrack[trackId.Value] = new HumanSourcePose2D(
                keypointFormat,
                new Rect(x, y, width, height),
                keypoints);
        }
    }
}
