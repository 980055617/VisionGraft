using System.Collections.Generic;
using UnityEngine;

// 映り始めの「一部しか映っていないフレーム」（2026-10-25、12 通りの点検の「端で映り始めた物が跳ぶ・急に出る」。既定 Off）。
//
// meta.bin の box と anchor は、見えている部分だけを表す。映り始めの数フレームでは、それが物の全体と違う:
//   - Else が画面の端から入ってくる（風船は vf 4〜24 で左端、犬の動画のボールは vf 71〜75 で上端）。
//     anchor は見えている部分の重心で、生成側は端に接している間、最初のフレームの値を止めておく（u・v・z が前のフレームと同じ）。
//     Else の大きさは毎フレームの bboxH、つまり見えている部分の高さで決まる。そのためモデルは端に止まった小さな物として出て、膨らみ、
//     端を離れたフレームで anchor が動いて跳ぶ（風船 73 px、ボール 253 px）。
//   - 人・動物が shot の途中で、物陰から一部だけ見えて映り始める（テレビの女性は vf 101・102 が扉の陰の足先だけで、bbox は 30×18・42×31）。
//     倍率は ScaleReference が出そろったフレーム（vf 103）で決めるので、モデルは最初から全身の大きさで出る。
//     姿勢は足先から推定された座ったような SMPL（腰・膝 40〜60°）で、中心 5 tap の平滑がそれを vf 103・104 にも残す。
// 端に接しているかは、meta.bin の box だけで決める（余白は生成側の edge_margin_px と同じ 4 px）。source/* は使わない（runtime の配置に使ってはいけない）。
//
// partialAppearanceMode（bundle を読み込むときに表を作る。再生中に切り替えたら読み込み直しで効く）:
//   Off   … 今のまま。
//   Hide  … これらのフレームでは、その track を「読んだフレームの object」から外す。表示・⑨⑩・端の処理・くじ・SMPL の中心 5 tap が、
//            どれも「出そろったフレームで映り始めた」と見る。モデルは出そろったフレームで、そのフレームの置き方のまま出る。
//   Slide … Else が端から入るときだけ、出そろったフレームの box・anchor・深さで置き、画面の上で box と一緒に動かす。
//            端で切れている軸は画面の内側の辺の動き、切れていない軸は box の中心の動きを使う。こうすると端の外から入ってきて、
//            出そろったフレームの置き方へ切れ目なくつながる。人・動物の一部だけの映り始めは Hide と同じ扱い（端が無いので動かせない）。
// partialAppearanceMaxSeconds のうちに端を離れない Else（端に居続ける物）は、今のまま。
public partial class StreamingStereoVideoPlayer
{
    public enum PartialAppearanceMode
    {
        Off = 0,
        Hide = 1,
        Slide = 2,
    }

    [Header("Partial appearance at a track's start (2026-10-25, default Off)")]
    // 2026-10-27 から既定 Hide（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public PartialAppearanceMode partialAppearanceMode = PartialAppearanceMode.Hide;
    [Min(0.1f)] public float partialAppearanceMaxSeconds = 2f;
    public bool logPartialAppearance = true;

    // 生成側が「端に接している」とみなす余白（source/placement_observations.json の policy.edge_margin_px = 4.0。値だけ写した）。
    private const int PartialAppearanceEdgeMarginPx = 4;

    private struct PartialAppearanceEntry
    {
        public int fullFrame;   // この映り始めで、物が出そろった最初のフレーム
        public bool slide;      // Else の端からの入り（Slide なら動かせる）。false は人・動物の一部だけの映り始め（Slide でも外す）
        public float shiftU;    // このフレームの box の動き。出そろったフレームからの差（eye px）
        public float shiftV;
    }

    // (フレーム, track) → 扱い。null なら表が無い（Off、または読み込み前）。
    private Dictionary<long, PartialAppearanceEntry> partialAppearanceFrames;

    private static long PartialAppearanceKey(int frame, uint trackId)
    {
        return ((long)frame << 32) | trackId;
    }

    // LoadMeta から呼ぶ（CalibrateAnchorDepthRange の後、ほかの先読みの前）。全フレームの box を一度だけ読んで表を作る。
    // TryReadFrameObjectsRaw で読むので、表を作る前の値（外す前・ずらす前）が入る。
    private void PrecomputePartialAppearanceOnLoad()
    {
        partialAppearanceFrames = null;
        int numFrames = (int)metaHeader.numFrames;
        if (partialAppearanceMode == PartialAppearanceMode.Off || numFrames <= 0 ||
            manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var perTrack = new Dictionary<uint, List<KeyValuePair<int, MetaObj>>>();
        var buffer = new List<MetaObj>(16);
        for (int f = 0; f < numFrames; f++)
        {
            if (!TryReadFrameObjectsRaw(f, buffer))
            {
                continue;
            }

            for (int i = 0; i < buffer.Count; i++)
            {
                MetaObj o = buffer[i];
                o.jointsCam = null;
                o.jointsVis = null;
                if (!perTrack.TryGetValue(o.trackId, out List<KeyValuePair<int, MetaObj>> list))
                {
                    list = new List<KeyValuePair<int, MetaObj>>();
                    perTrack[o.trackId] = list;
                }

                list.Add(new KeyValuePair<int, MetaObj>(f, o));
            }
        }

        // 表のために読んだフレームの SMPL/SMAL は再生に使わないので捨てる（CalibrateAnchorDepthRange と同じ）。
        humanSmplPosesMetaBin.Clear();
        animalSmalPosesMetaBin.Clear();

        float fps = Mathf.Max(1f, ResolveMetaFps());
        int maxFrames = Mathf.Max(1, Mathf.RoundToInt(partialAppearanceMaxSeconds * fps));
        int settleFrames = Mathf.Max(1, Mathf.RoundToInt(scaleReferenceSettleSeconds * fps));
        var table = new Dictionary<long, PartialAppearanceEntry>();
        foreach (KeyValuePair<uint, List<KeyValuePair<int, MetaObj>>> kv in perTrack)
        {
            uint trackId = kv.Key;
            List<KeyValuePair<int, MetaObj>> list = kv.Value;
            int lastShot = int.MinValue;
            for (int i = 0; i < list.Count; i++)
            {
                int f = list[i].Key;
                int shot = shotBoundaries.ResolveShotIndex(f);
                bool firstInShot = shot != lastShot;
                lastShot = shot;
                // 映り始め: track の最初、前のフレームに居ない（途切れの後）、または shot の切れ目の後。
                bool appearance = i == 0 || list[i - 1].Key != f - 1 || shotBoundaries.ResolveShotIndex(list[i - 1].Key) != shot;
                if (!appearance)
                {
                    continue;
                }

                MetaObj first = list[i].Value;
                if (IsCategoryOther(first.categoryId))
                {
                    if (!TouchesImageEdgeForPartialAppearance(first))
                    {
                        continue;
                    }

                    // 端を離れる最初のフレーム（同じ映り始めの中、続いているフレームだけ、maxFrames まで）。
                    int j = i + 1;
                    while (j < list.Count && list[j].Key == list[j - 1].Key + 1 && shotBoundaries.ResolveShotIndex(list[j].Key) == shot &&
                           list[j].Key - f <= maxFrames && TouchesImageEdgeForPartialAppearance(list[j].Value))
                    {
                        j++;
                    }

                    bool released = j < list.Count && list[j].Key == list[j - 1].Key + 1 &&
                                    shotBoundaries.ResolveShotIndex(list[j].Key) == shot && list[j].Key - f <= maxFrames;
                    if (!released)
                    {
                        if (logPartialAppearance)
                        {
                            Debug.Log($"[PARTIAL] track={trackId} f={f} else edge entry not released within {maxFrames} frames: unchanged");
                        }

                        continue;
                    }

                    MetaObj full = list[j].Value;
                    for (int k = i; k < j; k++)
                    {
                        ResolvePartialAppearanceShift(list[k].Value, full, out float du, out float dv);
                        table[PartialAppearanceKey(list[k].Key, trackId)] = new PartialAppearanceEntry
                        {
                            fullFrame = list[j].Key,
                            slide = true,
                            shiftU = du,
                            shiftV = dv,
                        };
                    }

                    if (logPartialAppearance)
                    {
                        ResolvePartialAppearanceShift(first, full, out float du0, out float dv0);
                        Debug.Log($"[PARTIAL] track={trackId} else edge entry f={f}..{list[j - 1].Key} -> full f={list[j].Key} " +
                                  $"box0=({first.bboxX},{first.bboxY},{first.bboxW},{first.bboxH}) anchor0=({first.anchorU},{first.anchorV}) " +
                                  $"full=({full.bboxX},{full.bboxY},{full.bboxW},{full.bboxH}) anchor=({full.anchorU},{full.anchorV}) " +
                                  $"shift0=({du0:F0},{dv0:F0}) mode={partialAppearanceMode}");
                    }
                }
                else if ((IsCategoryPerson(first.categoryId) || IsCategoryAnimal(first.categoryId)) &&
                         firstInShot && f != shotBoundaries.GetStartFrame(shot))
                {
                    // ScaleReference（TryResolveSettledAppearanceReference）と同じ判定: 現れてから scaleReferenceSettleSeconds のうちの bboxH の最大の
                    // scaleReferenceSettleFraction 倍に届いた最初のフレームが基準。それより前のフレームを外す。
                    float maxH = 0f;
                    int end = i;
                    while (end < list.Count && (end == i || list[end].Key == list[end - 1].Key + 1) &&
                           shotBoundaries.ResolveShotIndex(list[end].Key) == shot && list[end].Key - f <= settleFrames)
                    {
                        maxH = Mathf.Max(maxH, list[end].Value.bboxH);
                        end++;
                    }

                    float threshold = maxH * scaleReferenceSettleFraction;
                    int r = i;
                    while (r < end && list[r].Value.bboxH < threshold)
                    {
                        r++;
                    }

                    if (r <= i || r >= end)
                    {
                        continue;
                    }

                    for (int k = i; k < r; k++)
                    {
                        table[PartialAppearanceKey(list[k].Key, trackId)] = new PartialAppearanceEntry
                        {
                            fullFrame = list[r].Key,
                            slide = false,
                        };
                    }

                    if (logPartialAppearance)
                    {
                        Debug.Log($"[PARTIAL] track={trackId} partial start f={f}..{list[r - 1].Key} bboxH={first.bboxH} -> full f={list[r].Key} " +
                                  $"bboxH={list[r].Value.bboxH} (max {maxH:F0}, threshold {threshold:F0}) mode={partialAppearanceMode}: hidden");
                    }
                }
            }
        }

        partialAppearanceFrames = table;
        Debug.Log($"[PARTIAL] table frames={table.Count} tracks={perTrack.Count} ({stopwatch.ElapsedMilliseconds}ms) mode={partialAppearanceMode}");
    }

    private bool TouchesImageEdgeForPartialAppearance(MetaObj o)
    {
        int m = PartialAppearanceEdgeMarginPx;
        return o.bboxX <= m || o.bboxY <= m ||
               o.bboxX + o.bboxW >= manifest.eye_w - 1 - m ||
               o.bboxY + o.bboxH >= manifest.eye_h - 1 - m;
    }

    // このフレームの box が、出そろったフレームの box からどれだけ動いたか（eye px）。
    // 端で切れている軸は、画面の内側に残っている辺（左で切れていれば右の辺）の動き。切れていない軸（と両側が切れている軸）は中心の動き。
    private void ResolvePartialAppearanceShift(MetaObj now, MetaObj full, out float shiftU, out float shiftV)
    {
        int m = PartialAppearanceEdgeMarginPx;
        bool clipL = now.bboxX <= m;
        bool clipR = now.bboxX + now.bboxW >= manifest.eye_w - 1 - m;
        bool clipT = now.bboxY <= m;
        bool clipB = now.bboxY + now.bboxH >= manifest.eye_h - 1 - m;
        if (clipL && !clipR)
        {
            shiftU = (now.bboxX + now.bboxW) - (full.bboxX + full.bboxW);
        }
        else if (clipR && !clipL)
        {
            shiftU = now.bboxX - full.bboxX;
        }
        else
        {
            shiftU = (now.bboxX + now.bboxW * 0.5f) - (full.bboxX + full.bboxW * 0.5f);
        }

        if (clipT && !clipB)
        {
            shiftV = (now.bboxY + now.bboxH) - (full.bboxY + full.bboxH);
        }
        else if (clipB && !clipT)
        {
            shiftV = now.bboxY - full.bboxY;
        }
        else
        {
            shiftV = (now.bboxY + now.bboxH * 0.5f) - (full.bboxY + full.bboxH * 0.5f);
        }
    }

    // TryReadFrameObjects から（表があるときだけ）。Hide とひとの一部だけの映り始めは外し、Slide の Else は出そろったフレームの値へ置き換える。
    // 生の読み（TryReadFrameObjectsRaw・メモ）は変えないので、全フレームを生で読む推定（SMPL の AimAt の rest・C4-T・S）は今のまま。
    private void ApplyPartialAppearanceToFrameObjects(int frameIndex, List<MetaObj> objs)
    {
        for (int i = objs.Count - 1; i >= 0; i--)
        {
            MetaObj obj = objs[i];
            if (!partialAppearanceFrames.TryGetValue(PartialAppearanceKey(frameIndex, obj.trackId), out PartialAppearanceEntry e))
            {
                continue;
            }

            if (partialAppearanceMode == PartialAppearanceMode.Slide && e.slide &&
                TryReadFrameObjectsMemo(e.fullFrame, out List<MetaObj> fullObjs) &&
                TryFindTrackObject(fullObjs, obj.trackId, out MetaObj full))
            {
                // track・種別はこのフレームのまま。box・anchor・深さは出そろったフレームのものにし、画面の上のずれは別に持つ
                // （端の外へ出る値になるので ushort の anchor には入れない。ApplyMetaTarget が clamp の後に足す）。
                MetaObj bridged = obj;
                bridged.bboxX = full.bboxX;
                bridged.bboxY = full.bboxY;
                bridged.bboxW = full.bboxW;
                bridged.bboxH = full.bboxH;
                bridged.anchorU = full.anchorU;
                bridged.anchorV = full.anchorV;
                bridged.anchorZ01 = full.anchorZ01;
                bridged.anchorZ = DecodeAnchorDepthMetersFromBundle(full.anchorZ01);
                bridged.hasScreenShift = true;
                bridged.screenShiftU = e.shiftU;
                bridged.screenShiftV = e.shiftV;
                objs[i] = bridged;
                continue;
            }

            objs.RemoveAt(i);
        }
    }

    // その track がこのフレームでは外されているか（SMPL の中心 5 tap が、外したフレームの姿勢を混ぜないため）。
    private bool IsPartialAppearanceFrameRemoved(int frame, uint trackId)
    {
        return partialAppearanceFrames != null &&
               partialAppearanceFrames.TryGetValue(PartialAppearanceKey(frame, trackId), out PartialAppearanceEntry e) &&
               !(partialAppearanceMode == PartialAppearanceMode.Slide && e.slide);
    }
}
