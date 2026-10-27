using System.Collections.Generic;
using UnityEngine;

// V の「その場では大きさを変えない」（2026-10-27、新しい振る舞い・既定 OFF: placementFollowVideoDepthHoldWhileFeetStill）。
// ユーザー（10/27、プレビュー j を実機で見て）「猫急に後半ででかくなった気がする，27秒くらい？」「犬も同じ，やっぱり動画だとその場で動いているだけで，
// 急に大きくなることがあるから，それを対処してほしい」。
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/27: j 版を Quest で見た感想」、scratchpad validate/combined/sizejump/）: V は奥行きを動画の奥行き Zv に置き、
//   片目の絵の大きさは ⑧ が箱の高さに合わせる。なので 3D の中のモデルの大きさ ∝ Zv × 箱の高さ ÷ モデルの投影の高さ。動物がその場で頭を上げる・座る・前を低くすると、
//   箱は縦に伸びるのにモデルは同じ姿勢をとらない（座れない・頭は視聴者を見る）ので、3D の中で大きくなる（猫 23.0 → 25.5 秒 +63%、犬 26.0 → 27.5 秒 +33%、
//   犬 21.0 → 22.5 秒 +18%、猫 2.5 → 3.5 秒 +28%）。その間、箱の下端（足元の行）は 20 px 以内しか動かない。動画の奥行きもその場で揺れる（猫 +19%、犬は跳び +46%）。
// しくみ: bundle を読み込むときに、track ごと・フレームごとに「足元の行（箱の下端）が前後 placementFollowVideoDepthStillWindowSeconds の間で、
//   箱の高さ（窓の中央値）の placementFollowVideoDepthStillFraction より動かない」フレームを表にする（meta.bin の箱だけ。先のフレームも見るので、
//   歩き出しで遅れない）。横へ歩くだけなら足元の行は変わらないので、その場と同じに扱う（距離が変わらない）。
//   箱の下が画面の下で切れているフレーム（⑦・⑧ と同じヒステリシスの判定を、この表のための状態で）は足元が読めないので、その場にしない（端は端の扱いが持つ）。
//   その場のフレームは、端の Hold（placementFollowVideoDepthAtEdge == Hold）と同じに扱う: f を動かさず（3D の大きさをそのまま）、⑧ は箱の比の代わりに
//   合わせる点を合わせる先の奥行きに置く比を使う（TryResolveVideoDepthFollowEdgeHoldRatio）。placementFollowVideoDepthStillHoldsDepth なら、合わせる先も
//   その場に入った tick の奥行きで止める（その場で Zv が揺れても前後に動かない）。
// 既定は動物の track だけ（placementFollowVideoDepthStillAnimalsOnly、下のフィールドの説明）。足元が動く（歩いて近づく・離れる）間は今の V のまま。
// 直らないもの: 本当に近づくのに Zv が近づかない場面（犬 8〜9.5 秒）は、足元が動くので今のまま大きくなる。その場の間に姿勢で箱が変わった分は、
//   歩き出したときに ⑧ の平滑（τ）で追いつく。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 主スイッチ（既定 OFF、新しい振る舞い）。V（placementFollowVideoDepth）が ON のときだけ効く。OFF なら表を作らず、配置は今とビット単位で同じ。
    public bool placementFollowVideoDepthHoldWhileFeetStill = false;
    // 動物の track だけに効かせる（既定 true）。2026-10-27 の撮影（sizejump/runs/*_still2）: 人に効かせると、止まっては歩くテレビの女性（足元が 1 秒に
    // 箱の高さの 3〜4% しか動かない）の歩きの大半が「その場」になり、遠くにいた始めの大きさのまま近づいて、最後は元の女性の 2/3 ほどに小さく写った
    // （サッカーの男性も今の 0.8 倍）。人のモデルはデータの姿勢（腕を上げる・歩く）をそのまま取るので箱に合わせても姿勢で大きさがずれにくく、ずれるのは
    // 座れない・頭が視聴者を向く動物（猫が座る・犬が頭を上げる・前を低くする）。false なら人にも効く。
    public bool placementFollowVideoDepthStillAnimalsOnly = true;
    // その場の判定の窓（前後の秒）と、足元の行の動きの許し（箱の高さに対する割合）。10/27 の実測: その場で座る・頭を上げる・前を低くする所の下端の動きは
    // 箱の高さの 5% 以内（猫 20 px / 400 px、犬 0〜1 px）、歩いて近づく所は 1 秒に 10〜20%（犬 8〜9 秒 33 px / 200 px、猫 12〜17 秒 約 35 px / 250 px）。
    [Min(0.04f)] public float placementFollowVideoDepthStillWindowSeconds = 0.5f;
    [Range(0.01f, 0.5f)] public float placementFollowVideoDepthStillFraction = 0.08f;
    // その場の間は合わせる先の奥行きも止める（その場に入った tick の合わせる先の奥行き）。OFF なら合わせる先は Zv のまま（3D の大きさだけ止める）。
    public bool placementFollowVideoDepthStillHoldsDepth = true;
    // その場が終わった（足元が動き出した）後の追いつき（2026-10-27 の撮影 sizejump/runs: 止めていた間に箱が変わった分を、動き出しで 0.3 秒の平滑のまま
    // 追うと、テレビの女性の 21 秒で 1 秒に +24%、猫がソファから降りる 5.5 秒で +52% と跳んだ）。その場の最後のフレームからこの秒数の間は、⑧ の比と
    // f の平滑の時定数を placementFollowVideoDepthStillReleaseTauSeconds から普段の値へまっすぐ戻す（速追従も切る）。0 なら追いつきは普段のまま。
    [Min(0f)] public float placementFollowVideoDepthStillReleaseSeconds = 2.5f;
    [Min(0f)] public float placementFollowVideoDepthStillReleaseTauSeconds = 1.2f;
    // 大きさをゆっくり変える形（2026-10-27 午前、k2 を実機で見たユーザー「猫の20秒当たりでも急に変わった気がする」「後半は小さい気がする」「結局犬も18秒くらいで
    // 急に変わってない？」）。0 より大きいと、この track（動物だけなら動物）の 3D の大きさを止めずに、箱に合わせた大きさ（合わせる先の奥行きで測る）へ
    // 1 秒にこの値まで（対数で、0.08 ≒ 8%）しか変えない。⑧ はいつも箱の比の代わりに合わせる点を合わせる先（その場では止めた奥行き、ほかは Zv）へ置く比を使い、
    // 片目の絵は大きさが追いつくまで箱とずれてよい。その場でも大きさは止めず（上限の速さで追う）、その場の後の追いつき（上の 2 つ）は使わない。
    // 端に掛かった間は今までどおり f を動かさない（端の Hold）。0 なら k2 の形（その場では大きさを止め、歩き出しはゆっくり追いつく）。
    // 試算（sizejump/sim_slew.py、今の版の大きさに上限を掛けた）: 8%/s で猫の 25.2 秒 3.4 倍・29 秒 4.6 倍（今 5.1、k2 2.6）、1 秒の間の変化は 8% 以下。
    [Min(0f)] public float placementFollowVideoDepthSizeMaxRatePerSecond = 0f;
    // [VFOLLOW-STILL] を出す（読み込み時の表の要約と、その場に入る・出る tick）。
    public bool logPlacementFollowVideoDepthStill = false;

    // (フレーム, track) のうち「その場」のもの。null なら表が無い（OFF、または読み込み前）。
    private HashSet<long> videoDepthFollowStillFrames;
    // (フレーム, track) → その場の最後のフレームから何フレーム後か（1〜追いつきのフレーム数）。その場の後の追いつきの間だけ入る。
    private Dictionary<long, int> videoDepthFollowReleaseAge;
    private int videoDepthFollowReleaseFrames;
    // その場に入った tick の合わせる先の奥行き（track ごと。その場が続く間だけ）。
    private readonly Dictionary<uint, float> videoDepthFollowStillHeldDepthByTrack = new Dictionary<uint, float>();
    // 大きさをゆっくり変える形: ⑧ が箱の比で置いていたらの基準点の奥行き − 実際に置いた奥行き（m、この tick。箱の比が使えない tick は NaN）。
    private readonly Dictionary<uint, float> videoDepthFollowBoxDepthShiftByTrack = new Dictionary<uint, float>();
    private readonly Dictionary<uint, int> videoDepthFollowBoxDepthShiftTickByTrack = new Dictionary<uint, int>();

    private static long VideoDepthFollowStillKey(int frame, uint trackId)
    {
        return ((long)frame << 32) | trackId;
    }

    // 大きさをゆっくり変える形の track か（主スイッチ・上限 > 0・表がある・動物だけなら動物）。
    private bool IsVideoDepthFollowSlowSizeTrack(MetaObj obj)
    {
        return placementFollowVideoDepth && placementFollowVideoDepthHoldWhileFeetStill && placementFollowVideoDepthSizeMaxRatePerSecond > 0f &&
               videoDepthFollowStillFrames != null && (!placementFollowVideoDepthStillAnimalsOnly || IsCategoryAnimal(obj.categoryId));
    }

    // ⑧ が合わせる先へ置く比を使った tick に、箱の比で置いていたらの奥行きとの差を控える（V の測りが f の目標 = 箱に合わせた大きさを出すのに使う）。
    private void NoteVideoDepthFollowBoxDepthShift(uint trackId, float shift)
    {
        videoDepthFollowBoxDepthShiftByTrack[trackId] = shift;
        videoDepthFollowBoxDepthShiftTickByTrack[trackId] = Time.frameCount;
    }

    private bool TryGetVideoDepthFollowBoxDepthShiftThisTick(uint trackId, out float shift)
    {
        shift = 0f;
        return videoDepthFollowBoxDepthShiftTickByTrack.TryGetValue(trackId, out int tick) && tick == Time.frameCount &&
               videoDepthFollowBoxDepthShiftByTrack.TryGetValue(trackId, out shift);
    }

    // 大きさの上限（純粋な計算、テスト VideoDepthFollowTests）: next を current から対数で ratePerSecond × advanced ÷ fps までに丸める。
    // 上限が 0 以下・値が正でない・進んでいないなら next のまま。
    internal static float LimitVideoDepthFollowFactorRate(float current, float next, float ratePerSecond, int advanced, float fps)
    {
        if (!(ratePerSecond > 0f) || !(current > 0f) || !(next > 0f) || advanced <= 0)
        {
            return next;
        }

        float step = ratePerSecond * advanced / Mathf.Max(0.01f, fps);
        return current * Mathf.Exp(Mathf.Clamp(Mathf.Log(next / current), -step, step));
    }

    // この tick の track がその場か（表が無ければ false）。
    private bool IsVideoDepthFollowStillFrame(MetaObj obj)
    {
        return placementFollowVideoDepth && placementFollowVideoDepthHoldWhileFeetStill && videoDepthFollowStillFrames != null &&
               videoDepthFollowStillFrames.Contains(VideoDepthFollowStillKey(GetCurrentPlaybackFrame(), obj.trackId));
    }

    // 合わせる先の奥行き（その場で止めるなら、その場に入った tick の値）。その場でなければ止めた値を捨てる。
    private float ResolveVideoDepthFollowStillTargetDepth(MetaObj obj, bool still, float targetDepth)
    {
        if (!still)
        {
            if (videoDepthFollowStillHeldDepthByTrack.Remove(obj.trackId) && logPlacementFollowVideoDepthStill)
            {
                Debug.Log($"[VFOLLOW-STILL] track={obj.trackId} f={GetCurrentPlaybackFrame()} leave targetDepth={targetDepth:F4}");
            }

            return targetDepth;
        }

        if (!placementFollowVideoDepthStillHoldsDepth)
        {
            return targetDepth;
        }

        if (videoDepthFollowStillHeldDepthByTrack.TryGetValue(obj.trackId, out float held))
        {
            return held;
        }

        videoDepthFollowStillHeldDepthByTrack[obj.trackId] = targetDepth;
        if (logPlacementFollowVideoDepthStill)
        {
            Debug.Log($"[VFOLLOW-STILL] track={obj.trackId} f={GetCurrentPlaybackFrame()} enter heldDepth={targetDepth:F4}");
        }

        return targetDepth;
    }

    // その場の後の追いつきの時定数（純粋な計算、テスト VideoDepthFollowTests）: age = その場の最後のフレームから何フレーム後か（1〜releaseFrames）。
    // 1 で releaseTau、releaseFrames でほぼ baseTau（まっすぐ）。範囲の外・releaseTau が baseTau 以下なら baseTau。
    internal static float ResolveVideoDepthFollowReleaseTau(int age, int releaseFrames, float baseTau, float releaseTau)
    {
        if (age < 1 || releaseFrames < 1 || age > releaseFrames || !(releaseTau > baseTau))
        {
            return baseTau;
        }

        float w = 1f - (age - 1) / (float)releaseFrames;
        return baseTau + (releaseTau - baseTau) * w;
    }

    // この tick の track がその場の後の追いつきの間なら、平滑の時定数を返す（⑧ と f が使う）。
    private bool TryResolveVideoDepthFollowReleaseTau(MetaObj obj, float baseTau, out float tau)
    {
        tau = baseTau;
        // 大きさをゆっくり変える形では使わない（上限の速さと重ねると二重に遅くなる）。
        if (!placementFollowVideoDepth || !placementFollowVideoDepthHoldWhileFeetStill || videoDepthFollowReleaseAge == null || IsVideoDepthFollowSlowSizeTrack(obj) ||
            !videoDepthFollowReleaseAge.TryGetValue(VideoDepthFollowStillKey(GetCurrentPlaybackFrame(), obj.trackId), out int age))
        {
            return false;
        }

        tau = ResolveVideoDepthFollowReleaseTau(age, videoDepthFollowReleaseFrames, baseTau, placementFollowVideoDepthStillReleaseTauSeconds);
        return tau > baseTau;
    }

    // 記録用: この tick の追いつきの何フレーム目か（0 = 追いつきの間ではない）。
    private int ResolveVideoDepthFollowReleaseAgeForLog(MetaObj obj)
    {
        return videoDepthFollowReleaseAge != null &&
               videoDepthFollowReleaseAge.TryGetValue(VideoDepthFollowStillKey(GetCurrentPlaybackFrame(), obj.trackId), out int age) ? age : 0;
    }

    // V の状態を捨てるとき（ロックの作り直し・巻き戻り・本物のカット・主スイッチを切った）に一緒に捨てる。
    private void ForgetVideoDepthFollowStillHeldDepth(uint trackId)
    {
        videoDepthFollowStillHeldDepthByTrack.Remove(trackId);
    }

    // LoadMeta から呼ぶ（PrecomputePartialAppearanceOnLoad の後）。全フレームの箱を一度だけ読んで表を作る。
    private void PrecomputeVideoDepthFollowStillOnLoad()
    {
        videoDepthFollowStillFrames = null;
        videoDepthFollowReleaseAge = null;
        videoDepthFollowStillHeldDepthByTrack.Clear();
        int numFrames = (int)metaHeader.numFrames;
        if (!placementFollowVideoDepth || !placementFollowVideoDepthHoldWhileFeetStill || numFrames <= 0 ||
            manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bottomByTrack = new Dictionary<uint, List<Vector3>>();   // (フレーム, 下端, 高さ)。下で切れたフレームは入れない
        var clipState = new Dictionary<uint, bool>();
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
                if (!IsStereoDepthSkeletonTrack(o.categoryId) || o.bboxH == 0 ||
                    (placementFollowVideoDepthStillAnimalsOnly && !IsCategoryAnimal(o.categoryId)))
                {
                    continue;
                }

                float bottom = o.bboxY + o.bboxH;
                clipState.TryGetValue(o.trackId, out bool wasClipped);
                bool clipped = ResolveBBoxBottomClipState(wasClipped, bottom);
                clipState[o.trackId] = clipped;
                if (clipped)
                {
                    continue;
                }

                if (!bottomByTrack.TryGetValue(o.trackId, out List<Vector3> list))
                {
                    list = new List<Vector3>();
                    bottomByTrack[o.trackId] = list;
                }

                list.Add(new Vector3(f, bottom, o.bboxH));
            }
        }

        // 表のために読んだフレームの SMPL/SMAL は再生に使わないので捨てる（PrecomputePartialAppearanceOnLoad と同じ）。
        humanSmplPosesMetaBin.Clear();
        animalSmalPosesMetaBin.Clear();

        float fps = Mathf.Max(1f, ResolveMetaFps());
        int half = Mathf.Max(1, Mathf.RoundToInt(placementFollowVideoDepthStillWindowSeconds * fps));
        int releaseFrames = Mathf.Max(0, Mathf.RoundToInt(placementFollowVideoDepthStillReleaseSeconds * fps));
        var table = new HashSet<long>();
        var releaseAge = new Dictionary<long, int>();
        foreach (KeyValuePair<uint, List<Vector3>> kv in bottomByTrack)
        {
            List<int> stillFrames = ClassifyVideoDepthFollowStillFrames(kv.Value, shotBoundaries.ResolveShotIndex, half, placementFollowVideoDepthStillFraction);
            for (int i = 0; i < stillFrames.Count; i++)
            {
                table.Add(VideoDepthFollowStillKey(stillFrames[i], kv.Key));
            }

            // その場の後の追いつき: その場の最後のフレームの後の releaseFrames フレーム（同じ shot、その場でないフレーム）。
            int lastStill = -1;
            int lastShot = -1;
            for (int f = 0; f < numFrames && releaseFrames > 0; f++)
            {
                int shot = shotBoundaries.ResolveShotIndex(f);
                if (shot != lastShot)
                {
                    lastStill = -1;
                    lastShot = shot;
                }

                long key = VideoDepthFollowStillKey(f, kv.Key);
                if (table.Contains(key))
                {
                    lastStill = f;
                }
                else if (lastStill >= 0 && f - lastStill <= releaseFrames)
                {
                    releaseAge[key] = f - lastStill;
                }
            }

            if (logPlacementFollowVideoDepthStill)
            {
                Debug.Log($"[VFOLLOW-STILL] table track={kv.Key} frames={kv.Value.Count} still={stillFrames.Count} runs={DescribeVideoDepthFollowStillRuns(table, kv.Key, numFrames)}");
            }
        }

        videoDepthFollowStillFrames = table;
        videoDepthFollowReleaseAge = releaseAge;
        videoDepthFollowReleaseFrames = releaseFrames;
        if (logPlacementFollowVideoDepthStill)
        {
            Debug.Log($"[VFOLLOW-STILL] precompute tracks={bottomByTrack.Count} stillFrames={table.Count} window=±{half}f fraction={placementFollowVideoDepthStillFraction:F3} " +
                      $"holdsDepth={(placementFollowVideoDepthStillHoldsDepth ? 1 : 0)} release={releaseFrames}f tau={placementFollowVideoDepthStillReleaseTauSeconds:F2} " +
                      $"releaseFrames={releaseAge.Count} ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    // その場のフレーム（純粋な計算、テスト VideoDepthFollowTests）。samples = (フレーム, 足元の行 = 箱の下端 px, 箱の高さ px) をフレームの昇順で
    // （下で切れたフレームは入れない）。フレームごとに、同じ shot の前後 half フレームの samples の足元の行の幅（最大 − 最小）が、その窓の箱の高さの
    // 中央値 × fraction 以下ならその場。窓の samples が max(3, half) より少ないフレーム（欠け・切れが多い）は決めない（その場にしない）。
    internal static List<int> ClassifyVideoDepthFollowStillFrames(List<Vector3> samples, System.Func<int, int> shotOf, int half, float fraction)
    {
        var still = new List<int>();
        if (samples == null || samples.Count == 0)
        {
            return still;
        }

        half = Mathf.Max(1, half);
        int minCount = Mathf.Max(3, half);
        var heights = new List<float>(2 * half + 1);
        int lo = 0;
        int hi = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            int f = (int)samples[i].x;
            int shot = shotOf != null ? shotOf(f) : 0;
            while (lo < samples.Count && samples[lo].x < f - half)
            {
                lo++;
            }

            while (hi < samples.Count && samples[hi].x <= f + half)
            {
                hi++;
            }

            float minB = float.MaxValue;
            float maxB = float.MinValue;
            heights.Clear();
            for (int j = lo; j < hi; j++)
            {
                if (shotOf != null && shotOf((int)samples[j].x) != shot)
                {
                    continue;
                }

                minB = Mathf.Min(minB, samples[j].y);
                maxB = Mathf.Max(maxB, samples[j].y);
                heights.Add(samples[j].z);
            }

            if (heights.Count < minCount)
            {
                continue;
            }

            heights.Sort();
            if (maxB - minB <= fraction * heights[heights.Count / 2])
            {
                still.Add(f);
            }
        }

        return still;
    }

    // 記録用: その場のフレームの続き（"a-b,c-d"）。
    private static string DescribeVideoDepthFollowStillRuns(HashSet<long> table, uint trackId, int numFrames)
    {
        var sb = new System.Text.StringBuilder();
        int start = -1;
        for (int f = 0; f <= numFrames; f++)
        {
            bool still = f < numFrames && table.Contains(VideoDepthFollowStillKey(f, trackId));
            if (still && start < 0)
            {
                start = f;
            }
            else if (!still && start >= 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(start).Append('-').Append(f - 1);
                start = -1;
            }
        }

        return sb.Length > 0 ? sb.ToString() : "-";
    }
}
