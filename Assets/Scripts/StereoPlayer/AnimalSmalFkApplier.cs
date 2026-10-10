using System.Collections.Generic;
using UnityEngine;

public sealed partial class AnimalPoseApplier
{
    // SMAL parent joint index for joints 0-34.
    // Root (0) = -1. Virtual spine chain: 1-6 (no Unity bone). Front legs: 7-10, 11-14 (parent=6).
    // Neck/Head: 15-16 (parent=6). Rear legs: 17-20 (parent=0), 21-24 (parent=0).
    // Tail: 25-31 (parent=0 for 25, chain after). Mouth/Ears: 32-34 (parent=16).
    private static readonly int[] SmalJointParentArray =
    {
        -1,              // 0 root
        0, 1, 2, 3, 4, 5, // 1-6  pelvis0..spine3 (BONE MISSING)
        6, 7, 8, 9,      // 7-10  LLeg1..LFoot
        6, 11, 12, 13,   // 11-14 RLeg1..RFoot
        6, 15,           // 15-16 Neck, Head
        0, 17, 18, 19,   // 17-20 LLegBack1..LFootBack
        0, 21, 22, 23,   // 21-24 RLegBack1..RFootBack
        0, 25, 26, 27, 28, 29, 30, // 25-31 Tail1..7
        16, 16, 16,      // 32-34 Mouth, LEar, REar
    };

    // Topological order: parents always before children. Since all parent indices < child indices, order is 1..34.
    private static readonly int[] SmalJointTopologicalOrder =
    {
        1,  2,  3,  4,  5,  6,          // virtual spine chain
        7,  8,  9,  10,                  // front left
        11, 12, 13, 14,                  // front right
        15, 16,                          // neck, head
        17, 18, 19, 20,                  // rear left
        21, 22, 23, 24,                  // rear right
        25, 26, 27, 28, 29, 30, 31,      // tail
        32, 33, 34,                      // mouth, ears (BONE MISSING in first impl)
    };

    // Rest-pose bone direction (this joint -> its kinematic child), in SMAL's own native
    // coordinate frame, extracted from the SMAL rest skeleton J array
    // (Docs/smal-rest-skeleton.json, third_party/AniMer/data/smal/my_smpl_00781_4_all.pkl).
    // Used to compute a per-joint geometric correction directly from real rest geometry
    // (SMAL rest direction vs. the Unity dog rig's own actual bind direction for the same
    // joint), instead of reusing the single global canonicalCorrection - whose roll/twist
    // component was only ever constrained by a single nose-direction check and turned out
    // to be unreliable when reused for per-joint local rotations (2026-06-18, see
    // Docs/adr/0001-animal-smal-fk.md). Joints without an entry here keep using the
    // previous parentTW*localPose*bindLoc path - this covers the joints that also have a
    // registered Unity aim-child (RegisterAnimalAimPairs), which is what we need for an
    // independent, geometry-grounded Unity-side rest direction to compare against.
    // 測定 B 用。true にすると body_pose の曲げを当てず、bind pose を globalOrient で
    // 回しただけの姿勢になる。診断専用（詳細は下の bendUnity のところ）。
    public bool disableSmalBendForDiag;

    // 親の曲げを子へ積むか（2026-09-10）。既定 OFF。
    //
    // OFF（従来）: tw[joint] = bendUnity * (worldFk0 * boneBindWorld)。
    // **親がその時点で取っている姿勢が入らない**ので、各ボーンは bind pose から
    // 自分のローカル曲げぶんだけずれた向きに留まる。伏せる・座るのように
    // 胴と四肢が合成されて成立する姿勢は原理的に作れない。
    // 測定 B（-noBend true）で body_pose を丸ごと切っても [ANIMALKP] が
    // 中央 -4.0 度しか変わらないことが実測で確認された。
    //
    // ON: 「rest からのずれ」を親から積む。restWorldRot 自体は変えないので、
    // ADR-0002 が避けた「仮想 SMAL 親と実 Unity 親の食い違い」は再導入しない。
    // 詳細は Docs/smpl-retargeting.md「Animal の FK は親の姿勢を積んでいない」。
    public bool accumulateSmalParentBend = true;

    // SMAL body_pose の平滑の半減期（秒）。0 で平滑なし。既定 0.12 は 2026-06-16 の雑音対策の値。
    // プレイヤーの同名フィールドが毎フレーム代入するので、変えるならプレイヤー側を変える。
    public float smalSmoothHalfLifeSec = 0.12f;
    // body_pose（関節 1〜34）だけの平滑の半減期（秒）。負なら smalSmoothHalfLifeSec と同じ（従来）。
    // 根（worldFk0）は smalSmoothHalfLifeSec のまま。体に対する脚の形の改善は body_pose の平滑だけで決まり
    // （根の平滑 0 は体全体の向きを 10〜14° 変えるだけ）、2026-10-02 の A/B で確かめた。
    public float smalBodyPoseSmoothHalfLifeSec = -1f;

    // 向きの切り分け用（2026-09-10）。0 = 自動判定、1 = 常に 180 度、-1 = 常に 0 度。
    // rootYawFix は kpForward との内積で 0/180 を一度だけ決めて固定するので、
    // その判定が正しいかを外から確かめる口が無かった。
    public int forceRootYawFix;

    // 頭（joint 16）を連鎖から外すか。既定 true。
    //
    // 頭だけ Unity 側の rest 方向が **aim child ではなくメッシュ重心へのフォールバック**
    // （AnimalSmalFkApplier.SmalRestDirByJoint の 16 のコメント）。frame map が他より
    // 不確かなので、連鎖で入力を大きくすると誤差もそのぶん増える。実測（2026-09-10、
    // [ANIMALKP] の Head 中央値）:
    //   従来 39度 / 2軸のみ 41度 / 連鎖+2軸 53度（全編）
    //   27-30秒では 105度 / 97度 / 135度
    // 四肢の改善は 2 軸だけで出ており、連鎖は頭にだけ効いて悪化させていた。
    // **既定 false（2026-09-11 再変更）。**上の悪化は「照準が未当てはめ・2 軸基底（耳）併用」
    // という別構成での話。照準とロールを当てはめた今は、外していると**むしろ致命的**:
    // 頭の振り（体軸と鼻のなす角）は 27-30 秒で 68 度あり、これは首・背骨に乗っているので
    // ローカル回転だけの頭には届かない。しかも当てはめ自体が連鎖（Aacc）を前提に解いてある。
    // 実測（[HEADAIM] と keypoints3d の 頭18 → 鼻24 の角度差、27-30 秒の中央値）:
    //   00_Dog        95.9度 → 44.3度（当てはめのみ）→ **11.3度**（当てはめ+連鎖）
    //   36_Labrador   90.6度 → 45.1度（当てはめのみ）→ **6.5度**（当てはめ+連鎖）
    // 四肢（[ANIMALKP] の Paw 除外平均）は 23.0→23.1 度・42.7→42.8 度で変化なし。
    public bool excludeHeadFromChain;

    // 頭の照準を「モデルが rest で向いている方向」に揃えるか。既定 true。
    //
    // 従来は SMAL 側が「頭 → 口」（joint 16 → 32）、Unity 側が
    // 「頭ボーン → 頭メッシュの重心」（aim child が無いためのフォールバック）で、
    // **別のものを対応づけていた**。jointFrameMap はこのずれを rest では吸収するが、
    // 曲げの平面がずれるので**振れ幅に比例して誤差が出る**。実測で 27-30 秒の頭は
    // rest から 66.5 度振れており、そこで誤差が最大になっていた。
    //
    // ON のときは両側を「体の前方」で揃える:
    //   SMAL 側  = +X（rest skeleton の軸: +X が尾から頭へ）
    //   Unity 側 = worldFk0 * modelForwardLocal
    //              （コード内の証明 visual_forward = rawWorldFk0 * modelFwdLocal と同じ）
    // 副軸は前肢（7）にする。首（15）は +X と 6.6 度しか離れておらず縮退するため。
    // **既定 true（2026-09-11 再変更）。**上の「モデルの前方」案は不採用のままで、
    // 現在このフラグが入口にしているのは**当てはめ表**（Assets/Resources/animal_head_fit.json）:
    //   - 照準 bindDirLocal … 頭メッシュの重心ではなく、当てはめた向き
    //   - ロール θ         … M = FromToRotation が拘束しない 1 自由度を埋める
    //   - 頭は jointFrameMap を FromToRotation に固定（2 軸基底と併用すると二重補正）
    // 表に無いモデルは従来どおり。excludeHeadFromChain = false と**必ずセット**で使う
    // （当てはめが連鎖前提で解かれているため）。詳細は Docs/smpl-retargeting.md。
    public bool headAimFromModelForward = true;

    // 頭だけ per-joint の FromToRotation をやめ、**体レベルのフレーム写像**で解く。
    // 既定 true（2026-09-11）。
    //
    // 理由: `M = FromToRotation(smalRestDir, unityRestDirWorld)` は方向 1 本しか拘束せず、
    // **その軸まわりのロールが任意**。`bendUnity = M * bendSmal * M^-1` は曲げの回転軸を
    // `M * n` に写すので、ロールがずれると**曲げる平面が回る**。四肢は隣の骨で
    // 副軸を作れる（2 軸版）が、**頭には Unity 側に信頼できる副軸が無い**
    // （耳を試しても変化なし。Labrador は主軸すら gotDir=false のフォールバック）。
    //
    // worldFk0 = G * S（S = rootYawFix * modelOrientFix、G = instanceRootYaw * camRot *
    // globalOrient * SmalDataAxisCorrection）なので、SMAL の体フレームから world への
    // 写像は G = worldFk0 * S^-1。累積回転 A を world へ共役すると
    //     tw = (G * A * G^-1) * (worldFk0 * boneBindWorld) = worldFk0 * S^-1 * A * S * boneBindWorld
    // **この式にロールの自由度は無い。**A = identity のとき restWorldRot に一致する。
    // **既定 false（2026-09-11 実測で悪化）。**相関が +0.365 -> -0.158（00_Dog）、
    // +0.169 -> -0.113（Labrador）。つまり**頭ボーンの rest 軸は SMAL の頭関節の軸と
    // 体レベルの写像では対応しない**。診断としては重要な否定的結果。
    public bool headUseBodyFrameMap;

    // 既定 OFF（2026-10-04、調査役 AM の B / C / A2）。プレイヤーの同名フィールドが毎フレーム代入する。
    // bodyFrameNeckHead: 首（15）と頭（16）を、根と同じ「体の写像」で共役する（B）。
    //   根の写像は鏡映 D_x を含む（globalOrient を行 1 反転で読むので Q = D_y·R·D_x）。SMAL の回転 X は
    //   mirX(X) = (x, −y, −z, w) にしてから K = SmalDataAxisCorrection·rootYawFix·modelOrientFix で共役する:
    //     tw = worldFk0 · K⁻¹ · mirX(X) · K · boneBindWorld（X: 首は振り FromTo(s, A·s)、頭は連鎖の回転 A[16] そのもの）
    //   今の首は jointFrameMap を world の FromTo で作っていて、体の向きで写像が回る（AM の D1）。
    //   2026-09-11 の headUseBodyFrameMap（悪化）は K に SmalDataAxisCorrection が無く鏡映も無かった（全軸で 89〜90° ずれ、AM の am18）。
    // bodyFrameKeepFittedHead: B のうち、当てはめ表にあるモデル（00_Dog・Labrador）の頭は今のまま（B′）。
    // bodyFrameLimbs: 四肢と尾（7, 8, 11, 12, 17, 18, 21, 22, 25, 26）も同じ写像で（C）。今の 2 軸 jointFrameMap は正則な回転で、
    //   鏡映を含む根と組むと左右が反転して写る（AM の D3）。過去に否定された S 共役（2026-09-10）の再提案なので、採否は絵で。
    // passiveBoneUnityParent: 受け身の骨（肉球・つま先・尾の先）を SMAL の親関節の tw ではなく実際の Unity の親に付ける（A2）。
    public bool bodyFrameNeckHead;
    public bool bodyFrameKeepFittedHead;
    public bool bodyFrameLimbs;
    // bodyFrameLimbs のうち前肢（7, 8, 11, 12）と尾（25, 26）だけに掛ける（反論役 C-AM: 後肢の変化は別に判断してもらう）。
    public bool bodyFrameLimbsFrontAndTailOnly;
    // 後肢（17, 18, 21, 22）だけを体の写像で（2026-10-07、実機の再確認「40 秒からの猫の後ろ脚が交差しているように感じる」）。
    // 今の 2 軸 jointFrameMap は鏡映を含む根と組むと脚の横の成分が反転し、データで外へ開いた後ろ脚がモデルでは内へ入って交差していた
    // （Lynx shot 20 で 480 tick 中 437、データは 0。10/04 の脚の割り当ての表で前後の反転を直してから見えるようになった）。
    // 写像は bodyFrameLimbs（C）の後肢と同じ。前肢と尾は今の写像のまま（前肢の左右の反転は F2 の約束どおり残る）。
    public bool bodyFrameRearLimbs;
    // 脚の残り（2026-10-08、全関節の監査 J-05・J-06・J-08・J-20。bool はすべて既定 OFF。プレイヤーの同名フィールドが毎フレーム代入する）。
    // bodyFrameFrontLimbs: 前肢（7, 8, 11, 12）だけを体の写像で（bodyFrameJoint。後肢の bodyFrameRearLimbs と同じ式。既存の欠陥の直し、J-06）。
    //   F2 の 2 軸の写像は鏡映を持たない回転なので、鏡映を含む根と組むと前脚の横の成分が左右反転して写る（監査で横の相関 −0.93〜−1.00。
    //   Labrador の横倒しの伏せ f341-410 で右前腕の横の角（体の左が +）の中央値がモデル −25.9°、データ +34.7°、体の写像で +24.8°（残りは bind の横のずれ）。
    //   右手根は p50 145 px 動く。数値は監査の移植 audit_joints/AL と I2 の predict_I2.py）。bodyFrameLimbs でも前脚は同じになるが
    //   尾 25/26 まで変わるので分けた。F2 の対象外のモデル（首を副軸にした 2 軸の写像、これも鏡映）にも同じ式が掛かる
    //   （対象外だった 16_Deer1 は 2026-10-09 に背骨の役を直して F2 に入れた。対象外は今は無い）。
    // smalDriveCarpusHock: 手根・飛節（9, 13, 19, 23。paw 役の骨 = 中手・中足）の body_pose を写す（新しい振る舞い、係数なし、J-08）。今は rest の向きが無く
    //   受け身（前腕・下腿に bind の角で剛体で付くだけ）で、関節角の振れはデータ 23〜59° に対しモデル 0.2〜3.4°。rest の向きは SmalCarpusHockRestDir に置く
    //   （SmalRestDirByJoint に足すと既定の経路と方向の転写まで変わる）。写像は親の関節と同じ: 9/13 は前腕 8/12 と同じ（F2。bodyFrameFrontLimbs か
    //   bodyFrameLimbs が ON なら体の写像）、19/23 は下腿 18/22 と同じ（bodyFrameRearLimbs が既定 ON なので体の写像）。IsSmalLegBodyFrameExtra。
    //   Roll 骨のリグ（Beaver・Fox。paw 役が前腕・下腿の骨幹の途中）は除外する（SmalDistalLegExcludedModels）。
    // smalDriveFeet: 前足 10/14（paw 役のただ 1 つの子 = Labrador・Lynx の front_x_paw）と後足 20/24（toe 役）を写す（新しい振る舞い、J-20）。
    //   葉の関節で rest の向きが無いので、B の頭と同じく積んだ回転全体を体の写像で: tw = worldFk0·K⁻¹·mirX(smalAccum[j])·K·bindWorld
    //   （bind から作り、今の回転は読まない）。smalDriveCarpusHock と組で使う（単独だと中手・中足が受け身のまま、手根・飛節の曲げも指の骨に乗る）。
    // smalAbsoluteDirectionLegsOnly: 方向の転写（smalAbsoluteDirection の式）を脚 7/8/11/12/17/18/21/22 だけに掛ける（新しい振る舞い、式は既存、J-05）。
    //   尾 25/26 と首・頭は変えない。smalDriveCarpusHock と同時なら 9/13/19/23 も絶対の向きにする（IsSmalAbsoluteDirectionLegJoint）。
    //   方向の転写が掛かる関節では体の写像の切り替え（bodyFrame*）は効かない（smalAbsoluteDirection と同じ優先順）。
    // 非四足モード（smalNonQuadrupedRig、2026-10-08、2026-10-09 に既定 ON）の前肢の無いリグ（鳥、cache.smalNoFrontLimbs）には、上の smalDriveCarpusHock・smalDriveFeet は効かない:
    //   脛 19/23 は smalNonQuadrupedHock だけで手根・飛節と同じ扱い（rest の向き・親と同じ写像・smalAbsoluteDirectionLegsOnly の絶対の向き。IsSmalCarpusHockDriven）、
    //   前足・指は受け身のまま（IsSmalFootParentOnBodyFrame）。前肢のあるリグ（四足・カンガルー）は上のとおり。
    public bool bodyFrameFrontLimbs;
    public bool smalDriveCarpusHock;
    public bool smalDriveFeet;
    public bool smalAbsoluteDirectionLegsOnly;
    // イベントの「既定の姿勢へ戻す」重み（2026-10-07、ユーザーの要望「human みたいにデフォルトのポーズに戻してからやる方がいい」）。
    // 0 = SMAL の入力のまま、1 = 既定の姿勢（body_pose = 恒等 → prefab の bind の立ち姿、根は鼻の水平の向きを保った上下まわりの回転だけ）。
    // Apply が要求ごとに代入する（追従の要求は常に 0）。混ぜるのは平滑の前の入力なので、切り替わりは今の SMAL の平滑を通る。
    public float smalDefaultPoseWeight;
    public bool passiveBoneUnityParent;
    // 首の中間の骨に首の回転を配る（2026-10-04、反論役 C-DM の U-c、既定 OFF）。ApplySmalNeckChain を参照。
    public bool smalDriveNeckChain;
    // 方向の転写（2026-10-04 第 3 ラウンド、調査役 FKQ の (c)、既定 OFF）。駆動する関節の骨の向きを、SMAL の FK の骨の向き
    // （smalAccum · s）そのものにする。写像は根と同じ物理的な写像（鏡映 D_x → K⁻¹ → worldFk0）で、bind の向きから最小回転で向ける。
    //   相対転写（今の方式・B・C）は bind の形を保つが、モデルの rest と SMAL の rest のずれ（Labrador で 10〜57°）がそのまま残る。
    //   (c) はそのずれを捨ててデータの向きに合わせる（元動画の 2D に対する誤差 今 53° / CB 36° / (c) 21°、Labrador・29 区間）。
    //   代わりに骨を bind から大きく回す（尾で最大 90〜114°）ので、メッシュのねじれは絵で確かめる（09-06 に bind の正規化で否定された帯と同種）。
    //   首と頭は「当てはめ済みの頭 ＋ 首の中間の骨がある ＋ smalDriveNeckChain」のときだけ（首→頭の局所の折れを鎖で配るため）。
    // smalAbsoluteDirectionHeadTailOnly: (c) を首・頭（上の条件つき）と尾の付け根・中ほど（25・26）だけに絞る。
    public bool smalAbsoluteDirection;
    public bool smalAbsoluteDirectionHeadTailOnly;
    // 尾の鎖（2026-10-07、担当 B「猫の尾が元動画のように見えて動く」、bool は既定 OFF。プレイヤーの同名フィールドが毎フレーム代入する）。
    // smalTailFullChain: 尾の付け根（tailBase）から先（tailTip）までの Unity の骨すべてを、SMAL の尾の関節 25〜31 の折れ線（rest の骨格を
    //   smalAccum で曲げたもの）の同じ正規化した弧長の区間の向きに合わせる（新しい振る舞い、係数なし）。写像は方向の転写（:782-789）と同じ
    //   （鏡映 D_x → K⁻¹ → worldFk0）。今の写し方は 25・26 の 2 本だけで、モデルの尾の先の区間（Lynx で尾の 6 割）を関節 26 で動かし 27〜31 を捨てていた。
    //   2 軸の写像は鏡映を持たない回転なので左右も反転していた（SMAL の左 15° がモデルの右 19°、:147-148 の D3）。bind の形（Lynx の付け根の垂れ 32.4°、
    //   Tail03 の prefab の 74° の曲げ）は捨てる。今は誰も書かない中間の骨（Lynx の Tail03、Puma・Labrador の Tail02/03/05/06）も書く。
    //   tailTip が tailBase の子孫でないリグは今の経路のまま（CaptureSmalTailChainBind が鎖を控えない）。
    // smalTailFullChainModels: smalTailFullChain を掛けるモデル。prefab 名のカンマ区切りで、先頭の「数字_」は無視する（"Lynx" と "39_Lynx" は同じ）。
    //   載っていないモデルは今の写し方のまま（犬の尾を変えないため、既定は "Lynx" だけ）。
    // smalTailMatchSmalLength: 尾の骨の局所位置を「SMAL の尾の長さ ÷ 尾の付け根から頭（0.519）」とモデルの同じ比の比で伸び縮みさせる
    //   （新しい振る舞い。smalTailFullChain と組で、smalTailMatchSmalLengthModels に載ったモデルだけ。既定 "Lynx"）。倍率に上限は無い:
    //   Lynx 1.93、Labrador 0.99、Puma 0.73。尾の短いモデルは 41_Moose 10.9、Deer1.0・Elk1.0・Pronghorn1.0 9.4、Bear 7.1、Goat 6.7 倍になる。
    //   メッシュは骨の間で引き伸ばされる。局所位置は値が違うときだけ書く（入れたとき・切ったときの 1 回）。
    // bodyFrameTail: 尾の付け根・中ほど（25・26）だけ体の写像で（bodyFrameJoint、後肢の bodyFrameRearLimbs と同じ式）。比較用（写像の切り替え）。
    //   左右の向きは変わるが仰角は今のまま（Lynx shot 23 で方位 −7.4° → +0.4°、データ +7.0°。仰角 1.3° のまま、データ 21.6°）。全モデルに効く。
    public bool smalTailFullChain;
    public string smalTailFullChainModels = "Lynx";
    public bool smalTailMatchSmalLength;
    public string smalTailMatchSmalLengthModels = "Lynx";
    public bool bodyFrameTail;
    // smalTrunkChordFromSmal（J-09、新しい振る舞い・既定 OFF・係数なし。捨てているデータを写す）。
    //   SMAL の背骨（関節 1〜6）は骨を持たず（GetSmalBoneForJoint に無い）、smalAccum に入って首・頭・前脚の向きには届くが、胴の骨（cache.trunkChain = spine の子から
    //   肩甲帯まで。Labrador・Lynx は Spine〜Spine4）は誰も書かない（局所回転の広がり 0.000°、毎 tick bind に戻る）。胴の曲がりと、き甲・肩・首の付け根の位置が出ない
    //   （き甲 p50 22/22 px・p95 53/81 px、犬/猫。監査 AX-2 と反論役）。
    //   - 弦の振り D = FromTo(rest の弦 J6 − J0, smalAccum で曲げた弦)（SMAL の体の座標）。
    //   - 肩甲帯の手前の骨（Spine〜Spine3）: world = worldFk0·K⁻¹·mirX(D^w_k)·K·bindW。B（bodyFrameNeckHead）と同じ体の写像で鏡映 D_x を含むので、横の曲がりの
    //     符号が保たれる。w_k = 2 ×（その骨の区間の中点の弧長 ÷ 区間の全長）: 曲率が一様なら弦がちょうど D だけ回る（係数ではなく弧長で決まる）。
    //   - 肩甲帯（Spine4）: B の写像で A6（smalAccum[6] 全部）。前脚・首・頭は主ループが world の絶対値で書くので、向きは変わらず位置だけ付いてくる。
    //   - 必須の付随修正: 首の中間の骨（ApplySmalNeckChain）の Δ を今の肩甲帯から作る（根に剛体の姿勢のままだと A6 が二重に掛かる）。
    //   10/04 の調査役 AM の案 D（根→肩の線の振りを配る）の再提案。反論役 C-DM は「方向は正しいが効果は小さい（データの寄与の約半分）」と判定した
    //   （Docs/smpl-retargeting.md の 10/04 の節 :4889・:4949・:4967）。10/04 に否定された「A6 を胴に配る」とは違い、A6 は肩甲帯の向きにだけ使う
    //   （弦の上下と A6 のピッチの相関は犬 +0.25 で逆向き。A6 を配ると犬の弦が逆に動く。監査 ax_spine_sim）。
    //   予測（監査の移植 chordC と I3 の移植 impl_joints/I3/pred_i3.py）: き甲 p50 25 → 7 px（犬）・30 → 8 px（猫）、p95 55 → 23・81 → 38 px。
    //   猫が頭を下げる動きの不足（J-10）も単独のフラグは出さずこれで確かめる: き甲→頭の仰角の傾き 0.62 → 0.99、頭が低い上位 10% の誤差 +14.8° → −8.2°。
    //   ただし猫は全フレームで頭がデータより約 8° 低くなる（モデルの bind のき甲→頭 −0.1° と SMAL の rest +8.6° の差が、傾きが 1 になってそのまま残る。
    //   頭の位置 p50 24 → 32 px、上下 −9 → −26 px）。犬はき甲→頭の誤差 +25.9 → +26.5°（bind の首の差 J-04 は残る）。shot の先頭の倍率への影響は未予測。
    //   イベント中はジェスチャの胴の点（AnimalGesturePosePlayer.ApplyTrunk）が胴の骨を bind の上に書き直すので、その間は曲がりが置き換わる。
    //   body_pose が恒等なら D も A6 も恒等で bind に戻るので、イベントの既定の姿勢（smalDefaultPoseWeight）とはそのまま両立する。
    public bool smalTrunkChordFromSmal;
    // smalHeadNoseAim（J-04 (1)、新しい振る舞い・既定 OFF）: 頭の鼻（頭ローカルの cache.headNoseLocal、顔の骨から。ResolveHeadNoseLocal）を、SMAL の鼻の向き
    //   worldFk0·K⁻¹·D_x(smalAccum[16]·n_smal) へ最小回転で向ける（ロールは B のまま）。B は相対の転写なので、モデルの bind の鼻と SMAL の鼻の差が一定のまま残り、
    //   Labrador の鼻が頭→鼻で +15.9°・目→鼻で +21.9° 上を向く（10/04 に「B は Labrador の鼻を約 36° 上げる」と承知で採った差の今の量。監査 AX-4）。
    //   主ループ・首の鎖の後に掛け、イベントの既定の姿勢へ戻す間は (1 − smalDefaultPoseWeight) で弱める（重み 1 で B のまま = prefab の bind）。
    //   照準の回転はモデルごとにほぼ一定（I3 の移植で犬 20.8°・猫 6.1°、広がり ±0.1°。B が頭と目標を同じ A16 で回すため）なので、実質は頭の bind の向きを
    //   SMAL の鼻へ一度だけ回し直すのと同じで、否定リストの「bind を共通基準へ揃える／絶対姿勢化」の系統（監査 AX-4 も同じ扱い）。採否は絵で。
    //   予測（I3 の移植）: 犬の頭→鼻（左右の鼻の骨の中点）と keypoints kp18→kp24 の仰角の差 +15.9° → −4.7°、目→鼻 +21.9° → +1.4°（runtime の鼻は
    //   NoseBridge・UpperNose を含む 6 本の重心で鼻の穴より上を指すので、頭→鼻の定義では 4.7° 下へ回り過ぎる）。猫 −6.8° → −0.9°（猫の差は基準点の不確かさの範囲内）。
    //   副作用: 頭が首に対して下へ折れる（犬で一定の 20.8°）。
    public bool smalHeadNoseAim;
    // smalNeckFullChain + smalNeckFullChainModels（J-04 (2)、新しい振る舞い・既定 OFF・名簿は既定 空）: 首の骨（肩甲帯の子の Neck01 → Neck02 → neck、終点は頭）を、
    //   SMAL の首の折れ線 6→15→16（rest の骨格を smalAccum で曲げたもの）の同じ正規化した弧長の区間の向きに合わせる。尾の鎖（smalTailFullChain）と同じ仕組みで、
    //   骨を書く本体（WriteSmalPolylineChain）を共有し、写像も同じ（鏡映 D_x → K⁻¹ → worldFk0）。肩甲帯 → Neck01 の区間は剛体のまま（弧長には入る）。
    //   頭の向きは B のまま（鎖を書いた後に tw[16] で書き直す）。bind の首の形（Labrador の首の弦の仰角 +47.4°）は捨てる（否定リストの「絶対姿勢化」の系統。尾の鎖と同じ）。
    //   **犬（LabradorDog）だけに使う想定で、猫には入れない**: 背中の線を基準にすると行き過ぎる（き甲→頭 犬 +25.9 → −8.6°、猫 −2.6 → −22.7°。監査の反論役 vfix_sim）。
    //   副作用: 頭が首に対して反る（犬 1101 フレーム中 560、smalHeadNoseAim と組で 198）、喉のメッシュの変形、shot の先頭の倍率（最上の骨が頭でなくなり、
    //   犬の多くの shot で ×1.08〜1.35、一次近似）。smalTrunkChordFromSmal と組むと犬の頭は根→頭 −3.0°・腰→頭 −1.6°（首の鎖だけなら −5.7°・−4.2°、I3 の移植）。
    //   名簿は smalTailFullChainModels と同じ書き方（カンマ区切りの prefab 名、先頭の「数字_」は無視）。
    //   イベントの既定の姿勢へ戻す間は、尾の鎖と同じく各骨の world を今の写し方の結果へ混ぜる（J-23）。
    public bool smalNeckFullChain;
    public string smalNeckFullChainModels = "";
    // smalDriveJaw（J-21、新しい振る舞い・既定 OFF。捨てているデータを写す）: 頭の子で名前に jaw を含む骨（Labrador の Jaw、Lynx の fJaw。上顎（UpperJaw など、
    //   名前に upper）と末端（_end）は除く）を SMAL の口（関節 32）で回す。B と同じ体の写像 world = worldFk0·K⁻¹·mirX(smalAccum[32])·K·bindW[顎]（2 軸の写像だと
    //   左右が反転する）で、頭の後に書く。頭は B のあと鼻の照準などでも書かれるので、実装は「頭の今の world ×（頭に対する口の局所回転を同じ写像で頭の bind の座標へ）×
    //   顎の bind の局所」（頭が B のままなら上の式と同じ値で、顎が頭から外れない）。今は骨があるのに捨てている（顎の局所回転 0.000°、データ p95 口 14.3°/23.5°）。
    //   予測（監査の移植 ax_face）: 顎の先 p95 24 px（犬）・46 px（猫）。耳は入れない（犬の耳の p50 27° は垂れ耳と SMAL の耳の形の差の疑い）。body_pose が恒等なら bind。
    public bool smalDriveJaw;
    // F2（2026-10-04、MAP。プレイヤーの animalFrontLimbBodyLateralSecondary が毎フレーム代入）: 前肢 7, 8, 11, 12 の 2 軸写像の副軸を
    // 首から体の横へ。2 軸の写像は鏡映を持たない回転なので、SMAL の体の左（rest 骨格で左の脚の y > 0）と Unity の体の「右」を組むと
    // 前後・上下が保たれ、左右は今の首の副軸と同じく鏡映になる（今の約束を変えない）。
    public bool frontLimbBodyLateralSecondary;
    private static readonly Vector3 SmalBodyLeft = new Vector3(0f, 1f, 0f);

    // jointFrameMap をロールまで拘束した 2 軸版で作る（2026-08-28）。
    // 詳細は jointFrameMap を組んでいるところのコメント。
    public bool useTwoAxisJointFrameMap = true;

    // 頭（SMAL joint 16）に body_pose を当てるか。**既定 ON。**
    // false にすると 2026-09-06 以前の挙動（頭は首に付いて動くだけ）に戻る。A/B 用。
    public bool enableAnimalHeadPose = true;

    private static readonly Dictionary<int, int> SmalRollRefJoint = new Dictionary<int, int>
    {
        // **前肢の副軸は首（15）を使う。**（2026-09-10）
        // 肩と肘を互いの副軸にしていたが、rest 方向のなす角が **5.4 度**しかなく
        // 基底が縮退してロールが決まらない。後肢（32.6 度）で 2 軸が効いて
        // 前肢で効かなかったのはこれ。首の rest 方向（体の前後軸）とは 83 度あり、
        // 十分に条件が良い。実測: 27-30 秒で LFLo が rest から 45 度振れており、
        // 振れ幅が大きい区間ほど誤差が出ていた（Docs/smpl-retargeting.md）。
        { 7, 15 }, { 8, 15 },    // 前肢 左: 副軸 = 首
        { 11, 15 }, { 12, 15 },  // 前肢 右: 副軸 = 首
        { 17, 18 }, { 18, 17 },  // 後肢 左: 股 <-> 膝
        { 21, 22 }, { 22, 21 },  // 後肢 右
        { 25, 26 }, { 26, 25 },  // 尾
        // 頭は首を副軸にする。頭単体では「どちらが上か」が決まらず、
        // 首振りが頷きに化けうる（FromToRotation はロールを拘束しない）。
        { 16, 15 },
        // 手根・飛節（smalDriveCarpusHock、2026-10-08）。rest の向きが SmalCarpusHockRestDir にしか無いので、フラグ OFF ではこの 4 行に来ない。
        // 非四足モードの前肢の無いリグ（鳥、2026-10-08）は smalDriveCarpusHock でなく smalNonQuadrupedHock のときだけ 19/23 の行に来る（9/13 には来ない）。
        // 2 軸の写像に落ちるのは親の関節も 2 軸のとき（前は F2 が使えないモデル = FrontLimbBodyLateralExcluded（2026-10-09 から空）、後ろは bodyFrameRearLimbs を切ったとき）。親と同じ副軸にする。
        { 9, 15 }, { 13, 15 },    // 手根: 前腕 8/12 と同じく首
        { 19, 18 }, { 23, 22 },  // 飛節: 下腿（SMAL の rest の向きで 22.8° 離れる）
        // **首（15）に副軸は入れない。**（2026-09-11 に撤回）
        // 2026-09-10 に `{ 15, 16 }` を入れたが、当時から数値も絵も変化が無く、
        // 頭の照準を +Y に直したあとは**むしろ悪化した**（頭の絶対差 42.3 度 → 79.3 度）。
        // 首の副軸は Unity 側で頭の照準を読むので、頭を変えると首まで動いてしまう。
        // 首は従来どおり FromToRotation にフォールバックする。
    };

    private static readonly Dictionary<int, Vector3> SmalRestDirByJoint = new Dictionary<int, Vector3>
    {
        { 7,  new Vector3(0.044701f, -0.095438f, -0.994431f) },  // LLeg1 -> LLeg2
        { 8,  new Vector3(0.006267f, -0.179885f, -0.983668f) },  // LLeg2 -> LLeg3
        { 11, new Vector3(0.044701f, 0.095438f, -0.994431f) },   // RLeg1 -> RLeg2
        { 12, new Vector3(0.006267f, 0.179885f, -0.983668f) },   // RLeg2 -> RLeg3
        { 15, new Vector3(0.993402f, -0.000000f, -0.114686f) },  // Neck -> Head
        // Head -> Mouth（joint 32）。2026-09-06 追加。
        //
        // **それまで頭は駆動されていなかった。**rest 方向が無いので
        // `tw = parentTW * bindLoc` の分岐に落ち、首に付いて動くだけで頭自身の回転は
        // 一切当たっていなかった。bind pose で頭が傾いている・下を向いているモデルは
        // 何を再生してもそのままだった。
        //
        // データには動きが入っている。body_pose が rest から回っている量の実測
        // （bundle_animal、40 秒、808 サンプル）: 首 31.6°、**頭 22.4°**（p90 32.0°、最大 49.2°）。
        // 首に次ぐ大きさを丸ごと捨てていた。
        //
        // Unity 側の rest 方向は `PrimeAnimalBind` が既に採取している
        // （頭ボーン → 頭のメッシュ重心。`TryGetBoneCenterDirectionWorld` のフォールバック）。
        // 他の joint のような aim-child は無いが、どちらも「頭の付け根から口元へ」を
        // 指す方向なので対応づけとして成立する。
        { 16, new Vector3(0.800236f, 0.000000f, -0.599685f) },   // Head -> Mouth
        { 17, new Vector3(0.337957f, 0.086988f, -0.937133f) },   // LLegBack1 -> LLegBack2
        { 18, new Vector3(-0.194889f, -0.083158f, -0.977294f) }, // LLegBack2 -> LLegBack3
        { 21, new Vector3(0.337957f, -0.086988f, -0.937133f) },  // RLegBack1 -> RLegBack2
        { 22, new Vector3(-0.194889f, 0.083158f, -0.977294f) },  // RLegBack2 -> RLegBack3
        // Tail1 -> Tail2, Tail2 -> Tail3 (Docs/smal-rest-skeleton.json, joints 25/26).
        // Tail3 (joint 27, tailTip) intentionally has no entry here - its own further SMAL
        // child (Tail4, joint 28) has no corresponding Unity bone in the canonical rig, so it
        // stays in the same passive parentTW*bindLoc fallback paws/toes already use.
        { 25, new Vector3(-0.992647f, 0.000000f, -0.121171f) },
        { 26, new Vector3(-0.999506f, 0.000000f, 0.031008f) },
    };

    // tailBase / tailMid の body_pose をどれだけ効かせるか。
    //
    // 2026-07-16 に「実機で見て確認したら調整・撤去せよ」として 0.5（半分）で入れた暫定値。
    // **2026-08-28 に実機で確認した。** 犬・猫とも「動画の中よりしっぽが動かない」と
    // 指摘され、原因がこの減衰だった。等倍に戻す。
    //
    // 入力側にはしっぽの回転がちゃんとある（meta.bin 実測で犬の joint 25 は恒等回転から
    // median 28.6 度・p90 42.5 度。首に次いで大きい）。それを半分にしていた。
    //
    // 戻すと「一部のモデルでしっぽが体にめり込む」という当初の懸念が再発しうる。
    // 実機で見て問題があればここを下げる（0.75 など）。
    private const float TailBodyPoseScale = 1f;

    // SMAL global_orient/pose rotation matrices are read from the bin in SMAL's own native
    // axis convention. This fixed correction re-expresses that raw decode into a usable Unity
    // world rotation - validated against DogRoot by comparing the model's nose direction
    // against the source video (see Docs/smpl-retargeting.md). Treated as a property of the
    // SMAL data/decoding convention itself, not of any specific animal rig (every per-model
    // adjustment needed instead lives in bindRotWorld[spine], which is captured directly per
    // model with no math involved) - see ADR-0002 for the (failed) attempts at deriving a
    // per-model replacement for this constant, and why we went back to it.
    private static readonly Quaternion SmalDataAxisCorrection = Quaternion.Euler(0f, 90f, 90f);

    private sealed class AnimalSmalRetargetState
    {
        public readonly Quaternion[] tw = new Quaternion[35];
        public readonly Quaternion[] smoothedLocal = new Quaternion[35]; // [0]=worldFk0, [1-34]=bodyPose smoothed
        public bool smoothingInitialized;
        public int debugFrameCount;
        // Per-model root yaw fix, decided once from real keypoint data (not bind-time
        // geometry guessing - see ADR-0002) and cached for the rest of the session so it
        // doesn't flicker frame to frame on noisy keypoints.
        public bool rootYawFixDecided;
        public Quaternion rootYawFix = Quaternion.identity;
        // Snapshot of tw[] at the previous *logged* sample (~every 30 ticks), plus real
        // elapsed time, so we can measure true rotation speed over a visually-relevant
        // window instead of a single-tick delta (which is too small to judge by) or a
        // misleading Euler-angle diff (which can look huge near gimbal/wrap singularities).
        public readonly Quaternion[] lastLoggedTw = new Quaternion[35];
        public readonly bool[] hasLoggedTw = new bool[35];
        public float lastLogRealTime;
        public bool hasLastLogRealTime;
        // Accumulated per-tick bend-direction angle change since the last logged sample,
        // for the bone-to-child "BEND" diagnostic (7=leftFrontUpper, 8=leftFrontLower,
        // 17=leftRearUpper, 21=rightRearUpper, 15=neck).
        public float bendAccumJoint7;
        public float bendAccumJoint8;
        public float bendAccumJoint17;
        public float bendAccumJoint21;
        public float bendAccumJoint15;
    }

    private readonly Dictionary<AnimalRigCache, AnimalSmalRetargetState> smalRetargetStates
        = new Dictionary<AnimalRigCache, AnimalSmalRetargetState>();

    // shot 境界で呼ぶ。state 自体は破棄しない: rootYawFix は「実データから一度だけ決めて
    // セッション中キャッシュする」もので shot とは無関係なため（消すとカットごとに
    // 向き判定がやり直しになりちらつく）。時間方向の平滑化だけ未初期化に戻して、
    // 新しい shot の先頭フレームでは前 shot の姿勢と混ざらない生値を採用させる。
    private void ResetSmalSmoothing()
    {
        foreach (KeyValuePair<AnimalRigCache, AnimalSmalRetargetState> kv in smalRetargetStates)
        {
            if (kv.Value != null)
            {
                kv.Value.smoothingInitialized = false;
            }
        }
    }

    private AnimalSmalRetargetState GetOrCreateSmalRetargetState(AnimalRigCache cache)
    {
        if (smalRetargetStates.TryGetValue(cache, out AnimalSmalRetargetState existing))
            return existing;
        var state = new AnimalSmalRetargetState();
        smalRetargetStates[cache] = state;
        return state;
    }

    // 四肢の上下と尾の付け根・中（AM の C で体の写像に替える関節）。
    private static bool IsSmalLimbOrTailJoint(int joint)
    {
        return joint == 7 || joint == 8 || joint == 11 || joint == 12 ||
               joint == 17 || joint == 18 || joint == 21 || joint == 22 ||
               joint == 25 || joint == 26;
    }

    // smalAbsoluteDirection（FKQ の (c)）を掛ける関節か。首と頭は「当てはめ済みの頭 ＋ 首の中間の骨 ＋ smalDriveNeckChain」が
    // そろうときだけ（当てはめ表に無い頭は照準が局所 +Z の代用で、(c) にすると bind から 108〜164° 回る、FKQ の a06）。
    private bool IsSmalAbsoluteDirectionJoint(AnimalRigCache cache, int joint)
    {
        bool neckOrHead = joint == 15 || joint == 16;
        if (smalAbsoluteDirectionHeadTailOnly && !(neckOrHead || joint == 25 || joint == 26))
        {
            return false;
        }

        if (!neckOrHead)
        {
            return true;
        }

        bool hasNeckChain = cache.neck != null && cache.neck.parent != null && cache.neck.parent != cache.spine &&
                            cache.neck.parent.name.IndexOf("neck", System.StringComparison.OrdinalIgnoreCase) >= 0;
        return smalDriveNeckChain && hasNeckChain && HasBakedHeadFit(cache);
    }

    // 既定の姿勢の根: bind の立ち姿を上下まわりだけ回し、今の鼻の水平の向き（rawWorldFk0 · modelForwardLocal。TryGetCurrentNoseWorldDirection と同じ定義）に合わせる。
    // 体の前の水平成分が無いとき（鼻が真上・真下）は root の向き（instanceRootYaw）のまま。
    private static Quaternion ResolveDefaultPoseRoot(AnimalRigCache cache, Quaternion rawWorldFk0, Quaternion instanceRootYaw)
    {
        Vector3 forwardLocal = cache.modelForwardLocal.sqrMagnitude > 0.000001f ? cache.modelForwardLocal : Vector3.back;
        Vector3 bindFlat = Vector3.ProjectOnPlane(forwardLocal, Vector3.up);
        Vector3 noseFlat = Vector3.ProjectOnPlane(rawWorldFk0 * forwardLocal, Vector3.up);
        if (bindFlat.sqrMagnitude <= 0.000001f || noseFlat.sqrMagnitude <= 0.000001f)
        {
            return instanceRootYaw;
        }

        return Quaternion.AngleAxis(Vector3.SignedAngle(bindFlat, noseFlat, Vector3.up), Vector3.up);
    }

    private static Quaternion ExtractYawOnly(Quaternion rotation)
    {
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            return Quaternion.identity;
        }
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    private void TryApplyAnimalSmalFk(AnimalRigCache cache, AnimalSmalPose pose, AnimalPoseSettings settings, Vector3[] jointsWorld, byte[] jointVis, Transform instanceRoot)
    {
        if (cache == null || !cache.ready || !pose.hasGlobalOrient || pose.bodyPose == null)
            return;

        AnimalSmalRetargetState state = GetOrCreateSmalRetargetState(cache);

        // Set to 0 to disable smoothing and pass raw SMAL values directly.
        // [SMAL-FK-DBG] trueDeltaDegSincePrevSample logging (2026-06-16) showed the raw
        // per-frame body_pose is largely incoherent noise: 20-100+ deg swings every ~0.2s,
        // with bodyPose_maxAngle landing on a different random joint almost every sample
        // (no multi-sample run on the same limb, as a real gait would show). That noise,
        // applied raw, looks like high-frequency jitter rather than motion - visually
        // reads as "frozen/stuck" even though the Transforms are moving a lot. Smooth it.
        // 半減期はプレイヤーの smalSmoothHalfLifeSec（既定 0.12）が毎フレーム代入する（2026-10-02 に検証用に外へ出した）。
        float halfLife = smalSmoothHalfLifeSec;
        float dt = Time.deltaTime;
        float smoothAlpha = halfLife > 0f
            ? 1f - Mathf.Exp(-dt * 0.693147f / halfLife)
            : 1f;
        float bodyHalfLife = smalBodyPoseSmoothHalfLifeSec >= 0f ? smalBodyPoseSmoothHalfLifeSec : halfLife;
        float bodySmoothAlpha = bodyHalfLife > 0f
            ? 1f - Mathf.Exp(-dt * 0.693147f / bodyHalfLife)
            : 1f;

        Quaternion[] tw = state.tw;

        // SMAL の運動連鎖を root から積んだ回転。smalAccum[j] = smalAccum[parent] * local[j]。
        // **ボーンの向きを決めるのはこれ**（標準形 R[j] = R[parent] * local[j] そのもの）。
        // globalOrient は worldFk0 が持っているので、ここには入れない。
        Quaternion[] smalAccum = new Quaternion[tw.Length];
        for (int i = 0; i < smalAccum.Length; i++) smalAccum[i] = Quaternion.identity;

        // Joint 0 (root) orientation. 2026-06-18: reverted to the simple, DogRoot-validated
        // form after two failed attempts at a "smarter" per-model derivation (FromToRotation
        // bend - lost roll; full-basis conjugation via cache.root.TransformDirection - picked
        // up drift from unrelated placement code) each introduced new bugs without first
        // confirming the simple form was actually the problem on P_GermanShepherd. See
        // ADR-0002 for the full history - going back to first principles before guessing again.
        if (!IsFiniteQ(pose.camRotation) || !IsFiniteQ(pose.globalOrient) ||
            !cache.bindRotWorld.TryGetValue(cache.spine, out Quaternion spineBindWorldForRoot) ||
            !IsFiniteQ(spineBindWorldForRoot))
        {
            return;
        }

        // Per-model orientation correction (2026-07-09): all models must present the same
        // forward/up convention to the SMAL FK formula. DogRoot's T-pose has spine=identity
        // and the dog faces -Z in local space, so LookRotation(-Z,+Y) is the reference basis.
        // New models (Wolf, Bear, Boar…) have modelForwardLocal ≈ +X because their FBX was
        // imported with a different axis convention.
        //
        // Proof:  tw[0] = rawWorldFk0 * S   visual_forward = tw[0] * Inv(S) * modelFwdLocal
        //       = rawWorldFk0 * modelFwdLocal
        //       = (…*modelOrientFix) * modelFwdLocal
        //       = (…*refModelBasis*Inv(thisModelBasis)) * thisModelBasis * (+Z)
        //       = (…*refModelBasis) * (+Z)  = (…) * LookRotation(-Z,+Y) * (+Z) = (…)*(-Z) ✓
        Quaternion refModelBasis = Quaternion.LookRotation(Vector3.back, Vector3.up);
        // Project modelForwardLocal onto the XZ plane before building the basis.
        // globalOrient carries all pitch/tilt; the T-pose "forward" only needs the
        // horizontal facing direction. If modelForwardLocal has a Y component (front legs
        // higher than rear legs in T-pose) and we pass it raw into LookRotation, the
        // correction bakes that tilt into modelOrientFix and the model ends up pitched down.
        Vector3 thisModelFwdRaw = cache.modelForwardLocal.sqrMagnitude > 0.001f ? cache.modelForwardLocal.normalized : Vector3.back;
        Vector3 thisModelFwdFlat = new Vector3(thisModelFwdRaw.x, 0f, thisModelFwdRaw.z);
        Vector3 thisModelFwd = thisModelFwdFlat.sqrMagnitude > 0.001f ? thisModelFwdFlat.normalized : thisModelFwdRaw;
        Vector3 thisModelUp  = cache.modelUpLocal.sqrMagnitude > 0.001f  ? cache.modelUpLocal.normalized  : Vector3.up;
        Quaternion thisModelBasis  = Quaternion.LookRotation(thisModelFwd, thisModelUp);
        Quaternion modelOrientFix  = refModelBasis * Quaternion.Inverse(thisModelBasis);

        if (!state.rootYawFixDecided && jointsWorld != null && jointVis != null &&
            cache.spineToNeckBindDirWorld.sqrMagnitude > 0.000001f)
        {
            Vector3 preferredUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
            if (AnimalBodyBasisResolver.TryResolveFromJoints(jointsWorld, jointVis, preferredUp, out Vector3 kpForward, out _, out _) &&
                kpForward.sqrMagnitude > 0.000001f)
            {
                // candidate * modelOrientFix * spineToNeckBindDirWorld_world correctly predicts
                // the neck world direction because:
                //   tw[0] = candidate * modelOrientFix * S
                //   neck_world = tw[0] * Inv(S) * spineToNeck_world
                //              = candidate * modelOrientFix * spineToNeck_world
                Quaternion candidate0 = pose.camRotation * pose.globalOrient * SmalDataAxisCorrection * modelOrientFix;
                Quaternion candidate180 = candidate0 * Quaternion.Euler(0f, 180f, 0f);
                float dot0 = Vector3.Dot(candidate0 * cache.spineToNeckBindDirWorld, kpForward);
                float dot180 = Vector3.Dot(candidate180 * cache.spineToNeckBindDirWorld, kpForward);
                const float kRootYawFlipMinMargin = 0.3f;
                state.rootYawFix = forceRootYawFix > 0 ? Quaternion.Euler(0f, 180f, 0f)
                    : forceRootYawFix < 0 ? Quaternion.identity
                    : ((dot180 - dot0 > kRootYawFlipMinMargin) ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity);
                state.rootYawFixDecided = true;
                Debug.Log($"[SMAL-FK-DBG] MODEL rootYawFix decided: dot0={dot0:F3} dot180={dot180:F3} chose180={state.rootYawFix != Quaternion.identity} kpForward={kpForward:F3} spineToNeckBindDirWorld={cache.spineToNeckBindDirWorld:F3} modelFwd={thisModelFwd:F3} modelOrientFix={modelOrientFix.eulerAngles:F1}");
            }
        }

        // Prepend the track root's own yaw so that interactive-motion gestures (which
        // explicitly set instanceRoot.rotation) compose naturally into the SMAL FK.
        // During normal playback AlignAnimalRootToSkeleton only touches position, so
        // instanceRootYaw = identity and this term has no effect.
        Quaternion instanceRootYaw = instanceRoot != null ? ExtractYawOnly(instanceRoot.rotation) : Quaternion.identity;
        // modelOrientFix aligns this model's T-pose axis convention to DogRoot (-Z fwd,
        // +Y up) so that tw[0] = worldFk0 * spineBindW produces the same visual direction
        // for every model. For DogRoot modelOrientFix ≡ identity (no change).
        Quaternion rawWorldFk0 = instanceRootYaw * pose.camRotation * pose.globalOrient * SmalDataAxisCorrection * state.rootYawFix * modelOrientFix;
        // 既定 ON（2026-10-08、非四足モードの smalNonQuadrupedUprightRoot。2026-10-09 に採用）: 体の根を上下まわり（yaw）だけにする（既定の姿勢の根と同じ ResolveDefaultPoseRoot、
        // 鼻の水平の向きは保つ）。犬が横に寝る・座る・跳ぶときに鳥・カンガルーが一緒に倒れない。モードで作ったキャッシュ（cache.smalNonQuadruped）だけ。
        if (smalNonQuadrupedUprightRoot && cache.smalNonQuadruped)
        {
            rawWorldFk0 = ResolveDefaultPoseRoot(cache, rawWorldFk0, instanceRootYaw);
        }
        // 既定の姿勢へ混ぜる（smalDefaultPoseWeight。イベントの to_default の段だけ 0 より大きい）。根は平滑の前に混ぜる。
        float defaultPoseWeight = Mathf.Clamp01(smalDefaultPoseWeight);
        if (defaultPoseWeight > 0f)
        {
            rawWorldFk0 = Quaternion.Slerp(rawWorldFk0, ResolveDefaultPoseRoot(cache, rawWorldFk0, instanceRootYaw), defaultPoseWeight);
        }

        Quaternion worldFk0 = state.smoothingInitialized
            ? Quaternion.Slerp(state.smoothedLocal[0], rawWorldFk0, smoothAlpha)
            : rawWorldFk0;
        state.smoothedLocal[0] = worldFk0;

        state.debugFrameCount++;
        // Log first frame + every 30 frames to sample orientation across the full video.
        bool debugLog = !state.smoothingInitialized || state.debugFrameCount % 30 == 0;

        if (debugLog)
        {
            float realDt = state.hasLastLogRealTime ? Time.time - state.lastLogRealTime : 0f;
            Debug.Log($"[SMAL-FK-DBG] sampleRealTimeSec={Time.time:F2} dtSincePrevSample={realDt:F3} unityDeltaTime={dt:F4}");
            state.lastLogRealTime = Time.time;
            state.hasLastLogRealTime = true;

            // Per-model ground truth, logged once per sample so we can compare models
            // directly instead of re-deriving formulas from theory. bindSpineW != ~identity
            // means this model's spine bone is NOT the literal hierarchy root (DogRoot's
            // "ボーン" bone is; P_GermanShepherd's DEF-spine.004 sits several bones deep in
            // its own spine chain) - that changes what "correct" looks like to compare against.
            Debug.Log($"[SMAL-FK-DBG] MODEL spineName={cache.spine.name} bindSpineW.euler={spineBindWorldForRoot.eulerAngles:F1} modelForwardLocal={cache.modelForwardLocal:F3} modelUpLocal={cache.modelUpLocal:F3} rootYawFixApplied={state.rootYawFix != Quaternion.identity}");
        }

        // worldFk0, applied via LEFT-multiplication onto ANY bone's own bindRotWorld (not just
        // spine's), gives "this bone, rotated rigidly as part of the whole body" (2026-06-18,
        // see ADR-0002). Using this instead of parentTW*bindLoc for bones whose SMAL-logical
        // parent (e.g. virtual joint 6) isn't their real Unity parent sidesteps that mismatch
        // entirely - bindRotWorld[bone] already correctly encodes the bone's full real
        // ancestor chain via Unity's own bone.rotation, no matter how many untracked
        // intermediate bones (e.g. a long DEF-spine.005..009 chain on a model whose spine
        // isn't a single bone like DogRoot's) sit in between.

        // tw[0] = worldFk0 * bindRotWorld[spine]; apply to spine bone
        {
            tw[0] = worldFk0 * spineBindWorldForRoot;
            if (IsFiniteQ(tw[0]))
            {
                TransformWriter.ApplyWorldRotation(cache.spine, tw[0]);

                // Live visual calibration aid: compare these rays against the source video every frame.
                // Yellow = nose direction, cyan = up direction. Visible in Scene view (and Game view with Gizmos on).
                Vector3 spinePos = cache.spine.position;
                Debug.DrawRay(spinePos, -cache.spine.forward * 0.5f, Color.yellow, 0f, false);
                Debug.DrawRay(spinePos, cache.spine.up * 0.3f, Color.cyan, 0f, false);

                if (debugLog) Debug.Log($"[SMAL-FK-DBG] frame={state.debugFrameCount} camRot={pose.camRotation.eulerAngles:F1} rawGO={pose.globalOrient.eulerAngles:F1} worldFk0={worldFk0.eulerAngles:F1} tw0={tw[0].eulerAngles:F1}");
                if (debugLog) Debug.Log($"[SMAL-FK-DBG] spine.fwd={cache.spine.forward:F3} spine.up={cache.spine.up:F3} nose(=-spine.fwd)={(-cache.spine.forward):F3}");
            }
        }

        // Walk joints 1-34 in topological order
        for (int i = 0; i < SmalJointTopologicalOrder.Length; i++)
        {
            int joint = SmalJointTopologicalOrder[i];
            int parentJoint = SmalJointParentArray[joint];
            Quaternion parentTW = tw[parentJoint];

            Quaternion rawLocal = Quaternion.identity;
            int bodyPoseIdx = joint - 1; // bodyPose[0] = SMAL joint 1, ..., bodyPose[33] = SMAL joint 34
            if (bodyPoseIdx >= 0 && bodyPoseIdx < pose.bodyPose.Length && IsFiniteQ(pose.bodyPose[bodyPoseIdx]))
                rawLocal = pose.bodyPose[bodyPoseIdx];

            if (joint == 25 || joint == 26)
            {
                // TailBodyPoseScale の経緯はその宣言のところに書いた。
                // 1 のときは Slerp が恒等になるので、無駄な計算を避けて素通しする。
                if (TailBodyPoseScale < 0.999f)
                {
                    rawLocal = Quaternion.Slerp(Quaternion.identity, rawLocal, TailBodyPoseScale);
                }
            }

            // 既定の姿勢へ混ぜる（body_pose → 恒等、平滑の前）。
            if (defaultPoseWeight > 0f)
            {
                rawLocal = Quaternion.Slerp(rawLocal, Quaternion.identity, defaultPoseWeight);
            }

            Quaternion smalLocal = state.smoothingInitialized
                ? Quaternion.Slerp(state.smoothedLocal[joint], rawLocal, bodySmoothAlpha)
                : rawLocal;
            state.smoothedLocal[joint] = smalLocal;

            // **仮想 spine（1-6）も必ず通す。**胴の曲げはここに乗るので、
            // 飛ばすと座位・伏せの主成分がそのまま落ちる（2026-09-10）。
            smalAccum[joint] = smalAccum[parentJoint] * smalLocal;

            Transform bone = GetSmalBoneForJoint(cache, joint);

            if (bone == null)
            {
                // 既定 OFF（2026-10-08）: 前足 10/14 は骨の役が無いので、smalDriveFeet のときだけ paw 役のただ 1 つの子（front_x_paw）に書く（TryApplySmalFrontFoot）。
                // 切ったときは、書いた前足を一度だけ「親 × bind の局所」へ戻す（書いていなければ何もしない）。
                if (joint == 10 || joint == 14)
                {
                    // 前肢の無いリグ（鳥、非四足モード、2026-10-08）は IsSmalFootParentOnBodyFrame が false（前足は書かない。書いていなければ戻しも何もしない）。
                    if (!smalDriveFeet || !IsSmalFootParentOnBodyFrame(cache, joint))
                    {
                        RestoreSmalFrontFoot(cache, joint);
                    }
                    else if (TryApplySmalFrontFoot(cache, joint, worldFk0, SmalDataAxisCorrection * state.rootYawFix * modelOrientFix, smalAccum[joint], tw))
                    {
                        continue;
                    }
                }

                // BONE MISSING (virtual spine chain joints 1-6): no real bone, so there's no
                // bind-pose-specific local frame to re-express into - just accumulate in world
                // frame directly (reverted 2026-06-18 along with joint 0, see ADR-0002).
                tw[joint] = parentTW * smalLocal;
                continue;
            }

            if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindLoc) || !IsFiniteQ(bindLoc))
                bindLoc = Quaternion.identity;

            // 非四足モード（2026-10-08、2026-10-09 に既定 ON）: 尾の付け根のあるモデル（カンガルーの Tail01）は関節 25/26 も bind に保つ。尾の付け根の Unity の親は spine の役（Hips）なので
            // tw[0] × bind の局所 = 腰に剛体（毎 tick 書くのでジェスチャが積み重ならない）。鳥は尾の役が null なので上の「骨なし」の分岐に入り、ここには来ない。
            if (AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint(joint) ||
                (cache.smalNonQuadruped && cache.tailBase != null && (joint == 25 || joint == 26)))
            {
                // ApplyWorldRotation, not ApplyLocalRotation (CLAUDE.md: FK loop uses
                // ApplyWorldRotation only) - parentTW already is this bone's real Unity
                // parent's current world rotation (applied earlier in this same topological
                // walk), so parentTW * bindLoc is the world-space equivalent of setting
                // bone.localRotation = bindLoc under that parent.
                tw[joint] = (passiveBoneUnityParent && bone.parent != null ? bone.parent.rotation : parentTW) * bindLoc;
                TransformWriter.ApplyWorldRotation(bone, tw[joint]);
                continue;
            }

            // 手根・飛節 9/13/19/23 は smalDriveCarpusHock のときだけ rest の向きを持つ（既定 OFF。TryGetSmalCarpusHockRestDir。OFF なら今の受け身の分岐のまま）。
            // 非四足モードの前肢の無いリグ（鳥、2026-10-08）は smalDriveCarpusHock を見ず、smalNonQuadrupedHock（2026-10-09 に既定 ON）のときだけ脛 19/23 が rest の向きを持つ
            // （TryGetSmalCarpusHockRestDir の IsSmalCarpusHockDriven）。
            if ((SmalRestDirByJoint.TryGetValue(joint, out Vector3 smalRestDir) || TryGetSmalCarpusHockRestDir(cache, joint, out smalRestDir)) &&
                (joint != 16 || enableAnimalHeadPose) &&
                cache.bindRotWorld.TryGetValue(bone, out Quaternion boneBindWorld) &&
                cache.bindDirLocal.TryGetValue(bone, out Vector3 boneBindDirLocal))
            {
                // 頭だけ照準を当てはめ表から与える（headAimFromModelForward）。
                //
                // 実測（[AIMBIND] / [HEADCHILD] / [HEADMESH]）で分かったこと:
                //   - **口・鼻のボーンが無い**リグが多く、既定の照準は頭メッシュの重心だった
                //   - neck / 四肢 / 尾は概ねローカル +Y を照準にしている。頭だけ 43 度ずれていた
                // そこで照準とロールをクリップ全体から当てはめて表に持つ。
                //
                // **当てはめ表に載っているモデルだけ新経路に入れる**（2026-09-11）。
                // 表が無いと照準は頭メッシュの重心のまま・ロールは 0 で、
                // FromToRotation 固定にするとロールが拘束されずむしろ悪くなる。
                //
                // **リグの形では条件を付けない。**以前は「頭の子が 3 つ以上」を条件にして
                // 耳の軸で副軸を作っていたが、その `headEarAxisLocal` は**どこでも使われて
                // いない死んだ計算**になっていた。条件だけが残ると、子が 2 つ以下のリグで
                // 当てはめ（FromToRotation 前提）と runtime（2 軸基底）が食い違う。
                // 52 体へ広げる前に外した（2026-09-11）。
                bool headFitted = joint == 16 && HasBakedHeadFit(cache);
                bool headAim = headAimFromModelForward && headFitted;

                // Geometry-grounded per-joint correction (2026-06-18): instead of reusing the
                // single global canonicalCorrection (whose roll/twist was only ever constrained
                // by a one-off nose-direction check, and produced near-invisible "twist instead
                // of bend" results when reused per-joint - see ADR), derive the correction
                // directly from comparing this joint's REST bone direction in SMAL's own native
                // frame (smalRestDir, from the SMAL rest skeleton J array) against this exact
                // Unity bone's REAL rest bone direction.
                // Quaternion.FromToRotation between the two gives an unambiguous, per-joint
                // SMAL-frame -> Unity-frame map with no global-axis guessing involved.
                //
                // unityRestDirWorld uses restWorldRot (= worldFk0 * boneBindWorld) instead of
                // the static bind-time direction (boneBindWorld * boneBindDirLocal). The static
                // version is wrong for models whose T-pose faces a different world direction than
                // DogRoot (e.g. Wolf/Bear facing +X vs Dog facing -Z): the same SMAL neck-down
                // bend would map to a sideways roll for those models instead of a forward-down tilt.
                // worldFk0 already incorporates modelOrientFix, so restWorldRot * boneBindDirLocal
                // gives the rest-pose bone direction in a model-neutral frame. (2026-07-09)
                // **smalLocal（平滑化後）を使う。rawLocal ではない。**
                // 2026-09-10: ここは実装当初から rawLocal のままで、上で計算・保存している
                // 平滑化値が **実ボーンには一度も使われていなかった**（git log -S で確認）。
                // smalLocal を使っていたのは BONE MISSING 分岐（仮想 spine 1-6）だけ。
                // つまり「生の body_pose はジッタで止まって見えるから平滑化する」という
                // 平滑化（smalSmoothHalfLifeSec、300 行付近の宣言コメント）の意図が、駆動される 12 関節に
                // 届いていなかった。meta.bin 実測でも 27-30 秒で単発 40-43 度の飛びがある
                // （中央は 0.2-0.4 度）。Docs/smpl-retargeting.md「Animal の棚卸し」参照。
                // 標準の運動連鎖では、ボーンの世界方向は R[j] * rest方向。
                // R[j] は root から積んだ回転（smalAccum）で、ローカル回転単体ではない。
                // 従来（smalLocal）は**親の曲げを全部無視**していたので、
                // 各ボーンが bind pose から自分のぶんだけずれた向きにしかならず、
                // 座る・伏せるのように連鎖して成立する姿勢が作れなかった。
                // 頭を連鎖に入れるのは**当てはめ済みのモデルだけ**。
                // 当てはめ自体が連鎖（Aacc）を前提に解いてあるので、両者はセットでしか成立しない。
                bool useChain = accumulateSmalParentBend
                    && (joint != 16 || (!excludeHeadFromChain && headFitted));
                Quaternion smalPose = useChain ? smalAccum[joint] : smalLocal;
                Vector3 smalPosedDir = (smalPose * smalRestDir).normalized;
                Quaternion bendSmal = Quaternion.FromToRotation(smalRestDir, smalPosedDir);

                // worldFk0 * boneBindWorld (not parentTW * bindLoc): see the comment above the
                // joint-0 block. parentTW for this joint may be a virtual SMAL joint (e.g. 6)
                // that doesn't correspond to this bone's real Unity parent on models with a
                // multi-bone spine chain, which would silently compose bindLoc relative to the
                // wrong frame.
                Quaternion restWorldRot = worldFk0 * boneBindWorld;
                Vector3 unityRestDirWorld = (restWorldRot * boneBindDirLocal).normalized;

                // 頭の照準とロールをオフラインで当てはめるための材料（2026-09-11）。
                // restWorldRot があれば、bindDirLocal を変えたときの unityRestDirWorld を
                // ログだけから再計算できるので、Unity を回さずに探索できる。
                if (joint == 16 && debugLog)
                {
                    // useChain も出す。「直したのに効かない」と「そもそも分岐が切り替わって
                    // いない」を取り違えないため（2026-09-11。平滑化値が実ボーンに
                    // 届いていなかった前例がある）。
                    Debug.Log("[HEADFIT] restWorldRot=" + restWorldRot.eulerAngles.ToString("F3") +
                        " bindDirLocal=" + boneBindDirLocal.ToString("F4") +
                        " unityRestDirWorld=" + unityRestDirWorld.ToString("F4") +
                        " useChain=" + useChain + " fitted=" + headFitted);
                }

                // 2 軸版（既定 OFF）。ロールを同じ肢のもう 1 本で拘束する。
                // 従来の FromToRotation は smalRestDir -> unityRestDirWorld しか拘束せず、
                // jointFrameMap * R(smalRestDir, θ) はどの θ でも同じ条件を満たす。
                // bendUnity は曲げの回転軸 n を jointFrameMap * n に写すので、ロールが
                // ずれると**屈曲が伸展に化ける**。SmalRollRefJoint のコメント参照。
                SmalRollRefOverrideJoint = -1;
                Quaternion jointFrameMap;
                if (headAim && joint == 16)
                {
                    // **頭は FromToRotation に固定する。**ロールは当てはめた θ で埋めるので、
                    // 2 軸基底と併用すると二重補正になる。
                    // （2026-09-11: オフラインの当てはめは FromToRotation を前提にしていたのに、
                    //  runtime は耳ベースの 2 軸基底を使っており、式が食い違っていた）
                    jointFrameMap = Quaternion.FromToRotation(smalRestDir, unityRestDirWorld);
                }
                // 9/13（手根）は smalDriveCarpusHock のときだけここまで来る（前腕 8/12 と同じ F2 の写像。主軸は SmalCarpusHockRestDir と paw 役の bindDirLocal）。
                else if (frontLimbBodyLateralSecondary && (joint == 7 || joint == 8 || joint == 11 || joint == 12 || joint == 9 || joint == 13)
                    && cache.bodyRightBindWorld.sqrMagnitude > 0.5f
                    && TryBuildDirectionBasis(smalRestDir, SmalBodyLeft, out Quaternion smalBasisLat)
                    && TryBuildDirectionBasis(unityRestDirWorld, worldFk0 * cache.bodyRightBindWorld, out Quaternion unityBasisLat))
                {
                    // F2（既定 OFF、上の宣言を参照）。首の副軸は bind の首の向きが後ろ（Puma）・横（Hyena 等）・上腕と反平行（GSD 等）の
                    // モデルで裏返り・回転・縮退していた。体の横は 52 体すべての前肢関節で |up|² ≥ 0.767（MAP の map04 / map05）。
                    jointFrameMap = unityBasisLat * Quaternion.Inverse(smalBasisLat);
                }
                else if (!useTwoAxisJointFrameMap
                    || !TryGetRollRef(joint, out int rollRefJoint)
                    || !SmalRestDirByJoint.TryGetValue(rollRefJoint, out Vector3 smalRollRefDir)
                    || !TryGetUnityRestDirWorld(cache, worldFk0, rollRefJoint, out Vector3 unityRollRefDir)
                    || !TryBuildDirectionBasis(smalRestDir, smalRollRefDir, out Quaternion smalBasis)
                    || !TryBuildDirectionBasis(unityRestDirWorld, unityRollRefDir, out Quaternion unityBasis))
                {
                    jointFrameMap = Quaternion.FromToRotation(smalRestDir, unityRestDirWorld);
                }
                else
                {
                    // どちらの基底も「主軸 = このボーンの rest 方向、副軸 = 同じ肢のもう 1 本」。
                    // 主軸の対応は FromToRotation と同じ（smalRestDir -> unityRestDirWorld）で、
                    // 加えてロールも決まる。
                    jointFrameMap = unityBasis * Quaternion.Inverse(smalBasis);
                }
                // 頭は当てはめたロールを掛ける（2026-09-11）。
                // M = FromToRotation(smalRestDir, unityRestDirWorld) は方向 1 本しか拘束せず
                // ロールが任意。クリップ全体から θ を当てはめて埋める。
                // 照準（bindDirLocal）も同じ当てはめで決めている（PrimeAnimalBind）。
                if (headFitted && headAimFromModelForward)
                {
                    float roll = GetBakedHeadRoll(cache);
                    if (Mathf.Abs(roll) > 0.001f)
                    {
                        jointFrameMap = jointFrameMap * Quaternion.AngleAxis(roll, smalRestDir);
                    }
                }
                Quaternion bendUnity = jointFrameMap * bendSmal * Quaternion.Inverse(jointFrameMap);

                // 診断専用（測定 B、2026-08-28）。曲げを恒等に固定すると tw = restWorldRot に
                // なり、ボーンは「bind pose を globalOrient で回しただけ」の姿勢に留まる。
                // これで body_pose の寄与と jointFrameMap の経路が**両方**消えるので、
                // 残る誤差は「リグの bind pose・比率が SMAL の betas 適用後の形状と違う」
                // ぶんだけになる。[ANIMALKP] を有無で比べて切り分ける。
                // 既定 false。本番で true にしない。
                if (disableSmalBendForDiag)
                {
                    bendUnity = Quaternion.identity;
                }

                // 2026-07-17 diagnostic: FromToRotation's axis becomes ill-defined as the two
                // vectors approach anti-parallel (~180deg), which would make jointFrameMap (and
                // therefore bendUnity) unstable for models whose default/bind tail direction
                // points nearly opposite SMAL's canonical tail rest direction. Logging this
                // angle per model to check whether that's actually happening for any of the
                // 52 animal prefabs before considering a bind-pose realignment pass.
                //
                // 2026-08-28: 尻尾（25/26）にしか配線していなかったので **rest dir を持つ全
                // joint** に広げた（タグも TAIL-REST-CHECK → REST-CHECK に変更）。理由:
                //  - body_pose はフレーム間 median 0.1〜0.7° しか動かず（meta.bin 実測）、
                //    bendSmal ≈ 恒等 ⇒ tw ≈ restWorldRot。つまりボーンは「bind pose を胴体の
                //    向きで回しただけ」に留まり、**誤差の主成分が静的な rest のずれ**になる。
                //  - その尻尾での実測が 62〜83°、[ANIMALKP] の Upper / Neck の誤差が 72〜83° と
                //    同じ桁。四肢では一度も測っていない。
                // 「150 度以上か」だけを見て「軸不定域ではないので問題なし」と結論した過去が
                // あるが（Docs/smpl-retargeting.md、Lion で 95〜122°）、**70〜80° のずれ自体**が
                // 疑いの対象。Docs/smpl-retargeting.md「Animal の姿勢誤差は静止姿勢の問題」参照。
                if (debugLog)
                {
                    float restDirAngleDeg = Vector3.Angle(smalRestDir, unityRestDirWorld);
                    Debug.Log($"[SMAL-FK-DBG] REST-CHECK model={cache.root?.name} joint={joint} smalRestDir={smalRestDir:F3} unityRestDirWorld={unityRestDirWorld:F3} restDirAngleDeg={restDirAngleDeg:F1} (150+=FromToRotation軸不定の疑いあり)");
                }

                bool bodyFrameJoint =
                    (bodyFrameNeckHead && (joint == 15 || (joint == 16 && !(bodyFrameKeepFittedHead && headFitted)))) ||
                    (bodyFrameLimbs && IsSmalLimbOrTailJoint(joint) &&
                     !(bodyFrameLimbsFrontAndTailOnly && (joint == 17 || joint == 18 || joint == 21 || joint == 22))) ||
                    (bodyFrameRearLimbs && (joint == 17 || joint == 18 || joint == 21 || joint == 22)) ||
                    (bodyFrameTail && (joint == 25 || joint == 26));
                // 既定 OFF（2026-10-08）: 前肢だけの体の写像（bodyFrameFrontLimbs）と、手根・飛節（smalDriveCarpusHock）の写像。IsSmalLegBodyFrameExtra を参照。
                // 鳥（非四足モードの前肢の無いリグ）の脛 19/23 は smalNonQuadrupedHock（2026-10-08、2026-10-09 に既定 ON）のときだけここに入る（キャッシュを渡す。IsSmalCarpusHockDriven）。
                bodyFrameJoint = bodyFrameJoint || IsSmalLegBodyFrameExtra(cache, joint);
                // smalAbsoluteDirectionLegsOnly（既定 OFF）: 方向の転写を脚だけに掛ける（IsSmalAbsoluteDirectionLegJoint）。smalAbsoluteDirection が ON ならその範囲のまま。
                if ((smalAbsoluteDirection && IsSmalAbsoluteDirectionJoint(cache, joint)) ||
                    (smalAbsoluteDirectionLegsOnly && IsSmalAbsoluteDirectionLegJoint(joint)))
                {
                    // 既定 OFF（FKQ の (c)）。上の宣言のコメントを参照。
                    Quaternion kMapAbs = SmalDataAxisCorrection * state.rootYawFix * modelOrientFix;
                    Vector3 smalDirAbs = smalAccum[joint] * smalRestDir;
                    Vector3 targetAbs = worldFk0 * (Quaternion.Inverse(kMapAbs) * new Vector3(-smalDirAbs.x, smalDirAbs.y, smalDirAbs.z));
                    tw[joint] = Quaternion.FromToRotation(unityRestDirWorld, targetAbs.normalized) * restWorldRot;
                }
                else if (bodyFrameJoint)
                {
                    // 既定 OFF（AM の B / C）。上の宣言のコメントを参照。
                    Quaternion bodyX = joint == 16
                        ? smalAccum[joint]
                        : Quaternion.FromToRotation(smalRestDir, (smalAccum[joint] * smalRestDir).normalized);
                    Quaternion kMap = SmalDataAxisCorrection * state.rootYawFix * modelOrientFix;
                    Quaternion mirroredX = new Quaternion(bodyX.x, -bodyX.y, -bodyX.z, bodyX.w);
                    tw[joint] = worldFk0 * Quaternion.Inverse(kMap) * mirroredX * kMap * boneBindWorld;
                }
                else if (headFitted && TryGetBakedHeadRot(cache, out Quaternion headRotC))
                {
                    // **頭は「globalOrient を含む連鎖 × 定数」で直接置く**（2026-09-11）。
                    //
                    //   tw[16] = restWorldRot * C^-1 * smalAccum[16] * C
                    //          = worldFk0 * boneBindWorld * C^-1 * Aacc * C
                    //
                    // 従来の `bendUnity * restWorldRot` は、写像
                    // `M = FromToRotation(smalRestDir, restWorldRot * bindDirLocal)` が
                    // restWorldRot 経由で globalOrient を含むため、**胴が回るたびに写像
                    // 自体が揺れていた**（FromToRotation は同変でない:
                    // FromTo(a, G b) != G FromTo(a, b)）。この式は G が左端に出るので揺れない。
                    //
                    // 実測（keypoints3d の 頭18 → 鼻24 との角度差、2 分割の交差検証）:
                    //   従来の式 23〜26 度 / この式 5.7〜7.5 度 / データの下限 5.1 度
                    // C はモデルごとに当てはめて animal_head_fit.json の "rot" に持つ。
                    tw[joint] = restWorldRot * Quaternion.Inverse(headRotC)
                        * smalAccum[joint] * headRotC;
                }
                else if (headUseBodyFrameMap && joint == 16)
                {
                    // 頭は体レベルの写像で解く（ロールの自由度が無い）。
                    // 連鎖を積んだ回転をそのまま使うので、首の振りも入る。
                    Quaternion sMap = state.rootYawFix * modelOrientFix;
                    tw[joint] = worldFk0 * Quaternion.Inverse(sMap) * smalAccum[joint] * sMap * boneBindWorld;
                }
                else
                {
                    tw[joint] = bendUnity * restWorldRot;
                }
            }
            else if (smalDriveFeet && (joint == 20 || joint == 24) && IsSmalFootParentOnBodyFrame(cache, joint) &&
                     TryGetSmalRearToeWorld(cache, bone, worldFk0, SmalDataAxisCorrection * state.rootYawFix * modelOrientFix, smalAccum[joint], out Quaternion rearToeWorld))
            {
                // 既定 OFF（2026-10-08）: 後足 20/24（toe 役の骨）を積んだ回転全体の体の写像で（SmalLeafBodyFrameWorld）。OFF なら下の受け身のまま。
                tw[joint] = rearToeWorld;
            }
            else
            {
                // No validated geometric correction exists yet for this joint (paws, head,
                // tail - none have a registered aim-child to derive a real Unity rest
                // direction from, see ADR-0001/0002). Rather than guess with an unvalidated
                // correction, just carry the rest pose through (no body_pose contribution) -
                // these bones still follow their parent's sway via parentTW * bindLoc.
                // passiveBoneUnityParent（既定 OFF、AM の A2）: 実際の Unity の親に付ける（尾の先は SMAL の親と食い違う）。
                tw[joint] = (passiveBoneUnityParent && bone.parent != null ? bone.parent.rotation : parentTW) * bindLoc;
            }

            if (!IsFiniteQ(tw[joint]))
                continue;

            Vector3 posBeforeApply = bone.position;

            // Quaternion.Angle includes twist-around-own-axis, which is invisible on a
            // limb segment. The thing that's actually visible is whether the direction
            // from this bone to its child swings - i.e. a genuine bend. Measure that
            // directly (child.position updates automatically once we rotate bone, since
            // it's a real Unity child) to settle whether the axis-correction conjugation
            // actually produced a visible bend or just changed which axis the twist is on.
            Transform bendChild = joint == 7 ? cache.leftFrontLower
                : joint == 8 ? cache.leftFrontPaw
                : joint == 17 ? cache.leftRearLower
                : joint == 21 ? cache.rightRearLower
                : joint == 15 ? cache.head
                : null;
            Vector3 bendDirBefore = bendChild != null ? (bendChild.position - bone.position).normalized : Vector3.zero;

            TransformWriter.ApplyWorldRotation(bone, tw[joint]);

            // 頭が実際にどちらを向いたかを直接出す（2026-09-11）。
            // 指標 [ANIMALKP] はメッシュ重心方向を測っており、照準に使う軸とは別物。
            // ここでは適用後のボーンの照準そのものを出す。
            if (joint == 16 && debugLog && cache.bindDirLocal.TryGetValue(bone, out Vector3 hd))
            {
                Vector3 aimW = (bone.rotation * hd).normalized;
                Debug.Log("[HEADAIM] headAimWorld=" + aimW.ToString("F3") +
                    " bindDirLocal=" + hd.ToString("F3") +
                    " boneFwd=" + bone.forward.ToString("F3"));
            }

            if (bendChild != null)
            {
                Vector3 bendDirAfter = (bendChild.position - bone.position).normalized;
                float bendDeg = Vector3.Angle(bendDirBefore, bendDirAfter);
                if (joint == 7) state.bendAccumJoint7 += bendDeg;
                else if (joint == 8) state.bendAccumJoint8 += bendDeg;
                else if (joint == 17) state.bendAccumJoint17 += bendDeg;
                else if (joint == 21) state.bendAccumJoint21 += bendDeg;
                else if (joint == 15) state.bendAccumJoint15 += bendDeg;

                if (debugLog)
                {
                    float accum = joint == 7 ? state.bendAccumJoint7
                        : joint == 8 ? state.bendAccumJoint8
                        : joint == 17 ? state.bendAccumJoint17
                        : joint == 21 ? state.bendAccumJoint21
                        : state.bendAccumJoint15;
                    Debug.Log($"[SMAL-FK-DBG] joint={joint} BEND childDirAngleAccumSincePrevSample={accum:F2}deg (sum of per-tick bone->child direction swings - this is the visually-relevant bend, vs the twist-inclusive Quaternion.Angle logged separately)");
                    if (joint == 7) state.bendAccumJoint7 = 0f;
                    else if (joint == 8) state.bendAccumJoint8 = 0f;
                    else if (joint == 17) state.bendAccumJoint17 = 0f;
                    else if (joint == 21) state.bendAccumJoint21 = 0f;
                    else state.bendAccumJoint15 = 0f;
                }
            }

            if (debugLog && (joint == 7 || joint == 8 || joint == 9 || joint == 10 ||
                              joint == 11 || joint == 12 || joint == 13 || joint == 14 ||
                              joint == 15 || joint == 16 || joint == 17 || joint == 21 ||
                              joint == 25 || joint == 26 || joint == 27))
            {
                // Compare against the snapshot taken at the PREVIOUS logged sample (~30
                // ticks ago), not the previous single tick, so the angle reflects motion
                // over a visually-relevant window. Quaternion.Angle is geodesic, so it
                // doesn't suffer from the Euler 0/360-wrap illusion of "huge" deltas above.
                float trueDeltaDegSincePrevSample = state.hasLoggedTw[joint]
                    ? Quaternion.Angle(state.lastLoggedTw[joint], tw[joint])
                    : 0f;
                state.lastLoggedTw[joint] = tw[joint];
                state.hasLoggedTw[joint] = true;

                Debug.Log($"[SMAL-FK-DBG] model={cache.root?.name} joint={joint} smalLocal={smalLocal.eulerAngles:F1} bindLoc={bindLoc.eulerAngles:F1} tw={tw[joint].eulerAngles:F1} boneRotAfter={bone.rotation.eulerAngles:F1} posDelta={(bone.position - posBeforeApply).magnitude:F4} trueDeltaDegSincePrevSample={trueDeltaDegSincePrevSample:F1}");
            }

            // Live visual calibration aid: lets us see in Scene view whether each limb/head
            // bone is actually rotating frame-to-frame, independent of whether the rendered
            // mesh appears to move (rules out "FK computes motion but wrong bone is driven").
            if (joint == 7 || joint == 11 || joint == 17 || joint == 21)
                Debug.DrawRay(bone.position, bone.forward * 0.2f, Color.green, 0f, false);
            else if (joint == 16)
                Debug.DrawRay(bone.position, bone.forward * 0.2f, Color.blue, 0f, false);
        }

        // 既定 OFF（2026-10-08、J-09 smalTrunkChordFromSmal、ApplySmalTrunkChord）: 胴の骨（cache.trunkChain）を SMAL の背骨 1〜6 で書く。
        // 胴を回すと主ループが world で書いた前脚・首・頭の world も動くので、中で書く前の world へ戻す。肩甲帯を書いたら首の中間の骨の Δ を今の肩甲帯から作る。
        bool smalTrunkGirdleWritten = smalTrunkChordFromSmal &&
            ApplySmalTrunkChord(cache, worldFk0, SmalDataAxisCorrection * state.rootYawFix * modelOrientFix, smalAccum);

        // 既定 OFF（2026-10-04、C-DM の U-c）: 首の中間の骨（Neck01・Neck02）に首の回転を配る。
        if (smalDriveNeckChain)
        {
            ApplySmalNeckChain(cache, worldFk0, tw, smalTrunkGirdleWritten);
        }

        // 既定 OFF（2026-10-08、J-04・J-21、ApplySmalNeckHeadJaw）: 首の骨を SMAL の 6→15→16 の折れ線で（smalNeckFullChain、smalNeckFullChainModels の
        // モデルだけ）、頭の鼻を SMAL の鼻へ（smalHeadNoseAim）、顎を SMAL の口（関節 32）で（smalDriveJaw）。親から順。どれも OFF で、前に書いた骨も無ければ呼ばない。
        if (smalNeckFullChain || smalHeadNoseAim || smalDriveJaw || HasSmalNeckHeadJawState())
        {
            ApplySmalNeckHeadJaw(cache, worldFk0, SmalDataAxisCorrection * state.rootYawFix * modelOrientFix, smalAccum, tw, state.smoothedLocal[32]);
        }

        // 既定 OFF（2026-10-07）: 尾の鎖を SMAL の尾の関節 25〜31 で書き直す（smalTailFullChainModels に載ったモデルだけ。主ループが書いた 25・26・27 も
        // 上書きし、tw も合わせる）。切ったとき・載っていないモデルでは、書いた中間の骨と伸ばした局所位置を一度だけ bind へ戻す（書いていなければ何もしない）。
        if (smalTailFullChain && TryGetSmalTailChainForModel(cache, out SmalTailChainBind tailChain))
        {
            ApplySmalTailChain(cache, tailChain, worldFk0, SmalDataAxisCorrection * state.rootYawFix * modelOrientFix, smalAccum, tw);
        }
        else
        {
            RestoreSmalTailChainBind(cache, tw);
        }

        state.smoothingInitialized = true;
        // 首・頭をこの tick に書き直した印（インタラクティブモーションの頭の向けは、これが今の frame のときだけ掛ける。2026-10-04）。
        cache.smalFkWrittenFrame = Time.frameCount;

        if (debugLog)
        {
            // body_pose 全 joint の最大角度を計測 → genuinely near-zero か読み取りバグかを判断
            float maxBPAngle = 0f;
            int maxBPJoint = -1;
            for (int dbgI = 0; dbgI < pose.bodyPose.Length; dbgI++)
            {
                float a = Quaternion.Angle(Quaternion.identity, pose.bodyPose[dbgI]);
                if (a > maxBPAngle) { maxBPAngle = a; maxBPJoint = dbgI + 1; }
            }
            Debug.Log($"[SMAL-FK-DBG] bodyPose_maxAngle={maxBPAngle:F1}deg at SMAL_joint={maxBPJoint}  (near-zero=data問題, >20=データ正常)");
            // body mesh の実際の world 向き: nose = -spine.fwd を確認
            if (cache.spine != null)
            {
                // 子の中から "body" meshを探して向きをチェック
                Transform bodyMesh = null;
                for (int ci = 0; ci < cache.spine.childCount; ci++)
                {
                    Transform c = cache.spine.GetChild(ci);
                    if (c.name == "body") { bodyMesh = c; break; }
                }
                if (bodyMesh != null)
                    Debug.Log($"[SMAL-FK-DBG] bodyMesh.fwd={bodyMesh.forward:F3} bodyMesh.up={bodyMesh.up:F3}  [nose≈bodyMesh.up toward viewer=-Z]");
            }
            // 前脚・頭の実際の向きを確認 → 向きのズレを特定
            if (cache.leftFrontUpper != null)
                Debug.Log($"[SMAL-FK-DBG] leftFront.fwd={cache.leftFrontUpper.forward:F3} leftFront.up={cache.leftFrontUpper.up:F3}");
            if (cache.leftFrontLower != null)
                Debug.Log($"[SMAL-FK-DBG] leftFrontLower.fwd={cache.leftFrontLower.forward:F3} leftFrontLower.up={cache.leftFrontLower.up:F3}");
            if (cache.head != null)
                Debug.Log($"[SMAL-FK-DBG] head.fwd={cache.head.forward:F3} head.up={cache.head.up:F3}");
        }
    }

    // 主軸と副軸から基底を作る。副軸は主軸に直交化してから使う。
    // 2 つが平行に近いときは基底が定まらないので false を返し、呼び出し側が
    // 従来の FromToRotation にフォールバックする。
    // 首の鎖（smalDriveNeckChain、2026-10-04 から既定 ON）。joint 15 は cache.neck（首の鎖の先端の骨）だけに当たり、その手前の中間の骨
    // （Labrador・Lynx の Neck01・Neck02）は根に剛体で付いたまま。SMAL の首は肩の付け根から頭までなので、その回転が首の先端の
    // 短い区間にしか乗らない（Lynx で首の鎖の長さの 16%、C-DM）。neck の剛体からのずれ Δ = tw[15]·(worldFk0·bindRotWorld[neck])⁻¹ を
    // 中間の骨へ根の側から Δ^(k/(n+1)) で配り（各骨の今の world 回転 = 根に剛体の姿勢 に左から掛ける）、そのあと neck と head を
    // tw のとおりに書き直す（中間の骨を回すと子の world 回転も動くため）。中間の骨は、名前に "neck" を含む cache.neck の祖先。
    private static void ApplySmalNeckChain(AnimalRigCache cache, Quaternion worldFk0, Quaternion[] tw, bool deltaFromGirdle)
    {
        if (cache.neck == null || !cache.bindRotWorld.TryGetValue(cache.neck, out Quaternion neckBind) || !IsFiniteQ(tw[15]))
        {
            return;
        }

        var chain = new System.Collections.Generic.List<Transform>(4);
        for (Transform t = cache.neck.parent;
             t != null && t != cache.spine && t.name.IndexOf("neck", System.StringComparison.OrdinalIgnoreCase) >= 0;
             t = t.parent)
        {
            chain.Add(t);
        }

        if (chain.Count == 0)
        {
            return;
        }

        chain.Reverse();
        // 基準は「剛体の姿勢」= 親（Spine4。smalTrunkChordFromSmal が OFF なら誰も書かず根に剛体、ON ならこの tick に胴の鎖が書いた world）× bind の局所を積んだもの。
        // 今の回転を基準にすると、Animal のリグは毎 tick bind に戻さないので前の tick に掛けた分が積み重なって暴走した（2026-10-04、G16）。
        var before = new Quaternion[chain.Count];
        Quaternion rigid = chain[0].parent != null ? chain[0].parent.rotation : Quaternion.identity;
        for (int i = 0; i < chain.Count; i++)
        {
            if (!cache.neckChainBindLocal.TryGetValue(chain[i], out Quaternion bindLocal))
            {
                bindLocal = chain[i].localRotation;
                cache.neckChainBindLocal[chain[i]] = bindLocal;
            }

            rigid = rigid * bindLocal;
            before[i] = rigid;
        }

        // Δ = neck の剛体の姿勢からのずれ。deltaFromGirdle（smalTrunkChordFromSmal が肩甲帯を書いた tick、2026-10-08 J-09）は剛体の姿勢を今の肩甲帯から作る
        // （肩甲帯 × 中間の骨の bind の局所 × neck の bind の局所）。根に剛体の姿勢（worldFk0 × bindRotWorld[neck]）のままだと、肩甲帯に掛けた A6 が
        // tw[15]（B の首の振りは A15 = A6·L15 から作る）からの Δ にも残り、中間の骨に A6 が二重に掛かる。false なら今までと同じ式（同じ値）。
        Quaternion rigidNeck = worldFk0 * neckBind;
        if (deltaFromGirdle && cache.bindRotLocal.TryGetValue(cache.neck, out Quaternion neckBindLocal) && IsFiniteQ(neckBindLocal))
        {
            rigidNeck = rigid * neckBindLocal;
        }

        Quaternion delta = tw[15] * Quaternion.Inverse(rigidNeck);
        // 積の四元数は符号が任意（q と −q は同じ回転）。w < 0 のまま Slerp(identity, Δ, k) に渡すと遠回りして、
        // 50° のずれが中間の骨で 100° 以上の折れになった（2026-10-04、G15 の初回）。近回りの側へそろえる。
        if (delta.w < 0f)
        {
            delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
        }

        for (int i = 0; i < chain.Count; i++)
        {
            float w = (i + 1f) / (chain.Count + 1f);
            TransformWriter.ApplyWorldRotation(chain[i], Quaternion.Slerp(Quaternion.identity, delta, w) * before[i]);
        }

        TransformWriter.ApplyWorldRotation(cache.neck, tw[15]);
        if (cache.head != null && IsFiniteQ(tw[16]))
        {
            TransformWriter.ApplyWorldRotation(cache.head, tw[16]);
        }
    }

    // 尾の鎖（smalTailFullChain、2026-10-07）。SMAL の尾の関節 25→26 … 30→31 の rest の区間（Docs/smal-rest-skeleton.json の位置の差。
    // SMAL の体の座標: +X が尾から頭、+Y が左、+Z が上）。最初の 2 本の向きは SmalRestDirByJoint の 25・26 と同じ。
    private static readonly Vector3[] SmalTailRestSegments =
    {
        new Vector3(-0.157202f, 0f, -0.019190f),
        new Vector3(-0.076840f, 0f, 0.002384f),
        new Vector3(-0.089562f, 0f, 0.008274f),
        new Vector3(-0.083850f, 0f, 0.005951f),
        new Vector3(-0.124971f, 0f, 0.014698f),
        new Vector3(-0.051586f, 0f, -0.000992f),
    };
    // 尾の長さ（区間の和 0.5867）÷ 尾の付け根（関節 25）から頭（関節 16）の距離（1.1297）。smalTailMatchSmalLength の目標の比。
    private const float SmalTailLengthToBaseHeadRatio = 0.5193f;
    private static readonly float[] SmalTailArcNorm = BuildSmalTailArcNorm();

    private sealed class SmalTailChainBind
    {
        public Transform[] bones;          // tailBase → … → tailTip（Unity の親子をたどった順）
        public Quaternion[] bindLocalRot;  // bind の局所回転
        public Vector3[] bindLocalPos;     // bind の局所位置（[0] は使わない）
        public float[] arcNorm;            // 各骨の位置の正規化した弧長（bind の world の距離で 0 … 1）
        public Vector3 tipDirLocal;        // 最後の骨の参照の向き（入ってくる区間の向きを最後の骨の局所で。先のメッシュはこの向きに伸びる）
        public float lengthFactor = 1f;    // SmalTailLengthToBaseHeadRatio ÷（モデルの尾の長さ ÷ 尾の付け根から頭）
        public string modelKey;            // prefab 名から先頭の「数字_」を外したもの（AnimalLegMappingFix.Key）。モデルの名簿と照らす
        public readonly Vector3[] smalPoints = new Vector3[7];
        public Quaternion[] relWorld;      // 既定の姿勢の重みで混ぜる相対の結果（J-23、2026-10-08）
        public Quaternion[] writtenWorld;  // この tick に書いた world（WriteSmalPolylineChain の出力）
        public bool rotationsWritten;
        public bool positionsScaled;
        public bool loggedApply;
        public bool loggedSkip;
    }

    private readonly Dictionary<AnimalRigCache, SmalTailChainBind> smalTailChainBinds = new Dictionary<AnimalRigCache, SmalTailChainBind>();

    // smalTailFullChainModels / smalTailMatchSmalLengthModels の解釈（カンマ区切りの prefab 名。先頭の「数字_」は AnimalLegMappingFix.Key で外す）。
    // 文字列が変わったときだけ分解し直す（毎 tick の比較は同じ参照の一致でほぼ終わる）。
    private sealed class SmalTailModelList
    {
        private string source;
        private readonly HashSet<string> keys = new HashSet<string>(System.StringComparer.Ordinal);

        public bool Contains(string list, string modelKey)
        {
            if (!string.Equals(list, source, System.StringComparison.Ordinal))
            {
                source = list;
                keys.Clear();
                if (!string.IsNullOrEmpty(list))
                {
                    foreach (string part in list.Split(','))
                    {
                        string key = AnimalLegMappingFix.Key(part.Trim());
                        if (key.Length > 0)
                        {
                            keys.Add(key);
                        }
                    }
                }
            }

            return !string.IsNullOrEmpty(modelKey) && keys.Contains(modelKey);
        }
    }

    private readonly SmalTailModelList smalTailChainModelList = new SmalTailModelList();
    private readonly SmalTailModelList smalTailLengthModelList = new SmalTailModelList();

    private static float[] BuildSmalTailArcNorm()
    {
        var c = new float[SmalTailRestSegments.Length + 1];
        for (int i = 0; i < SmalTailRestSegments.Length; i++)
        {
            c[i + 1] = c[i] + SmalTailRestSegments[i].magnitude;
        }

        for (int i = 1; i < c.Length; i++)
        {
            c[i] /= c[c.Length - 1];
        }

        return c;
    }

    // リグのキャッシュを作るとき（GetOrBuildAnimalRigCache、まだ誰も骨を書いていない bind）に一度だけ控える。
    // 中間の骨の局所回転は bind のまま誰も書かないので、ここで控えた値が「親 × bind の局所」の bind の局所になる。
    private void CaptureSmalTailChainBind(AnimalRigCache cache)
    {
        if (cache == null || cache.tailBase == null || cache.tailTip == null || cache.tailTip == cache.tailBase ||
            smalTailChainBinds.ContainsKey(cache))
        {
            return;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        var list = new List<Transform>(8);
        Transform t = cache.tailTip;
        while (t != null && t != cache.tailBase)
        {
            list.Add(t);
            t = t.parent;
        }

        if (t != cache.tailBase)
        {
            Debug.Log($"[TAILCHAIN] model={modelKey} tailTip={cache.tailTip.name} は tailBase={cache.tailBase.name} の子孫でない → 今の経路のまま");
            return;
        }

        list.Add(cache.tailBase);
        list.Reverse();
        int n = list.Count;
        var chain = new SmalTailChainBind
        {
            bones = list.ToArray(),
            bindLocalRot = new Quaternion[n],
            bindLocalPos = new Vector3[n],
            arcNorm = new float[n],
            modelKey = modelKey,
        };
        float arc = 0f;
        for (int i = 0; i < n; i++)
        {
            chain.bindLocalRot[i] = list[i].localRotation;
            chain.bindLocalPos[i] = list[i].localPosition;
            if (i > 0)
            {
                arc += Vector3.Distance(list[i].position, list[i - 1].position);
            }

            chain.arcNorm[i] = arc;
        }

        Vector3 incoming = chain.bindLocalPos[n - 1];
        if (arc < 0.000001f || incoming.sqrMagnitude < 0.000000000001f)
        {
            return;
        }

        for (int i = 0; i < n; i++)
        {
            chain.arcNorm[i] /= arc;
        }

        chain.tipDirLocal = Quaternion.Inverse(chain.bindLocalRot[n - 1]) * incoming.normalized;
        float baseToHead = cache.head != null ? Vector3.Distance(cache.head.position, cache.tailBase.position) : 0f;
        chain.lengthFactor = baseToHead > 0.000001f ? SmalTailLengthToBaseHeadRatio / (arc / baseToHead) : 1f;
        smalTailChainBinds[cache] = chain;
        Debug.Log($"[TAILCHAIN] model={modelKey} bones={n} ({chain.bones[0].name} … {chain.bones[n - 1].name}) arc={arc:F4} baseToHead={baseToHead:F4} lengthFactor={chain.lengthFactor:F3}");
    }

    // 鎖を控えてあり、smalTailFullChainModels に載ったモデルなら true。載っていなければ一度だけログを出して false（今の写し方のまま）。
    private bool TryGetSmalTailChainForModel(AnimalRigCache cache, out SmalTailChainBind chain)
    {
        chain = null;
        if (cache == null || !smalTailChainBinds.TryGetValue(cache, out SmalTailChainBind found) || found == null)
        {
            return false;
        }

        if (smalTailChainModelList.Contains(smalTailFullChainModels, found.modelKey))
        {
            chain = found;
            return true;
        }

        if (!found.loggedSkip)
        {
            found.loggedSkip = true;
            Debug.Log($"[TAILCHAIN] model={found.modelKey} は smalTailFullChainModels='{smalTailFullChainModels}' に無い → 今の写し方のまま");
        }

        return false;
    }

    // 尾の鎖を書く。各骨の rest =（この tick に書いた親の world）×（bind の局所）、向き = SMAL の折れ線の同じ弧長の区間の向き（world）。
    // 今の回転から始めない（Animal のリグは毎 tick bind に戻らないので、前の tick の分が積み重なる。2026-10-04 の首の鎖の暴走と同じ）。
    private void ApplySmalTailChain(AnimalRigCache cache, SmalTailChainBind chain, Quaternion worldFk0, Quaternion kMap, Quaternion[] smalAccum, Quaternion[] tw)
    {
        if (cache == null || chain == null)
        {
            return;
        }

        int n = chain.bones.Length;
        // 長さ（smalTailMatchSmalLength、新しい振る舞い）: smalTailMatchSmalLengthModels に載ったモデルだけ「bind の局所位置 × 倍率」にする。
        // 値が違うときだけ書く（入れた tick の 1 回）。切ったら一度だけ bind へ戻す。
        bool scaleLength = smalTailMatchSmalLength && Mathf.Abs(chain.lengthFactor - 1f) > 0.001f &&
                           smalTailLengthModelList.Contains(smalTailMatchSmalLengthModels, chain.modelKey);
        if (scaleLength || chain.positionsScaled)
        {
            WriteSmalTailChainLocalPositions(chain, scaleLength ? chain.lengthFactor : 1f);
            chain.positionsScaled = scaleLength;
        }

        // SMAL の尾の折れ線（体の座標、関節 25 からの相対）。smalAccum は主ループで 25〜31 まで積んである（平滑後の局所の積）。
        Vector3[] pts = chain.smalPoints;
        pts[0] = Vector3.zero;
        for (int s = 0; s < SmalTailRestSegments.Length; s++)
        {
            pts[s + 1] = pts[s] + smalAccum[25 + s] * SmalTailRestSegments[s];
        }

        Quaternion kInv = Quaternion.Inverse(kMap);
        Transform first = chain.bones[0];
        Quaternion parentWorld = first.parent != null ? first.parent.rotation : Quaternion.identity;
        if (chain.relWorld == null || chain.relWorld.Length != n)
        {
            chain.relWorld = new Quaternion[n];
            chain.writtenWorld = new Quaternion[n];
        }

        // 既定の姿勢の重み（2026-10-08、J-23。欠陥の直しで新しい係数・フラグなし）: イベントの to_default の間（smalDefaultPoseWeight > 0）、各骨の world を
        // Slerp(鎖の結果, 相対の結果, 重み) にする（重み 1 なら鎖は計算せず相対の結果を書く）。鎖は smalAccum から SMAL の rest の折れ線を作るので、body_pose を
        // 恒等へ混ぜると SMAL の rest の尾（真後ろ・ほぼ水平、弦の仰角 +1.1°）になり、smalDefaultPoseWeight の約束（恒等 → prefab の bind の立ち姿）と食い違っていた
        // （Lynx の付け根の垂れ約 39° が水平まで上がる見込み。監査 AX-5、コードを読んだだけで未測定）。相対の結果 = 主ループの tw[25〜27]（この関数が上書きする前の値）、
        // 中間の骨は「相対の結果の親 × bind の局所」（今の写し方で誰も書かないときの world）。重み 0（追従）は混ぜないので今と同じ結果。長さ（局所位置）は混ぜない。
        float relWeight = Mathf.Clamp01(smalDefaultPoseWeight);
        if (relWeight > 0f)
        {
            Quaternion relParent = parentWorld;
            for (int i = 0; i < n; i++)
            {
                Transform b = chain.bones[i];
                Quaternion rel = relParent * chain.bindLocalRot[i];
                if (b == cache.tailBase && IsFiniteQ(tw[25])) rel = tw[25];
                else if (b == cache.tailMid && IsFiniteQ(tw[26])) rel = tw[26];
                else if (b == cache.tailTip && IsFiniteQ(tw[27])) rel = tw[27];
                chain.relWorld[i] = rel;
                relParent = rel;
            }
        }

        // 骨を書く本体は首の鎖（smalNeckFullChain）と共通（WriteSmalPolylineChain）。重み 0 なら式も計算の順番も切り出す前と同じ。
        int written = WriteSmalPolylineChain(chain.bones, chain.bindLocalRot, chain.bindLocalPos, chain.arcNorm, chain.tipDirLocal, false,
            pts, SmalTailArcNorm, worldFk0, kInv, parentWorld, chain.relWorld, relWeight, chain.writtenWorld);
        for (int i = 0; i < written; i++)
        {
            if (chain.bones[i] == cache.tailBase) tw[25] = chain.writtenWorld[i];
            else if (chain.bones[i] == cache.tailMid) tw[26] = chain.writtenWorld[i];
            else if (chain.bones[i] == cache.tailTip) tw[27] = chain.writtenWorld[i];
        }

        if (written < n)
        {
            return;
        }

        chain.rotationsWritten = true;
        if (!chain.loggedApply)
        {
            chain.loggedApply = true;
            Debug.Log($"[TAILCHAIN] applied model={chain.modelKey} bones={n} lengthScaled={scaleLength} factor={chain.lengthFactor:F3}");
        }
    }

    // 尾の鎖の骨の局所位置を「bind × k」にする。値が違う骨だけ書く（入れた・切った tick の 1 回。ほかの経路が戻したときだけ書き直す）。
    private static void WriteSmalTailChainLocalPositions(SmalTailChainBind chain, float k)
    {
        for (int i = 1; i < chain.bones.Length; i++)
        {
            Vector3 want = chain.bindLocalPos[i] * k;
            if ((chain.bones[i].localPosition - want).sqrMagnitude > 0.000000000001f)
            {
                TransformWriter.ApplyLocalPosition(chain.bones[i], want);
            }
        }
    }

    // 折れ線 pts（各点の正規化した弧長 arcNorm）の上で弧長 u の点。尾（SmalTailArcNorm）と首（SmalNeckArcNorm）で共通（2026-10-08 に尾の PointOnSmalTail を
    // 一般化した。式は同じ）。
    private static Vector3 PointOnSmalPolyline(Vector3[] pts, float[] arcNorm, float u)
    {
        u = Mathf.Clamp01(u);
        int k = 0;
        while (k < arcNorm.Length - 2 && u > arcNorm[k + 1])
        {
            k++;
        }

        float span = arcNorm[k + 1] - arcNorm[k];
        float t = span > 0.000001f ? (u - arcNorm[k]) / span : 0f;
        return Vector3.LerpUnclamped(pts[k], pts[k + 1], t);
    }

    // 「Unity の骨の鎖 × SMAL の折れ線」を書く本体（尾の鎖 smalTailFullChain と首の鎖 smalNeckFullChain で共通。2026-10-08 に ApplySmalTailChain から切り出した。
    // 尾は relWeight が 0 なら式も計算の順番も切り出す前と同じ）。骨 i の rest =（この tick に書いた親の world）×（bind の局所）、向き = SMAL の折れ線の同じ正規化した
    // 弧長の区間の向き（world。写像は方向の転写と同じ鏡映 D_x → K⁻¹ → worldFk0）。今の回転から始めない（Animal のリグは毎 tick bind に戻らないので積み重なる）。
    //   bindLocalPos[i + 1]: 骨 i の区間の先（次の骨。hasEndPoint なら最後の骨の先は終点の骨）の bind の局所位置。arcNorm[i]: 骨 i の正規化した弧長（hasEndPoint なら [n] = 終点）。
    //   終点の無い鎖（尾）の最後の骨は、入ってくる区間の向き（tipDirLocal）を SMAL の最後の区間へ向ける。
    //   relWeight（J-23、イベントの既定の姿勢の重み）: 0 なら鎖の結果を書く。0〜1 なら Slerp(鎖の結果, relWorld[i], relWeight)、1 なら鎖は計算せず relWorld[i]。
    //   次の骨の rest は混ぜる前の鎖の結果から作る（混ぜるのは各骨の world だけ）。
    //   written[i] に書いた world を入れる。戻り値は書いた骨の数（world が有限でなければそこで止める）。
    private static int WriteSmalPolylineChain(Transform[] bones, Quaternion[] bindLocalRot, Vector3[] bindLocalPos, float[] arcNorm, Vector3 tipDirLocal,
        bool hasEndPoint, Vector3[] pts, float[] smalArcNorm, Quaternion worldFk0, Quaternion kInv, Quaternion parentWorld, Quaternion[] relWorld, float relWeight,
        Quaternion[] written)
    {
        int n = bones.Length;
        for (int i = 0; i < n; i++)
        {
            Quaternion world;
            if (relWeight >= 1f)
            {
                world = relWorld[i];
            }
            else
            {
                Quaternion rest = parentWorld * bindLocalRot[i];
                Vector3 restDir;
                Vector3 smalDir;
                if (i + 1 < n || hasEndPoint)
                {
                    restDir = rest * bindLocalPos[i + 1];
                    smalDir = PointOnSmalPolyline(pts, smalArcNorm, arcNorm[i + 1]) - PointOnSmalPolyline(pts, smalArcNorm, arcNorm[i]);
                }
                else
                {
                    restDir = rest * tipDirLocal;
                    smalDir = pts[pts.Length - 1] - pts[pts.Length - 2];
                }

                world = rest;
                if (restDir.sqrMagnitude > 0.000000000001f && smalDir.sqrMagnitude > 0.000000000001f)
                {
                    Vector3 target = worldFk0 * (kInv * new Vector3(-smalDir.x, smalDir.y, smalDir.z));
                    world = Quaternion.FromToRotation(restDir, target) * rest;
                }

                parentWorld = world;
                if (relWeight > 0f)
                {
                    world = Quaternion.Slerp(world, relWorld[i], relWeight);
                }
            }

            if (!IsFiniteQ(world))
            {
                return i;
            }

            TransformWriter.ApplyWorldRotation(bones[i], world);
            written[i] = world;
        }

        return n;
    }

    // smalTailFullChain を切ったとき・名簿に無いモデル: 伸ばした局所位置と、書いた中間の骨（役の骨は主ループが毎 tick 書く）を一度だけ bind へ戻す。
    // 中間の骨を戻すと子の world が動くので、役の骨（26・27）は主ループの tw で書き直す。鎖を書いたことが無ければ何もしない。
    private void RestoreSmalTailChainBind(AnimalRigCache cache, Quaternion[] tw)
    {
        if (cache == null || !smalTailChainBinds.TryGetValue(cache, out SmalTailChainBind chain) || chain == null)
        {
            return;
        }

        if (chain.positionsScaled)
        {
            WriteSmalTailChainLocalPositions(chain, 1f);
            chain.positionsScaled = false;
        }

        if (!chain.rotationsWritten)
        {
            return;
        }

        for (int i = 0; i < chain.bones.Length; i++)
        {
            Transform b = chain.bones[i];
            if (b == cache.tailBase)
            {
                continue;
            }

            if (b == cache.tailMid && IsFiniteQ(tw[26]))
            {
                TransformWriter.ApplyWorldRotation(b, tw[26]);
            }
            else if (b == cache.tailTip && IsFiniteQ(tw[27]))
            {
                TransformWriter.ApplyWorldRotation(b, tw[27]);
            }
            else
            {
                Quaternion parent = b.parent != null ? b.parent.rotation : Quaternion.identity;
                TransformWriter.ApplyWorldRotation(b, parent * chain.bindLocalRot[i]);
            }
        }

        chain.rotationsWritten = false;
    }

    // ハンドオフの控え（CaptureBoneLocalRotations）に尾の鎖の骨を足す。鎖を書いていないときは何もしない。
    private void AddSmalTailChainBoneLocalRotations(AnimalRigCache cache, Dictionary<Transform, Quaternion> destination)
    {
        if (cache == null || !smalTailChainBinds.TryGetValue(cache, out SmalTailChainBind chain) || chain == null || !chain.rotationsWritten)
        {
            return;
        }

        for (int i = 0; i < chain.bones.Length; i++)
        {
            AddBoneLocalRotation(destination, chain.bones[i]);
        }
    }

    private static bool TryBuildDirectionBasis(Vector3 primary, Vector3 secondary, out Quaternion basis)
    {
        basis = Quaternion.identity;
        if (primary.sqrMagnitude < 0.000001f || secondary.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        Vector3 forward = primary.normalized;
        Vector3 up = Vector3.ProjectOnPlane(secondary, forward);
        if (up.sqrMagnitude < 0.01f)
        {
            return false;
        }

        basis = Quaternion.LookRotation(forward, up.normalized);
        return true;
    }

    // ある joint の Unity ボーンの rest 方向を、unityRestDirWorld と同じ系で返す。
    private static bool TryGetUnityRestDirWorld(AnimalRigCache cache, Quaternion worldFk0, int joint, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        Transform bone = GetSmalBoneForJoint(cache, joint);
        if (bone == null ||
            !cache.bindRotWorld.TryGetValue(bone, out Quaternion boneBindWorld) ||
            !cache.bindDirLocal.TryGetValue(bone, out Vector3 boneBindDirLocal))
        {
            return false;
        }

        Vector3 d = worldFk0 * boneBindWorld * boneBindDirLocal;
        if (d.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        dirWorld = d.normalized;
        return true;
    }

    // 手根・飛節（smalDriveCarpusHock、2026-10-08、全関節の監査 J-08）の rest の向き。SMAL の rest の骨格の区間（Docs/smal-rest-skeleton.json の位置の差
    // J10−J9・J14−J13・J20−J19・J24−J23 を正規化した値。SMAL の体の座標: +X が尾から頭、+Y が左、+Z が上）。SmalRestDirByJoint とは別に置く
    // （そちらに足すと、フラグに関係なく rest の向きの分岐の入口と方向の転写の対象が変わる）。
    private static readonly Dictionary<int, Vector3> SmalCarpusHockRestDir = new Dictionary<int, Vector3>
    {
        { 9,  new Vector3(0.334665f, -0.030523f, -0.941843f) },  // LLeg3 -> LFoot（左の手根 → 前の球節）
        { 13, new Vector3(0.334665f, 0.030523f, -0.941843f) },   // RLeg3 -> RFoot
        { 19, new Vector3(0.179494f, 0.046659f, -0.982652f) },   // LLegBack3 -> LFootBack（左の飛節 → 後ろの球節）
        { 23, new Vector3(0.179494f, -0.046659f, -0.982652f) },  // RLegBack3 -> RFootBack
    };

    // 手根・飛節・指（smalDriveCarpusHock / smalDriveFeet）を写さないモデル（キーは AnimalLegMappingFix.Key と同じ、完全一致）。脚の割り当ての表の
    // RollStyle の行（Beaver・Fox）は paw 役が前腕・下腿の骨幹の途中の Roll 骨（Fox の LeftArmRoll は高さ 0.253 で、前腕の上端 0.388 と手根 0.117 の間）、
    // toe 役が中足の骨（rear_l_lower）、paw 役の子が中手の骨（front_l_lower）で、関節 9/10/19/20 のデータが 1 つ上の区間に当たる（監査 J-25、AL-8）。
    // Dog・Mammoth は paw 役の最初の子が剛体の部品（arm.003）・踵で、手根の主軸（paw 役の bindDirLocal = 最初の子の支点への向き）が手根 → 球節にならない。
    // Donkey1.0 は前肢の割り当てが 1 本ずれている疑いで脚の表に入っていない（反論役 I2 の指摘）。どれも実験のモデルではないので外しておく。
    private static readonly HashSet<string> SmalDistalLegExcludedModels = new HashSet<string>(System.StringComparer.Ordinal)
    {
        "Beaver",
        "Fox",
        "Dog",
        "Mammoth",
        "Donkey1.0",
    };

    // 手根・飛節・指のキャッシュごとの控え。smalDriveCarpusHock か smalDriveFeet が ON になって初めて作る（OFF のままなら作らない）。
    // 非四足モードの前肢の無いリグ（鳥、2026-10-08）は smalNonQuadrupedHock が ON になって初めて作る（鳥には smalDriveCarpusHock・smalDriveFeet が効かない）。
    private sealed class SmalDistalLegState
    {
        public string modelKey = string.Empty;
        public bool excluded;
        // 前足（関節 10/14）の骨 = paw 役のただ 1 つの子と、その bind の局所回転（gestureBindLocal にあればキャッシュを作ったときの値、無ければ最初に使うときの値。
        // どちらも誰もまだ書いていない骨の bind）。[0] = 左（10）、[1] = 右（14）。
        public readonly Transform[] frontFoot = new Transform[2];
        public readonly Quaternion[] frontFootBindLocal = new Quaternion[2];
        public readonly bool[] frontFootWritten = new bool[2];
        public bool loggedCarpusHock;
        public bool loggedFeet;
    }

    private readonly Dictionary<AnimalRigCache, SmalDistalLegState> smalDistalLegStates = new Dictionary<AnimalRigCache, SmalDistalLegState>();

    private SmalDistalLegState GetSmalDistalLegState(AnimalRigCache cache)
    {
        if (smalDistalLegStates.TryGetValue(cache, out SmalDistalLegState found))
        {
            return found;
        }

        var created = new SmalDistalLegState { modelKey = AnimalLegMappingFix.Key(KeyFor(cache)) };
        created.excluded = SmalDistalLegExcludedModels.Contains(created.modelKey);
        if (!created.excluded)
        {
            created.frontFoot[0] = ResolveSmalFrontFoot(cache, cache.leftFrontPaw, out created.frontFootBindLocal[0]);
            created.frontFoot[1] = ResolveSmalFrontFoot(cache, cache.rightFrontPaw, out created.frontFootBindLocal[1]);
        }

        smalDistalLegStates[cache] = created;
        return created;
    }

    // 前足の骨: paw 役（手根 → 球節の骨）の子がただ 1 つで、メッシュの部品でないときのその子（ResolveAnimalAimChild の既定と同じ最初の子。paw 役の
    // bindDirLocal もこの子への向きで採っている）。子が 2 つ以上（40_Mammoth の踵と指 3 本）・子がメッシュ（剛体の部品の 00_Dog の arm.003）・子が無いなら null
    // （前足は写さない）。prefab の棚卸しでは、ほかの YAML の 46 体はどれも子が 1 つの骨（Labrador・Lynx は front_x_paw、Wolf は LegFLAnkle）。
    private static Transform ResolveSmalFrontFoot(AnimalRigCache cache, Transform paw, out Quaternion bindLocal)
    {
        bindLocal = Quaternion.identity;
        if (paw == null || paw.childCount != 1)
        {
            return null;
        }

        Transform foot = paw.GetChild(0);
        if (foot == null || foot.GetComponent<Renderer>() != null)
        {
            return null;
        }

        bindLocal = cache.gestureBindLocal.TryGetValue(foot, out Quaternion captured) ? captured : foot.localRotation;
        return foot;
    }

    private static string SmalBoneLabel(Transform bone)
    {
        return bone != null ? bone.name : "none";
    }

    // smalDriveCarpusHock（既定 OFF）: 手根・飛節 9/13/19/23 の rest の向き。フラグ OFF・ほかの関節・除外のモデルなら false（今の受け身の分岐のまま）。
    private bool TryGetSmalCarpusHockRestDir(AnimalRigCache cache, int joint, out Vector3 restDir)
    {
        restDir = Vector3.zero;
        // 前肢の無いリグ（鳥、非四足モード、2026-10-08）は smalDriveCarpusHock でなく smalNonQuadrupedHock の脛 19/23 だけ（IsSmalCarpusHockDriven）。
        // 非四足モードが OFF なら今と同じ（!smalDriveCarpusHock）。
        if (!IsSmalCarpusHockDriven(cache, joint) || !SmalCarpusHockRestDir.TryGetValue(joint, out restDir))
        {
            return false;
        }

        SmalDistalLegState s = GetSmalDistalLegState(cache);
        if (!s.loggedCarpusHock)
        {
            s.loggedCarpusHock = true;
            Debug.Log(s.excluded
                ? $"[SMALLEG] {SmalCarpusHockTrigger(cache)} model={s.modelKey} は除外（Roll 骨のリグ）→ 手根・飛節は受け身のまま"
                : $"[SMALLEG] {SmalCarpusHockTrigger(cache)} model={s.modelKey} 9={SmalBoneLabel(cache.leftFrontPaw)} 13={SmalBoneLabel(cache.rightFrontPaw)} 19={SmalBoneLabel(cache.leftRearPaw)} 23={SmalBoneLabel(cache.rightRearPaw)}");
        }

        if (s.excluded)
        {
            restDir = Vector3.zero;
            return false;
        }

        return true;
    }

    // bodyFrameJoint に足す条件（既定 OFF）。bodyFrameFrontLimbs: 前肢 7/8/11/12。smalDriveCarpusHock: 手根・飛節 9/13/19/23 は親の関節（前腕 8/12・下腿 18/22）が
    // 体の写像なら体の写像（同じ肢の中で写像を混ぜない。19/23 は bodyFrameRearLimbs が既定 ON なので既定で体の写像、9/13 は bodyFrameFrontLimbs か bodyFrameLimbs の
    // ときだけ）。9/13/19/23 が rest の向きを持つのは smalDriveCarpusHock のときだけなので、ここに来るのもそのときだけ。
    // 鳥（非四足モードの前肢の無いリグ、2026-10-08）は smalDriveCarpusHock でなく smalNonQuadrupedHock の脛 19/23 だけがここに来る（IsSmalCarpusHockDriven。キャッシュを渡す）。
    private bool IsSmalLegBodyFrameExtra(AnimalRigCache cache, int joint)
    {
        if (bodyFrameFrontLimbs && (joint == 7 || joint == 8 || joint == 11 || joint == 12))
        {
            return true;
        }

        if (IsSmalCarpusHockDriven(cache, joint) && (joint == 9 || joint == 13 || joint == 19 || joint == 23))
        {
            return IsSmalLegJointOnBodyFrame(SmalJointParentArray[joint]);
        }

        return false;
    }

    // 手根・飛節 9/13/19/23 の写し（rest の向き SmalCarpusHockRestDir・親と同じ写像）を掛ける切り替えが入っているか（2026-10-08、非四足モードで分けた。
    // 除外のモデルは TryGetSmalCarpusHockRestDir がこの後で外す）。
    // 前肢の上の役が左右とも無いリグ（鳥、非四足モードの cache.smalNoFrontLimbs）: smalNonQuadrupedHock（2026-10-09 に既定 ON）だけを見る（IsNonQuadrupedHock、脛 19/23 だけ）。
    //   全体の smalDriveCarpusHock（J-08、判断待ち）は見ない（ON でも鳥の脛は動かない。判断 1: 鳥の脛を全体の切り替えに結び付けない）。
    // それ以外のリグ（四足・カンガルー）: 今のまま smalDriveCarpusHock。非四足モードが OFF なら smalNoFrontLimbs は常に false なので、常にこちら（今と同じ）。
    private bool IsSmalCarpusHockDriven(AnimalRigCache cache, int joint)
    {
        if (cache != null && cache.smalNoFrontLimbs)
        {
            return IsNonQuadrupedHock(cache, joint);
        }

        return smalDriveCarpusHock;
    }

    // smalNonQuadrupedHock（2026-10-08、2026-10-09 に既定 ON）: 前肢の無いリグ（鳥、cache.smalNoFrontLimbs）の脛 19/23（LegL3/LegR3 = 足根中足骨）に、手根・飛節
    // （smalDriveCarpusHock、J-08）と同じ写し方を掛ける（rest の向き SmalCarpusHockRestDir、写像は親の 18/22 と同じ = bodyFrameRearLimbs なら体の写像）。
    // 鳥の脚は犬の後脚と同じジグザグ（前下・後下・前下）なので、同じ関節を同じ向きに曲げる。指 20/24 は smalDriveFeet が ON でも受け身のまま
    // （IsSmalFootParentOnBodyFrame が鳥では false）。
    private bool IsNonQuadrupedHock(AnimalRigCache cache, int joint)
    {
        return smalNonQuadrupedHock && cache != null && cache.smalNoFrontLimbs && (joint == 19 || joint == 23);
    }

    // [SMALLEG] のログに出す切り替えの名前（2026-10-08）。前肢の無いリグ（鳥）は smalNonQuadrupedHock でしか入らないのでそう書く。それ以外は今のまま。
    private static string SmalCarpusHockTrigger(AnimalRigCache cache)
    {
        return cache != null && cache.smalNoFrontLimbs ? "smalNonQuadrupedHock（非四足モードの前肢の無いリグ = 鳥の脛 19/23）" : "smalDriveCarpusHock";
    }

    // 脚の関節 7/8/11/12/17/18/21/22 が体の写像か（bodyFrameJoint の四肢の部分と bodyFrameFrontLimbs。bodyFrameJoint の条件を変えたらここも合わせる）。
    private bool IsSmalLegJointOnBodyFrame(int legJoint)
    {
        bool front = legJoint == 7 || legJoint == 8 || legJoint == 11 || legJoint == 12;
        bool rear = legJoint == 17 || legJoint == 18 || legJoint == 21 || legJoint == 22;
        return (bodyFrameLimbs && (front || rear) && !(bodyFrameLimbsFrontAndTailOnly && rear)) ||
               (bodyFrameRearLimbs && rear) ||
               (bodyFrameFrontLimbs && front);
    }

    // smalDriveFeet の指（10/14/20/24）を書いてよいか: 親の関節（手根・飛節 9/13/19/23）が体の写像で駆動されているときだけ（同じ肢の中で写像を混ぜない。
    // 指の式は体の写像なので、親が受け身・F2・方向の転写のまま指だけ体の写像にすると球節で 2 つの写像が混ざる。反論役 I2 の指摘）。
    // 19/23 は smalDriveCarpusHock が ON なら既定で体の写像（bodyFrameRearLimbs）、9/13 は加えて bodyFrameFrontLimbs か bodyFrameLimbs が要る。
    // 前肢の無いリグ（鳥、非四足モードの cache.smalNoFrontLimbs、2026-10-08）は false: 前足 10/14・指 20/24 は smalDriveFeet が ON でも受け身のまま
    // （鳥の脛 19/23 は smalNonQuadrupedHock だけで動かし、全体の手根・飛節・指の切り替えに結び付けない）。非四足モードが OFF なら smalNoFrontLimbs は false で今と同じ。
    private bool IsSmalFootParentOnBodyFrame(AnimalRigCache cache, int footJoint)
    {
        if (cache != null && cache.smalNoFrontLimbs)
        {
            return false;
        }

        if (!smalDriveCarpusHock || smalAbsoluteDirectionLegsOnly || (smalAbsoluteDirection && !smalAbsoluteDirectionHeadTailOnly))
        {
            return false;
        }

        int carpusOrHock = SmalJointParentArray[footJoint];
        return IsSmalLegJointOnBodyFrame(SmalJointParentArray[carpusOrHock]);
    }

    // smalAbsoluteDirectionLegsOnly（既定 OFF）を掛ける関節: 脚 7/8/11/12/17/18/21/22。尾 25/26・首 15・頭 16 は入れない。
    // 手根・飛節 9/13/19/23 も入れる（rest の向きを持つのは smalDriveCarpusHock のときだけなので、効くのもそのときだけ）。上の区間を SMAL の向きにして中手・中足だけ
    // 相対の転写（根に剛体の bind × 振り）にすると、手根・飛節の角が SMAL の角にも bind の角にもならないため。smalAbsoluteDirection と同時の扱いと同じ。
    // 非四足モードの前肢の無いリグ（鳥、2026-10-08）の脛 19/23 は、rest の向きを持つ smalNonQuadrupedHock のときだけ効く（smalDriveCarpusHock は見ない）。
    private static bool IsSmalAbsoluteDirectionLegJoint(int joint)
    {
        return joint == 7 || joint == 8 || joint == 11 || joint == 12 ||
               joint == 17 || joint == 18 || joint == 21 || joint == 22 ||
               joint == 9 || joint == 13 || joint == 19 || joint == 23;
    }

    // 葉の関節（指 10/14/20/24、smalDriveFeet）の world 回転: B の頭と同じく、積んだ回転全体を根と同じ体の写像で（鏡映 D_x → K⁻¹ → worldFk0）。
    //   tw = worldFk0 · K⁻¹ · mirX(smalAccum[j]) · K · bindWorld
    // smalAccum[j] が恒等なら根に剛体の bind（イベントの既定の姿勢と両立する）。bind の world から作り、骨の今の回転は読まないので、前の tick の分は
    // 積み重ならない（メモリ animal_bones_not_reset_each_tick）。ねじれも入る（葉の関節は区間の向きを持たないため）。
    private static Quaternion SmalLeafBodyFrameWorld(Quaternion worldFk0, Quaternion kMap, Quaternion accum, Quaternion bindWorld)
    {
        Quaternion mirrored = new Quaternion(accum.x, -accum.y, -accum.z, accum.w);
        return worldFk0 * Quaternion.Inverse(kMap) * mirrored * kMap * bindWorld;
    }

    private static void LogSmalFeetOnce(AnimalRigCache cache, SmalDistalLegState s)
    {
        if (s.loggedFeet)
        {
            return;
        }

        s.loggedFeet = true;
        Debug.Log(s.excluded
            ? $"[SMALFEET] smalDriveFeet model={s.modelKey} は除外（Roll 骨のリグ）→ 指は今のまま"
            : $"[SMALFEET] smalDriveFeet model={s.modelKey} 10={SmalBoneLabel(s.frontFoot[0])} 14={SmalBoneLabel(s.frontFoot[1])} 20={SmalBoneLabel(cache.leftRearToe)} 24={SmalBoneLabel(cache.rightRearToe)}" +
              "（none = 写さない: 前は paw 役の子が 1 つでない・メッシュ・無い、後ろは toe 役が無い）");
    }

    // smalDriveFeet（既定 OFF）: 前足 10/14 を書く。前足の骨が無い・除外のモデル・paw 役の bind が無いなら false（今の経路: tw だけ計算して何も書かない）。
    // 前足の bind の world = paw 役の bind の world × 前足の bind の局所（控えた時点の「親 × bind の局所」。前足の親は paw 役）。
    private bool TryApplySmalFrontFoot(AnimalRigCache cache, int joint, Quaternion worldFk0, Quaternion kMap, Quaternion accum, Quaternion[] tw)
    {
        SmalDistalLegState s = GetSmalDistalLegState(cache);
        LogSmalFeetOnce(cache, s);
        int side = joint == 10 ? 0 : 1;
        Transform paw = side == 0 ? cache.leftFrontPaw : cache.rightFrontPaw;
        Transform foot = s.frontFoot[side];
        if (s.excluded || foot == null || paw == null || !cache.bindRotWorld.TryGetValue(paw, out Quaternion pawBindWorld))
        {
            return false;
        }

        Quaternion world = SmalLeafBodyFrameWorld(worldFk0, kMap, accum, pawBindWorld * s.frontFootBindLocal[side]);
        if (!IsFiniteQ(world))
        {
            return false;
        }

        tw[joint] = world;
        TransformWriter.ApplyWorldRotation(foot, world);
        s.frontFootWritten[side] = true;
        return true;
    }

    // smalDriveFeet を切ったとき: 書いた前足を一度だけ「親 × bind の局所」へ戻す（gestureBindLocal に入っている front_x_paw は毎 tick 戻されるが、入っていない骨
    // （Wolf の LegFLAnkle など）は書いた回転が残るため）。主ループの関節 10/14 で呼ぶので親（paw 役）はこの tick に書いた後。書いていなければ何もしない。
    private void RestoreSmalFrontFoot(AnimalRigCache cache, int joint)
    {
        if (smalDistalLegStates.Count == 0 || !smalDistalLegStates.TryGetValue(cache, out SmalDistalLegState s))
        {
            return;
        }

        int side = joint == 10 ? 0 : 1;
        Transform foot = s.frontFoot[side];
        if (!s.frontFootWritten[side] || foot == null)
        {
            return;
        }

        Quaternion parentWorld = foot.parent != null ? foot.parent.rotation : Quaternion.identity;
        TransformWriter.ApplyWorldRotation(foot, parentWorld * s.frontFootBindLocal[side]);
        s.frontFootWritten[side] = false;
    }

    // smalDriveFeet（既定 OFF）: 後足 20/24（toe 役の骨）の world 回転。除外のモデル・bind が無いなら false（今の受け身のまま）。
    private bool TryGetSmalRearToeWorld(AnimalRigCache cache, Transform toe, Quaternion worldFk0, Quaternion kMap, Quaternion accum, out Quaternion world)
    {
        world = Quaternion.identity;
        SmalDistalLegState s = GetSmalDistalLegState(cache);
        LogSmalFeetOnce(cache, s);
        if (s.excluded || toe == null || !cache.bindRotWorld.TryGetValue(toe, out Quaternion toeBindWorld))
        {
            return false;
        }

        world = SmalLeafBodyFrameWorld(worldFk0, kMap, accum, toeBindWorld);
        return IsFiniteQ(world);
    }

    // ハンドオフの控え（CaptureBoneLocalRotations）に、smalDriveFeet が書いた前足を足す（役の骨ではないので控えに入っておらず、混ぜないとハンドオフの
    // 最初の tick で跳ぶ。尾の鎖の AddSmalTailChainBoneLocalRotations と同じ）。書いていないときは何もしない。
    private void AddSmalFrontFootBoneLocalRotations(AnimalRigCache cache, Dictionary<Transform, Quaternion> destination)
    {
        if (cache == null || !smalDistalLegStates.TryGetValue(cache, out SmalDistalLegState s))
        {
            return;
        }

        for (int side = 0; side < 2; side++)
        {
            if (s.frontFootWritten[side])
            {
                AddBoneLocalRotation(destination, s.frontFoot[side]);
            }
        }
    }

    // 副軸の一時上書き（頭の照準を差し替えたときだけ使う）。
    private int SmalRollRefOverrideJoint = -1;

    private bool TryGetRollRef(int joint, out int rollRefJoint)
    {
        if (SmalRollRefOverrideJoint >= 0)
        {
            rollRefJoint = SmalRollRefOverrideJoint;
            return true;
        }
        return SmalRollRefJoint.TryGetValue(joint, out rollRefJoint);
    }

    private static Transform GetSmalBoneForJoint(AnimalRigCache cache, int joint)
    {
        switch (joint)
        {
            case 7:  return cache.leftFrontUpper;
            case 8:  return cache.leftFrontLower;
            case 9:  return cache.leftFrontPaw;
            case 11: return cache.rightFrontUpper;
            case 12: return cache.rightFrontLower;
            case 13: return cache.rightFrontPaw;
            case 15: return cache.neck;
            case 16: return cache.head;
            case 17: return cache.leftRearUpper;
            case 18: return cache.leftRearLower;
            case 19: return cache.leftRearPaw;
            case 20: return cache.leftRearToe;
            case 21: return cache.rightRearUpper;
            case 22: return cache.rightRearLower;
            case 23: return cache.rightRearPaw;
            case 24: return cache.rightRearToe;
            case 25: return cache.tailBase;
            case 26: return cache.tailMid;
            case 27: return cache.tailTip;
            default: return null;
        }
    }

    // ================================================================ 動物の胴・首・頭・口（2026-10-08、全関節の監査の J-09・J-04・J-21、担当 I3）
    // どれも既定 OFF の新しい振る舞い（フラグの説明は smalTrunkChordFromSmal などの宣言）。TryApplyAnimalSmalFk の主ループの後に、親から順に呼ぶ:
    // 胴（ApplySmalTrunkChord）→ 首の中間の骨（ApplySmalNeckChain、Δ の基準）→ 首の鎖を SMAL の折れ線で（smalNeckFullChain）→ 頭の鼻（smalHeadNoseAim）→
    // 顎（smalDriveJaw）。尾の鎖の既定の姿勢の混ぜ（J-23）は ApplySmalTailChain。骨は ApplyWorldRotation だけで書く。新しく駆動する骨は毎 tick
    // 「この tick に書いた親の world × bind の局所」から作る（今の回転から始めない。Animal のリグは毎 tick bind に戻らないので、前の tick に掛けた分が
    // 積み重なる。2026-10-04 の首の鎖の暴走、メモリ animal_bones_not_reset_each_tick）。

    // 背骨の SMAL の区間（関節 1→2 … 5→6 の rest の位置の差。Docs/smal-rest-skeleton.json、SMAL の体の座標: +X が尾から頭、+Y が左、+Z が上）。区間 s は関節 s + 1 の
    // smalAccum で回る（0→1 は長さ 0 なので入れない）。rest の弦（J6 − J0）は区間を同じ順に足して作る（body_pose が恒等なら D がちょうど恒等になる）。
    private static readonly Vector3[] SmalTrunkRestSegments =
    {
        new Vector3(0.110146f, 0f, 0.008641f),
        new Vector3(0.094800f, 0f, -0.178190f),
        new Vector3(0.128642f, 0f, -0.023034f),
        new Vector3(0.133944f, 0f, 0.001210f),
        new Vector3(0.129375f, 0f, 0.169815f),
    };
    private static readonly Vector3 SmalTrunkRestChord = SumSmalSegments(SmalTrunkRestSegments);

    // 首の SMAL の区間（関節 6→15、15→16 の rest の位置の差。同じ json）。区間 s は関節 SmalNeckSegmentJoint[s] の smalAccum で回る。2 本目の向きは SmalRestDirByJoint[15] と同じ。
    private static readonly Vector3[] SmalNeckRestSegments =
    {
        new Vector3(0.217657f, 0f, 0.082016f),
        new Vector3(0.184805f, 0f, -0.021335f),
    };
    private static readonly int[] SmalNeckSegmentJoint = { 6, 15 };
    private static readonly float[] SmalNeckArcNorm = BuildSmalArcNorm(SmalNeckRestSegments);

    // n_smal（smalHeadNoseAim の新しい定数）: SMAL の頭の座標での鼻の向き (cos15°, 0, −sin15°)（前から 15° 下）。
    // 監査（AX-4 と反論役 vax4）が keypoints3d の kp18（頭）→ kp24（鼻）を生の SMAL の頭の座標（smalAccum[16]⁻¹）へ戻して較正した値: 中央 犬 −15.3°・猫 −14.2°
    // （2 本の track が 1.2° 以内で一致、広がり p50 3.7° / 2.8°）。kp18 は Head（J16）から 0.042 m 離れた表面の点（D-007）なので、生成側に rest の kp18・kp24 の
    // 位置を照会して確定させたい（未確定）。データから較正した定数なので新しい振る舞い。
    private static readonly Vector3 SmalHeadNoseRest = new Vector3(0.9659258f, 0f, -0.2588190f);

    private static Vector3 SumSmalSegments(Vector3[] segments)
    {
        Vector3 sum = Vector3.zero;
        for (int s = 0; s < segments.Length; s++)
        {
            sum += segments[s];
        }

        return sum;
    }

    // 折れ線の各点の正規化した弧長（尾の BuildSmalTailArcNorm と同じ作り方）。
    private static float[] BuildSmalArcNorm(Vector3[] segments)
    {
        var c = new float[segments.Length + 1];
        for (int i = 0; i < segments.Length; i++)
        {
            c[i + 1] = c[i] + segments[i].magnitude;
        }

        for (int i = 1; i < c.Length; i++)
        {
            c[i] /= c[c.Length - 1];
        }

        return c;
    }

    // ---------------------------------------------------------------- J-09 胴

    private sealed class SmalTrunkChordBind
    {
        public Transform[] bones;        // cache.trunkChain（spine の子 → … → 肩甲帯）。最後が肩甲帯
        public Quaternion[] bindLocal;   // bind の局所回転（cache.gestureBindLocal。ApplyAnimalSkeletonPlacement が毎 tick この値へ戻している）
        public float[] weight;           // 肩甲帯の手前の骨 k の w_k（骨 k の区間 = 骨 k → 骨 k + 1）
        public Quaternion[] world;       // この tick に書く world（全部求めてから書く）
        public Transform[] keep;         // 胴の子孫の役の骨（前脚・首・頭など、GetSmalBoneForJoint の骨。階層の浅い順）。胴を書いても world を保つ
        public Quaternion[] keepWorld;   // keep の、胴を書く前の world（この tick に主ループなどが書いた値）
        public string modelKey;
        public bool loggedApply;
    }

    private readonly Dictionary<AnimalRigCache, SmalTrunkChordBind> smalTrunkChordBinds = new Dictionary<AnimalRigCache, SmalTrunkChordBind>();

    // 最初に使うときに一度だけ作る（使えないリグは null を控える）。骨の間の距離は回転に依らない（局所位置は誰も書かない）ので、いつ測っても bind と同じ。
    private SmalTrunkChordBind GetSmalTrunkChordBind(AnimalRigCache cache)
    {
        if (smalTrunkChordBinds.TryGetValue(cache, out SmalTrunkChordBind found))
        {
            return found;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        SmalTrunkChordBind made = null;
        string why = string.Empty;
        int n = cache.trunkChain.Count;
        if (cache.spine == null || n == 0)
        {
            why = "胴の鎖が無い（肩甲帯が spine 自身のリグ）";
        }
        else if (cache.trunkChain[0] == null || cache.trunkChain[0].parent != cache.spine)
        {
            why = "胴の鎖の最初の骨の親が spine でない";
        }
        else
        {
            var bones = cache.trunkChain.ToArray();
            var bindLocal = new Quaternion[n];
            for (int k = 0; k < n && why.Length == 0; k++)
            {
                if (bones[k] == null || !cache.gestureBindLocal.TryGetValue(bones[k], out bindLocal[k]))
                {
                    why = "胴の骨の bind の局所が控えてない";
                }
            }

            if (why.Length == 0)
            {
                var seg = new float[n - 1];
                float total = 0f;
                for (int k = 0; k + 1 < n; k++)
                {
                    seg[k] = Vector3.Distance(bones[k + 1].position, bones[k].position);
                    total += seg[k];
                }

                var weight = new float[n - 1];
                float arc = 0f;
                for (int k = 0; k + 1 < n; k++)
                {
                    weight[k] = total > 0.000001f ? 2f * (arc + 0.5f * seg[k]) / total : 0f;
                    arc += seg[k];
                }

                // 胴の子孫の役の骨（主ループが world の絶対値で書く骨）。重なりは除く（同じ骨が 2 つの役に当たるリグ）。書き直す順は Unity の階層の浅い順
                // （親を先に戻す。SMAL のトポロジカル順と階層の順が食い違うリグでも子の world が崩れない）。
                var keep = new List<Transform>(SmalJointTopologicalOrder.Length);
                for (int i = 0; i < SmalJointTopologicalOrder.Length; i++)
                {
                    Transform role = GetSmalBoneForJoint(cache, SmalJointTopologicalOrder[i]);
                    if (role != null && role != bones[0] && role.IsChildOf(bones[0]) && !keep.Contains(role))
                    {
                        keep.Add(role);
                    }
                }

                var depth = new Dictionary<Transform, int>(keep.Count);
                foreach (Transform role in keep)
                {
                    int dpt = 0;
                    for (Transform t = role.parent; t != null; t = t.parent)
                    {
                        dpt++;
                    }

                    depth[role] = dpt;
                }

                var ordered = new List<Transform>(keep.Count);
                for (int dpt = 0; ordered.Count < keep.Count; dpt++)
                {
                    foreach (Transform role in keep)
                    {
                        if (depth[role] == dpt)
                        {
                            ordered.Add(role);
                        }
                    }
                }

                keep = ordered;

                made = new SmalTrunkChordBind
                {
                    bones = bones, bindLocal = bindLocal, weight = weight, world = new Quaternion[n],
                    keep = keep.ToArray(), keepWorld = new Quaternion[keep.Count], modelKey = modelKey,
                };
            }
        }

        smalTrunkChordBinds[cache] = made;
        if (made != null)
        {
            var names = new string[made.bones.Length];
            for (int k = 0; k < names.Length; k++)
            {
                names[k] = made.bones[k].name;
            }

            var weights = new string[made.weight.Length];
            for (int k = 0; k < weights.Length; k++)
            {
                weights[k] = made.weight[k].ToString("F3");
            }

            Debug.Log($"[TRUNKCHORD] model={modelKey} bones={n} ({string.Join(",", names)}) w=[{string.Join(",", weights)}] 肩甲帯={names[names.Length - 1]}（A6） " +
                      $"world を保つ子孫の役の骨 {made.keep.Length} 本");
        }
        else
        {
            Debug.Log($"[TRUNKCHORD] model={modelKey} は胴を書かない（{why}）→ 今の写し方のまま");
        }

        return made;
    }

    // 胴を書く。戻り値: 肩甲帯まで書いたら true（首の中間の骨の Δ を今の肩甲帯から作る合図）。
    private bool ApplySmalTrunkChord(AnimalRigCache cache, Quaternion worldFk0, Quaternion kMap, Quaternion[] smalAccum)
    {
        SmalTrunkChordBind trunk = GetSmalTrunkChordBind(cache);
        if (trunk == null || !cache.bindRotWorld.TryGetValue(cache.spine, out Quaternion spineBind) || !IsFiniteQ(spineBind))
        {
            return false;
        }

        // SMAL の曲げた弦（体の座標、関節 0 → 6。smalAccum は主ループで 1〜6 まで積んである平滑後の局所の積）と、rest の弦からの振り D。
        Vector3 posedChord = Vector3.zero;
        for (int s = 0; s < SmalTrunkRestSegments.Length; s++)
        {
            posedChord += smalAccum[s + 1] * SmalTrunkRestSegments[s];
        }

        Quaternion chordSwing = posedChord.sqrMagnitude > 0.000000000001f
            ? Quaternion.FromToRotation(SmalTrunkRestChord, posedChord)
            : Quaternion.identity;
        Quaternion kInv = Quaternion.Inverse(kMap);
        // bindW は spine の bind の world に bind の局所を積んで作る（worldFk0 × bindW =「この tick に spine を書いた world × bind の局所 …」= 根に剛体の姿勢）。
        Quaternion bindWorld = spineBind;
        int n = trunk.bones.Length;
        for (int k = 0; k < n; k++)
        {
            bindWorld = bindWorld * trunk.bindLocal[k];
            // 肩甲帯の手前は D^w_k（w_k は 1 を超えるので SlerpUnclamped。D は FromTo なので w ≥ 0 の側にあり、恒等からの延長がそのまま D の w_k 乗）、肩甲帯は A6。
            Quaternion x = k + 1 < n ? Quaternion.SlerpUnclamped(Quaternion.identity, chordSwing, trunk.weight[k]) : smalAccum[6];
            Quaternion mirroredX = new Quaternion(x.x, -x.y, -x.z, x.w);
            trunk.world[k] = worldFk0 * kInv * mirroredX * kMap * bindWorld;
            if (!IsFiniteQ(trunk.world[k]))
            {
                return false;
            }
        }

        // 胴を回すと、その子孫で主ループ（とそれより前の処理）が world で書いた役の骨（前脚・首・頭）の world も動くので、胴を書く前の world を控えて、
        // 書いた後に同じ world へ書き直す（向きはそのまま、位置だけ胴に付いてくる。ApplySmalNeckChain が中間の骨を回した後に neck・head を書き直すのと同じ理由）。
        // 親から順（階層の浅い順）。役の骨より下の骨（肉球の先・顔の骨など）は局所のままなので、親を戻せば world も戻る。
        for (int i = 0; i < trunk.keep.Length; i++)
        {
            trunk.keepWorld[i] = trunk.keep[i].rotation;
        }

        for (int k = 0; k < n; k++)
        {
            TransformWriter.ApplyWorldRotation(trunk.bones[k], trunk.world[k]);
        }

        for (int i = 0; i < trunk.keep.Length; i++)
        {
            TransformWriter.ApplyWorldRotation(trunk.keep[i], trunk.keepWorld[i]);
        }

        if (!trunk.loggedApply)
        {
            trunk.loggedApply = true;
            Debug.Log($"[TRUNKCHORD] applied model={trunk.modelKey} bones={n} chordSwing={Quaternion.Angle(Quaternion.identity, chordSwing):F1}deg A6={Quaternion.Angle(Quaternion.identity, smalAccum[6]):F1}deg");
        }

        return true;
    }

    // ---------------------------------------------------------------- J-04 / J-21 首・頭・顎

    // 主ループと首の中間の骨の後に呼ぶ（呼び出し側は、どれかのフラグが ON か、前に書いた骨が残っているときだけ呼ぶ）。親から順: 首 → 頭 → 顎。
    private void ApplySmalNeckHeadJaw(AnimalRigCache cache, Quaternion worldFk0, Quaternion kMap, Quaternion[] smalAccum, Quaternion[] tw, Quaternion mouthLocal)
    {
        if (smalNeckFullChain && TryGetSmalNeckFullChainForModel(cache, out SmalNeckChainBind neckChain))
        {
            ApplySmalNeckFullChain(cache, neckChain, worldFk0, kMap, smalAccum, tw);
        }
        else
        {
            RestoreSmalNeckFullChainBind(cache, tw);
        }

        if (smalHeadNoseAim)
        {
            ApplySmalHeadNoseAim(cache, worldFk0, kMap, smalAccum, tw);
        }

        if (smalDriveJaw)
        {
            ApplySmalJaw(cache, kMap, mouthLocal);
        }
        else
        {
            RestoreSmalJawBind(cache);
        }
    }

    // 前に首の鎖・顎を書いたことがあるか（フラグをすべて切った後も、一度だけ bind へ戻すために呼び出し側が見る）。
    private bool HasSmalNeckHeadJawState()
    {
        return smalNeckChainBinds.Count > 0 || smalJawBinds.Count > 0;
    }

    private sealed class SmalNeckChainBind
    {
        public Transform[] bones;          // Neck01 → … → neck（cache.neck の祖先で名前に neck を含む骨と cache.neck。ApplySmalNeckChain と同じ決め方）
        public Quaternion[] bindLocalRot;  // bind の局所回転（中間の骨は cache.neckChainBindLocal を ApplySmalNeckChain と共有、neck は cache.bindRotLocal）
        public Vector3[] bindLocalPos;     // [i] = bones[i] の bind の局所位置、[n] = 頭の局所位置（終点）
        public float[] arcNorm;            // [i] = bones[i] の正規化した弧長（始点 = 鎖の最初の骨の親（肩甲帯）、終点 = 頭）、[n] = 1
        public string modelKey;
        public readonly Vector3[] smalPoints = new Vector3[3];
        public Quaternion[] relWorld;
        public Quaternion[] writtenWorld;
        public bool rotationsWritten;
        public bool loggedApply;
        public bool loggedSkip;
    }

    private readonly Dictionary<AnimalRigCache, SmalNeckChainBind> smalNeckChainBinds = new Dictionary<AnimalRigCache, SmalNeckChainBind>();
    private readonly SmalTailModelList smalNeckChainModelList = new SmalTailModelList();

    // 首の鎖を控え（最初に使うときに一度だけ。使えないリグは null）、smalNeckFullChainModels に載ったモデルなら true。載っていなければ一度だけログを出して false。
    private bool TryGetSmalNeckFullChainForModel(AnimalRigCache cache, out SmalNeckChainBind chain)
    {
        chain = null;
        if (!smalNeckChainBinds.TryGetValue(cache, out SmalNeckChainBind found))
        {
            found = CaptureSmalNeckFullChainBind(cache);
            smalNeckChainBinds[cache] = found;
        }

        if (found == null)
        {
            return false;
        }

        if (smalNeckChainModelList.Contains(smalNeckFullChainModels, found.modelKey))
        {
            chain = found;
            return true;
        }

        if (!found.loggedSkip)
        {
            found.loggedSkip = true;
            Debug.Log($"[NECKCHAIN] model={found.modelKey} は smalNeckFullChainModels='{smalNeckFullChainModels}' に無い → 今の写し方のまま");
        }

        return false;
    }

    // 中間の骨の bind の局所は cache.neckChainBindLocal（ApplySmalNeckChain と同じく、最初に見たときに控える。どちらが先でも、この 2 つのほかに中間の骨を書く処理は
    // 無いので、控えた時点ではまだ bind）。局所位置は誰も書かず、骨の間の距離は回転に依らないので、弧長はいつ測っても bind と同じ。
    private SmalNeckChainBind CaptureSmalNeckFullChainBind(AnimalRigCache cache)
    {
        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        if (cache.neck == null || cache.head == null || cache.head.parent != cache.neck ||
            !cache.bindRotLocal.TryGetValue(cache.neck, out Quaternion neckBindLocal))
        {
            Debug.Log($"[NECKCHAIN] model={modelKey} は頭の親が neck でない（または neck の bind が無い）→ 今の写し方のまま");
            return null;
        }

        var list = new List<Transform>(4);
        for (Transform t = cache.neck.parent;
             t != null && t != cache.spine && t.name.IndexOf("neck", System.StringComparison.OrdinalIgnoreCase) >= 0;
             t = t.parent)
        {
            list.Add(t);
        }

        list.Reverse();
        list.Add(cache.neck);
        Transform start = list[0].parent;
        if (start == null)
        {
            return null;
        }

        int n = list.Count;
        var chain = new SmalNeckChainBind
        {
            bones = list.ToArray(),
            bindLocalRot = new Quaternion[n],
            bindLocalPos = new Vector3[n + 1],
            arcNorm = new float[n + 1],
            modelKey = modelKey,
            relWorld = new Quaternion[n],
            writtenWorld = new Quaternion[n],
        };
        float arc = 0f;
        Vector3 previous = start.position;
        for (int i = 0; i < n; i++)
        {
            Transform b = list[i];
            if (b == cache.neck)
            {
                chain.bindLocalRot[i] = neckBindLocal;
            }
            else
            {
                if (!cache.neckChainBindLocal.TryGetValue(b, out Quaternion bindLocal))
                {
                    bindLocal = b.localRotation;
                    cache.neckChainBindLocal[b] = bindLocal;
                }

                chain.bindLocalRot[i] = bindLocal;
            }

            chain.bindLocalPos[i] = b.localPosition;
            arc += Vector3.Distance(b.position, previous);
            chain.arcNorm[i] = arc;
            previous = b.position;
        }

        chain.bindLocalPos[n] = cache.head.localPosition;
        arc += Vector3.Distance(cache.head.position, previous);
        chain.arcNorm[n] = arc;
        if (arc < 0.000001f)
        {
            return null;
        }

        for (int i = 0; i <= n; i++)
        {
            chain.arcNorm[i] /= arc;
        }

        var names = new string[n];
        var arcs = new string[n + 1];
        for (int i = 0; i < n; i++)
        {
            names[i] = chain.bones[i].name;
        }

        for (int i = 0; i <= n; i++)
        {
            arcs[i] = chain.arcNorm[i].ToString("F3");
        }

        Debug.Log($"[NECKCHAIN] model={modelKey} bones={n} ({string.Join(",", names)}、始点 {start.name}・終点 {cache.head.name}) arcNorm=[{string.Join(",", arcs)}] SMAL={SmalNeckArcNorm[1]:F3}");
        return chain;
    }

    private void ApplySmalNeckFullChain(AnimalRigCache cache, SmalNeckChainBind chain, Quaternion worldFk0, Quaternion kMap, Quaternion[] smalAccum, Quaternion[] tw)
    {
        int n = chain.bones.Length;
        // SMAL の首の折れ線（体の座標、関節 6 からの相対。smalAccum は主ループで積んである平滑後の局所の積）。
        Vector3[] pts = chain.smalPoints;
        pts[0] = Vector3.zero;
        for (int s = 0; s < SmalNeckRestSegments.Length; s++)
        {
            pts[s + 1] = pts[s] + smalAccum[SmalNeckSegmentJoint[s]] * SmalNeckRestSegments[s];
        }

        Transform first = chain.bones[0];
        Quaternion parentWorld = first.parent != null ? first.parent.rotation : Quaternion.identity;
        // 既定の姿勢の重み（J-23、尾の鎖と同じ混ぜ方）。相対の結果 = 今の写し方の world: neck は tw[15]（B）、中間の骨は smalDriveNeckChain なら
        // ApplySmalNeckChain がこの tick に書いた world（どれも書き換える前に読む）、OFF なら「相対の結果の親 × bind の局所」（誰も書かないときの world）。
        float relWeight = Mathf.Clamp01(smalDefaultPoseWeight);
        if (relWeight > 0f)
        {
            Quaternion relParent = parentWorld;
            for (int i = 0; i < n; i++)
            {
                Transform b = chain.bones[i];
                Quaternion rel;
                if (b == cache.neck && IsFiniteQ(tw[15]))
                {
                    rel = tw[15];
                }
                else if (b != cache.neck && smalDriveNeckChain)
                {
                    rel = b.rotation;
                }
                else
                {
                    rel = relParent * chain.bindLocalRot[i];
                }

                chain.relWorld[i] = rel;
                relParent = rel;
            }
        }

        int written = WriteSmalPolylineChain(chain.bones, chain.bindLocalRot, chain.bindLocalPos, chain.arcNorm, Vector3.zero, true,
            pts, SmalNeckArcNorm, worldFk0, Quaternion.Inverse(kMap), parentWorld, chain.relWorld, relWeight, chain.writtenWorld);
        for (int i = 0; i < written; i++)
        {
            if (chain.bones[i] == cache.neck)
            {
                tw[15] = chain.writtenWorld[i];
            }
        }

        // 頭の向きは B のまま（鎖を回すと子の頭の world も動くので書き直す）。
        if (cache.head != null && IsFiniteQ(tw[16]))
        {
            TransformWriter.ApplyWorldRotation(cache.head, tw[16]);
        }

        if (written > 0)
        {
            chain.rotationsWritten = true;
        }

        if (written == n && !chain.loggedApply)
        {
            chain.loggedApply = true;
            Debug.Log($"[NECKCHAIN] applied model={chain.modelKey} bones={n}");
        }
    }

    // smalNeckFullChain を切ったとき・名簿に無いモデル: 書いた首の骨を一度だけ今の写し方へ戻す（書いていなければ何もしない）。smalDriveNeckChain が ON なら中間の骨は
    // ApplySmalNeckChain がこの tick に（剛体の姿勢 × bind の局所から）書き直していて、neck・head も tw で書いてあるので何もしない。
    private void RestoreSmalNeckFullChainBind(AnimalRigCache cache, Quaternion[] tw)
    {
        if (!smalNeckChainBinds.TryGetValue(cache, out SmalNeckChainBind chain) || chain == null || !chain.rotationsWritten)
        {
            return;
        }

        chain.rotationsWritten = false;
        if (smalDriveNeckChain)
        {
            return;
        }

        for (int i = 0; i < chain.bones.Length; i++)
        {
            Transform b = chain.bones[i];
            if (b == cache.neck)
            {
                if (IsFiniteQ(tw[15]))
                {
                    TransformWriter.ApplyWorldRotation(b, tw[15]);
                }

                continue;
            }

            Quaternion parent = b.parent != null ? b.parent.rotation : Quaternion.identity;
            TransformWriter.ApplyWorldRotation(b, parent * chain.bindLocalRot[i]);
        }

        if (cache.head != null && IsFiniteQ(tw[16]))
        {
            TransformWriter.ApplyWorldRotation(cache.head, tw[16]);
        }
    }

    private readonly HashSet<AnimalRigCache> smalNoseAimLogged = new HashSet<AnimalRigCache>();

    // 頭の鼻を SMAL の鼻へ（最小回転、ロールは頭のまま）。基準はこの tick に主ループ（と首の鎖）が書いた tw[16]。頭の今の回転を基準にすると、
    // tw[16] が非有限で主ループが頭を書かなかった tick に前の tick の照準へ重なるので、tw[16] が有限でなければ何もしない（反論役 I3 の指摘）。
    private void ApplySmalHeadNoseAim(AnimalRigCache cache, Quaternion worldFk0, Quaternion kMap, Quaternion[] smalAccum, Quaternion[] tw)
    {
        float aimWeight = 1f - Mathf.Clamp01(smalDefaultPoseWeight);
        if (cache.head == null || aimWeight <= 0f || !IsFiniteQ(tw[16]) || !ResolveHeadNoseLocal(cache))
        {
            return;
        }

        Vector3 noseNow = tw[16] * cache.headNoseLocal;
        Vector3 smalNose = smalAccum[16] * SmalHeadNoseRest;
        Vector3 target = worldFk0 * (Quaternion.Inverse(kMap) * new Vector3(-smalNose.x, smalNose.y, smalNose.z));
        if (noseNow.sqrMagnitude < 0.000000000001f || target.sqrMagnitude < 0.000000000001f)
        {
            return;
        }

        Quaternion aim = Quaternion.FromToRotation(noseNow, target);
        if (aimWeight < 1f)
        {
            aim = Quaternion.Slerp(Quaternion.identity, aim, aimWeight);
        }

        Quaternion world = aim * tw[16];
        if (!IsFiniteQ(world))
        {
            return;
        }

        if (smalNoseAimLogged.Add(cache))
        {
            Debug.Log($"[NOSEAIM] model={AnimalLegMappingFix.Key(KeyFor(cache))} nose={cache.headNoseSource} local={cache.headNoseLocal:F3} 最初の照準 {Quaternion.Angle(Quaternion.identity, aim):F1}deg");
        }

        TransformWriter.ApplyWorldRotation(cache.head, world);
        tw[16] = world;
    }

    private sealed class SmalJawBind
    {
        public Transform jaw;
        public Quaternion bindLocal;   // 頭に対する bind の局所回転（最初に使うときに控える。顎を書くのはこの処理だけなので、そのときはまだ bind）
        public bool written;
        public bool loggedApply;
    }

    private readonly Dictionary<AnimalRigCache, SmalJawBind> smalJawBinds = new Dictionary<AnimalRigCache, SmalJawBind>();

    private SmalJawBind GetSmalJawBind(AnimalRigCache cache)
    {
        if (smalJawBinds.TryGetValue(cache, out SmalJawBind found))
        {
            return found;
        }

        SmalJawBind made = null;
        int candidates = 0;
        if (cache.head != null)
        {
            for (int i = 0; i < cache.head.childCount; i++)
            {
                Transform c = cache.head.GetChild(i);
                string name = c.name.ToLowerInvariant();
                if (name.Contains("jaw") && !name.Contains("upper") && !name.EndsWith("_end"))
                {
                    candidates++;
                    if (made == null)
                    {
                        made = new SmalJawBind { jaw = c, bindLocal = c.localRotation };
                    }
                }
            }
        }

        smalJawBinds[cache] = made;
        Debug.Log(made != null
            ? $"[JAW] model={AnimalLegMappingFix.Key(KeyFor(cache))} jaw={made.jaw.name}（頭の子の候補 {candidates} 本の最初）"
            : $"[JAW] model={AnimalLegMappingFix.Key(KeyFor(cache))} は頭の子に顎の骨が無い → 書かない");
        return made;
    }

    // mouthLocal: 関節 32 の平滑後の局所回転（頭に対する口、smalAccum[16]⁻¹·smalAccum[32]）。
    private void ApplySmalJaw(AnimalRigCache cache, Quaternion kMap, Quaternion mouthLocal)
    {
        SmalJawBind jaw = GetSmalJawBind(cache);
        if (jaw == null || jaw.jaw == null || !IsFiniteQ(mouthLocal) ||
            !cache.bindRotWorld.TryGetValue(cache.head, out Quaternion headBind) || !IsFiniteQ(headBind))
        {
            return;
        }

        // 顎の world = 頭の今の world × headBind⁻¹·K⁻¹·mirX(L32)·K·headBind × 顎の bind の局所。頭が B（worldFk0·K⁻¹·mirX(A16)·K·headBind）のままなら
        // worldFk0·K⁻¹·mirX(A16·L32)·K·bindW[顎] = worldFk0·K⁻¹·mirX(smalAccum[32])·K·bindW[顎] と同じ値（mirX は積を保つ）。
        Quaternion mirrored = new Quaternion(mouthLocal.x, -mouthLocal.y, -mouthLocal.z, mouthLocal.w);
        Quaternion world = cache.head.rotation * Quaternion.Inverse(headBind) * Quaternion.Inverse(kMap) * mirrored * kMap * headBind * jaw.bindLocal;
        if (!IsFiniteQ(world))
        {
            return;
        }

        TransformWriter.ApplyWorldRotation(jaw.jaw, world);
        jaw.written = true;
        if (!jaw.loggedApply)
        {
            jaw.loggedApply = true;
            Debug.Log($"[JAW] applied model={AnimalLegMappingFix.Key(KeyFor(cache))} jaw={jaw.jaw.name} 最初の開き {Quaternion.Angle(Quaternion.identity, mouthLocal):F1}deg");
        }
    }

    // smalDriveJaw を切ったとき: 書いた顎を一度だけ bind（頭 × bind の局所）へ戻す。書いていなければ何もしない。
    private void RestoreSmalJawBind(AnimalRigCache cache)
    {
        if (!smalJawBinds.TryGetValue(cache, out SmalJawBind jaw) || jaw == null || !jaw.written || jaw.jaw == null || cache.head == null)
        {
            return;
        }

        TransformWriter.ApplyWorldRotation(jaw.jaw, cache.head.rotation * jaw.bindLocal);
        jaw.written = false;
    }

    private static bool IsFiniteQ(Quaternion q)
    {
        return !float.IsNaN(q.x) && !float.IsInfinity(q.x)
            && !float.IsNaN(q.y) && !float.IsInfinity(q.y)
            && !float.IsNaN(q.z) && !float.IsInfinity(q.z)
            && !float.IsNaN(q.w) && !float.IsInfinity(q.w);
    }
}
