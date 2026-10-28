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
    // 2026-10-27 から既定 ON（見る用の版 l と同じ。犬・猫の大きさはまだ詰めている途中）。上の既定の説明はそれまでの話。
    public bool placementFollowVideoDepthHoldWhileFeetStill = true;
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
    // 2026-10-27 から既定 0.08（見る用の版 l と同じ。犬・猫の大きさはまだ詰めている途中）。上の既定の説明はそれまでの話。
    [Min(0f)] public float placementFollowVideoDepthSizeMaxRatePerSecond = 0.08f;
    // [VFOLLOW-STILL] を出す（読み込み時の表の要約と、その場に入る・出る tick）。
    public bool logPlacementFollowVideoDepthStill = false;
    // その場の判定に体の大きさも使う（2026-10-28、新しい振る舞い・既定 OFF）。ユーザー（10/28、o の動画 sizefit/dog_o_ba.mp4 を見て）「犬の25秒の青い線って
    // 結局上がってから下がってるじゃん，でも元動画はその場所にいるよね」「猫も20秒くらいではめっちゃ小さくなっているけど，その場から動いていないよね」
    // 「ちゃんと元動画で動いているかどうか，その時のモデルがどうなったかも考えて直してほしい」。
    // 起きていたこと（Docs/tmp/survey_20261025/README.md「10/28: o の動画を見た感想」）: 足元の行（箱の下端）だけで決めると、犬が前を低くして前足を前へ出す
    //   （下端だけ 645 → 687 px、24.2 秒）・横向きから振り向いて座る（633 → 654 px）・寝た猫の下端が 5 px 揺れる（箱の高さ 50 px の 8% = 4 px を超える）・
    //   伸びをして前足を座面の縁から下へ伸ばす所が「動いた」になり、足元から決める大きさが動く（犬 +28%、猫のソファの上 +14%）。その間、動物の頭の大きさ
    //   （両耳の間）も AniMer の当てはめの大きさ（5000 ÷ SMAL の transl z）も変わらない（犬 21〜27.75 秒 283〜297）。
    // しくみ: 読み込み時に、動物の track ごとに AniMer の大きさ log(1 / transl z)（meta.bin の SMAL block。画面の縁で切れたフレームは使わない）を前後
    //   placementFollowVideoDepthStillBodyScaleMedianFrames フレームの中央値でならし、前後 placementFollowVideoDepthStillWindowSeconds の幅（最大 − 最小）が
    //   placementFollowVideoDepthStillBodyScaleTolerance 以下のフレームを「体の大きさが動かない」とする（続きが placementFollowVideoDepthStillBodyScaleMinRunSeconds
    //   より短いものは捨てる。歩いている途中に当てはめが 0.3 秒止まる所がある）。足元の行が動かないフレームと合わせ、その場の区間どうしの切れ目が
    //   placementFollowVideoDepthStillBridgeSeconds 以下で、両端の足元の行（端の 6 フレームの中央値）の差が箱の高さの placementFollowVideoDepthStillFraction 以内なら
    //   切れ目もその場にする（寝た猫が頭を上げる・伸びをして前足を下へ伸ばす所）。足元の行は VideoDepthFollowFeet.partial.cs がその場の間止める。
    //   試算（scratchpad validate/combined/sizefit/sim_inplace2.py、meta.bin だけ）: 犬 0.00〜3.33・6.71〜7.04・8.33〜10.58・11.67〜13.67・14.79〜27.29 秒、
    //   猫 0.00〜10.83・18.25〜20.25・22.04〜30.00 秒（目で見たその場とそろう。足元の行だけでは犬の 14.79〜16.25・23.75〜24.75 秒、猫のソファの上の 3 か所が抜ける）。
    // 直らないもの: その場で体ごと少し動く所（AniMer の大きさが ±3% を超え、足元の行も動く）。人の track（SMAL block が無い）。
    // 2026-10-28 から既定 ON（ユーザー、sizefit/dog_np2_ba.mp4・cat_np2_ba.mp4 を見て「どちらも新しい版の方がいいと思う．そのまま実装して」。
    // 足元から奥行き・大きさを決める 2 つ（VideoDepthFollowFeet.partial.cs）・animalSupportBottomIgnoresLimbReach と一緒）。上の既定の説明はそれまでの話。
    public bool placementFollowVideoDepthStillUsesBodyScale = true;
    [Range(0.005f, 0.5f)] public float placementFollowVideoDepthStillBodyScaleTolerance = 0.06f;
    [Min(0)] public int placementFollowVideoDepthStillBodyScaleMedianFrames = 3;
    [Min(0f)] public float placementFollowVideoDepthStillBodyScaleMinRunSeconds = 0.5f;
    [Min(0f)] public float placementFollowVideoDepthStillBridgeSeconds = 2f;

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
        videoDepthFollowFeetHeldBoxHByTrack.Remove(trackId);   // 足元の位置から作る大きさのその場の H（VideoDepthFollowFeet.partial.cs）
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
        // 体の大きさ（placementFollowVideoDepthStillUsesBodyScale）: (フレーム, log(1 / transl z))。動物だけ、どこかの縁で切れたフレーム・transl が無いフレームは入れない。
        bool bodyScaleOn = placementFollowVideoDepthStillUsesBodyScale;
        var bodyByTrack = bodyScaleOn ? new Dictionary<uint, List<Vector2>>() : null;
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
                if (bodyScaleOn && !clipped && IsCategoryAnimal(o.categoryId) && TryGetAnimalSmalPose(f, o.trackId, out AnimalSmalPose smal) && smal.hasTransl)
                {
                    float bx = o.bboxX;
                    if (ResolveAnchorToScreen(o.anchorU, out _, out _, out bool isRightEye) && isRightEye)
                    {
                        bx -= manifest.eye_w;
                    }

                    bool anyCut = o.bboxY <= 0 || bx <= BodyFrameOutEdgeMarginPx || bx + o.bboxW >= manifest.eye_w - 1 - BodyFrameOutEdgeMarginPx;
                    float tz = Mathf.Abs(smal.transl.z);
                    if (!anyCut && tz > 1e-4f)
                    {
                        if (!bodyByTrack.TryGetValue(o.trackId, out List<Vector2> bodyList))
                        {
                            bodyList = new List<Vector2>();
                            bodyByTrack[o.trackId] = bodyList;
                        }

                        bodyList.Add(new Vector2(f, -Mathf.Log(tz)));
                    }
                }

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
        int bodyMedianHalf = Mathf.Max(0, placementFollowVideoDepthStillBodyScaleMedianFrames);
        int bodyMinRun = Mathf.Max(1, Mathf.RoundToInt(placementFollowVideoDepthStillBodyScaleMinRunSeconds * fps));
        int bridgeFrames = Mathf.Max(0, Mathf.RoundToInt(placementFollowVideoDepthStillBridgeSeconds * fps));
        foreach (KeyValuePair<uint, List<Vector3>> kv in bottomByTrack)
        {
            List<int> stillFrames = ClassifyVideoDepthFollowStillFrames(kv.Value, shotBoundaries.ResolveShotIndex, half, placementFollowVideoDepthStillFraction);
            string bodyText = "";
            if (bodyScaleOn)
            {
                int rowStill = stillFrames.Count;
                var merged = new HashSet<int>(stillFrames);
                int bodyStill = 0;
                if (bodyByTrack.TryGetValue(kv.Key, out List<Vector2> bodySamples))
                {
                    List<int> body = ClassifyVideoDepthFollowBodyScaleStillFrames(bodySamples, shotBoundaries.ResolveShotIndex, half, bodyMedianHalf,
                        placementFollowVideoDepthStillBodyScaleTolerance, bodyMinRun);
                    bodyStill = body.Count;
                    for (int i = 0; i < body.Count; i++)
                    {
                        merged.Add(body[i]);
                    }
                }

                int bridged = BridgeVideoDepthFollowStillGaps(merged, kv.Value, shotBoundaries.ResolveShotIndex, bridgeFrames, 6, placementFollowVideoDepthStillFraction);
                stillFrames = new List<int>(merged);
                stillFrames.Sort();
                bodyText = $" rowStill={rowStill} bodyStill={bodyStill} bodySamples={(bodySamples != null ? bodySamples.Count : 0)} bridged={bridged}";
            }

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
                Debug.Log($"[VFOLLOW-STILL] table track={kv.Key} frames={kv.Value.Count} still={stillFrames.Count} runs={DescribeVideoDepthFollowStillRuns(table, kv.Key, numFrames)}{bodyText}");
            }
        }

        videoDepthFollowStillFrames = table;
        videoDepthFollowReleaseAge = releaseAge;
        videoDepthFollowReleaseFrames = releaseFrames;
        if (logPlacementFollowVideoDepthStill)
        {
            Debug.Log($"[VFOLLOW-STILL] precompute tracks={bottomByTrack.Count} stillFrames={table.Count} window=±{half}f fraction={placementFollowVideoDepthStillFraction:F3} " +
                      $"holdsDepth={(placementFollowVideoDepthStillHoldsDepth ? 1 : 0)} release={releaseFrames}f tau={placementFollowVideoDepthStillReleaseTauSeconds:F2} " +
                      $"releaseFrames={releaseAge.Count} bodyScale={(bodyScaleOn ? 1 : 0)} bodyTol={placementFollowVideoDepthStillBodyScaleTolerance:F3} " +
                      $"bodyMed=±{bodyMedianHalf} bodyMinRun={bodyMinRun}f bridge={bridgeFrames}f ms={stopwatch.ElapsedMilliseconds}");
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

    // 体の大きさが動かないフレーム（placementFollowVideoDepthStillUsesBodyScale、純粋な計算、テスト VideoDepthFollowTests）。samples = (フレーム, log(1 / transl z))
    // をフレームの昇順で（縁で切れた・transl が無いフレームは入れない）。値を同じ shot の前後 medianHalf フレームの samples の中央値でならし、同じ shot の
    // 前後 half フレームのならした値の幅（最大 − 最小）が tolerance 以下ならその場。窓の samples が max(3, half) より少ないフレームは決めない。
    // 最後に、続き（隣のフレームとの間が 1 フレーム以内）が minRun フレームより短いものを捨てる。
    internal static List<int> ClassifyVideoDepthFollowBodyScaleStillFrames(List<Vector2> samples, System.Func<int, int> shotOf, int half, int medianHalf,
        float tolerance, int minRun)
    {
        var still = new List<int>();
        if (samples == null || samples.Count == 0)
        {
            return still;
        }

        int n = samples.Count;
        half = Mathf.Max(1, half);
        medianHalf = Mathf.Max(0, medianHalf);
        var shot = new int[n];
        for (int i = 0; i < n; i++)
        {
            shot[i] = shotOf != null ? shotOf((int)samples[i].x) : 0;
        }

        var smooth = new float[n];
        var window = new List<float>(2 * medianHalf + 1);
        for (int i = 0; i < n; i++)
        {
            window.Clear();
            for (int j = i; j >= 0 && samples[i].x - samples[j].x <= medianHalf; j--)
            {
                if (shot[j] == shot[i])
                {
                    window.Add(samples[j].y);
                }
            }

            for (int j = i + 1; j < n && samples[j].x - samples[i].x <= medianHalf; j++)
            {
                if (shot[j] == shot[i])
                {
                    window.Add(samples[j].y);
                }
            }

            window.Sort();
            smooth[i] = window[window.Count / 2];
        }

        int minCount = Mathf.Max(3, half);
        var raw = new List<int>();
        int lo = 0;
        int hi = 0;
        for (int i = 0; i < n; i++)
        {
            float f = samples[i].x;
            while (lo < n && samples[lo].x < f - half)
            {
                lo++;
            }

            while (hi < n && samples[hi].x <= f + half)
            {
                hi++;
            }

            float min = float.MaxValue;
            float max = float.MinValue;
            int count = 0;
            for (int j = lo; j < hi; j++)
            {
                if (shot[j] != shot[i])
                {
                    continue;
                }

                min = Mathf.Min(min, smooth[j]);
                max = Mathf.Max(max, smooth[j]);
                count++;
            }

            if (count >= minCount && max - min <= tolerance)
            {
                raw.Add((int)f);
            }
        }

        int runStart = 0;
        for (int i = 1; i <= raw.Count; i++)
        {
            if (i < raw.Count && raw[i] - raw[i - 1] <= 1)
            {
                continue;
            }

            if (i - runStart >= Mathf.Max(1, minRun))
            {
                for (int k = runStart; k < i; k++)
                {
                    still.Add(raw[k]);
                }
            }

            runStart = i;
        }

        return still;
    }

    // その場の区間どうしの短い切れ目をつなぐ（placementFollowVideoDepthStillUsesBodyScale、純粋な計算、テスト VideoDepthFollowTests）。still = この track のその場の
    // フレーム（足していく）、rows = (フレーム, 足元の行 = 箱の下端 px, 箱の高さ px) をフレームの昇順で（下で切れたフレームは入れない）。同じ shot の隣り合う区間の
    // 切れ目が maxGap フレーム以下で、切れ目の rows が切れ目のフレーム数の 2/3 以上あり、前の区間の最後の endFrames フレーム・後の区間の最初の endFrames フレームの
    // rows の足元の行の中央値の差が、その rows の箱の高さの中央値 × fraction 以内なら、切れ目のフレームを足す。足したフレームの数を返す。
    internal static int BridgeVideoDepthFollowStillGaps(HashSet<int> still, List<Vector3> rows, System.Func<int, int> shotOf, int maxGap, int endFrames, float fraction)
    {
        if (still == null || still.Count == 0 || rows == null || maxGap <= 0)
        {
            return 0;
        }

        var byFrame = new Dictionary<int, Vector3>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            byFrame[(int)rows[i].x] = rows[i];
        }

        var sorted = new List<int>(still);
        sorted.Sort();
        var runs = new List<Vector2Int>();
        int start = sorted[0];
        for (int i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] - sorted[i - 1] <= 1)
            {
                continue;
            }

            runs.Add(new Vector2Int(start, sorted[i - 1]));
            if (i < sorted.Count)
            {
                start = sorted[i];
            }
        }

        int added = 0;
        endFrames = Mathf.Max(1, endFrames);
        var bottoms = new List<float>(2 * endFrames);
        var heights = new List<float>(2 * endFrames);
        var ends = new List<float>(endFrames);
        for (int r = 0; r + 1 < runs.Count; r++)
        {
            int last = runs[r].y;
            int next = runs[r + 1].x;
            int gap = next - last - 1;
            if (gap <= 0 || gap > maxGap || (shotOf != null && shotOf(last) != shotOf(next)))
            {
                continue;
            }

            int present = 0;
            for (int f = last + 1; f < next; f++)
            {
                if (byFrame.ContainsKey(f))
                {
                    present++;
                }
            }

            if (present * 3 < gap * 2)
            {
                continue;
            }

            heights.Clear();
            float before = MedianOfEndRows(byFrame, last - endFrames + 1, last, ends, heights);
            float after = MedianOfEndRows(byFrame, next, next + endFrames - 1, ends, heights);
            if (float.IsNaN(before) || float.IsNaN(after) || heights.Count == 0)
            {
                continue;
            }

            heights.Sort();
            if (Mathf.Abs(before - after) > fraction * heights[heights.Count / 2])
            {
                continue;
            }

            for (int f = last + 1; f < next; f++)
            {
                if (still.Add(f))
                {
                    added++;
                }
            }
        }

        return added;
    }

    // BridgeVideoDepthFollowStillGaps の端の足元の行の中央値（rows に無いフレームは飛ばす。無ければ NaN）。箱の高さは heights へ足す。
    private static float MedianOfEndRows(Dictionary<int, Vector3> byFrame, int from, int to, List<float> scratch, List<float> heights)
    {
        scratch.Clear();
        for (int f = from; f <= to; f++)
        {
            if (byFrame.TryGetValue(f, out Vector3 s))
            {
                scratch.Add(s.y);
                heights.Add(s.z);
            }
        }

        if (scratch.Count == 0)
        {
            return float.NaN;
        }

        scratch.Sort();
        return scratch[scratch.Count / 2];
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
