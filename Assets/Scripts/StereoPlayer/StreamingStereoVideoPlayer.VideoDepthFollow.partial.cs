using System.Collections.Generic;
using UnityEngine;

// V（placementFollowVideoDepth、2026-10-26 夜、新しい振る舞い・既定 OFF。種別で分けない: 倍率をロックする track（人・動物）すべて）。
// ユーザーの案（10/26 夜、プレビュー f を実機で見て）: 「犬や猫が近づくと目がおかしくなる…画面上でサイズ合わせて表示して，frameoutするときだけ前に来た方がいい」。
// 起きていたこと（runs_lenscale = f と同じ設定、videodepth/depth_series.py）: ⑧ は投影の高さを箱に合わせるため、近づく場面でモデルを映像の 0.35〜0.46 倍の
// 距離まで寄せる（猫の後半 vf 380〜719 は AABB の中心が目から 0.32〜0.35 m・一番近い骨が下限 0.25 m、映像の猫は 0.82〜0.95 m。犬 vf 682 は 0.388 対 0.999 m）。
// V: 映っている間は、片目の絵は今の ⑧ のまま（箱に合わせた大きさ）にし、両目の奥行きだけを毎フレーム映像の奥行き Zv（S と同じ焼き込まれた視差の契約、
// TryResolveVideoDepthMeters）に置く。やり方は S（StereoDepth.partial.cs）の k をフレームごとにしたもの: 目（ピンホールの原点）を中心に配置を f 倍する
// （anchor の深さ × f、倍率 × f）。目のまわりの一様な拡大なので、⑧ の比（anchor の深さに対する倍率）とその平滑・ガード・厳密に解く式・⑦・頭合わせ（px）は
// f によらず同じ値になり、片目の絵は今と同じになる。変わるのは両目の奥行きと、絶対の長さで決まるもの（⑧ の下限 0.25 m・上限 3·D、画面の外で横を通る帯）の
// 当たり方だけ（近い場面で下限に当たらなくなるので、そこでは絵が箱に合う側へ変わる）。
// 倍率はショットの中で変わる（runs_lenscale の推定で猫 1.6〜2.9 倍、S なしならさらに k 倍）。10/01・10/14 の「倍率はショットごとに 1 つ」と食い違うので
// 既定 OFF、採否はユーザーが絵で決める。画面の端から出るとき（frame out の続き・横を通る）は、続きが最後の配置（倍率 × f）のまま進める。
// 前に来るのは横を通る決まり（frameOutPassByViewer、作業ツリーの既定は OFF。プレビュー f はビルドの間だけ ON）が ON のときだけ。OFF なら続きは測った向き
// （V では奥行きが Zv に沿うので、今より視聴者の方への成分が小さい）か端の外向きへ進む。
// S なしでも動く（S が ON なら k の上に掛かる。S のロックが Zv に合わせた後は f ≈ 1 から始まる）。契約が無い bundle（チュートリアル: noDepthPolicy）は状態を作らず今のまま。
// C（placementVideoDepthFloor、VideoDepthFloor.partial.cs）と両方 ON にしたときは C が退く（C の下限は V なしの奥行きで測るため）。
//
// f はロックの外に持つ（lockedModelLocalScaleByTrack に入れない）。ロックを読む物（U1・持ち越し・Model のしまい・S の確定と外し）は f を知らなくてよい。
// f を読むのは S の k と同じ所で、k × f を掛ける: 倍率の書き込み（ApplyReplaceableModelTransform、C4-T と同じ所。f があるとき C4-T は 1）、⑨ の kSkel、
// 順序の丸めの kObj、動物の root の位置の平滑の beta（1/(k f)）、動作の長さ（placementFollowVideoDepthScalesMotionLengths）、システムのフレームアウトの戻り先。
//
// tick の流れ（ApplyMetaTarget）:
//   1. 配置の前（ApplyReplaceableModelTransform の直前。モーション中・frame out の続きの tick はその前で return するので来ない = f は止まり、倍率も置き方も保つ）:
//      前の tick の測りで f を 1 動画フレーム分だけ進め、変わった比だけ長さを持つ状態（インスタンスの置き方・動物の root の位置の平滑・人の根の奥行きの平滑・
//      上下とも切れた tick の基準の高さ）を目のまわりにずらしてから、この tick の anchorZ を f 倍する（ApplyPlacementFollowVideoDepthBeforePlacement）。
//   2. 配置の後（S のロックの後、ObserveInteractiveMotionDisplayedRoot の前）: 合わせる点（stereoDepthMatchPoint、既定は AABB の中心）の深さを測り、
//      合わせる点を Zv に置く f（f × Zv ÷ 合わせる点の深さ）を控えるだけ（MeasurePlacementFollowVideoDepth）。transform は書かない（この tick の配置を読む物
//      ― 動作の起点・frame out の速さ・⑨・犬のボール ― がみな同じ配置を見る）。1 動画フレームに 1 回だけ控える。
// 控えないフレーム（f は前の値のまま）: 倍率の測り直し（U1）の前・この tick に姿勢を書かなかった・⑧ が目標を出さなかった（ガード・入口で戻った）・
//   Zv が読めない。⑧ が上限（3·D）で止まったフレームは、測りが f を小さくする（手前へ寄せる）ときだけ控える（大きくする側は控えない: 動物は root が体の奥に
//   あるので、上限で止まったまま大きくすると f が増え続ける。小さくする側は上限から離れる向きなので止まる）。⑧ が下限（一番近い骨を 0.25 m）で止まったフレームは
//   控える（押し戻された分 f が小さめに出て、次の tick に下限が外れて合う）。
// 巻き戻り・長い間（10/26 の点検の指摘 1: 周の頭で ⑧ が上限で止まったまま、周の終わりの f 約 4.5 倍で猫が 3 m に貼り付いて大きく見え続ける見込み）:
//   配置の前の tick ごとに、前に配置の前を通った動画フレーム（lastPlacedFrame）と比べ、RewindResetMinFrames（12）より大きく戻った・
//   placementFollowVideoDepthSnapAfterFrames より大きく飛んだ（映っていなかった・frame out の続き・シーク）ら、ロックの作り直しと同じく f を戻して状態を捨て、
//   この tick は f なしで置き、この tick の測りから始め直す（[VFOLLOW-RESET reason=rewind|gap]）。測りの有無に依らないので、上限で止まっていても戻る。
//   測りが続けて控えられない間（⑧ のガード・Zv が読めない）も配置の前は通るので、その間は飛んだと数えない（f を保つ）。
// 画面の端（上・左右、下端は ⑦・⑧ と同じヒステリシス）に掛かったフレーム（placementFollowVideoDepthAtEdge、既定 NoGrow）:
//   Follow    = 端でも Zv に合わせ続ける。⑧ が見切れを補った目標（外挿・1.6 倍の上限）で寄せる分も f が打ち消すので、出口で f が膨らみ（犬の左下の出口 vf 674〜684 で
//               +68 %、テレビの女性 f528〜563 で 1.66 → 2.93 倍、map_pose）、続きはその膨らんだ倍率のまま進む（10/26 の点検の指摘 2）。
//   NoGrow    = 端では f を大きくしない（小さくはする: 箱が潰れて ⑧ が押し戻す分（左右も切れて外挿を諦める vf 685〜）は Zv で止める）。外挿で寄せる分はそのまま
//               前に来る（端に掛かる前の最後の f を保つ）。測りの揺れで f が下へだけ動く分（端に長く掛かる shot）は、1 % より小さく下がる測りでは動かさない
//               （VideoDepthFollowEdgeShrinkDeadband）。
//   EaseToLock = 端では f をこのロックの最初の f（S なしなら S の k に当たる値、S が ON なら約 1）へ placementFollowVideoDepthEdgeEaseSeconds で寄せる
//               （大きくはしない）。出ていく間に片目の絵を変えずに前へ来て、続き・横を通る所はプレビュー f に近い倍率になる（推定でテレビの女性 1.0 倍、
//               犬は端に掛かってから続きまで 10 フレームしかないので 1.15 倍。NoGrow ではテレビの女性 1.38 倍・犬 2.15 倍、Follow では 2.13 倍・2.57 倍。
//               videodepth/wf/final/v_sim_final.py）。
//               端に長く掛かる shot（足が下で切れたまま近づく）では、その間ずっと今の近い置き方になる。
//   Hold       = 端では f を動かさず（大きさは端に掛かる前のまま）、⑧ も箱の比の代わりに合わせる点を合わせる先の奥行きへ置く比を使う
//               （TryResolveVideoDepthFollowEdgeHoldRatio、A の TryResolveStereoFollowRatio と同じ式）。見切れた箱で ⑧ が外挿した目標（モデルを手前へ寄せる）も、
//               EaseToLock の寄せも無いので、端で前へ跳ばない。出ていく続き・横を通る所は、端の奥行きと大きさのまま始まる（2026-10-26 夜、プレビュー g を
//               実機で見たユーザー「frameoutした瞬間めっちゃ一気に前に来ていない？そこから行けばいいのに」。g の EaseToLock では犬の頭が vf 674〜681 の
//               0.29 秒で目から 0.85 → 0.31 m、テレビの女性の合わせる点が f525〜545 で 0.89 → 0.40 m。NoGrow でも ⑧ の寄せで犬 0.85 → 0.41 m・女性 0.88 → 0.51 m）。
//               片目の絵は箱に合わなくなる（映像の対象が端で近づいても、モデルは奥行きの分しか大きく写らない）。端に掛かる前の測りの遅れ（τ）の分、
//               端に掛かった最初の 0.3 秒ほどで奥行きが Zv へ寄る（犬は約 12 % 奥へ）。
// 平滑: f の対数を 1 動画フレームにつき 1 − e^(−1/fps/τ) だけ測りへ寄せる（τ は ⑧ と同じ、今のシーンは 0.3 s）。⑧ の比も同じ τ で平滑されているので、⑧ の目標が
//   段で変わると（見切れの状態の切り替わり）奥行きが一時 Zv から外れる（段の対数の大きさの約 37 %、段の 0.3 s 後が最大）。f の目標が動き続ける場面では
//   τ × その速さだけ遅れる（猫の後半へ近づく区間で Zv より約 2 % 手前の見込み）。測りの間が空いても（動作の音のフェードで動画だけ進んだ）1 フレーム分しか
//   進めない（受け渡しで奥行きが跳ばない）。ロックの最初の値は平滑しない（S なしなら最初から約 1.3〜1.5 倍 = S の k）。
// ロックを作り直す tick（shot の頭・差し替え・Model・表示し直し・視聴距離）は f を捨てて今のまま置き、その tick の測りから始め直す（モデルごとに f が違う。10/26 の推定で
//   前のモデルの f を持ち越すと 0.54〜1.94 倍ずれる）。例外は動作の途中で Model を押した結び直し（keepInteractiveMotionOnModelChange）: 動作の道は前のモデルの
//   f の世界にあるので、新しいモデルも同じ f で置く（結び直しのずれ = 2 体の「表示 − anchor」の差が f 倍の世界で揃う）。追従に戻ってから自分の測りへ寄せる。
// 絶対の目標へ向ける姿勢の経路（keypoint・制御点・IK）で始まったロックでは使わない（目のまわりの f 倍で曲げが変わる。S の R1-11・R2-3 と同じ）。使い始めた後に
//   この経路を通った tick は f を進めないだけ（S も確定の後は数えるだけ）。runs_lenscale の 11 本では kp=1 の tick は 0。
// 走っている間に主スイッチを切ったら f を目のまわりに戻して捨てる。動作・frame out の続きの最中の track は、次に追従で置く tick まで待って戻す（S の pendingUndo と同じ。
//   動作の道は f の世界にあり、途中で倍率だけ 1/f にすると絵が縮むため）。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public enum VideoDepthFollowEdgeMode
    {
        Follow = 0,      // 端でも Zv に合わせ続ける（出口で f が膨らみ、続きはその倍率）
        NoGrow = 1,      // 端では f を大きくしない（外挿で寄せる分は前に来る、押し戻す分は Zv で止める）
        EaseToLock = 2,  // 端では f をこのロックの最初の f へ寄せる（出ていく間に前へ来て、続きはプレビュー f と同じ倍率）
        Hold = 3,        // 端では f を動かさず、奥行きは合わせる先に置いたまま（前へ跳ばない。続きは端の奥行き・大きさから進む）
    }

    // V で合わせる先の奥行き（2026-10-26 夜、ユーザー「画面上でやった方がいいのかな」）。
    public enum VideoDepthFollowTarget
    {
        VideoDepth = 0,   // 映像の焼き込まれた視差が示す奥行き Zv（動画の対象が見える距離。契約の無い bundle では V を使わない）
        ScreenPlane = 1,  // 画面の面（screenDistanceMeters、ふつう 1.0 m）。背景の映像と同じ距離なので両方に同時に目が合う。契約の無い bundle（練習）でも効く
    }

    // V の主スイッチ（既定 OFF）。OFF なら状態を作らず、配置は今とビット単位で同じ（f を読む所はどれも状態が無ければ 1 を掛ける・割る）。
    // 2026-10-27 から既定 ON（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public bool placementFollowVideoDepth = true;
    // f の平滑の時定数（秒、動画フレームで刻む）。0 以下なら ⑧ と同じ（animal は animalDepthRefineSmoothingSeconds が正ならそれ、ほかは projectedDepthSmoothingSeconds）。
    [Min(0f)] public float placementFollowVideoDepthSmoothingSeconds = 0f;
    // 画面の端に掛かったフレームの f の扱い（上の説明）。既定 NoGrow: 出口で f が膨らまず、続きは端に掛かる前の倍率で進む。
    // 2026-10-27 から既定 Hold（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public VideoDepthFollowEdgeMode placementFollowVideoDepthAtEdge = VideoDepthFollowEdgeMode.Hold;
    // 合わせる先（上の VideoDepthFollowTarget）。既定は映像の奥行き。
    public VideoDepthFollowTarget placementFollowVideoDepthTarget = VideoDepthFollowTarget.VideoDepth;
    // EaseToLock の寄せる時定数（秒、動画フレームで刻む）。犬の出口は端に掛かってから続きまで 10 フレーム（0.42 s）で、0.25 s なら対数の 81 % 寄る
    // （videodepth/wf/final/v_sim_final.py の推定で続きの f はロックの最初の f の 1.15 倍）。0.15 s なら 94 % 寄るが、寄り始めの 1 フレームで奥行きが 2 割近く動く。
    [Min(0f)] public float placementFollowVideoDepthEdgeEaseSeconds = 0.25f;
    // f の測りの上下限（1/x〜x 倍）。測りの破綻（合わせる点が目の直前など）で f が飛ばないための止め。runs_lenscale からの推定（videodepth/wf/map_pose）では
    // S なしで猫が最大約 4.7 倍（S の k 1.365 × 3.46）、犬は端で大きくしなければ約 3.9 倍。
    [Min(1f)] public float placementFollowVideoDepthMaxFactor = 8f;
    // 配置の前を前に通った動画フレームからこの数より大きく飛んだら（映っていなかった・frame out の続き・シーク）、f を捨てて測り直す。
    // 動作の音のフェード（約 12 フレーム、動作の間は動画が止まる）より長く取る。測りが続けて控えられない間は配置の前を通るので数えない。
    [Min(1)] public int placementFollowVideoDepthSnapAfterFrames = 48;
    // 自分から動く動作の長さ（止まる距離・歩く速さ・歩ける余地の下限・Model を押したときのずらす上限）にも f を掛ける（S の stereoDepthScalesMotionLengths と同じ考え:
    // 片目の絵の動きを V なしと同じにする）。OFF なら動作はメートルのまま（f で遠く大きくなったモデルが、今と同じメートルだけ前へ歩いてくる）。
    public bool placementFollowVideoDepthScalesMotionLengths = true;
    // [VFOLLOW-*] を出す（CONFIG・START・RESET・CARRY・SNAP・SKIP・CLEAR は起きたとき、[VFOLLOW] は N tick ごと）。
    public bool logPlacementFollowVideoDepth = false;
    [Min(1)] public int logPlacementFollowVideoDepthEveryNTicks = 30;

    // NoGrow・EaseToLock の端のフレームで、測りがこの割合より小さく f を下げるときは動かさない（測りの揺れで f が下へだけ動き続けないため）。
    private const float VideoDepthFollowEdgeShrinkDeadband = 0.01f;

    private sealed class VideoDepthFollowState
    {
        public float f = 1f;              // 直前の配置（と、この tick に配置の前で決めた後のこの tick の配置）に掛けている倍率。S の k の上に掛かる
        public bool engaged;              // このロックで一度でも測りから f を決めたか（最初の 1 回は平滑しない）
        public float fLock = 1f;          // このロックの最初の f（EaseToLock の寄せる先）
        public bool disabledForLock;      // 絶対の目標へ向ける経路を通ったので、このロックでは使わない
        public bool pendingDisable;       // 次の配置の前に f を 1 へ戻す
        public bool pendingSwitchOff;     // 主スイッチを切ったときに動作・frame out の続きの最中だった: 次に追従で置く tick に戻して捨てる
        public int updateFrame = -1;      // f を最後に進めたときの測りの動画フレーム
        public int lastPlacedFrame = -1;  // 配置の前（ApplyPlacementFollowVideoDepthBeforePlacement）を最後に通った動画フレーム
        public bool hasMeasure;
        public int measureFrame = -1;     // 測りの動画フレーム（1 フレームに 1 回だけ控える）
        public float measureRaw = 1f;     // 測った f の目標（f × Zv ÷ 合わせる点の深さ、上下限で丸めた値）
        public bool measureAtEdge;        // 測ったフレームで箱が画面の端に掛かっていた
        public bool measureStill;         // 測ったフレームで足元が動いていない（placementFollowVideoDepthHoldWhileFeetStill、VideoDepthFollowStill.partial.cs）
        public bool measureSlow;          // 大きさをゆっくり変える形で測った（placementFollowVideoDepthSizeMaxRatePerSecond > 0、VideoDepthFollowStill.partial.cs）
        public bool measureFeetSize;      // 測りの目標を足元の位置から作った（placementFollowVideoDepthSizeFromFeet、VideoDepthFollowFeet.partial.cs）
        public float feetSizeC = float.NaN;   // 足元の位置から作る大きさのモデルの定数 c̄（最初の測りの前は NaN）
        public List<float> feetSizeSamples;   // c̄ の見積もりに入れたフレームの c（昇順）
        public int feetSizeUpdateFrame = -1;  // c̄ を最後に進めた測りの動画フレーム
        public int stopSegment = int.MinValue; // 止まった所で形を合わせる形（placementFollowVideoDepthSizeMatchesBox）の今の区切り（動いている間は −1）
        public int stopEntryFrame = -1;        // その区切りに入った測りの動画フレーム
        public float stopFactor = float.NaN;   // 形の高さが区切りの箱の高さになる f（測る前は NaN）
        public bool measureCeiling;       // 測ったフレームで ⑧ が上限で止まっていた（小さくする測りだけ控える）
        public float measureZv;           // 記録用
        public float measureMatchZ;       // 記録用（測った tick の配置の合わせる点の深さ）
        public int instanceId;            // f を掛けているインスタンス（作り直されたら状態は使わない）
        public int createdFrame = -1;
        public int holdTicks;             // 記録用: 測れなかった tick の数
        public string lastHold = "-";
    }

    private readonly Dictionary<uint, VideoDepthFollowState> videoDepthFollowByTrack = new Dictionary<uint, VideoDepthFollowState>();
    private readonly List<uint> videoDepthFollowTrackIdBuffer = new List<uint>(8);
    private bool videoDepthFollowConfigLogged;
    private bool videoDepthFollowHasPendingSwitchOff;

    // この track の配置に今掛かっている f（状態が無い・OFF なら 1。1 を掛けても割っても同じ値なので、OFF の経路は今と同じ）。
    private float ResolveVideoDepthFollowFactor(uint trackId)
    {
        return videoDepthFollowByTrack.TryGetValue(trackId, out VideoDepthFollowState st) ? st.f : 1f;
    }

    // 動作の長さに掛ける f（placementFollowVideoDepthScalesMotionLengths、StereoDepth.partial.cs の ResolveMotionLengthScale が読む）。発火の tick に 1 回読まれ、動作の間は f も止まる。
    private float ResolveVideoDepthFollowMotionLengthFactor(uint trackId)
    {
        return placementFollowVideoDepthScalesMotionLengths ? ResolveVideoDepthFollowFactor(trackId) : 1f;
    }

    // DisplayModelTick の頭（SyncStereoDepthSettings の後）。走っている間に主スイッチを切ったら、掛けている f を目のまわりに戻して状態を捨てる。
    // 動作・frame out の続きの最中の track は印だけ付けて、次に追従で置く tick（配置の前）で戻す。OFF のまま始めた走りでは状態が無いので何もしない。
    private void SyncPlacementFollowVideoDepthSettings()
    {
        if (placementFollowVideoDepth)
        {
            if (!videoDepthFollowConfigLogged && logPlacementFollowVideoDepth)
            {
                videoDepthFollowConfigLogged = true;
                IsPlacementFollowDepthAvailable(out string contract);
                Debug.Log($"[VFOLLOW-CONFIG] on=1 target={placementFollowVideoDepthTarget} contract={contract} s={(stereoDepthMatchVideo ? 1 : 0)} matchPoint={stereoDepthMatchPoint} " +
                          $"tau={placementFollowVideoDepthSmoothingSeconds:F2}(0=⑧ {projectedDepthSmoothingSeconds:F2}/{animalDepthRefineSmoothingSeconds:F2}) " +
                          $"edge={placementFollowVideoDepthAtEdge} edgeEase={placementFollowVideoDepthEdgeEaseSeconds:F2} maxFactor={placementFollowVideoDepthMaxFactor:F2} " +
                          $"snapAfter={placementFollowVideoDepthSnapAfterFrames} rewindFrames={RewindResetMinFrames} lengths={(placementFollowVideoDepthScalesMotionLengths ? 1 : 0)} " +
                          $"refine8={(refineDepthFromProjectedBones ? 1 : 0)} exact8={(depthRefineExactSolve ? 1 : 0)} exactU1={(exactScaleRefine ? 1 : 0)} " +
                          $"passBy={(frameOutPassByViewer ? 1 : 0)} rewindReset={(rewindResetsSmoothing ? 1 : 0)}");
            }

            if (videoDepthFollowHasPendingSwitchOff)
            {
                // 切って戻した（まだ戻していない track は f を掛けたまま続ける）。
                videoDepthFollowHasPendingSwitchOff = false;
                foreach (KeyValuePair<uint, VideoDepthFollowState> kv in videoDepthFollowByTrack)
                {
                    kv.Value.pendingSwitchOff = false;
                }
            }

            return;
        }

        if (videoDepthFollowByTrack.Count == 0)
        {
            return;
        }

        bool hasEye = TryGetStereoDepthEye(out Vector3 eye);
        videoDepthFollowTrackIdBuffer.Clear();
        videoDepthFollowTrackIdBuffer.AddRange(videoDepthFollowByTrack.Keys);
        for (int i = 0; i < videoDepthFollowTrackIdBuffer.Count; i++)
        {
            uint id = videoDepthFollowTrackIdBuffer[i];
            VideoDepthFollowState st = videoDepthFollowByTrack[id];
            if (st.pendingSwitchOff)
            {
                continue;
            }

            if (st.f != 1f && (IsTrackInInteractiveMotion(id) || IsBodyFrameOutActive(id)))
            {
                st.pendingSwitchOff = true;
                videoDepthFollowHasPendingSwitchOff = true;
                if (logPlacementFollowVideoDepth)
                {
                    Debug.Log($"[VFOLLOW-CLEAR] track={id} reason=switchOffPending f={GetCurrentPlaybackFrame()} fv={st.f:F4} undone=0 holdTicks={st.holdTicks}");
                }

                continue;
            }

            bool undone = false;
            if (st.f != 1f && hasEye && trackInstances.TryGetValue(id, out GameObject instance) && instance != null && instance.GetInstanceID() == st.instanceId)
            {
                ApplyVideoDepthFollowRatio(id, instance, eye, 1f / st.f);
                undone = true;
            }

            videoDepthFollowByTrack.Remove(id);
            ForgetVideoDepthFollowStillHeldDepth(id);
            if (logPlacementFollowVideoDepth)
            {
                Debug.Log($"[VFOLLOW-CLEAR] track={id} reason=switchOff f={GetCurrentPlaybackFrame()} fv={st.f:F4} undone={(undone ? 1 : 0)} holdTicks={st.holdTicks}");
            }
        }
    }

    // ResetPerShotTrackState（本物のカット）: ロックと一緒に捨てる。動物の root の位置の平滑・人の根の奥行きの平滑・上下とも切れた tick の高さも同じ所で消え、
    // 置き方は次の tick に書き直されるので戻さない。偽のカット（ロックを持ち越す）では呼ばれないので f も持ち越す。
    private void ClearVideoDepthFollowStatesForRealCut()
    {
        if (videoDepthFollowByTrack.Count == 0)
        {
            return;
        }

        videoDepthFollowTrackIdBuffer.Clear();
        videoDepthFollowTrackIdBuffer.AddRange(videoDepthFollowByTrack.Keys);
        for (int i = 0; i < videoDepthFollowTrackIdBuffer.Count; i++)
        {
            uint id = videoDepthFollowTrackIdBuffer[i];
            VideoDepthFollowState st = videoDepthFollowByTrack[id];
            videoDepthFollowByTrack.Remove(id);
            ForgetVideoDepthFollowStillHeldDepth(id);
            if (logPlacementFollowVideoDepth)
            {
                Debug.Log($"[VFOLLOW-CLEAR] track={id} reason=realCut f={GetCurrentPlaybackFrame()} fv={st.f:F4} undone=0 holdTicks={st.holdTicks}");
            }
        }
    }

    // f を ratio 倍に変える tick の、長さを持つ状態のずらし（目のまわりに ratio 倍）。S の ApplyStereoDepthFactorChange からロックの行を除いたもの
    // （V の f はロックの外に持つ）。⑧ の比の平滑・⑨ の差（k で割った長さ）・頭合わせの px・SMAL/SMPL の回転・関節の world 位置の平滑は触らない（S と同じ理由）。
    // 配置の前に呼ぶ（置き方はこの後の配置が書き直すが、anchor の子を合わせる配置が今の倍率を読むので揃えておく）。
    private void ApplyVideoDepthFollowRatio(uint trackId, GameObject instance, Vector3 eye, float ratio)
    {
        if (bottomFitHeldReferenceCamYByTrack.TryGetValue(trackId, out float heldReferenceY))
        {
            bottomFitHeldReferenceCamYByTrack[trackId] = heldReferenceY * ratio;
        }

        if (instance == null)
        {
            return;
        }

        Transform root = instance.transform;
        TrackPlacementWriter.Apply(root, new TrackPlacementCommand(eye + (root.position - eye) * ratio, root.rotation, root.localScale * ratio));
        animalPoseApplier.ScaleRootPositionFilterAbout(root, eye, ratio);   // 人の root には何もしない
        ScaleHumanSmplRootDepthState(instance, ratio);                       // 動物には何もしない
    }

    // 配置の前を前に通ったフレームからの飛び方（純粋な計算）: "rewind" = rewindFrames より大きく戻った、"gap" = snapAfterFrames より大きく進んだ、null = どちらでもない。
    // 小さく戻った（rewindFrames 以内）ときは null（f は測りの側で、戻った測りへ飛ぶ）。lastFrame が負（まだ通っていない）なら null。
    internal static string ClassifyVideoDepthFollowFrameJump(int frameNow, int lastFrame, int rewindFrames, int snapAfterFrames)
    {
        if (lastFrame < 0 || frameNow < 0)
        {
            return null;
        }

        if (frameNow < lastFrame - Mathf.Max(0, rewindFrames))
        {
            return "rewind";
        }

        return frameNow - lastFrame > Mathf.Max(1, snapAfterFrames) ? "gap" : null;
    }

    // ApplyMetaTarget の配置の前（ApplyReplaceableModelTransform の直前。インスタンスとロックの有無はこの tick の分がもう決まっている）。
    // 状態が無ければ何もしない（最初の測りは配置の後で作る）。target は ApplyMetaTarget の値渡しの複製で、anchorWorld もこの tick だけのもの。
    private void ApplyPlacementFollowVideoDepthBeforePlacement(ref MetaObj target, GameObject instance, Transform screen, float uEye, float vEye,
        ref Vector3 anchorWorld, bool rebinding)
    {
        if (videoDepthFollowByTrack.Count == 0 || instance == null || !IsStereoDepthSkeletonTrack(target.categoryId) ||
            !videoDepthFollowByTrack.TryGetValue(target.trackId, out VideoDepthFollowState st))
        {
            return;
        }

        int frameNow = GetCurrentPlaybackFrame();
        int instanceId = instance.GetInstanceID();
        bool sameInstance = st.instanceId == instanceId;
        bool hasEye = TryGetPinholeBasis(screen, out Vector3 eye, out _);
        bool hasLock = lockedModelLocalScaleByTrack.ContainsKey(target.trackId);
        bool contractOk = IsPlacementFollowDepthAvailable(out string contract);
        int lastFrame = st.lastPlacedFrame >= 0 ? st.lastPlacedFrame : st.createdFrame;
        string jump = ClassifyVideoDepthFollowFrameJump(frameNow, lastFrame, RewindResetMinFrames, placementFollowVideoDepthSnapAfterFrames);
        if (placementFollowVideoDepth && jump == null && hasEye && contractOk && !hasLock && rebinding && st.engaged && !st.disabledForLock)
        {
            // 動作の途中の Model（結び直しの tick）: 新しいインスタンスにも同じ f を掛ける。新しいロックは ApplyReplaceableModelTransform が f で割り戻した深さで作る。
            st.instanceId = instanceId;
            st.hasMeasure = false;
            // 足元の位置から作る大きさのモデルの定数はモデルごと（VideoDepthFollowFeet.partial.cs）: 次の測りで新しいモデルから始め直す。
            st.feetSizeC = float.NaN;
            st.feetSizeSamples = null;
            st.feetSizeUpdateFrame = -1;
            st.stopSegment = int.MinValue;
            st.stopEntryFrame = -1;
            st.stopFactor = float.NaN;
            if (logPlacementFollowVideoDepth)
            {
                Debug.Log($"[VFOLLOW-CARRY] track={target.trackId} f={frameNow} fv={st.f:F4} model={instance.name} reason=rebind");
            }
        }
        else if (!placementFollowVideoDepth || jump != null || !hasEye || !contractOk || !hasLock || !sameInstance)
        {
            // 主スイッチを切った（動作の最中だった track）・巻き戻り・長い間・ロックの作り直し・インスタンスの作り直し・契約が無くなった: f を戻して捨てる
            // （同じインスタンスなら平滑の状態に掛けた f も戻す）。この tick は f なしで置き、配置の後の測りから始め直す。
            bool undone = false;
            if (hasEye && sameInstance && st.f != 1f)
            {
                ApplyVideoDepthFollowRatio(target.trackId, instance, eye, 1f / st.f);
                undone = true;
            }

            videoDepthFollowByTrack.Remove(target.trackId);
            ForgetVideoDepthFollowStillHeldDepth(target.trackId);
            if (logPlacementFollowVideoDepth)
            {
                string reason = !placementFollowVideoDepth ? "switchOff" : !hasLock ? "relock" : !sameInstance ? "instance" : !contractOk ? "contract" :
                    jump ?? "basis";
                Debug.Log($"[VFOLLOW-RESET] track={target.trackId} f={frameNow} shot={lastAppliedShotIndex} reason={reason} contract={contract} " +
                          $"lastPlacedF={lastFrame} fv={st.f:F4} undone={(undone ? 1 : 0)}");
            }

            return;
        }
        else
        {
            float before = st.f;
            if (st.pendingDisable)
            {
                st.pendingDisable = false;
                st.f = 1f;
            }
            else if (!st.disabledForLock && st.hasMeasure && st.measureFrame != st.updateFrame)
            {
                st.f = StepVideoDepthFollowFactor(target, st);
            }

            if (st.f != before)
            {
                ApplyVideoDepthFollowRatio(target.trackId, instance, eye, st.f / before);
            }
        }

        st.lastPlacedFrame = frameNow;
        // placementFollowVideoDepthFromFeet（2026-10-27、10/28 から既定 ON、VideoDepthFollowFeet.partial.cs）: 最初に置く奥行きも足元から決めた z01 に置き直す
        // （bundle の深さとの比を掛けるので、ほかの倍率はそのまま）。bundle の目印の深さのまま置くと、⑧ が合わせる先へ寄せる比の平滑の遅れの分だけ、
        // 流れる深さが置いた奥行きに漏れる（犬の 26〜27 秒: 置く前の深さ 1.50 → 2.15 m、合わせる先 0.852 m のまま、置いた奥行き 0.83 → 0.99 m）。
        // 箱に合わせた大きさの目標（⑧ の箱の比 × 置く前の深さ）は置く前の深さに依らないので変わらない。
        float feetRebase = 1f;
        if (TryGetVideoDepthFollowFeetZ01(target, out float feetZ01))
        {
            float bundleDepth = DecodeAnchorDepthMetersFromBundle(target.anchorZ01);
            float feetDepth = DecodeAnchorDepthMetersFromBundle(feetZ01);
            if (bundleDepth > 0.0001f && feetDepth > 0.0001f)
            {
                feetRebase = feetDepth / bundleDepth;
            }
        }

        if (st.f != 1f || feetRebase != 1f)
        {
            target.anchorZ *= st.f * feetRebase;
            anchorWorld = AnchorUvZToWorldPinhole(screen, uEye, vEye, target.anchorZ);
        }
    }

    private float StepVideoDepthFollowFactor(MetaObj target, VideoDepthFollowState st)
    {
        bool first = !st.engaged;
        int advanced = st.measureFrame - st.updateFrame;
        float tau = placementFollowVideoDepthSmoothingSeconds > 0f
            ? placementFollowVideoDepthSmoothingSeconds
            : IsCategoryAnimal(target.categoryId) && animalDepthRefineSmoothingSeconds > 0f
                ? animalDepthRefineSmoothingSeconds
                : Mathf.Max(0f, projectedDepthSmoothingSeconds);
        // その場の後の追いつき（VideoDepthFollowStill.partial.cs）: 足元が動き出してしばらくは、止めていた分をゆっくり追う。
        if (TryResolveVideoDepthFollowReleaseTau(target, tau, out float releaseTau))
        {
            tau = releaseTau;
        }
        float fps = manifest != null && manifest.fps > 0.01f ? (float)manifest.fps : 30f;
        // 縮める向きの遊び（placementFollowVideoDepthFeetSizeShrinkBand、10/28 夜から既定 0.15、VideoDepthFollowFeet.partial.cs）: 足元の位置から作った目標が少し小さいだけなら f のまま。
        float raw = st.measureRaw;
        if (!first && st.measureFeetSize && placementFollowVideoDepthFeetSizeShrinkBand > 0f)
        {
            raw = ResolveVideoDepthFollowShrinkBandGoal(st.f, raw, placementFollowVideoDepthFeetSizeShrinkBand);
        }

        // その場（足元が動いていない）のフレームは、端の Hold と同じく f を動かさない（VideoDepthFollowStill.partial.cs）。
        float next = StepVideoDepthFollowFactorMath(st.f, raw, first, advanced, st.measureAtEdge || st.measureStill,
            st.measureStill ? VideoDepthFollowEdgeMode.Hold : placementFollowVideoDepthAtEdge, st.fLock,
            fps, tau, placementFollowVideoDepthEdgeEaseSeconds, placementFollowVideoDepthSnapAfterFrames, out bool snapped);
        // 大きさをゆっくり変える形（VideoDepthFollowStill.partial.cs）: 平滑の後に、1 動画フレームあたりの変わり方を上限で丸める（動作で動画が止まる間は進まない）。
        // 1 回に進めるのは 1 フレーム分まで（advanced を 1 に丸める）: 動作の前の音のフェード（約 12 フレーム、動画は進むが f は止まる）の後に、溜まった分を
        // 1 tick でまとめて進めると 3.7% 跳んだ（2026-10-27 の撮影 sizejump/runs/cat_slowchk、21.5 秒）。溜まった分は次のフレームから上限の速さで追う。
        if (st.measureSlow && !snapped)
        {
            // 足元の位置から作った目標（VideoDepthFollowFeet.partial.cs）は近づく・離れる速さで変わるので、上限は安全のための値にする。
            float rate = st.measureFeetSize ? placementFollowVideoDepthFeetSizeMaxRatePerSecond : placementFollowVideoDepthSizeMaxRatePerSecond;
            next = LimitVideoDepthFollowFactorRate(st.f, next, rate, Mathf.Min(advanced, 1), fps);
        }
        if (first)
        {
            st.fLock = next;
        }

        st.engaged = true;
        st.updateFrame = st.measureFrame;
        if (logPlacementFollowVideoDepth && snapped)
        {
            // 1 ロックに 1 行の START（最初の値）と、測りの側で戻った・飛んだ測りへ飛んだ SNAP（大きな巻き戻り・長い間は配置の前の RESET が先に捨てる）。
            trackPrefabSources.TryGetValue(target.trackId, out GameObject prefab);
            Debug.Log($"[VFOLLOW-{(first ? "START" : "SNAP")}] track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} f={GetCurrentPlaybackFrame()} " +
                      $"measuredF={st.measureFrame} advanced={(first ? 0 : advanced)} shot={lastAppliedShotIndex} prefab={(prefab != null ? prefab.name : "?")} " +
                      $"z01={target.anchorZ01:F4} Zv={st.measureZv:F4} matchZ={st.measureMatchZ:F4} fvBefore={st.f:F4} fv={next:F4} fLock={st.fLock:F4} " +
                      $"kS={ResolveStereoDepthFactor(target.trackId):F4} edge={(st.measureAtEdge ? 1 : 0)} ceil={(st.measureCeiling ? 1 : 0)} " +
                      $"sinceCreateFrames={GetCurrentPlaybackFrame() - st.createdFrame}");
        }

        return next;
    }

    // 縮める向きの遊び（純粋な計算、テスト VideoDepthFollowFeetTests）: 目標が今の f 以上なら目標のまま。小さいときは、今の f が目標の (1 + band) 倍以下なら
    // 今の f（縮めない）、それより大きければ目標の (1 + band) 倍（帯の端まで縮める）。band ≤ 0 なら目標のまま。
    internal static float ResolveVideoDepthFollowShrinkBandGoal(float current, float raw, float band)
    {
        if (!(band > 0f) || !(current > 0f) || !(raw > 0f) || raw >= current)
        {
            return raw;
        }

        return Mathf.Max(raw, Mathf.Min(current, raw * (1f + band)));
    }

    // f の 1 回の進め方（純粋な計算）。advanced = 測りの動画フレーム − 前に進めたときの測りの動画フレーム。
    //   最初・戻った（advanced < 0）・長い間（advanced > snapAfterFrames）: その場の測りへ飛ぶ（snapped）。
    //   それ以外: 目標を決めて、対数で 1 動画フレーム分（1 − e^(−1/fps/τ)）寄せる。τ ≤ 0 なら目標そのもの。
    //     端でない・Follow: 目標 = 測り。
    //     端で NoGrow: 測りが今の値の (1 − deadband) 倍より小さいときだけ測り、ほかは今の値（大きくしない）。
    //     端で EaseToLock: NoGrow の目標とこのロックの最初の f の小さい方、時定数は edgeTau。
    //     端で Hold: 今の値（動かさない）。
    internal static float StepVideoDepthFollowFactorMath(float current, float raw, bool first, int advanced, bool atEdge, VideoDepthFollowEdgeMode edgeMode,
        float lockFactor, float fps, float tau, float edgeTau, int snapAfterFrames, out bool snapped)
    {
        snapped = first || advanced < 0 || advanced > Mathf.Max(1, snapAfterFrames) || !(current > 0f);
        if (snapped)
        {
            return raw;
        }

        if (advanced == 0 || (atEdge && edgeMode == VideoDepthFollowEdgeMode.Hold))
        {
            return current;
        }

        float goal = raw;
        float tauUsed = tau;
        if (atEdge && edgeMode != VideoDepthFollowEdgeMode.Follow)
        {
            goal = raw < current * (1f - VideoDepthFollowEdgeShrinkDeadband) ? raw : current;
            if (edgeMode == VideoDepthFollowEdgeMode.EaseToLock)
            {
                if (lockFactor > 0f && lockFactor < goal)
                {
                    goal = lockFactor;
                }

                tauUsed = edgeTau;
            }
        }

        if (goal == current)
        {
            return current;
        }

        if (tauUsed <= 0.0001f)
        {
            return goal;
        }

        float a = 1f - Mathf.Exp(-1f / Mathf.Max(0.01f, fps) / tauUsed);
        return Mathf.Exp(Mathf.Log(current) + a * (Mathf.Log(goal) - Mathf.Log(current)));
    }

    // 箱が画面の端に掛かっているか（上端・左右の端、下端は ⑦・⑧ と同じヒステリシスの判定 IsBBoxBottomClipped。同じ tick に何度呼んでも同じ値で、
    // この tick の ⑦・⑧ が既に呼んでいる）。左右の余裕は frame out の続きの判定（ResolveBodyFrameOutClipSide）と同じ 2 px。右目の物体の bbox は eye_w ぶん右。
    private bool IsVideoDepthFollowBoxAtEdge(MetaObj obj)
    {
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 || obj.bboxW == 0 || obj.bboxH == 0)
        {
            return false;
        }

        float bx = obj.bboxX;
        if (ResolveAnchorToScreen(obj.anchorU, out _, out _, out bool isRightEye) && isRightEye)
        {
            bx -= manifest.eye_w;
        }

        return obj.bboxY <= 0 || IsBBoxBottomClipped(obj) ||
               bx <= BodyFrameOutEdgeMarginPx || bx + obj.bboxW >= manifest.eye_w - 1 - BodyFrameOutEdgeMarginPx;
    }

    // ApplyMetaTarget の配置の後（S のロック ApplyStereoDepthLockIfSettled の後、ObserveInteractiveMotionDisplayedRoot の前）。測って控えるだけで、transform は書かない。
    private void MeasurePlacementFollowVideoDepth(GameObject instance, MetaObj target, Transform screen)
    {
        if (!placementFollowVideoDepth || instance == null || !IsStereoDepthSkeletonTrack(target.categoryId))
        {
            return;
        }

        int frame = GetCurrentPlaybackFrame();
        videoDepthFollowByTrack.TryGetValue(target.trackId, out VideoDepthFollowState st);
        bool logDue = logPlacementFollowVideoDepth && Time.frameCount % Mathf.Max(1, logPlacementFollowVideoDepthEveryNTicks) == 0;
        bool measuredThisFrame = st != null && st.hasMeasure && st.measureFrame == frame;
        if (measuredThisFrame && !logDue)
        {
            return;
        }

        float fApplied = st != null ? st.f : 1f;
        bool isAnimal = IsCategoryAnimal(target.categoryId);
        float manual = Mathf.Max(0.01f, EvaluateManualScaleForFrame(target.trackId, frame));
        bool manualScaled = Mathf.Abs(manual - ManualScaleDefault) > 0.001f;
        bool scaleSettled = scaleRefinedByTrack.Contains(target.trackId) || !refineScaleFromProjectedBones || manualScaled;
        StereoDepthPosePath path = ResolveStereoDepthPosePathThisTick(instance);
        bool refined8 = TryGetStereoDepthRefineThisTick(target.trackId, out bool clamped8, out _, out float move8);
        bool floored8 = refined8 && TryGetStereoDepthRefineFloorThisTick(target.trackId);
        bool ceiling8 = refineDepthFromProjectedBones && refined8 && clamped8 && !floored8;
        string contract = "-";
        float zv = -1f;
        float match = -1f;
        float raw = -1f;
        bool rawClamped = false;
        bool atEdge = false;
        bool still = false;
        bool slow = IsVideoDepthFollowSlowSizeTrack(target);
        bool feetSize = false;
        VideoDepthFollowFeetSizeTick feetTick = default;
        float boxRawForLog = -1f;
        Vector3 eye = Vector3.zero;
        Quaternion camRotation = Quaternion.identity;
        StereoDepthMatchPoint used = stereoDepthMatchPoint;
        string hold = null;
        if (st != null && st.disabledForLock)
        {
            hold = "disabled";
        }
        else if (!lockedModelLocalScaleByTrack.ContainsKey(target.trackId))
        {
            hold = "noLock";
        }
        else if (!scaleSettled)
        {
            hold = "scaleNotSettled";
        }
        else if (path == StereoDepthPosePath.None)
        {
            hold = "noPose";
        }
        else if (path == StereoDepthPosePath.AbsoluteTarget)
        {
            hold = "absoluteTarget";
        }
        else if (refineDepthFromProjectedBones && !refined8)
        {
            hold = "no8";
        }
        else if (!IsPlacementFollowDepthAvailable(out contract))
        {
            hold = "contract";
        }
        else if (!TryResolvePlacementFollowTargetDepth(target, isAnimal, out zv))
        {
            hold = "noZv";
        }
        else if (!TryGetPinholeBasis(screen, out eye, out camRotation))
        {
            hold = "basis";
        }
        else if (!TryResolveStereoMatchDepth(instance, eye, camRotation, ref used, out float matchRaw, out float pivotZ))
        {
            hold = "noMatch";
        }
        else
        {
            // 手動倍率の track は S のロック（ApplyStereoDepthLockIfSettled）と同じく、配置の中心の骨のまわりの拡大を割り戻した点を合わせる。
            match = manualScaled && used != StereoDepthMatchPoint.Pivot ? pivotZ + (matchRaw - pivotZ) / manual : matchRaw;
            // 大きさをゆっくり変える形（VideoDepthFollowStill.partial.cs）: ⑧ は合わせる先へ置く比を使ったので、今の合わせる点は合わせる先にある。
            // f の目標は「⑧ が箱の比で置いていたら」の合わせる点を、合わせる先（その場では止めた奥行き、ほかは Zv）へ置く倍率（箱に合わせた大きさ）。
            float aimDepth = zv;
            float matchForRaw = match;
            bool boxRawOk = false;
            if (slow && TryGetVideoDepthFollowBoxDepthShiftThisTick(target.trackId, out float boxShift))
            {
                matchForRaw = float.IsNaN(boxShift) ? -1f : match + boxShift;
                boxRawOk = !float.IsNaN(boxShift);
                if (videoDepthFollowStillHeldDepthByTrack.TryGetValue(target.trackId, out float heldAim))
                {
                    aimDepth = heldAim;
                }
            }

            raw = matchForRaw > 0f ? fApplied * aimDepth / Mathf.Max(0.001f, matchForRaw) : -1f;
            // 3D の大きさを足元の位置から決める（placementFollowVideoDepthSizeFromFeet、10/28 から既定 ON、VideoDepthFollowFeet.partial.cs）: 箱に合わせた目標から
            // モデルの定数だけを取り出し、目標をその床の位置での箱の高さから作り直す（座る・頭を上げる・前を低くするで目標が動かない）。
            if (boxRawOk && raw > 0f && TryResolveVideoDepthFollowFeetSizeRaw(target, st, frame, aimDepth, raw, instance, screen, !measuredThisFrame,
                    out float feetRaw, out feetTick))
            {
                boxRawForLog = raw;
                raw = feetRaw;
                feetSize = true;
            }

            if (matchForRaw <= 0f)
            {
                hold = "noBox";
            }
            else if (!IsUsableStereoDepthFactor(raw))
            {
                hold = "badRatio";
            }
            else
            {
                float maxFactor = Mathf.Max(1f, placementFollowVideoDepthMaxFactor);
                float clampedRaw = Mathf.Clamp(raw, 1f / maxFactor, maxFactor);
                rawClamped = clampedRaw != raw;
                raw = clampedRaw;
                if (ceiling8 && !(raw < fApplied))
                {
                    // ⑧ が上限で止まっている tick は、f を小さくする（手前へ寄せる）測りだけ控える。
                    hold = "ceiling8";
                }
                else
                {
                    atEdge = IsVideoDepthFollowBoxAtEdge(target);
                    still = IsVideoDepthFollowStillFrame(target);
                }
            }
        }

        if (hold == "absoluteTarget" && (st == null || !st.engaged))
        {
            // 絶対の目標へ向ける経路で始まったロックでは V を使わない（S の確定と同じ決まり）。使い始めた後のこの経路の tick は f を進めないだけ（S も確定の後は数えるだけ）。
            if (st == null)
            {
                st = new VideoDepthFollowState { instanceId = instance.GetInstanceID(), createdFrame = frame };
                videoDepthFollowByTrack[target.trackId] = st;
            }

            if (!st.disabledForLock)
            {
                st.disabledForLock = true;
                st.pendingDisable = st.f != 1f;
                if (logPlacementFollowVideoDepth)
                {
                    Debug.Log($"[VFOLLOW-SKIP] track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} f={frame} shot={lastAppliedShotIndex} " +
                              $"reason={(isAnimal ? "noSmalFk" : "noSmplFk")} fv={st.f:F4}");
                }
            }
        }
        else if (hold == null && !measuredThisFrame)
        {
            if (st == null)
            {
                st = new VideoDepthFollowState { instanceId = instance.GetInstanceID(), createdFrame = frame };
                videoDepthFollowByTrack[target.trackId] = st;
            }

            st.hasMeasure = true;
            st.measureFrame = frame;
            st.measureRaw = raw;
            st.measureAtEdge = atEdge;
            // 大きさをゆっくり変える形では、その場でも大きさを止めない（上限の速さで追う。奥行きは ⑧ が止めた奥行きに置く）。
            st.measureStill = still && !slow;
            st.measureSlow = slow;
            st.measureFeetSize = feetSize;
            if (feetSize)
            {
                CommitVideoDepthFollowFeetSize(st, feetTick, frame);
            }
            st.measureCeiling = ceiling8;
            st.measureZv = zv;
            st.measureMatchZ = match;
        }

        if (st != null && hold != null)
        {
            st.holdTicks++;
            st.lastHold = hold;
        }

        if (!logDue)
        {
            return;
        }

        // [VFOLLOW]: fv = この tick の配置に掛かっている f、fRaw = 測り（hold のときは na）、matchOverZv = 合わせる点の深さ ÷ Zv（1 なら映像の奥行き、
        // 1 未満は映像より手前）、matchZ1 = f を外した合わせる点の深さ（V なしの今の置き方に当たる）、r8 = ⑧ の平滑した比、cl8 / fl8 = ⑧ が下限・上限で止まった /
        // そのうち下限、edge = 箱が画面の端、popoutZ = S と V を掛ける前の anchor の深さ、fLock = このロックの最初の f。
        if (eye == Vector3.zero && camRotation == Quaternion.identity)
        {
            TryGetPinholeBasis(screen, out eye, out camRotation);
        }

        float kS = ResolveStereoDepthFactor(target.trackId);
        float popout = stereoDepthPopoutThisTick.TryGetValue(target.trackId, out float popoutS) ? popoutS : target.anchorZ / Mathf.Max(0.0001f, fApplied);
        float nearestZ = TryResolveNearestProjectionBoneDepth(instance, eye, camRotation, out float nearest, out _) ? nearest : -1f;
        float r8 = smoothedProjectedDepthRatioByTrack.TryGetValue(target.trackId, out float r8Value) ? r8Value : -1f;
        string zvText = zv > 0f ? zv.ToString("F4") : "na";
        string matchText = match > 0f ? match.ToString("F4") : "na";
        string overText = zv > 0f && match > 0f ? (match / zv).ToString("F4") : "na";
        string match1Text = match > 0f ? (match / Mathf.Max(0.0001f, fApplied)).ToString("F4") : "na";
        lockedModelLocalScaleByTrack.TryGetValue(target.trackId, out Vector3 locked);
        Debug.Log($"[VFOLLOW] tick={Time.frameCount} f={frame} shot={lastAppliedShotIndex} track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} " +
                  $"hold={hold ?? "-"}{(hold == "contract" ? ":" + contract : "")} fv={fApplied:F4} fRaw={(hold == null ? raw.ToString("F4") : "na")}{(rawClamped ? "(clamped)" : "")} " +
                  $"fLock={(st != null && st.engaged ? st.fLock.ToString("F4") : "na")} kS={kS:F4} total={kS * fApplied:F4} " +
                  $"z01={target.anchorZ01:F4} Zv={zvText} matchZ={matchText} matchOverZv={overText} matchZ1={match1Text} mode={used} " +
                  $"popoutZ={popout:F4} anchorZ={target.anchorZ:F4} nearestZ={nearestZ:F4} r8={r8:F4} " +
                  $"e8={(refined8 ? 1 : 0)} cl8={(clamped8 ? 1 : 0)} fl8={(floored8 ? 1 : 0)} d8Mm={(refined8 ? (move8 * 1000f).ToString("+0.0;-0.0") : "na")} " +
                  $"edge={(atEdge ? 1 : 0)} still={(still ? 1 : 0)} slow={(slow ? 1 : 0)} rel={ResolveVideoDepthFollowReleaseAgeForLog(target)} edgeMode={placementFollowVideoDepthAtEdge} " +
                  $"scale={instance.transform.localScale.x:F5} lock={locked.x:F5} " +
                  $"engaged={(st != null && st.engaged ? 1 : 0)} holdTicks={(st != null ? st.holdTicks : 0)}" +
                  (TryGetVideoDepthFollowFeetZ01(target, out float feetZ01Log) ? $" z01Feet={feetZ01Log:F4}" : "") +
                  (feetSize ? $" feetSize=1 boxRaw={boxRawForLog:F4} cNow={feetTick.cNow:F6} cBar={feetTick.cBar:F6} boxHExp={feetTick.boxH:F1} " +
                              $"boxH={target.bboxH} elig={(feetTick.eligible ? 1 : 0)} stop={(feetTick.stop ? 1 : 0)} seg={feetTick.segment}" : ""));
    }
    // 合わせる先の奥行きが使えるか。画面の面なら映像の契約は要らない（契約の名前は記録に出すだけ）。
    private bool IsPlacementFollowDepthAvailable(out string contract)
    {
        bool ok = IsStereoDepthContractAvailable(out contract);
        return ok || placementFollowVideoDepthTarget == VideoDepthFollowTarget.ScreenPlane;
    }

    // 合わせる先の奥行き（m、目から）。画面の面は screenDistanceMeters（ピンホールの原点から画面までの距離、⑧ の上限と同じ D）。
    // placementFollowVideoDepthFromFeet（2026-10-27、10/28 から既定 ON、VideoDepthFollowFeet.partial.cs）: 動物は z01 の代わりに足元の行から決めた z01 を使う
    // （その場の奥行きの止め ResolveVideoDepthFollowStillTargetDepth もこの値を控える）。目印の深さそのもの（anchorZ01・anchorZ）は変えない。
    private bool TryResolvePlacementFollowTargetDepth(MetaObj target, bool isAnimal, out float depth)
    {
        if (placementFollowVideoDepthTarget == VideoDepthFollowTarget.ScreenPlane)
        {
            depth = Mathf.Max(0.05f, screenDistanceMeters);
            return true;
        }

        float z01 = TryGetVideoDepthFollowFeetZ01(target, out float feetZ01) ? feetZ01 : target.anchorZ01;
        return TryResolveVideoDepthMeters(z01, isAnimal, out depth, out _, out _, out _);
    }

    // Hold（placementFollowVideoDepthAtEdge）: ⑧（RefineDepthFromProjectedBones）が箱の比の前に呼ぶ。このロックで f を測り始めた track の箱が画面の端に
    // 掛かった tick は、合わせる点を合わせる先の奥行きに置く比（基準点の深さに掛ける倍率）を返す。⑧ の移動は基準点の光線に沿った平行移動なので、
    // どの点の深さも同じだけ動く: 目標の基準点の深さ = 基準点の深さ + (合わせる先 − 合わせる点の深さ)（A の TryResolveStereoFollowRatio と同じ）。
    // 測りの前・絶対の目標へ向ける経路で使わないロック・インスタンスの作り直し・合わせる先が読めない tick は false（⑧ は箱の比のまま）。
    // その場（placementFollowVideoDepthHoldWhileFeetStill、VideoDepthFollowStill.partial.cs）のフレームも同じ比を使う（端の扱いの設定に依らない）。
    // placementFollowVideoDepthStillHoldsDepth なら、その場の間の合わせる先は、その場に入った tick の奥行きのまま。
    private bool TryResolveVideoDepthFollowEdgeHoldRatio(GameObject instance, MetaObj obj, Transform screen, out float ratio)
    {
        ratio = 1f;
        bool still = IsVideoDepthFollowStillFrame(obj);
        bool edgeHold = placementFollowVideoDepthAtEdge == VideoDepthFollowEdgeMode.Hold && IsVideoDepthFollowBoxAtEdge(obj);
        // 大きさをゆっくり変える形の track は、いつもこの比を使う（大きさは f が上限の速さで追い、奥行きは合わせる先に置く）。
        bool slow = IsVideoDepthFollowSlowSizeTrack(obj);
        if (!placementFollowVideoDepth || !(edgeHold || still || slow) || instance == null ||
            !IsStereoDepthSkeletonTrack(obj.categoryId) ||
            !videoDepthFollowByTrack.TryGetValue(obj.trackId, out VideoDepthFollowState st) ||
            !st.engaged || st.disabledForLock || st.pendingDisable || st.instanceId != instance.GetInstanceID() ||
            !lockedModelLocalScaleByTrack.ContainsKey(obj.trackId) ||
            !IsPlacementFollowDepthAvailable(out _) ||
            !TryResolvePlacementFollowTargetDepth(obj, IsCategoryAnimal(obj.categoryId), out float targetDepth) ||
            !TryGetPinholeBasis(screen, out Vector3 eye, out Quaternion camRotation))
        {
            if (!still)
            {
                ForgetVideoDepthFollowStillHeldDepth(obj.trackId);
            }

            return false;
        }

        targetDepth = ResolveVideoDepthFollowStillTargetDepth(obj, still, targetDepth);
        // 縮める向きの遊び（placementFollowVideoDepthFeetSizeShrinkBand、10/28 夜から既定 0.15、VideoDepthFollowFeet.partial.cs）: f を目標より大きいまま止めている分
        // （f ÷ 目標、1 + 帯まで）だけ合わせる先を奥へずらす。⑧ は合わせる点をここへ置くので、片目の絵は箱のまま、立体の大きさは変えずにモデルが下がる
        // （これが無いと、同じ奥行きのまま絵の中で f ÷ 目標だけ大きくなった。2026-10-28 夕の 1 回目の撮影 sizefit/runs_fix/*_band）。
        if (placementFollowVideoDepthFeetSizeShrinkBand > 0f && st.measureFeetSize && st.measureRaw > 0f && st.f > st.measureRaw)
        {
            targetDepth *= Mathf.Min(st.f / st.measureRaw, 1f + placementFollowVideoDepthFeetSizeShrinkBand);
        }

        float bodyZ = (Quaternion.Inverse(camRotation) * (ResolveDepthReferenceWorld(instance) - eye)).z;
        StereoDepthMatchPoint used = stereoDepthMatchPoint;
        if (bodyZ <= 0.0001f || !TryResolveStereoMatchDepth(instance, eye, camRotation, ref used, out float matchRaw, out float pivotZ))
        {
            return false;
        }

        // 手動倍率の track は測り（MeasurePlacementFollowVideoDepth）と同じく、配置の中心の骨のまわりの拡大を割り戻した点を合わせる。
        float manual = Mathf.Max(0.01f, EvaluateManualScaleForFrame(obj.trackId, GetCurrentPlaybackFrame()));
        bool manualScaled = Mathf.Abs(manual - ManualScaleDefault) > 0.001f;
        float match = manualScaled && used != StereoDepthMatchPoint.Pivot ? pivotZ + (matchRaw - pivotZ) / manual : matchRaw;
        ratio = (bodyZ + (targetDepth - match)) / bodyZ;
        return IsUsableStereoDepthFactor(ratio);
    }
}
