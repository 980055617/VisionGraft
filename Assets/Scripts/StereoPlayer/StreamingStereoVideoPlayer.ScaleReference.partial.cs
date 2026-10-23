using System.Collections.Generic;
using UnityEngine;

// 途中から現れた track の倍率を決めるフレーム（2026-10-23、既定 ON。ユーザー「tv を先に直す」。bundle 6 本を同じ規則で走査し、
// 基準が動くのは bundle_tv の 1 か所だけ（cat・soccer と旧来の human・animal・car は person / animal がみな shot の頭から映っている。
// scratchpad/bundles/settle_scan.py。Docs/tmp/bundles_20261023/README.md）。
// 倍率は shot の中で 1 つ（ユーザーの決まり）。shot の頭に居る track は shot の頭のフレームで決める（今のまま）。shot の途中から現れた
// track は、今は現れた最初のフレームで決めて固定し、続く測り直し（RefineLockedScaleFromProjectedBones）もそのフレームの bbox に合わせる。
// bundle_tv（2026-10-23 の新しい実験の動画）では女性が扉の陰から足先だけ見えた 101 フレーム（bbox 30×18）で検出され、倍率 0.0139
// （モデルの高さ 2 cm）に固定されて 30 秒間ほとんど見えなかった（103 フレームからは全身で高さ約 300 px）。
// ON のとき: 現れてから scaleReferenceSettleSeconds 秒（同じ shot の中）の bbox の高さを meta.bin で先読みし、その間の最大の
// scaleReferenceSettleFraction 倍に届いた最初のフレームを倍率の基準にする（倍率はその 1 フレームの bbox と anchorZ から決める）。
// 測り直しもそのフレームに来るまで待つ。現れた最初のフレームがもう届いているなら今と同じ。
public partial class StreamingStereoVideoPlayer
{
    [Header("Scale Reference For Mid-Shot Appearance (2026-10-23)")]
    public bool scaleReferenceSkipsPartialAppearance = true;
    [Min(0.05f)] public float scaleReferenceSettleSeconds = 0.5f;
    [Range(0.1f, 1f)] public float scaleReferenceSettleFraction = 0.6f;

    private readonly List<MetaObj> scaleReferenceLookahead = new List<MetaObj>(16);

    // shot の頭に居ない track をロックするときに呼ぶ。基準をずらすときだけ true（frame は現れた最初のフレームより後）。
    private bool TryResolveSettledAppearanceReference(MetaObj obj, out MetaObj reference, out int referenceFrame)
    {
        reference = default;
        referenceFrame = -1;
        if (!scaleReferenceSkipsPartialAppearance || metaHeader.numFrames == 0)
        {
            return false;
        }

        int current = GetCurrentPlaybackFrame();
        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
        int last = Mathf.Min((int)metaHeader.numFrames - 1, current + Mathf.Max(1, Mathf.RoundToInt(fps * scaleReferenceSettleSeconds)));
        int shot = shotBoundaries.ResolveShotIndex(current);

        var heights = new List<KeyValuePair<int, MetaObj>>();
        float maxH = 0f;
        for (int f = current; f <= last; f++)
        {
            if (shotBoundaries.ResolveShotIndex(f) != shot || !TryReadFrameObjects(f, scaleReferenceLookahead))
            {
                break;
            }

            bool found = false;
            for (int i = 0; i < scaleReferenceLookahead.Count; i++)
            {
                MetaObj candidate = scaleReferenceLookahead[i];
                if (candidate.trackId != obj.trackId || candidate.bboxH <= 0)
                {
                    continue;
                }

                heights.Add(new KeyValuePair<int, MetaObj>(f, candidate));
                maxH = Mathf.Max(maxH, candidate.bboxH);
                found = true;
                break;
            }

            if (!found)
            {
                break;
            }
        }

        if (heights.Count == 0 || maxH <= 0f)
        {
            return false;
        }

        float threshold = maxH * scaleReferenceSettleFraction;
        for (int i = 0; i < heights.Count; i++)
        {
            if (heights[i].Value.bboxH < threshold)
            {
                continue;
            }

            if (heights[i].Key == current)
            {
                return false;
            }

            reference = heights[i].Value;
            referenceFrame = heights[i].Key;
            Debug.Log($"[SCALEREF] track={obj.trackId} appeared f={current} bboxH={obj.bboxH} -> reference f={referenceFrame} bboxH={reference.bboxH} " +
                      $"(max {maxH:F0} in {heights.Count} frames, threshold {threshold:F0})");
            return true;
        }

        return false;
    }

    // 測り直しは基準のフレームに来てから（それより前の bbox は見切れていて、合わせると倍率が小さく焼き付く）。
    private bool IsBeforeScaleReferenceFrame(uint trackId)
    {
        return scaleReferenceSkipsPartialAppearance &&
               scaleLockReferenceFrameByTrack.TryGetValue(trackId, out int referenceFrame) &&
               GetCurrentPlaybackFrame() < referenceFrame;
    }
}
