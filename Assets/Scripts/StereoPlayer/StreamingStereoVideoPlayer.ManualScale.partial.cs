using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 手動スケール。自動フィット（bbox 高さ合わせ）に対する**倍率**で、既定は 1.0。
    //
    // なぜ必要か: Else の向きは bundle に推定値が無く、ユーザーが手で回すしかない。
    // 回すと見かけの大きさが変わるので、合わせ直す手段が要る。
    // 自動で幅も合わせる（min(幅フィット, 高さフィット)）案は実測で否定した
    // （docs/bundle-placement.md「自動の『幅フィット』は入れない」）。
    //
    // なぜ倍率か: 自動フィットは depth と bbox から毎フレーム動くので、絶対値で持つと
    // 遠近の変化に追従しなくなる。倍率なら「自動より 1.4 倍大きく」という意図が保たれる。
    //
    // **真値は ReplaceableModel.userScale ではなくこの辞書に置く。** モデルを変えると
    // TrackInstanceLifecycle がインスタンスを作り直し、コンポーネントの値は既定へ戻る。
    // モデル選択で同じ罠を踏んで pendingModelNameByTrack を足したのと同型。

    private const float ManualScaleMin = 0.2f;
    private const float ManualScaleMax = 5f;
    public const float ManualScaleDefault = 1f;

    private float EvaluateManualScaleForFrame(uint trackId, int frame)
    {
        manualScaleKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys);
        float value = TrackKeyframeCurve.Evaluate(keys, frame, ManualScaleDefault);
        return Mathf.Clamp(value, ManualScaleMin, ManualScaleMax);
    }


    private float GetManualScaleForTrack(uint trackId)
    {
        return EvaluateManualScaleForFrame(trackId, GetCurrentPlaybackFrame());
    }


    private void SetManualScaleForTrack(uint trackId, float scale)
    {
        int frame = GetCurrentPlaybackFrame();
        if (!manualScaleKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            keys = new SortedDictionary<int, float>();
            manualScaleKeyframesByTrack[trackId] = keys;
        }

        keys[frame] = Mathf.Clamp(scale, ManualScaleMin, ManualScaleMax);
    }


    // 等倍に戻す。yaw の Reset が 0 を「打つ」のと同じで、キーを消すのではなく 1.0 を打つ。
    // 消してしまうと、前後のキーからの補間でその場所が 1.0 にならないため。
    private void ResetManualScaleForTrack(uint trackId)
    {
        SetManualScaleForTrack(trackId, ManualScaleDefault);
    }


    private int GetManualScaleKeyCountForTrack(uint trackId)
    {
        if (!manualScaleKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return 0;
        }

        return keys.Count;
    }


    private bool HasManualScaleKeyAtCurrentFrame(uint trackId)
    {
        if (!manualScaleKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return false;
        }

        return keys.ContainsKey(GetCurrentPlaybackFrame());
    }
}
