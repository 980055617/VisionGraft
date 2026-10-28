using System.Collections.Generic;
using UnityEngine;

// V の合わせる先の奥行きを足元の位置から決める（2026-10-27、新しい振る舞い・既定 OFF: placementFollowVideoDepthFromFeet）。
// ユーザー（10/27、l の大きさと奥行きの図を見て）「動画の奥行きについて，犬の方絶対にそんなに最後急に大きくなることはない，何故なら，動画見るとわかるけど
// ほぼその場ですよね．猫もおなじです」。
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/27 夕: 動画の奥行きがその場の動物で跳ぶ」、scratchpad validate/combined/depthjump/）:
//   動物の目印の深さ（meta.bin の anchor z01、0 近い〜1 遠い）は、生成側で深度マップを胴体の keypoints2d 7 点の 5×5 窓で取った中央値（D-016・D-017）。
//   その場で前を低くする犬（足元の行 687 px のまま）で 26.0 → 27.25 秒に 0.30 → 0.81、その場で頭を上げて座る猫（足元 644 → 624 px）で 0.03 → 0.37 と動く。
//   7 点の窓はどれも動物のマスクの上（source/keypoints3d.json で確かめた）なので、深度マップそのものがその場の動物を前後に動いたように描いている。
//   V（VideoDepthFollow.partial.cs）はこれを合わせる先の奥行きにするので、今の版 l の犬は 27.1 秒（その場の判定が切れた所）から 0.85 → 1.33 m へ下がった。
// しくみ: 床が平らでカメラが動かなければ、地面の点の視差は画面の行に比例する（近さ = 1 − z01 は視差に線形）。読み込み時に、動物の track ごと・shot ごとに、
//   近さを箱の下端の行（足元）に 1 本の直線で当てる（頑健な当てはめ。画面の縁で切れたフレームは使わない）。再生中は合わせる先の奥行き
//   （TryResolvePlacementFollowTargetDepth）だけを、前後 placementFollowVideoDepthFeetSmoothFrames フレームの足元の行の中央値を直線に入れた z01 から作る。
//   下が切れたフレーム（足元が読めない）は、同じ shot のその前の切れていないフレームの値（shot の頭から切れていれば最初の切れていないフレームの値）。
//   足元の行がほとんど動かない shot（placementFollowVideoDepthFeetMinRowSpanPx より狭い）は直線が決まらないので、その shot の z01 の中央値の一定値にする。
//   フレームが足りない・直線に乗らない（外れを除いた決定係数が placementFollowVideoDepthFeetMinR2 未満）shot は表に入れず、今の z01 のまま。
//   試算（depthjump/sim_feet.py、撮影の記録の z01 → Zv をそのまま使う）: 犬の 25〜28 秒は 0.85 m のまま、猫の座る間は 0.86〜0.88 m（l は 0.78〜0.80 m に止めていた）。
//   ほかの所は今の z01 とほぼ重なる（直線の決定係数 猫 0.93、犬 0.67）。
// 変えないもの: 目印の深さそのもの（anchorZ01・anchorZ。最初の置き方・前後関係の丸め・ロック・⑨・frame out の判定・S が読む）、人・物の track、
//   3D の大きさの決め方（猫が座って箱が縦に伸びる分は、大きさを 1 秒 8% で追うまま残る）。
//
// 3D の大きさも足元の位置から決める（2026-10-27 夜、新しい振る舞い・既定 OFF: placementFollowVideoDepthSizeFromFeet）。
// ユーザー（10/27 夜、上の奥行きの直しを入れた見る用の版 m を実機で見て）「見たけどやっぱり猫も犬も動画内ではその場のところも大きくなったり，
// 小さくなったりしている，本当にちゃんと見ている？合わせてほしいけどできないの？」。
// 起きていたこと（Docs/tmp/survey_20261025/README.md「10/27 夜: m を見た感想」、scratchpad validate/combined/sizefit/）: 大きさの目標は箱の高さに
//   合わせた大きさ（3D の大きさ ∝ 合わせる先の奥行き × 箱の高さ ÷ モデルの投影の高さ）で、1 秒 8% まで追う。その場でも止めないので、止まる前に動いた
//   目標へ何秒も追い続け（犬 25〜27 秒 2.43 → 2.16 倍）、座る・立つ・頭を上げ下げで目標そのものが動く（猫 頭を下げて歩く 3.4 → 座る 4.75。
//   モデルは座れない: 推定データが後脚を畳まない）。止める形（大きさの上限 0）にすると、その場の中は 0% だが、歩き出しに 1 秒 +35% と跳ぶ。
//   姿勢に左右されない手がかりは足元の行（床の位置 = 距離）だけ（AniMer の当てはめの大きさも座ると +31%、箱の幅も座ると +47%）。
// しくみ: 読み込み時に、足元の行（上の中央値）をもう一度 placementFollowVideoDepthFeetSizeMeanFrames フレームで平均して段をなくし（奥行きも同じ行）、
//   shot ごとに log(箱の高さ) を足元の行に頑健な直線で当てる（H(行) = その床の位置での箱の高さ。どこも切れていないフレーム）。
//   再生中（VideoDepthFollow.partial.cs の大きさをゆっくり変える形の目標を作る所）: c = 箱に合わせた目標 ÷（合わせる先の奥行き × 箱の高さ）は
//   モデルの姿勢だけで決まる（動画の姿勢に依らない）。定数 c̄ を最初の測りで「その瞬間の大きさが箱に合わせた大きさと同じになる値」から始め、
//   動いていて・どこも切れていない・箱の高さが H の placementFollowVideoDepthFeetSizeMinTypical 以上（寝る・かがむ・頭を下げるフレームを除く）
//   フレームの c の中央値へ 1 秒 placementFollowVideoDepthFeetSizeConstRatePerSecond まで寄せる（その場では止める）。目標 = c̄ × 合わせる先の奥行き × H(行)。
//   その場では行も入った瞬間の値で止める（奥行きを止めるのと同じ）。足元が動く速さで大きさが変わるので、上限の速さは
//   placementFollowVideoDepthFeetSizeMaxRatePerSecond（安全のため）にする。
//   試算（sizefit/sim_rowsize4.py、m の記録の数字）: その場の区間の中の変化 犬 0〜6.3%・猫 0〜5.4%（m は犬 +14〜22%・猫 +11〜78%）。
//   モデルの背 ÷ 箱の高さ: 座る猫 0.92（m は 2.7 → 4.7 倍へ大きくなり続ける）、前を低くする犬 1.31（モデルは立つ）。
// 直らないもの: 床でない面の上（猫がソファで立つ所は、足元の行から読む距離が実際より遠くモデルの背が箱の 0.72 倍）。足元が下で切れた間は行が止まる。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 主スイッチ（既定 OFF、新しい振る舞い）。V（placementFollowVideoDepth）が ON のときだけ効く。動物の track だけ。OFF なら表を作らず、今とビット単位で同じ。
    // 2026-10-28 から既定 ON（ユーザー、sizefit/dog_np2_ba.mp4・cat_np2_ba.mp4 を見て「どちらも新しい版の方がいいと思う．そのまま実装して」。下の大きさ・
    // animalSupportBottomIgnoresLimbReach・placementFollowVideoDepthStillUsesBodyScale と 4 つ一緒。止まった所で形を合わせる形は OFF のまま）。上の既定の説明はそれまでの話。
    public bool placementFollowVideoDepthFromFeet = true;
    // 足元の行をならす窓（前後のフレーム数）。0 ならならさない。
    [Min(0)] public int placementFollowVideoDepthFeetSmoothFrames = 6;
    // 直線を当てるのに要る切れていないフレームの数（shot ごと）。足りない shot は今の z01 のまま。
    [Min(8)] public int placementFollowVideoDepthFeetMinFrames = 48;
    // 足元の行の広がり（5〜95% の幅、px）がこれより狭い shot は、直線の代わりにその shot の z01 の中央値の一定値にする。
    [Min(0f)] public float placementFollowVideoDepthFeetMinRowSpanPx = 40f;
    // 外れを除いた決定係数がこれ未満の shot は使わない（床が平らでない・カメラが動く）。
    [Range(0f, 1f)] public float placementFollowVideoDepthFeetMinR2 = 0.5f;
    // [VFOLLOW-FEET] を出す（読み込み時の shot ごとの直線と、使うかの判定）。
    public bool logPlacementFollowVideoDepthFeet = true;
    // 3D の大きさも足元の位置から決める（既定 OFF、新しい振る舞い、上の説明）。placementFollowVideoDepthFromFeet と、大きさをゆっくり変える形
    // （placementFollowVideoDepthSizeMaxRatePerSecond > 0、VideoDepthFollowStill.partial.cs）の上でだけ効く。動物の track だけ。
    // 2026-10-28 から既定 ON（上の placementFollowVideoDepthFromFeet と同じ返事）。上の既定の説明はそれまでの話。
    public bool placementFollowVideoDepthSizeFromFeet = true;
    // 足元の行をもう一度ならす窓（前後のフレーム数、中央値の後の平均）。大きさの切り替えが ON のときだけ使い、奥行きも同じ行から作る。0 ならならさない。
    [Min(0)] public int placementFollowVideoDepthFeetSizeMeanFrames = 6;
    // モデルの定数を見積もるフレームの条件: 箱の高さがその床の位置の箱の高さ H の、この割合以上（寝る・かがむ・頭を下げるフレームを除く）。
    [Range(0f, 1f)] public float placementFollowVideoDepthFeetSizeMinTypical = 0.8f;
    // モデルの定数が見積もり（中央値）へ寄る速さ（1 秒あたり、対数）。その場では止める。
    [Min(0f)] public float placementFollowVideoDepthFeetSizeConstRatePerSecond = 0.08f;
    // 大きさの上限の速さ（1 秒あたり、対数）。足元の位置から決めた大きさは動物が近づく・離れる速さで変わる（試算で犬が走って遠ざかる所 −31%/秒）ので、
    // 8%/s では遅れる。跳びを止めるための安全の上限。
    [Min(0f)] public float placementFollowVideoDepthFeetSizeMaxRatePerSecond = 0.6f;
    // 止まった所で一度だけモデルの形を箱に合わせる（2026-10-27 夜、新しい振る舞い・既定 OFF。placementFollowVideoDepthSizeFromFeet の上で効く）。
    // ユーザー（10/27 夜、n を実機で見て）「だいぶましにはなった，全体を通してもっとサイズを合わせれる？」。n の見た目の形（[MESH2D] のモデルの形の高さ
    // ÷ 箱の高さ）が 10% 以内のフレームは犬 60%・猫 47%。外れるのは止まって姿勢が違う所（猫がかがむ 1.36 倍・ソファで立つ 0.77 倍、犬が前を低くする
    // 1.31 倍）。動物の大きさは骨の高さで箱に合わせているが、見える形には耳・毛・立てた尾が乗る（骨で箱に合わせ直す試算は猫の形で 47% → 42% と悪くなった）。
    // しくみ: 読み込み時に、その場の区間を姿勢の区切りに分け（箱の高さの前後 6 フレームの中央値が区切りの中央値から placementFollowVideoDepthMatchSplit より
    // placementFollowVideoDepthMatchMinSegmentFrames フレーム続けて外れたら区切る）、区切りごとの箱の高さの中央値 B を表にする（伸ばした手足の分は引いた高さ）。
    // 再生中、区切りに入ってから placementFollowVideoDepthMatchSettleFrames フレームたった測りで、モデルの形（描くメッシュの頂点を投影した高さ）を一度だけ測り、
    // 形の高さが B になる大きさにして、区切りの終わりまで止める。区切りに入ってからそこまでと、動いている間は n のまま。
    // 試算（n の [MESH2D] の記録、sizefit/）: 形が 10% 以内 犬 60 → 69%・猫 47 → 55%、20% 以内 犬 86 → 94%・猫 67 → 83%。止まった所の大きさの変わり方
    // 猫 ×0.75〜1.26、犬 ×0.79〜1.10（区切りに入って 0.5 秒の後に一度だけ。0.3 秒の平滑で寄る）。
    public bool placementFollowVideoDepthSizeMatchesBox = false;
    [Range(0.01f, 1f)] public float placementFollowVideoDepthMatchSplit = 0.15f;
    [Min(1)] public int placementFollowVideoDepthMatchMinSegmentFrames = 12;
    [Min(1)] public int placementFollowVideoDepthMatchSettleFrames = 12;
    // 形を測るのは、区切りの箱の高さが直近 settle フレームずっと区切りの中央値のこの割合以内に入ってから（姿勢が落ち着いてから）。
    // 10/27 夜の撮影（sizefit/runs/cat_nss）: 区切りに入って 0.5 秒で測ると、猫が立ち上がる途中（23.96 秒、モデルの形がまだ伸びきっていない 331 px）を測って
    // ×1.26 にし、その後座っている間ずっと形が箱の 1.24〜1.33 倍になった（n は 1.00〜1.04）。
    [Range(0.01f, 1f)] public float placementFollowVideoDepthMatchSteadyTolerance = 0.08f;
    // 小さくする向きだけ直す（既定 true）。10/27 夜の 2 回目の撮影（sizefit/runs/cat_nss2）: 大きくする向きは、測った後にモデルの姿勢が変わって
    // 行き過ぎた（猫が座る区切りを 24.17 秒に測って ×1.19、その後データの胴が立っていってモデルの形が 24% 高くなり、座る間ずっと箱の 1.25 倍。n は 1.03）。
    // 小さくする向き（動物がかがむ・前を低くする・頭を下げるのにモデルは立ったまま）は区切りの間ずっと大きすぎるので、一度小さくすれば合う
    // （見積もり: 形が 20% 以内 猫 68 → 73%・犬 86 → 91%、どちらも n より悪くなる所が無い）。下限は placementFollowVideoDepthMatchMinFactor 倍。
    public bool placementFollowVideoDepthMatchShrinkOnly = true;
    [Range(0.1f, 1f)] public float placementFollowVideoDepthMatchMinFactor = 0.6f;
    // その場の間は足元の行も止め、その場が終わったら時間で混ぜて今の行へ戻す（2026-10-28、新しい振る舞い。体の大きさでその場を決める
    // placementFollowVideoDepthStillUsesBodyScale（VideoDepthFollowStill.partial.cs、10/28 から既定 ON）が ON のときだけ効く）。
    // 体の大きさでその場を決めると、その場の間に足元の行が姿勢で動く（犬が前を低くして前足を前へ出すと 645 → 687 px、横向きから振り向くと 633 → 654 px）。
    // 奥行き・H はその場に入った tick の値で止めている（ResolveVideoDepthFollowStillTargetDepth と下の H）ので、そのままでは出た瞬間に今の行の値へ跳ぶ
    // （犬の 27.3 秒で行 55 px 分 ≒ +25%。10/27 の「最後急に大きくなる」と同じ見え方）。
    // しくみ: 読み込み時に、奥行きの直線・H の直線に入れる足元の行を、その場の間は区間に入ったフレームの行で止める。区間の後は
    //   placementFollowVideoDepthFeetReleaseHoldSeconds の間そのまま（その場の判定は前後 0.5 秒を見るので、区間の終わりは歩き出しより約 0.5 秒前）、
    //   その後は止めていた間にずれた分だけを placementFollowVideoDepthFeetReleaseBlendSeconds でまっすぐ戻す（出た後に本当に動いた分はすぐ通す、
    //   ResolveVideoDepthFollowFeetHeldRows）。直線そのもの（当てはめ）は今の行で作る。
    [Min(0f)] public float placementFollowVideoDepthFeetReleaseHoldSeconds = 0.5f;
    [Min(0f)] public float placementFollowVideoDepthFeetReleaseBlendSeconds = 1.0f;

    // (フレーム, track) → 足元から決めた z01。null なら表が無い（OFF、または読み込み前）。キーは VideoDepthFollowStillKey と同じ形。
    private Dictionary<long, float> videoDepthFollowFeetZ01;
    // (フレーム, track) → その床の位置での箱の高さ H（px）。null なら表が無い（大きさの切り替えが OFF、または読み込み前）。
    private Dictionary<long, float> videoDepthFollowFeetBoxH;
    // その場に入った tick の H（track ごと。その場が続く間だけ。奥行きを止めるのと同じ）。
    private readonly Dictionary<uint, float> videoDepthFollowFeetHeldBoxHByTrack = new Dictionary<uint, float>();
    // 区間ごとに箱へ合わせる形（placementFollowVideoDepthSizeMatchesBox）: (フレーム, track) → B（px）と、姿勢の区切りの番号（動いているフレームは −1）。
    private Dictionary<long, float> videoDepthFollowMatchBoxH;
    private Dictionary<long, int> videoDepthFollowMatchSegment;
    // 形を測ってよいフレーム（区切りの中で、直近 settle フレームの箱の高さが区切りの中央値の steady 以内）。
    private HashSet<long> videoDepthFollowMatchReady;

    // この tick の大きさの計算（記録と、測りを控えるときに状態へ書くため）。
    private struct VideoDepthFollowFeetSizeTick
    {
        public float cNow;       // 箱に合わせた目標 ÷（奥行き × 箱の高さ）
        public float cHat;       // 箱に合わせた目標 ÷（奥行き × H）。最初の測りの定数
        public float cBar;       // この tick に使う定数
        public float boxH;       // 使った H（その場では止めた値）。区間ごとに箱へ合わせる形では B
        public bool eligible;    // 定数の見積もりに入れるフレーム
        public bool still;
        public bool stop;        // 止まった所で形を合わせた大きさを使った（placementFollowVideoDepthSizeMatchesBox）
        public bool measured;    // この tick にモデルの形を測った
        public float stopFactor; // 形の高さが区切りの B になる f
        public int segment;      // 姿勢の区切りの番号（動いているフレーム・表が無いときは −1）
        public int stopEntryFrame;
        public bool atEdge;
    }

    // この tick の合わせる先に使う z01（足元の表にあればその値、無ければ false）。
    private bool TryGetVideoDepthFollowFeetZ01(MetaObj obj, out float z01)
    {
        z01 = obj.anchorZ01;
        return placementFollowVideoDepth && placementFollowVideoDepthFromFeet && videoDepthFollowFeetZ01 != null && IsCategoryAnimal(obj.categoryId) &&
               videoDepthFollowFeetZ01.TryGetValue(VideoDepthFollowStillKey(GetCurrentPlaybackFrame(), obj.trackId), out z01);
    }

    // LoadMeta から呼ぶ（PrecomputeVideoDepthFollowStillOnLoad の後）。全フレームの箱と z01 を一度だけ読んで表を作る。
    private void PrecomputeVideoDepthFollowFeetOnLoad()
    {
        videoDepthFollowFeetZ01 = null;
        videoDepthFollowFeetBoxH = null;
        videoDepthFollowFeetHeldBoxHByTrack.Clear();
        animalSupportBottomByKey = null;
        int numFrames = (int)metaHeader.numFrames;
        // 伸ばした手足の分の下端（animalSupportBottomIgnoresLimbReach、AnimalSupportBottom.partial.cs）も同じ samples から作るので、どちらかが ON なら読む。
        bool feetOn = placementFollowVideoDepth && placementFollowVideoDepthFromFeet;
        bool supportOn = animalSupportBottomIgnoresLimbReach;
        if (!(feetOn || supportOn) || numFrames <= 0 || manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var samplesByTrack = new Dictionary<uint, List<VideoDepthFollowFeetSample>>();
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
                if (!IsCategoryAnimal(o.categoryId) || !IsStereoDepthSkeletonTrack(o.categoryId) || o.bboxH == 0 || o.bboxW == 0 ||
                    float.IsNaN(o.anchorZ01) || float.IsInfinity(o.anchorZ01))
                {
                    continue;
                }

                float bottom = o.bboxY + o.bboxH;
                clipState.TryGetValue(o.trackId, out bool wasClipped);
                bool bottomCut = ResolveBBoxBottomClipState(wasClipped, bottom);
                clipState[o.trackId] = bottomCut;
                float bx = o.bboxX;
                if (ResolveAnchorToScreen(o.anchorU, out _, out _, out bool isRightEye) && isRightEye)
                {
                    bx -= manifest.eye_w;
                }

                bool anyCut = bottomCut || o.bboxY <= 0 || bx <= BodyFrameOutEdgeMarginPx || bx + o.bboxW >= manifest.eye_w - 1 - BodyFrameOutEdgeMarginPx;
                if (!samplesByTrack.TryGetValue(o.trackId, out List<VideoDepthFollowFeetSample> list))
                {
                    list = new List<VideoDepthFollowFeetSample>();
                    samplesByTrack[o.trackId] = list;
                }

                list.Add(new VideoDepthFollowFeetSample
                {
                    frame = f, shot = shotBoundaries.ResolveShotIndex(f), bottom = bottom, height = o.bboxH, z01 = o.anchorZ01, bottomCut = bottomCut, anyCut = anyCut,
                });
            }
        }

        // 表のために読んだフレームの SMPL/SMAL は再生に使わないので捨てる（PrecomputeVideoDepthFollowStillOnLoad と同じ）。
        humanSmplPosesMetaBin.Clear();
        animalSmalPosesMetaBin.Clear();

        // 伸ばした手足の分を引く（下の足元の行・箱の高さも引いた値で作る。伸ばした手足を「近づいた」と読まない）。
        if (supportOn)
        {
            var supportTable = new Dictionary<long, Vector2>();
            foreach (KeyValuePair<uint, List<VideoDepthFollowFeetSample>> kv in samplesByTrack)
            {
                ApplyAnimalSupportBottomToSamples(kv.Key, kv.Value, supportTable);
            }

            animalSupportBottomByKey = supportTable;
        }

        if (!feetOn)
        {
            return;
        }

        var table = new Dictionary<long, float>();
        bool sizeOn = placementFollowVideoDepthSizeFromFeet;
        bool matchOn = sizeOn && placementFollowVideoDepthSizeMatchesBox;
        int meanHalf = sizeOn ? placementFollowVideoDepthFeetSizeMeanFrames : 0;
        var boxHTable = sizeOn ? new Dictionary<long, float>() : null;
        var matchBoxTable = matchOn ? new Dictionary<long, float>() : null;
        var matchSegmentTable = matchOn ? new Dictionary<long, int>() : null;
        var matchReadyTable = matchOn ? new HashSet<long>() : null;
        int usedShots = 0;
        int skippedShots = 0;
        int sizeShots = 0;
        int matchSegments = 0;
        // その場の間は足元の行を止める（placementFollowVideoDepthStillUsesBodyScale、上の説明）。
        bool holdRows = placementFollowVideoDepthStillUsesBodyScale && videoDepthFollowStillFrames != null;
        float feetFps = Mathf.Max(1f, ResolveMetaFps());
        int releaseHoldFrames = Mathf.Max(0, Mathf.RoundToInt(placementFollowVideoDepthFeetReleaseHoldSeconds * feetFps));
        int releaseBlendFrames = Mathf.Max(0, Mathf.RoundToInt(placementFollowVideoDepthFeetReleaseBlendSeconds * feetFps));
        int heldRowFrames = 0;
        foreach (KeyValuePair<uint, List<VideoDepthFollowFeetSample>> kv in samplesByTrack)
        {
            List<VideoDepthFollowFeetSample> all = kv.Value;
            int segmentBase = 0;
            int start = 0;
            while (start < all.Count)
            {
                int end = start;
                while (end < all.Count && all[end].shot == all[start].shot)
                {
                    end++;
                }

                List<VideoDepthFollowFeetSample> shotSamples = all.GetRange(start, end - start);
                VideoDepthFollowFeetFit fit = FitVideoDepthFollowFeet(shotSamples, placementFollowVideoDepthFeetMinFrames, placementFollowVideoDepthFeetMinRowSpanPx,
                    placementFollowVideoDepthFeetMinR2);
                float[] rows = ResolveVideoDepthFollowFeetRows(shotSamples, placementFollowVideoDepthFeetSmoothFrames, meanHalf);
                // 表に入れる行（その場の間は止め、出た後は混ぜて戻す）。当てはめは今の行 rows のまま。
                float[] tableRows = rows;
                if (holdRows)
                {
                    var stillMask = new bool[shotSamples.Count];
                    for (int i = 0; i < shotSamples.Count; i++)
                    {
                        stillMask[i] = videoDepthFollowStillFrames.Contains(VideoDepthFollowStillKey(shotSamples[i].frame, kv.Key));
                    }

                    tableRows = ResolveVideoDepthFollowFeetHeldRows(shotSamples, rows, stillMask, releaseHoldFrames, releaseBlendFrames);
                    for (int i = 0; i < shotSamples.Count; i++)
                    {
                        if (!float.IsNaN(rows[i]) && Mathf.Abs(tableRows[i] - rows[i]) > 0.5f)
                        {
                            heldRowFrames++;
                        }
                    }
                }

                if (fit.mode != VideoDepthFollowFeetMode.Off)
                {
                    float[] z01 = ResolveVideoDepthFollowFeetZ01FromRows(tableRows, fit);
                    for (int i = 0; i < shotSamples.Count; i++)
                    {
                        if (!float.IsNaN(z01[i]))
                        {
                            table[VideoDepthFollowStillKey(shotSamples[i].frame, kv.Key)] = z01[i];
                        }
                    }

                    usedShots++;
                }
                else
                {
                    skippedShots++;
                }

                if (logPlacementFollowVideoDepthFeet)
                {
                    Debug.Log($"[VFOLLOW-FEET] fit track={kv.Key} shot={all[start].shot} frames={shotSamples.Count} uncut={fit.uncut} mode={fit.mode} " +
                              $"reason={fit.reason} span={fit.rowSpan:F1} slope={fit.slope:F6} intercept={fit.intercept:F4} r2in={fit.r2Inliers:F3} " +
                              $"inliers={fit.inliers} constZ01={fit.constantZ01:F4}");
                }

                // 大きさ: 奥行きの直線がある shot だけ（足元の行から奥行きを決めない shot で大きさだけ足元から決めると、奥行きと大きさが食い違う）。
                if (sizeOn && fit.mode == VideoDepthFollowFeetMode.Line)
                {
                    VideoDepthFollowFeetFit sizeFit = FitVideoDepthFollowFeetSize(shotSamples, rows, placementFollowVideoDepthFeetMinFrames,
                        placementFollowVideoDepthFeetMinRowSpanPx, placementFollowVideoDepthFeetMinR2);
                    if (sizeFit.mode == VideoDepthFollowFeetMode.Line)
                    {
                        for (int i = 0; i < shotSamples.Count; i++)
                        {
                            if (!float.IsNaN(tableRows[i]))
                            {
                                boxHTable[VideoDepthFollowStillKey(shotSamples[i].frame, kv.Key)] = Mathf.Exp(sizeFit.slope * tableRows[i] + sizeFit.intercept);
                            }
                        }

                        sizeShots++;
                    }

                    if (logPlacementFollowVideoDepthFeet)
                    {
                        Debug.Log($"[VFOLLOW-FEET] size track={kv.Key} shot={all[start].shot} uncut={sizeFit.uncut} mode={sizeFit.mode} reason={sizeFit.reason} " +
                                  $"span={sizeFit.rowSpan:F1} slopeLogH={sizeFit.slope:F6} interceptLogH={sizeFit.intercept:F4} r2in={sizeFit.r2Inliers:F3} " +
                                  $"inliers={sizeFit.inliers} meanFrames=±{meanHalf}");
                    }

                    // 区間ごとに箱へ合わせる形の B（placementFollowVideoDepthSizeMatchesBox）。その場のフレームは VideoDepthFollowStill.partial.cs の表。
                    if (matchOn && sizeFit.mode == VideoDepthFollowFeetMode.Line)
                    {
                        var stillMask = new bool[shotSamples.Count];
                        for (int i = 0; i < shotSamples.Count; i++)
                        {
                            stillMask[i] = videoDepthFollowStillFrames != null &&
                                           videoDepthFollowStillFrames.Contains(VideoDepthFollowStillKey(shotSamples[i].frame, kv.Key));
                        }

                        ResolveVideoDepthFollowMatchBoxHeights(shotSamples, stillMask, placementFollowVideoDepthMatchSplit,
                            placementFollowVideoDepthMatchMinSegmentFrames, 6, out float[] matchB, out int[] matchSeg);
                        bool[] matchReady = ResolveVideoDepthFollowMatchReady(shotSamples, matchSeg, placementFollowVideoDepthMatchSettleFrames,
                            placementFollowVideoDepthMatchSteadyTolerance);
                        int shotSegments = 0;
                        var segText = new System.Text.StringBuilder();
                        for (int i = 0; i < shotSamples.Count; i++)
                        {
                            if (float.IsNaN(matchB[i]) || !(matchB[i] > 0f))
                            {
                                continue;
                            }

                            long key = VideoDepthFollowStillKey(shotSamples[i].frame, kv.Key);
                            matchBoxTable[key] = matchB[i];
                            matchSegmentTable[key] = matchSeg[i] < 0 ? -1 : segmentBase + matchSeg[i];
                            if (matchReady[i])
                            {
                                matchReadyTable.Add(key);
                            }
                            if (matchSeg[i] >= 0 && (i == 0 || matchSeg[i - 1] != matchSeg[i]))
                            {
                                shotSegments = Mathf.Max(shotSegments, matchSeg[i] + 1);
                                segText.Append(segText.Length > 0 ? "," : "").Append(shotSamples[i].frame);
                            }
                        }

                        segmentBase += shotSegments;
                        matchSegments += shotSegments;
                        if (logPlacementFollowVideoDepthFeet)
                        {
                            Debug.Log($"[VFOLLOW-FEET] match track={kv.Key} shot={all[start].shot} segments={shotSegments} starts={(segText.Length > 0 ? segText.ToString() : "-")} " +
                                      $"split={placementFollowVideoDepthMatchSplit:F2} minSeg={placementFollowVideoDepthMatchMinSegmentFrames}");
                        }
                    }
                }

                start = end;
            }
        }

        videoDepthFollowFeetZ01 = table;
        videoDepthFollowFeetBoxH = boxHTable;
        videoDepthFollowMatchBoxH = matchBoxTable;
        videoDepthFollowMatchSegment = matchSegmentTable;
        videoDepthFollowMatchReady = matchReadyTable;
        if (logPlacementFollowVideoDepthFeet)
        {
            Debug.Log($"[VFOLLOW-FEET] precompute tracks={samplesByTrack.Count} usedShots={usedShots} skippedShots={skippedShots} frames={table.Count} " +
                      $"smooth=±{placementFollowVideoDepthFeetSmoothFrames}f size={(sizeOn ? 1 : 0)} sizeShots={sizeShots} meanFrames=±{meanHalf} " +
                      $"match={(matchOn ? 1 : 0)} matchSegments={matchSegments} support={(supportOn ? 1 : 0)} holdRows={(holdRows ? 1 : 0)} heldRowFrames={heldRowFrames} " +
                      $"releaseHold={releaseHoldFrames}f releaseBlend={releaseBlendFrames}f ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    // その場の間の足元の行を止める（placementFollowVideoDepthStillUsesBodyScale、純粋な計算、テスト VideoDepthFollowBodyScaleStillTests）。samples・rows・still は
    // 1 shot 分で同じ並び。その場の区間（隣の sample もその場）の行は、区間に入った sample の行（出た後の戻している途中ならその値）で止める。区間の後の sample は、
    // 区間の最後のフレームから hold フレームまでは止めた行。その後は「今の行 + ずれ × (1 → 0 を blend フレームでまっすぐ)」で、ずれ = 止めた行 − hold の後の最初の
    // sample の今の行（その場の間に姿勢で動いた分）。止めていた間のずれだけをゆっくり戻し、出た後に本当に動いた分（近づく・離れる）はすぐ通す。
    // 10/28 の撮影（sizefit/runs/dog_np）: 止めた行から今の行へ全体を混ぜる形だと、その場の後すぐ近づいた犬（10.58 秒）の行が 1.5 秒遅れ、次のその場（11.67 秒）を
    // 遅れたまま止めて大きさが n の 0.88 倍になった。NaN の行は NaN のまま（その場の区間も切る）。
    internal static float[] ResolveVideoDepthFollowFeetHeldRows(List<VideoDepthFollowFeetSample> samples, float[] rows, bool[] still, int hold, int blend)
    {
        int n = samples.Count;
        var result = new float[n];
        float held = float.NaN;
        float offset = float.NaN;
        int lastStillFrame = 0;
        bool prevStill = false;
        hold = Mathf.Max(0, hold);
        blend = Mathf.Max(0, blend);
        for (int i = 0; i < n; i++)
        {
            float row = rows[i];
            float eff = row;
            if (!float.IsNaN(held) && !float.IsNaN(row))
            {
                int age = samples[i].frame - lastStillFrame;
                if (age <= hold)
                {
                    eff = held;
                }
                else
                {
                    if (float.IsNaN(offset))
                    {
                        offset = held - row;
                    }

                    float decay = blend > 0 ? Mathf.Clamp01(1f - (age - hold - 1) / (float)blend) : 0f;
                    eff = row + offset * decay;
                    if (decay <= 0f)
                    {
                        held = float.NaN;
                        offset = float.NaN;
                    }
                }
            }

            bool s = still[i] && !float.IsNaN(row);
            if (s)
            {
                if (!prevStill)
                {
                    held = eff;
                    offset = float.NaN;
                }

                eff = held;
                lastStillFrame = samples[i].frame;
            }

            result[i] = eff;
            prevStill = s;
        }

        return result;
    }

    // 区間ごとに箱へ合わせる形の B と姿勢の区切り（純粋な計算、テスト VideoDepthFollowFeetTests）。samples は 1 shot 分をフレームの昇順で、still は
    // samples と同じ並びのその場の印。高さは下が切れたフレームで前の値（shot の頭から切れていれば最初の切れていない値）。その場が続く所（隣のフレームも
    // その場）を 1 つの区間とし、前後 half フレームの高さの中央値が区切りの中央値から split より minSeg フレーム続けて外れたら、外れ始めで区切る。
    // B = 区切りの中は区切りの高さの中央値、その場でない所は前後 half の中央値をさらに前後 half で平均した値。最後に全体を前後 half で平均する
    // （区切りの端を坂にする）。segment = 区切りの番号（0 から、shot の中で増える）、その場でないフレームは −1。
    internal static void ResolveVideoDepthFollowMatchBoxHeights(List<VideoDepthFollowFeetSample> samples, bool[] still, float split, int minSeg, int half,
        out float[] boxH, out int[] segment)
    {
        int n = samples.Count;
        boxH = new float[n];
        segment = new int[n];
        var held = new float[n];
        float first = float.NaN;
        for (int i = 0; i < n && float.IsNaN(first); i++)
        {
            if (!samples[i].bottomCut && samples[i].height > 0f)
            {
                first = samples[i].height;
            }
        }

        float last = first;
        for (int i = 0; i < n; i++)
        {
            if (!samples[i].bottomCut && samples[i].height > 0f)
            {
                last = samples[i].height;
            }

            held[i] = last;
            segment[i] = -1;
        }

        if (n == 0 || float.IsNaN(first))
        {
            for (int i = 0; i < n; i++)
            {
                boxH[i] = float.NaN;
            }

            return;
        }

        var median = new float[n];
        var window = new List<float>(2 * half + 1);
        for (int i = 0; i < n; i++)
        {
            window.Clear();
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= half; j--)
            {
                window.Add(held[j]);
            }

            for (int j = i + 1; j < n && samples[j].frame - samples[i].frame <= half; j++)
            {
                window.Add(held[j]);
            }

            window.Sort();
            median[i] = window[window.Count / 2];
        }

        var level = new float[n];
        int segCount = 0;
        float logSplit = Mathf.Log(1f + Mathf.Max(0.0001f, split));
        int r = 0;
        while (r < n)
        {
            if (!still[r])
            {
                r++;
                continue;
            }

            int runEnd = r;
            while (runEnd + 1 < n && still[runEnd + 1] && samples[runEnd + 1].frame - samples[runEnd].frame <= 1)
            {
                runEnd++;
            }

            int segStart = r;
            int outCount = 0;
            for (int k = r; k <= runEnd; k++)
            {
                window.Clear();
                for (int j = segStart; j <= k; j++)
                {
                    window.Add(median[j]);
                }

                window.Sort();
                float segLevel = window[window.Count / 2];
                if (Mathf.Abs(Mathf.Log(Mathf.Max(1e-3f, median[k]) / Mathf.Max(1e-3f, segLevel))) > logSplit)
                {
                    outCount++;
                    if (outCount >= Mathf.Max(1, minSeg))
                    {
                        int cut = k - Mathf.Max(1, minSeg) + 1;
                        AssignMatchSegment(held, segment, level, segStart, cut - 1, segCount++);
                        segStart = cut;
                        outCount = 0;
                    }
                }
                else
                {
                    outCount = 0;
                }
            }

            AssignMatchSegment(held, segment, level, segStart, runEnd, segCount++);
            r = runEnd + 1;
        }

        var raw = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (segment[i] >= 0)
            {
                raw[i] = level[i];
                continue;
            }

            double sum = 0;
            int count = 0;
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= half; j--)
            {
                sum += median[j];
                count++;
            }

            for (int j = i + 1; j < n && samples[j].frame - samples[i].frame <= half; j++)
            {
                sum += median[j];
                count++;
            }

            raw[i] = (float)(sum / count);
        }

        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            int count = 0;
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= half; j--)
            {
                sum += raw[j];
                count++;
            }

            for (int j = i + 1; j < n && samples[j].frame - samples[i].frame <= half; j++)
            {
                sum += raw[j];
                count++;
            }

            boxH[i] = (float)(sum / count);
        }
    }

    // 形を測ってよいフレーム（純粋な計算、テスト AnimalSupportBottomTests）: 区切りの中で、区切りに入ってから settle フレーム以上たち、直近 settle フレームの
    // 箱の高さ（下が切れたフレームは前の値）がどれも区切りの中央値の ±tolerance 以内。
    internal static bool[] ResolveVideoDepthFollowMatchReady(List<VideoDepthFollowFeetSample> samples, int[] segment, int settle, float tolerance)
    {
        int n = samples.Count;
        var ready = new bool[n];
        var held = new float[n];
        float last = float.NaN;
        for (int i = 0; i < n; i++)
        {
            if (!samples[i].bottomCut && samples[i].height > 0f)
            {
                last = samples[i].height;
            }

            held[i] = last;
        }

        int lo = 0;
        while (lo < n)
        {
            if (segment[lo] < 0)
            {
                lo++;
                continue;
            }

            int hi = lo;
            while (hi + 1 < n && segment[hi + 1] == segment[lo])
            {
                hi++;
            }

            var values = new List<float>(hi - lo + 1);
            for (int i = lo; i <= hi; i++)
            {
                if (!float.IsNaN(held[i]))
                {
                    values.Add(held[i]);
                }
            }

            if (values.Count > 0)
            {
                values.Sort();
                float m = Mathf.Max(1f, values[values.Count / 2]);
                int steady = 0;
                for (int i = lo; i <= hi; i++)
                {
                    steady = !float.IsNaN(held[i]) && Mathf.Abs(held[i] / m - 1f) <= tolerance ? steady + 1 : 0;
                    ready[i] = i - lo >= settle && steady >= settle;
                }
            }

            lo = hi + 1;
        }

        return ready;
    }

    private static void AssignMatchSegment(float[] held, int[] segment, float[] level, int lo, int hi, int id)
    {
        if (hi < lo)
        {
            return;
        }

        var values = new List<float>(hi - lo + 1);
        for (int i = lo; i <= hi; i++)
        {
            values.Add(held[i]);
        }

        values.Sort();
        float m = values[values.Count / 2];
        for (int i = lo; i <= hi; i++)
        {
            segment[i] = id;
            level[i] = m;
        }
    }


    // この tick の大きさの目標を足元の位置から作る（大きさの切り替えが ON・表がある・動物の track）。boxRaw = 箱に合わせた目標（f × 奥行き ÷ 合わせる点）、
    // aimDepth = 合わせる先の奥行き（その場では止めた値）。st は測りの状態（最初の測りの前は null）。状態は書かない（測りを控えるときに
    // CommitVideoDepthFollowFeetSize が書く）。その場の H だけ、奥行きと同じく track ごとに止める。
    private bool TryResolveVideoDepthFollowFeetSizeRaw(MetaObj target, VideoDepthFollowState st, int frame, float aimDepth, float boxRaw,
        GameObject instance, Transform screen, bool allowMeasure, out float feetRaw, out VideoDepthFollowFeetSizeTick tick)
    {
        feetRaw = boxRaw;
        tick = default;
        tick.segment = -1;
        if (!placementFollowVideoDepth || !placementFollowVideoDepthFromFeet || !placementFollowVideoDepthSizeFromFeet || videoDepthFollowFeetBoxH == null ||
            !IsCategoryAnimal(target.categoryId) || !(boxRaw > 0f) || !(aimDepth > 0f) || target.bboxH == 0 ||
            !videoDepthFollowFeetBoxH.TryGetValue(VideoDepthFollowStillKey(frame, target.trackId), out float boxHNow) || !(boxHNow > 0f))
        {
            return false;
        }

        bool still = IsVideoDepthFollowStillFrame(target);
        float boxHUsed = boxHNow;
        if (still)
        {
            if (videoDepthFollowFeetHeldBoxHByTrack.TryGetValue(target.trackId, out float held))
            {
                boxHUsed = held;
            }
            else
            {
                videoDepthFollowFeetHeldBoxHByTrack[target.trackId] = boxHNow;
            }
        }
        else
        {
            videoDepthFollowFeetHeldBoxHByTrack.Remove(target.trackId);
        }

        tick.still = still;
        tick.boxH = boxHUsed;
        tick.cNow = boxRaw / (aimDepth * target.bboxH);
        tick.cHat = boxRaw / (aimDepth * boxHUsed);
        tick.eligible = !still && !IsVideoDepthFollowBoxAtEdge(target) && target.bboxH >= placementFollowVideoDepthFeetSizeMinTypical * boxHNow &&
                        IsFiniteFloat(tick.cNow) && tick.cNow > 0f;
        // 止まった所で一度だけ形を合わせる（placementFollowVideoDepthSizeMatchesBox、既定 OFF、上の説明）。区切りに入ってから settle フレームたった
        // 測りでモデルの形を測り、形の高さが B になる f にして区切りの終わりまで使う。それまでと動いている間は下の n の目標。
        long matchKey = VideoDepthFollowStillKey(frame, target.trackId);
        if (placementFollowVideoDepthSizeMatchesBox && videoDepthFollowMatchBoxH != null && videoDepthFollowMatchSegment != null &&
            videoDepthFollowMatchSegment.TryGetValue(matchKey, out int segment) && segment >= 0 &&
            videoDepthFollowMatchBoxH.TryGetValue(matchKey, out float segmentBox) && segmentBox > 0f)
        {
            bool sameStop = st != null && st.stopSegment == segment;
            tick.segment = segment;
            tick.stopEntryFrame = sameStop ? st.stopEntryFrame : frame;
            tick.atEdge = IsVideoDepthFollowBoxAtEdge(target);
            if (sameStop && IsFiniteFloat(st.stopFactor) && st.stopFactor > 0f)
            {
                tick.stop = true;
                tick.stopFactor = st.stopFactor;
                feetRaw = st.stopFactor;
                return true;
            }

            // 測ったが直さない区切り（小さくする向きだけ直す形で、大きくする向きだった）: 区切りの終わりまで n のまま（測り直さない）。
            bool measuredNoChange = sameStop && IsFiniteFloat(st.stopFactor) && st.stopFactor < 0f;

            if (!measuredNoChange && allowMeasure && st != null && !tick.atEdge && videoDepthFollowMatchReady != null && videoDepthFollowMatchReady.Contains(matchKey) &&
                instance != null && screen != null &&
                TryProjectMeshVerticesToEye(instance, screen, out _, out _, out float meshTop, out float meshBottom, out _) && meshBottom - meshTop > 1f)
            {
                float ratio = segmentBox / (meshBottom - meshTop);
                bool skip = placementFollowVideoDepthMatchShrinkOnly && !(ratio < 1f);
                ratio = Mathf.Max(placementFollowVideoDepthMatchMinFactor, ratio);
                tick.stopFactor = st.f * ratio;
                if (IsFiniteFloat(tick.stopFactor) && tick.stopFactor > 0f)
                {
                    tick.measured = true;
                    if (logPlacementFollowVideoDepthFeet)
                    {
                        Debug.Log($"[VFOLLOW-FEET] stop track={target.trackId} f={frame} segment={segment} entryF={tick.stopEntryFrame} meshH={meshBottom - meshTop:F1} " +
                                  $"segmentBoxH={segmentBox:F1} fBefore={st.f:F4} fStop={tick.stopFactor:F4} applied={(skip ? 0 : 1)}");
                    }

                    if (skip)
                    {
                        // 大きくする向きは直さない: 印（負の値）だけ控えて、下の n の目標へ。
                        tick.stopFactor = -1f;
                    }
                    else
                    {
                        tick.stop = true;
                        tick.boxH = segmentBox;
                        feetRaw = tick.stopFactor;
                        return true;
                    }
                }
            }
        }

        if (st == null || float.IsNaN(st.feetSizeC))
        {
            tick.cBar = tick.cHat;
        }
        else
        {
            float fps = manifest != null && manifest.fps > 0.01f ? (float)manifest.fps : 30f;
            bool hasMedian = TryMedianWithCandidate(st.feetSizeSamples, tick.eligible, tick.cNow, out float median);
            tick.cBar = StepVideoDepthFollowFeetSizeConst(st.feetSizeC, median, hasMedian, still, frame - st.feetSizeUpdateFrame,
                placementFollowVideoDepthFeetSizeConstRatePerSecond, fps);
        }

        if (!IsFiniteFloat(tick.cBar) || !(tick.cBar > 0f))
        {
            return false;
        }

        feetRaw = tick.cBar * aimDepth * boxHUsed;
        return IsFiniteFloat(feetRaw) && feetRaw > 0f;
    }

    // 測りを控える tick に、定数と見積もりのフレームを状態へ書く（MeasurePlacementFollowVideoDepth の測りを控える所から）。
    private static void CommitVideoDepthFollowFeetSize(VideoDepthFollowState st, VideoDepthFollowFeetSizeTick tick, int frame)
    {
        // 止まった所の区切り（placementFollowVideoDepthSizeMatchesBox）: 入ったフレームを控え、測ったら形を合わせた f を区切りの終わりまで持つ。
        if (tick.segment >= 0)
        {
            if (st.stopSegment != tick.segment)
            {
                st.stopSegment = tick.segment;
                st.stopEntryFrame = tick.stopEntryFrame;
                st.stopFactor = float.NaN;
            }

            if (tick.measured)
            {
                st.stopFactor = tick.stopFactor;
            }
        }
        else
        {
            st.stopSegment = -1;
            st.stopFactor = float.NaN;
        }

        if (tick.stop)
        {
            // n の定数はその場では止めているので触らない。
            return;
        }

        if (st.feetSizeSamples == null)
        {
            st.feetSizeSamples = new List<float>();
        }

        if (tick.eligible)
        {
            int index = st.feetSizeSamples.BinarySearch(tick.cNow);
            st.feetSizeSamples.Insert(index >= 0 ? index : ~index, tick.cNow);
        }

        st.feetSizeC = tick.cBar;
        st.feetSizeUpdateFrame = frame;
    }

    private static bool IsFiniteFloat(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // 並べた samples に candidate（include なら）を足したときの中央値（純粋な計算、テスト VideoDepthFollowFeetTests）。samples は昇順。
    // 要素が 1 つも無ければ false。偶数個のときは上側（Median と同じ n / 2 番目）。
    internal static bool TryMedianWithCandidate(List<float> sorted, bool include, float candidate, out float median)
    {
        median = float.NaN;
        int n = (sorted != null ? sorted.Count : 0) + (include ? 1 : 0);
        if (n == 0)
        {
            return false;
        }

        int m = n / 2;
        if (!include)
        {
            median = sorted[m];
            return true;
        }

        int index = sorted != null ? sorted.BinarySearch(candidate) : 0;
        int insertAt = index >= 0 ? index : ~index;
        median = m < insertAt ? sorted[m] : m == insertAt ? candidate : sorted[m - 1];
        return true;
    }

    // 定数 c̄ の 1 回の進め方（純粋な計算、テスト VideoDepthFollowFeetTests）: その場・中央値が無い・進んでいないなら今の値、ほかは中央値へ
    // 対数で ratePerSecond × 1 フレーム分（advanced は 1 に丸める）まで寄せる。
    internal static float StepVideoDepthFollowFeetSizeConst(float current, float median, bool hasMedian, bool still, int advanced, float ratePerSecond, float fps)
    {
        if (still || !hasMedian || advanced <= 0 || !(median > 0f) || !(current > 0f) || !(ratePerSecond > 0f))
        {
            return current;
        }

        float step = ratePerSecond * Mathf.Min(advanced, 1) / Mathf.Max(0.01f, fps);
        return current * Mathf.Exp(Mathf.Clamp(Mathf.Log(median / current), -step, step));
    }

    internal struct VideoDepthFollowFeetSample
    {
        public int frame;
        public int shot;
        public float bottom;     // 箱の下端の行（px、下が大きい）
        public float height;     // 箱の高さ（px）
        public float z01;        // meta.bin の目印の深さ（0 近い〜1 遠い）
        public bool bottomCut;   // 下が画面の縁で切れた（足元が読めない）
        public bool anyCut;      // どこかが画面の縁で切れた（直線の当てはめに使わない）
    }

    internal enum VideoDepthFollowFeetMode
    {
        Off = 0,       // 使わない（今の z01 のまま）
        Line = 1,      // 近さ = slope × 足元の行 + intercept
        Constant = 2,  // 足元がほとんど動かない shot: z01 の中央値の一定値
    }

    internal struct VideoDepthFollowFeetFit
    {
        public VideoDepthFollowFeetMode mode;
        public string reason;
        public int uncut;
        public float rowSpan;
        public float slope;
        public float intercept;
        public float r2Inliers;
        public int inliers;
        public float constantZ01;
    }

    // shot 1 つ分の当てはめ（純粋な計算、テスト VideoDepthFollowFeetTests）。切れていないフレームの (足元の行, 近さ = 1 − z01) に頑健な直線
    // （バイスクエアの重みの繰り返し最小二乗、外れ = 重み 0）。足元の行の 5〜95% の幅が minRowSpanPx 未満なら z01 の中央値の一定値。
    internal static VideoDepthFollowFeetFit FitVideoDepthFollowFeet(List<VideoDepthFollowFeetSample> samples, int minFrames, float minRowSpanPx, float minR2)
    {
        var fit = new VideoDepthFollowFeetFit { mode = VideoDepthFollowFeetMode.Off, reason = "-", slope = 0f, intercept = 0f, r2Inliers = 0f, constantZ01 = float.NaN };
        var x = new List<float>();
        var y = new List<float>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (!samples[i].anyCut)
            {
                x.Add(samples[i].bottom);
                y.Add(1f - samples[i].z01);
            }
        }

        fit.uncut = x.Count;
        if (x.Count < Mathf.Max(8, minFrames))
        {
            fit.reason = "frames";
            return fit;
        }

        var sortedX = new List<float>(x);
        sortedX.Sort();
        fit.rowSpan = sortedX[Mathf.FloorToInt(0.95f * (sortedX.Count - 1))] - sortedX[Mathf.CeilToInt(0.05f * (sortedX.Count - 1))];
        if (fit.rowSpan < minRowSpanPx)
        {
            var sortedZ = new List<float>(y.Count);
            for (int i = 0; i < y.Count; i++)
            {
                sortedZ.Add(1f - y[i]);
            }

            sortedZ.Sort();
            fit.constantZ01 = sortedZ[sortedZ.Count / 2];
            fit.mode = VideoDepthFollowFeetMode.Constant;
            fit.reason = "narrow";
            return fit;
        }

        FitRobustLine(x, y, out fit.slope, out fit.intercept, out fit.r2Inliers, out fit.inliers);
        if (!(fit.slope > 0f))
        {
            // 足元が下（手前）ほど近いはず。逆向きは床の直線に当たらない（カメラが動く・段差）。
            fit.reason = "slope";
            return fit;
        }

        if (fit.r2Inliers < minR2)
        {
            fit.reason = "r2";
            return fit;
        }

        fit.mode = VideoDepthFollowFeetMode.Line;
        fit.reason = "ok";
        return fit;
    }

    // 頑健な直線 y = slope x + intercept（バイスクエア、c = 4.685 × 1.4826 × MAD、30 回）。r2Inliers は重みが正の点の決定係数。
    internal static void FitRobustLine(List<float> x, List<float> y, out float slope, out float intercept, out float r2Inliers, out int inliers)
    {
        int n = x.Count;
        var w = new float[n];
        var res = new float[n];
        for (int i = 0; i < n; i++)
        {
            w[i] = 1f;
        }

        slope = 0f;
        intercept = 0f;
        for (int iter = 0; iter < 30; iter++)
        {
            double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double wi = w[i] * w[i];   // 重み付き最小二乗の重みは w²（lstsq(A·w, y·w) と同じ）
                sw += wi; sx += wi * x[i]; sy += wi * y[i]; sxx += wi * x[i] * x[i]; sxy += wi * x[i] * y[i];
            }

            double det = sw * sxx - sx * sx;
            if (sw <= 0 || System.Math.Abs(det) < 1e-12)
            {
                break;
            }

            slope = (float)((sw * sxy - sx * sy) / det);
            intercept = (float)((sy - slope * sx) / sw);
            for (int i = 0; i < n; i++)
            {
                res[i] = y[i] - (slope * x[i] + intercept);
            }

            float med = Median(res);
            var dev = new float[n];
            for (int i = 0; i < n; i++)
            {
                dev[i] = Mathf.Abs(res[i] - med);
            }

            float s = 1.4826f * Median(dev) + 1e-9f;
            for (int i = 0; i < n; i++)
            {
                float u = res[i] / (4.685f * s);
                w[i] = Mathf.Abs(u) < 1f ? (1f - u * u) * (1f - u * u) : 0f;
            }
        }

        inliers = 0;
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            if (w[i] > 0f)
            {
                inliers++;
                mean += y[i];
            }
        }

        r2Inliers = 0f;
        if (inliers < 2)
        {
            return;
        }

        mean /= inliers;
        double ssRes = 0, ssTot = 0;
        for (int i = 0; i < n; i++)
        {
            if (w[i] > 0f)
            {
                double r = y[i] - (slope * x[i] + intercept);
                ssRes += r * r;
                ssTot += (y[i] - mean) * (y[i] - mean);
            }
        }

        r2Inliers = ssTot > 1e-12 ? (float)(1.0 - ssRes / ssTot) : 0f;
    }

    private static float Median(float[] values)
    {
        var sorted = (float[])values.Clone();
        System.Array.Sort(sorted);
        return sorted.Length == 0 ? 0f : sorted[sorted.Length / 2];
    }

    // フレームごとの足元から決めた z01（純粋な計算、テスト VideoDepthFollowFeetTests）。samples は 1 shot 分をフレームの昇順で。下が切れていないフレームは
    // 前後 half フレームの下が切れていない samples の足元の行の中央値、下が切れたフレームはその前の値（shot の頭から切れていれば最初の切れていない値）。
    // 一定値の shot はどのフレームも同じ値。下が切れていないフレームが 1 つも無ければ NaN。
    internal static float[] ResolveVideoDepthFollowFeetZ01(List<VideoDepthFollowFeetSample> samples, VideoDepthFollowFeetFit fit, int half)
    {
        return ResolveVideoDepthFollowFeetZ01FromRows(ResolveVideoDepthFollowFeetRows(samples, half, 0), fit);
    }

    // 足元の行から z01（一定値の shot はどのフレームも同じ値、行が NaN のフレームは NaN）。
    internal static float[] ResolveVideoDepthFollowFeetZ01FromRows(float[] rows, VideoDepthFollowFeetFit fit)
    {
        var result = new float[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            result[i] = fit.mode == VideoDepthFollowFeetMode.Constant ? Mathf.Clamp01(fit.constantZ01)
                : float.IsNaN(rows[i]) ? float.NaN : Mathf.Clamp01(1f - (fit.slope * rows[i] + fit.intercept));
        }

        return result;
    }

    // フレームごとの足元の行（純粋な計算、テスト VideoDepthFollowFeetTests）。samples は 1 shot 分をフレームの昇順で。下が切れていないフレームは
    // 前後 half フレームの下が切れていない samples の足元の行の中央値、下が切れたフレームはその前の値（shot の頭から切れていれば最初の切れていない値）。
    // meanHalf > 0 なら、その行をさらに前後 meanHalf フレームで平均する（中央値の段をなくす。大きさの切り替えが ON のとき）。
    // 下が切れていないフレームが 1 つも無ければどれも NaN。
    internal static float[] ResolveVideoDepthFollowFeetRows(List<VideoDepthFollowFeetSample> samples, int half, int meanHalf)
    {
        var rows = new float[samples.Count];
        var window = new List<float>(2 * Mathf.Max(0, half) + 1);
        for (int i = 0; i < samples.Count; i++)
        {
            rows[i] = float.NaN;
            if (samples[i].bottomCut)
            {
                continue;
            }

            window.Clear();
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= half; j--)
            {
                if (!samples[j].bottomCut)
                {
                    window.Add(samples[j].bottom);
                }
            }

            for (int j = i + 1; j < samples.Count && samples[j].frame - samples[i].frame <= half; j++)
            {
                if (!samples[j].bottomCut)
                {
                    window.Add(samples[j].bottom);
                }
            }

            window.Sort();
            rows[i] = window[window.Count / 2];
        }

        float first = float.NaN;
        for (int i = 0; i < rows.Length && float.IsNaN(first); i++)
        {
            first = rows[i];
        }

        float last = float.NaN;
        var held = new float[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            if (!float.IsNaN(rows[i]))
            {
                last = rows[i];
            }

            held[i] = !float.IsNaN(rows[i]) ? rows[i] : (!float.IsNaN(last) ? last : first);
        }

        if (meanHalf <= 0)
        {
            return held;
        }

        var smooth = new float[held.Length];
        for (int i = 0; i < held.Length; i++)
        {
            double sum = 0;
            int count = 0;
            for (int j = i; j >= 0 && samples[i].frame - samples[j].frame <= meanHalf; j--)
            {
                if (!float.IsNaN(held[j]))
                {
                    sum += held[j];
                    count++;
                }
            }

            for (int j = i + 1; j < held.Length && samples[j].frame - samples[i].frame <= meanHalf; j++)
            {
                if (!float.IsNaN(held[j]))
                {
                    sum += held[j];
                    count++;
                }
            }

            smooth[i] = count > 0 ? (float)(sum / count) : float.NaN;
        }

        return smooth;
    }

    // その床の位置での箱の高さの当てはめ（純粋な計算、テスト VideoDepthFollowFeetTests）: どこも切れていないフレームの (足元の行, log(箱の高さ)) に
    // 頑健な直線。rows は ResolveVideoDepthFollowFeetRows の値（samples と同じ並び）。フレームが足りない・行の 5〜95% の幅が minRowSpanPx 未満・
    // 向きが「下ほど大きい」でない・外れを除いた決定係数が minR2 未満なら Off（その shot は大きさを足元から決めない）。
    internal static VideoDepthFollowFeetFit FitVideoDepthFollowFeetSize(List<VideoDepthFollowFeetSample> samples, float[] rows, int minFrames, float minRowSpanPx,
        float minR2)
    {
        var fit = new VideoDepthFollowFeetFit { mode = VideoDepthFollowFeetMode.Off, reason = "-", slope = 0f, intercept = 0f, r2Inliers = 0f, constantZ01 = float.NaN };
        var x = new List<float>();
        var y = new List<float>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (!samples[i].anyCut && samples[i].height > 0f && !float.IsNaN(rows[i]))
            {
                x.Add(rows[i]);
                y.Add(Mathf.Log(samples[i].height));
            }
        }

        fit.uncut = x.Count;
        if (x.Count < Mathf.Max(8, minFrames))
        {
            fit.reason = "frames";
            return fit;
        }

        var sortedX = new List<float>(x);
        sortedX.Sort();
        fit.rowSpan = sortedX[Mathf.FloorToInt(0.95f * (sortedX.Count - 1))] - sortedX[Mathf.CeilToInt(0.05f * (sortedX.Count - 1))];
        if (fit.rowSpan < minRowSpanPx)
        {
            fit.reason = "narrow";
            return fit;
        }

        FitRobustLine(x, y, out fit.slope, out fit.intercept, out fit.r2Inliers, out fit.inliers);
        if (!(fit.slope > 0f))
        {
            fit.reason = "slope";
            return fit;
        }

        if (fit.r2Inliers < minR2)
        {
            fit.reason = "r2";
            return fit;
        }

        fit.mode = VideoDepthFollowFeetMode.Line;
        fit.reason = "ok";
        return fit;
    }
}
