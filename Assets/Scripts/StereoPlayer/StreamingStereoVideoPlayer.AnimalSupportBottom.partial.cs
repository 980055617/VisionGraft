using System.Collections.Generic;
using UnityEngine;

// 動物の下端合わせで、伸ばした手足の分の下端を使わない（2026-10-27 夜、新しい振る舞い・既定 OFF: animalSupportBottomIgnoresLimbReach）。
// ユーザー（10/27 夜、見る用の版 n を実機で見て）「猫のソファーの時一瞬したに行っちゃうのはどうすれば治る？」。
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/27 夜: n を見た感想」、scratchpad validate/combined/sizefit/）: 猫がソファの上にいたまま
//   前足を座面の前の縁から下へ伸ばす（5.6〜6.5 秒）と、箱の下端だけが 35 px 下がり、上端は 0〜1 px しか動かない。⑦ はモデルの一番下の骨を箱の下端へ
//   置くので、前足が猫ほど伸びないモデルは体ごと下がる（[MESH2D] のモデルの上端が m で +28 px、n で +42 px）。足元から奥行き・大きさを決める形
//   （VideoDepthFollowFeet.partial.cs）は同じ下端を「近づいた」と読み、大きさも +19% になる。
// しくみ: 読み込み時に、動物の track ごと・shot ごとに、どこも画面の縁で切れていないフレームの前後 animalSupportBottomWindowFrames フレームの
//   上端・下端・高さの中央値を取る。下端が中央値より下がった分から上端の下がった分を引いた「上端で説明できない下端の出っ張り」が、高さの中央値の
//   animalSupportBottomMinReach（から animalSupportBottomFullReach で全部）を超え、上端の揺れが高さの中央値の animalSupportBottomTopTolerance 以内
//   （その 2 倍で 0）なら、その出っ張りを手足と見て下端から引く。最後に前後 animalSupportBottomSmoothFrames フレームで平均する（効き始めに段を作らない）。
//   ⑦ の下端（ResolveBBoxBottomVEye。⑦ を写した ⑧ の解く式・2 回目の ⑦ の予測も同じ関数を読む）と、足元の行（奥行き・大きさの表）がこの値を使う。
//   試算（sizefit/support_bottom2.py、meta.bin だけ）: 効くのは猫の 5.58〜6.62 秒（最大 34.9 px）と 0.71〜0.96 秒（最大 2 px）だけ。猫が歩いて前足を
//   一歩出す 16.6〜17.4 秒（出っ張り 箱の高さの 8.5%）、犬が立って振り向く 4.0〜4.4 秒（7%）、犬が前を低くする所（上端が動く）、猫がソファから
//   降りる所（下端がそのまま新しい位置に移る）、人の動画では効かない。
// 直らないもの: 本当に体ごと一瞬下がって戻る動き（区別できない）。上端も一緒に動くとき（頭を下げながら手を伸ばす）は効かない。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 主スイッチ（既定 OFF、新しい振る舞い）。動物の track だけ。OFF なら表を作らず、下端は今とビット単位で同じ。
    // 2026-10-28 から既定 ON（ユーザー、sizefit/dog_np2_ba.mp4・cat_np2_ba.mp4 を見て「どちらも新しい版の方がいいと思う．そのまま実装して」。
    // 足元から奥行き・大きさを決める 2 つ（VideoDepthFollowFeet.partial.cs）・placementFollowVideoDepthStillUsesBodyScale と一緒）。上の既定の説明はそれまでの話。
    public bool animalSupportBottomIgnoresLimbReach = true;
    // 中央値の窓（前後のフレーム数）。手足を伸ばしている長さより十分長く取る（中央値が伸ばした下端に引かれない）。
    [Min(2)] public int animalSupportBottomWindowFrames = 24;
    // 出っ張り ÷ 高さの中央値: これ以下は引かない、FullReach で全部引く（間はまっすぐ）。歩いて前足を出す（8.5%）・振り向く（7%）を拾わない値。
    [Range(0f, 1f)] public float animalSupportBottomMinReach = 0.10f;
    [Range(0f, 1f)] public float animalSupportBottomFullReach = 0.20f;
    // 上端の揺れ ÷ 高さの中央値: これ以内なら全部、その 2 倍で 0（上端も動くなら体ごと動いている）。
    [Range(0f, 1f)] public float animalSupportBottomTopTolerance = 0.03f;
    // 引く量をならす窓（前後のフレーム数）。
    [Min(0)] public int animalSupportBottomSmoothFrames = 3;
    // [SUPPORT-BOTTOM] を出す（読み込み時の track ごとの要約と、引いたフレームの続き）。
    public bool logAnimalSupportBottom = true;

    // (フレーム, track) → (meta.bin の下端, 引く量 px)。引く量が 0 のフレームは入れない。null なら表が無い（OFF、または読み込み前）。
    private Dictionary<long, Vector2> animalSupportBottomByKey;

    // この tick の物体の下端から引く量（表にあり、今の箱の下端が表の下端と同じときだけ。箱が実行時に書き換えられていたら引かない）。
    private bool TryGetAnimalSupportBottomCorrection(MetaObj obj, out float correction)
    {
        correction = 0f;
        if (!animalSupportBottomIgnoresLimbReach || animalSupportBottomByKey == null || !IsCategoryAnimal(obj.categoryId) ||
            !animalSupportBottomByKey.TryGetValue(VideoDepthFollowStillKey(GetCurrentPlaybackFrame(), obj.trackId), out Vector2 entry) ||
            Mathf.Abs(obj.bboxY + obj.bboxH - entry.x) > 0.5f)
        {
            return false;
        }

        correction = entry.y;
        return correction > 0f;
    }

    // 1 track・1 shot 分の引く量（純粋な計算、テスト AnimalSupportBottomTests）。samples はフレームの昇順。どこも切れていないフレームだけを見る
    // （切れたフレームは 0）。窓の中の切れていないフレームが window × 2/3 より少なければ 0。
    internal static float[] ResolveAnimalSupportBottomCorrections(List<VideoDepthFollowFeetSample> samples, int window, float minReach, float fullReach,
        float topTolerance, int smooth)
    {
        int n = samples.Count;
        var raw = new float[n];
        var tops = new List<float>(2 * window + 1);
        var bottoms = new List<float>(2 * window + 1);
        var heights = new List<float>(2 * window + 1);
        for (int i = 0; i < n; i++)
        {
            if (samples[i].anyCut || samples[i].height <= 0f)
            {
                continue;
            }

            tops.Clear();
            bottoms.Clear();
            heights.Clear();
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= window; j--)
            {
                AddSupportWindowSample(samples[j], tops, bottoms, heights);
            }

            for (int j = i + 1; j < n && samples[j].frame - samples[i].frame <= window; j++)
            {
                AddSupportWindowSample(samples[j], tops, bottoms, heights);
            }

            if (heights.Count < Mathf.Max(3, (2 * window) / 3))
            {
                continue;
            }

            float medTop = MedianOfList(tops);
            float medBottom = MedianOfList(bottoms);
            float medHeight = Mathf.Max(1f, MedianOfList(heights));
            float top = samples[i].bottom - samples[i].height;
            float dBottom = samples[i].bottom - medBottom;
            float dTop = top - medTop;
            float excess = dBottom - Mathf.Max(0f, dTop);
            if (!(excess > 0f))
            {
                continue;
            }

            float tolerance = Mathf.Max(1e-6f, topTolerance * medHeight);
            float gate = Mathf.Clamp01((2f * tolerance - Mathf.Abs(dTop)) / tolerance);
            float reach = Mathf.Clamp01((excess / medHeight - minReach) / Mathf.Max(1e-6f, fullReach - minReach));
            raw[i] = gate * reach * excess;
        }

        if (smooth <= 0)
        {
            return raw;
        }

        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            int count = 0;
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= smooth; j--)
            {
                sum += raw[j];
                count++;
            }

            for (int j = i + 1; j < n && samples[j].frame - samples[i].frame <= smooth; j++)
            {
                sum += raw[j];
                count++;
            }

            // 切れたフレームは引かない（⑦ は切れた下端を別の扱いにする）。
            result[i] = samples[i].anyCut ? 0f : (float)(sum / count);
        }

        return result;
    }

    private static void AddSupportWindowSample(VideoDepthFollowFeetSample s, List<float> tops, List<float> bottoms, List<float> heights)
    {
        if (s.anyCut || s.height <= 0f)
        {
            return;
        }

        tops.Add(s.bottom - s.height);
        bottoms.Add(s.bottom);
        heights.Add(s.height);
    }

    private static float MedianOfList(List<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var sorted = new List<float>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    // 読み込み時（PrecomputeVideoDepthFollowFeetOnLoad の中、足元の行を作る前）: track の samples を shot ごとに見て表を作り、足元の行に使う
    // samples の下端と高さからも引く（伸ばした手足を「近づいた」と読まない）。引いたフレームの数を返す。
    private int ApplyAnimalSupportBottomToSamples(uint trackId, List<VideoDepthFollowFeetSample> all, Dictionary<long, Vector2> table)
    {
        int corrected = 0;
        float maxCorrection = 0f;
        var runs = new System.Text.StringBuilder();
        int runStart = -1;
        int runEnd = -1;
        int start = 0;
        while (start < all.Count)
        {
            int end = start;
            while (end < all.Count && all[end].shot == all[start].shot)
            {
                end++;
            }

            List<VideoDepthFollowFeetSample> shotSamples = all.GetRange(start, end - start);
            float[] corrections = ResolveAnimalSupportBottomCorrections(shotSamples, animalSupportBottomWindowFrames, animalSupportBottomMinReach,
                animalSupportBottomFullReach, animalSupportBottomTopTolerance, animalSupportBottomSmoothFrames);
            for (int i = 0; i < shotSamples.Count; i++)
            {
                float c = corrections[i];
                if (!(c > 0.01f))
                {
                    continue;
                }

                VideoDepthFollowFeetSample s = shotSamples[i];
                table[VideoDepthFollowStillKey(s.frame, trackId)] = new Vector2(s.bottom, c);
                s.bottom -= c;
                s.height = Mathf.Max(1f, s.height - c);
                all[start + i] = s;
                corrected++;
                maxCorrection = Mathf.Max(maxCorrection, c);
                if (runEnd >= 0 && s.frame - runEnd <= 2)
                {
                    runEnd = s.frame;
                }
                else
                {
                    if (runStart >= 0)
                    {
                        runs.Append(runs.Length > 0 ? "," : "").Append(runStart).Append('-').Append(runEnd);
                    }

                    runStart = s.frame;
                    runEnd = s.frame;
                }
            }

            start = end;
        }

        if (runStart >= 0)
        {
            runs.Append(runs.Length > 0 ? "," : "").Append(runStart).Append('-').Append(runEnd);
        }

        if (logAnimalSupportBottom)
        {
            Debug.Log($"[SUPPORT-BOTTOM] track={trackId} frames={all.Count} corrected={corrected} maxPx={maxCorrection:F1} runs={(runs.Length > 0 ? runs.ToString() : "-")} " +
                      $"window=±{animalSupportBottomWindowFrames} reach={animalSupportBottomMinReach:F2}-{animalSupportBottomFullReach:F2} " +
                      $"topTol={animalSupportBottomTopTolerance:F2} smooth=±{animalSupportBottomSmoothFrames}");
        }

        return corrected;
    }
}
