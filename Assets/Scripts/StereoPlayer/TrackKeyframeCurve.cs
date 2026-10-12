using System.Collections.Generic;
using UnityEngine;

// track ごと・フレームごとのユーザー調整値（手動 yaw / 手動スケール）を線形補間する。
//
// 元は EvaluateManualYawOffsetDegForFrame の中にあった処理をそのまま切り出したもの。
// スケールでも同じ補間が要る（「二つの frame でそれぞれ調整したら間が遷移してほしい」）ので、
// 2 本目を書き写すのではなく共有する。**挙動は yaw のときと 1 ビットも変えていない。**
//
// 端点の外側は最初／最後のキーで固定する（extrapolate しない）。調整は「この区間はこの向き・
// この大きさ」という意図なので、区間の外へ延長すると意図しない値になる。
public static class TrackKeyframeCurve
{
    public static float Evaluate(SortedDictionary<int, float> keys, int frame, float fallback)
    {
        if (keys == null || keys.Count == 0)
        {
            return fallback;
        }

        if (keys.Count == 1)
        {
            foreach (KeyValuePair<int, float> kv in keys)
            {
                return kv.Value;
            }
        }

        int firstFrame = int.MaxValue;
        int lastFrame = int.MinValue;
        float firstValue = fallback;
        float lastValue = fallback;
        int prevFrame = int.MinValue;
        int nextFrame = int.MaxValue;
        float prevValue = fallback;
        float nextValue = fallback;

        foreach (KeyValuePair<int, float> kv in keys)
        {
            int keyFrame = kv.Key;
            float keyValue = kv.Value;
            if (keyFrame < firstFrame)
            {
                firstFrame = keyFrame;
                firstValue = keyValue;
            }
            if (keyFrame > lastFrame)
            {
                lastFrame = keyFrame;
                lastValue = keyValue;
            }

            if (keyFrame <= frame && keyFrame > prevFrame)
            {
                prevFrame = keyFrame;
                prevValue = keyValue;
            }
            if (keyFrame >= frame && keyFrame < nextFrame)
            {
                nextFrame = keyFrame;
                nextValue = keyValue;
            }
        }

        if (frame <= firstFrame)
        {
            return firstValue;
        }
        if (frame >= lastFrame)
        {
            return lastValue;
        }
        if (prevFrame == int.MinValue)
        {
            return nextValue;
        }
        if (nextFrame == int.MaxValue)
        {
            return prevValue;
        }
        if (prevFrame == nextFrame)
        {
            return prevValue;
        }

        float t = Mathf.InverseLerp(prevFrame, nextFrame, frame);
        return Mathf.Lerp(prevValue, nextValue, t);
    }

    // 2026-10-12（manualRotationResetAtCut / manualScaleResetAtCut）: [rangeStart, rangeEnd) の中のキーだけを使い、Evaluate と同じ規則で求める。
    // 範囲の外のキーは無いものとして扱い、範囲の中に 1 つも無ければ fallback。本体は Evaluate の一般の経路の複製で、違いは
    // 「範囲の外のキーを飛ばす」と「count == 0 なら fallback」の 2 か所だけ（キーが全部範囲の中なら Evaluate と同じ演算で同じ値）。
    public static float EvaluateWithin(SortedDictionary<int, float> keys, int frame, int rangeStart, int rangeEnd, float fallback)
    {
        if (keys == null || keys.Count == 0)
        {
            return fallback;
        }

        int count = 0;
        int firstFrame = int.MaxValue;
        int lastFrame = int.MinValue;
        float firstValue = fallback;
        float lastValue = fallback;
        int prevFrame = int.MinValue;
        int nextFrame = int.MaxValue;
        float prevValue = fallback;
        float nextValue = fallback;

        foreach (KeyValuePair<int, float> kv in keys)
        {
            int keyFrame = kv.Key;
            if (keyFrame < rangeStart || keyFrame >= rangeEnd)
            {
                continue;
            }

            float keyValue = kv.Value;
            count++;
            if (keyFrame < firstFrame)
            {
                firstFrame = keyFrame;
                firstValue = keyValue;
            }
            if (keyFrame > lastFrame)
            {
                lastFrame = keyFrame;
                lastValue = keyValue;
            }
            if (keyFrame <= frame && keyFrame > prevFrame)
            {
                prevFrame = keyFrame;
                prevValue = keyValue;
            }
            if (keyFrame >= frame && keyFrame < nextFrame)
            {
                nextFrame = keyFrame;
                nextValue = keyValue;
            }
        }

        if (count == 0)
        {
            return fallback;
        }
        if (frame <= firstFrame)
        {
            return firstValue;
        }
        if (frame >= lastFrame)
        {
            return lastValue;
        }
        if (prevFrame == int.MinValue)
        {
            return nextValue;
        }
        if (nextFrame == int.MaxValue)
        {
            return prevValue;
        }
        if (prevFrame == nextFrame)
        {
            return prevValue;
        }

        float t = Mathf.InverseLerp(prevFrame, nextFrame, frame);
        return Mathf.Lerp(prevValue, nextValue, t);
    }
}
