using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public string bundleFileName = "bundle.svb";
    private const string BundleVideoEntryName = "video.mp4";
    private const string BundleManifestEntryName = "manifest.json";
    private const string BundleMetaEntryName = "meta.bin";
    private const string BundleAnimalControlTargetsEntryName = "source/animal_control_targets.json";
    private const string BundleOtherObjectProxiesEntryName = "source/other_object_proxies.json";
    private const string BundleHumanSmplEntryName = "source/human_smpl_from_sam2.json";
    private const string BundleNormalModeVideoEntryName = "source/pre_removal_stereo_video.mp4";
    private const string ExtractedVideoFileName = "video.mp4";
    private const string ExtractedManifestFileName = "manifest.json";
    private const string ExtractedMetaFileName = "meta.bin";
    private const string ExtractedAnimalControlTargetsFileName = "animal_control_targets.json";
    private const string ExtractedOtherObjectProxiesFileName = "other_object_proxies.json";
    private const string ExtractedHumanSmplFileName = "human_smpl_from_sam2.json";
    private const string ExtractedNormalModeVideoFileName = "pre_removal_stereo_video.mp4";

    private Transform leftScreen;
    private Transform rightScreen;

    [Header("Screens")]
    public GameObject leftScreenPrefab;
    public GameObject rightScreenPrefab;

    [Header("Placement")]
    public Transform headTransform;
    public float screenDistanceMeters = 2.0f;
    public Vector3 screenOffsetMeters = Vector3.zero;
    public bool fitScreenToFov = false;

    [System.Serializable]
    public struct TrackModelIndexOverride
    {
        public int trackId;
        public int modelIndex; // 対象 track の categoryId に応じて humanPrefabs/animalPrefabs のインデックスとして使う
    }

    [Header("Model Debug")]
    [FormerlySerializedAs("useMetaFollow")]
    public bool displayModel = true;
    public int[] displayTrackIds = new int[0]; // 空 = 全トラック表示, 指定あり = そのIDのみ
    public int selectedHumanIndex = 0;
    public int selectedAnimalIndex = 0;
    public int selectedElseIndex = 0;
    // displayTrackIds で表示した track ごとに使うモデルを個別指定したい場合のみ使用。
    // 未指定の track は selectedHumanIndex/selectedAnimalIndex/selectedElseIndex にフォールバックする。
    public TrackModelIndexOverride[] trackModelIndices = new TrackModelIndexOverride[0];

    // Resources/Models/Human, Resources/Models/Animal, Resources/Models/Else から起動時に自動ロード
    private GameObject[] humanPrefabs;
    private GameObject[] animalPrefabs;
    private GameObject[] elsePrefabs;

    [Header("Bones")]
    public bool enableBoneApply = true;
    public float boneApplyAlpha = 1f;
    public bool enableJointSmoothing = true;
    [Range(0f, 1f)] public float jointSmoothingAlpha = 0.35f;

    // SMPL FK のあとに keypoint(jointsWorld) で四肢の向きを上書きするか（AimAt）。
    //
    // AimAt は 2026-06-13 に「FK 座標フレーム変換の限界」への対処として入れたもので、その実体は
    // body_pose の基底変換が抜けていたこと（ConvertSmplBodyPoseToUnityBasis 参照）。変換漏れを
    // 修正する前は素の FK が keypoint と平均 78.2° ずれており、AimAt は補助ではなく
    // **四肢の姿勢を作っている主役**だった。off にすると腕が破綻するのはこのため。
    //
    // 2026-08-07 に基底変換を入れて素の FK は平均 8.5° まで一致するようになったので、
    // AimAt は残差を詰める補正の位置づけに変わる。false で純 FK と比較できる
    // （手の向きも TryApplyHandFkAfterAimAt ではなく FK ループ内で決まる）。
    //
    // 残差の性質: keypoint と body_pose は独立した推定で、左肘では 22〜30° 食い違う。
    // AimAt を有効にすると「腕の付け根は SMPL / 腕の向きは keypoint」という混在になり、
    // かつ AimAt は向きだけで位置を合わせないので骨長差が手先に出る
    // （Docs/smpl-retargeting.md 調査ログ 2026-08-07）。
    public bool enableKeypointAimAt = true;


    [Header("Other Proxy")]
    public bool showOtherProxyBoxes = true;
    public Color otherProxyBoxColor = new Color(1f, 0.78f, 0.18f, 0.32f);

    [Header("Anchor Depth")]
    // bundle の z01 は背景（床・観客席）を含めた全画面で 0..1 に正規化されているため、
    // 検出オブジェクトだけを見ると狭い範囲にしか分布しない（bundle_human.svb で 0.178〜0.406）。
    // その結果 PopoutRangeMeters 0.35m のうち 23% しか使われず、奥行きが潰れていた。
    // ON にすると、その bundle で実際に使われている範囲を 0..1 に引き伸ばしてから配置する。
    // 前後関係は単調変換なので保たれる。
    //
    // 2026-08-06 実測の結果、既定は OFF。理由は 2 つ:
    //   1. 接触関係から逆算した適正な深度差は 0.021m で、正規化なし 0.0192m が既に一致する。
    //      正規化すると 0.0718m と 3.4 倍過大になる。
    //   2. 深度を広げると、頭を動かしたときに 2D 映像と 3D モデルの上下ずれが拡大する
    //      （camLocal.y が z に比例するため）。実機でも y のずれが増えることを確認済み。
    public bool enableAnchorDepthRangeNormalization = false;
    public bool logAnchorDepthRange = false;

    // 診断用: disparity → 距離の変換に使っている実効レンジを 1 度だけ出す。
    public bool logInverseDepthRange = false;

    // スクリーン面からどれだけ手前に出せるかの幅。奥行きの「強さ」を決める。
    //
    // 反比例変換に直したことで奥行きの比は正しくなったが、anchor_z 自体の誤差も実寸で
    // 出るようになった。実測（bundle_human.svb, f=1500〜1800 の頭上のボール）では、
    // 頭に接しているボールが人より 7〜13cm 手前に浮く。ボールの world 直径が 0.049m
    // なので、本来は半径 0.024m 程度に収まるべき差である。
    //
    // 値の目安:
    //   0.35 … 実世界の奥行き変化をほぼそのまま再現するが、depth の誤差も等倍で出る
    //   0.25 … 実世界の変化幅（配置後の身長の約 0.88 倍）に相当。理屈上の適正値
    //   0.15 … 接触物の浮きは目立たなくなるが、奥行き感は乏しくなる
    // 実機で見ながら調整できるよう Inspector に出している。
    [Min(0f)] public float popoutRangeMeters = 0.35f;

    // ② のスケール決定は bboxWorldH = (2*bboxH/eye_h) * (anchorZ/fy) を使うが、この式は
    // 「被写体が anchorZ という 1 枚の面にある」前提である。人体は前後（視線方向）に広がって
    // いるため手前の部位ほど大きく投影され、骨格の投影高さは常に bbox より大きくなる。
    // 2026-08-18 のバッチ実測では立位で 7%、深い前傾で 76% 過大だった。meta.bin の
    // keypoints3d を同じスケールで投影しても同じ比（1.205 対 1.238）になるので、FK や
    // モデル固有の誤差ではなく式の前提そのものの問題である。
    //
    // ON にすると、shot 先頭で FK を適用したあとに骨格の投影高さを実測し、それが bbox
    // 高さに一致するようロック済みスケールを逆算し直す（RefineLockedScaleFromProjectedBones）。
    // 姿勢ごとに必要な補正量は 1.07〜1.76 と変動するため単一スケールで全姿勢は合わせられないが、
    // 基準フレームでの誤差は消える。shot 先頭が立位でない bundle_animal では効果が大きい
    // （実測で 8 shot 中 4 shot が先頭で 25% 以上外れ、最悪 2.62 倍）。
    public bool refineScaleFromProjectedBones = true;

    // ロック済みスケールを固定したまま、毎フレーム「投影された骨格の高さが bbox 高に一致する」
    // 深度へモデルを動かす。投影高は span(f) * scale * f_px / z(f) なので、z を bbox から
    // 逆算すれば boneRatio ≡ 1.0 になる。スケールは動かさないので、毎フレームのスケール補正で
    // 起きた破綻（0.3 秒で身長 ×0.56）は生じない。
    //
    // 2026-08-20 の全編実測（bundle_human.svb, 2156f、boneRatio は 1.0 が理想）:
    //   OFF（従来）    median 1.082 / p10-p90 0.985-1.279 / max 2.269 / 1.3 超 8.8% / 球が手前 79.0%
    //   ON  k=1.0     median 0.998 / p10-p90 0.986-1.066 / max 1.698 / 1.3 超 2.0% / 球が手前 87.7%
    //
    // 注意: `other`（Else）には骨格が無いため適用されない。人と Else で深度の基準が分かれるので、
    // `bundle_train.svb` のような Else のみの bundle には一切効果がない。
    // Generic リグ（Animal）でも投影ボーンを解決するか。false にすると従来どおり
    // Humanoid 以外では ⑧・スケール再ロック・投影下端合わせが動かない。
    // 2026-08-26 に、これらが Animal で一度も動いていなかったことが判明したため追加。
    public bool projectGenericRigBones = true;

    // ⑧ が合わせる相手を「bbox 高」から「見切れを補った推定全高」に変えるか。
    // bbox は可視部分だけなので、見切れフレームで bbox 高に合わせるとモデルが縮む。
    // 詳細は ResolveUnclippedTargetHeight のコメント。
    public bool extendTargetHeightForClippedBBox = true;

    // 外挿の上限（bbox 高の何倍まで許すか）。1 フレームの推定ミスで暴れないための保護。
    // 1.6 は実測で決めた（2026-08-27、bundle_animal）。1.6 / 2.0 / 3.0 を振ったところ
    // 全体の誤差 median が 11.1% / 12.5% / 12.5%、欠損率 45% 超の帯では期待 2.00 に対し
    // 1.81 / 2.43 / 2.59 で、1.6 が最も近い。上限なしだと外挿が効きすぎて逆に大きくなる。
    public float maxClippedHeightExtrapolation = 1.6f;

    // ⑧ が発動する ratio（投影高 ÷ 目標高）の下限。スケール再ロック側の
    // MinProjectedBoneRatioForScaleRefine とは独立に振れるようにした。
    // 0.2 は実測で決めた（2026-08-27）。0.4 のままだと shot 内で被写体の見かけが 3 倍に
    // なる場面（bundle_animal 29.9〜32.7s）でガードに張り付き、**最も補正が要るフレームで
    // ⑧ が何もしなくなる**（32 秒台の sizeRatio が 0.416）。0.2 にすると 0.692 まで戻る。
    // animal の全体誤差・揺れ、human の boneRatio/球との距離/姿勢一致はすべて不変。
    public float depthRefineMinRatio = 0.2f;

    // ⑧ の平滑化を速める相対誤差のしきい値。lo 以下は従来どおりの平滑化、hi 以上で
    // ほぼ即座に追従する。1.0 に設定すると実質無効（従来の挙動）。
    // 0.15 は実測（2026-08-27、animal）。0.30 / 0.15 / 0.08 を振り、0.15 が
    // 全体誤差 median 10.1%（従来 11.2%）で最良、揺れも 9mm（従来 8mm）と僅差。
    // human でも回帰なし（boneRatio 0.978→0.971、姿勢一致 5.30% で同値、揺れ 6.0mm 同値）。
    public float depthRefineFastTrackLow = 0.15f;
    public float depthRefineFastTrackHigh = 0.60f;

    // 下端が画面外に切れているフレームで、⑦ の基準を bbox 下端から bbox 上端に切り替えるか。
    // 切れた下端に合わせると下半身を画面内へ持ち上げてしまうため。上下とも切れている
    // フレームは従来どおり下端合わせにフォールバックする。
    public bool alignTopWhenBottomClipped = true;

    // 測定 B（2026-08-28、診断専用）: SMAL の曲げ（body_pose の寄与）を当てず、
    // bind pose を globalOrient で回しただけの姿勢にする。[ANIMALKP] を有無で比べて
    // 「形状・bind pose の不一致」と「jointFrameMap のロール未拘束」を切り分ける。
    // **シリアライズされる公開フィールドにしてあるのは、非シリアライズの実行時
    // フィールドに書くと play mode に持ち越されないため**（過去に同じ罠を踏んだ）。
    public bool disableSmalBendForDiag;

    // jointFrameMap のロールを「同じ肢のもう 1 本」で拘束する 2 軸版を使う（2026-08-28）。
    // 既定 false（従来の FromToRotation）。A/B で効果を確認してから既定を決める。
    public bool useTwoAxisJointFrameMap;

    // VR で選んだモデルと手動 yaw を動画ごと・track ごとに覚える。
    // 既定 false。docs/model-selection-persistence.md 参照。
    public bool rememberTrackCustomization;

    // SMAL FK のあとに四肢を keypoint の位置へ向ける（Human の AimAt に相当）。
    // 既定 false。A/B で確認してから既定を決める。docs/smpl-retargeting.md 参照。
    public bool enableAnimalKeypointAimAt;

    // 横方向のずれを測る診断ログ [HPOS]。配置には影響しない。
    public bool logHorizontalPlacement = false;

    // Animal 版の姿勢一致診断 [ANIMALKP]。human の logBoneVsKeypoint に対応する。
    public bool logAnimalBoneVsKeypoint = false;
    public bool refineDepthFromProjectedBones = true;

    // 上記で逆算した深度に掛ける係数。1.0 で「投影高 = bbox 高」ちょうど。
    // 大きくするとモデルが奥へ寄り Else との前後関係は改善するが、モデルが小さく写る。
    // 全編実測での比較:
    //   k=0.95 … median 1.052 / 球が手前 80.6%
    //   k=1.00 … median 0.998 / 球が手前 87.7%  ← 既定。サイズずれが最小
    //   k=1.10 … median 0.907 / 球が手前 94.4%  ただし 18% のフレームで bbox より 10% 以上小さくなる
    [Min(0.1f)] public float projectedDepthScaleK = 1.0f;

    // ⑧ の補正比率（投影高 / bboxH）を時間平滑化する時定数（秒）。0 で平滑化なし。
    // bbox は検出ノイズと姿勢でフレームごとに揺れ、それが素通しで深度に出ると
    // モデルが前後に暴れる（平滑化なしでは 1 フレームで最大 433mm 動いた）。
    // 深度そのものではなく比率を平滑化するので、人の実際の移動は保たれる。
    //
    // 全編実測（bundle_human.svb、深度の 1 フレーム間変化 / boneRatio / 球が人より手前）:
    //   0（なし）  p90 20.0mm / max 420.0mm   median 0.998 / 1.3 超 2.0% / 87.7%
    //   0.65s      p90  6.0mm / max  64.0mm   median 0.998 / 2.1% / 89.7%
    //   1.2s       p90  5.0mm / max  22.0mm   median 0.997 / 2.1% / 91.8%  ← 既定
    //   2.0s       p90  5.0mm / max  22.0mm   median 0.994 / 2.5% / 92.8%
    // 参考: ⑧ OFF は p90 5.0mm / max 22.0mm、median 1.082 / 8.8% / 79.0%。
    // 1.2s で揺れは ⑧ OFF と同等まで戻り、サイズ精度と前後関係は改善したままになる。
    // 平滑化を強めても boneRatio がほとんど悪化しないのは、外れ値の ratio が均されるため。
    [Min(0f)] public float projectedDepthSmoothingSeconds = 1.2f;

    // ⑧ の補正が、同じフレームの Else との前後関係（meta.bin の anchor_z が示す順序）を
    // 壊さないよう ratio を丸めるときの最小の隙間（m）。0 で無効。
    //
    // ⑧ は人の深度だけを bbox から決めるので、Else の深度（anchor_z 由来）との相対関係が
    // bundle の意図から外れる。実際、前傾でボールが背中に乗る f1250-1270 では、
    // bundle が「球が奥」と言っているのに ⑧ が人を奥へ動かして球が手前に出ていた。
    //
    // 全編実測（bundle_human.svb 2156f、前後一致 / 前傾 f1250-70 / boneRatio median / 深度 1f max）:
    //   A ⑧ OFF              91.3% / 47.6% / 1.082 /  22.0mm
    //   B ⑧ ON 制限なし        86.0% / 71.4% / 0.997 /  22.0mm  ← 実機「だいぶ治った」
    //   C 深度をクランプ 15mm  94.9% / 95.2% / 1.002 / 206.0mm  ← 実機「悪化」（跳ねる）
    //   D ratio を制限 15mm   90.2% / 71.4% / 1.003 /  22.0mm
    //   D ratio を制限 40mm   91.2% / 71.4% / 1.004 /  22.0mm  ← 既定
    //
    // 深度が決まった後にクランプすると前傾区間まで直るが、発動フレーム（17.8%）で一気に
    // 135mm 動いて跳ねる。ratio 側を制限すれば跳ねずに全体の前後一致は ⑧ OFF 並みに戻るが、
    // 後段の平滑化が制約を破るため前傾区間は改善しない。
    // 前傾区間の根本解決には Else 側の深度精度（D-004）が要る。
    [Min(0f)] public float projectedDepthOrderEpsilonMeters = 0.040f;

    // ⑧ の各段階（補正前 → 比率補正 → 順序クランプ → screen クランプ）を [DEPTH8] に出す。
    public bool logDepthRefineStages = false;

    // ⑩ Else が骨格モデルの内部に食い込んでいるとき、最小限だけ表面へ押し出す。
    //
    // 接触補正（Else を最寄りの部位へ引き寄せる）とは別物。**内部にあるときだけ、体から
    // 出る方向にのみ動かす**ので、空中にある Else は一切動かない（実測で影響 0 フレーム）。
    // 押し出す向きは meta.bin の anchor_z が示す前後関係に従うので、背中に乗ったボールは
    // 奥側の表面へ出る。手前に引き寄せることはない。
    //
    // 全編実測（bundle_human_shots_driftfix_test.svb, 2156f）:
    //   見た目で埋もれるフレーム 26.7% → 0.0%
    //   押し出し発動 26.7%、移動量 median 16.6mm / p90 39.5mm / max 72.0mm（球半径は約 21mm）
    //   Else の投影サイズ比 median 1.011 / p10 0.975 / p90 1.052
    //
    // **既定 OFF。** 2026-08-21 に実装して実機確認したが、狙った症状（4-8 秒・37 秒の埋もれ）は
    // 直らず、見た目もかえって悪くなったため無効化した。全編で 23324 回発動し Else が実際に
    // 動いてはいるが、埋もれ指標は 7.5% → 7.6% とほぼ不変だった。
    //
    // 効かない理由は未解明。有力なのは「ボーン半径（骨の中心から体表面までの実測値）が
    // 実際のメッシュ表面より内側にあり、押し出しても表面に届いていない」という線。
    // 再挑戦するなら、太さの実測値ではなく SkinnedMeshRenderer の実形状を見る必要がある。
    public bool resolveOtherPenetration = false;

    // ⑩ で「画面上で重なっている」と判定する余裕（px）。Else の投影半径にこれを足した
    // 距離より近ければ重なりとみなす。
    [Min(0f)] public float penetrationOverlapMarginPixels = 8f;

    // ⑨ で決めた「骨格 track と Else の深度差」を時間平滑化する時定数（秒）。0 で無効。
    //
    // **個別の深度ではなく差に掛けること。** 人と Else の深度は互いに打ち消し合って動いており、
    // 片方だけ平滑化すると相殺が壊れてばらつきが増える（2026-08-25 実測、クリアランスの
    // p10-p90 幅が 79.5mm → 人だけ固定 126.5mm / 球だけ固定 101.0mm）。
    //
    // 評価は depth map に依存しない独立推定（person は keypoints3d、ball は既知直径 18.5cm
    // から逆算）を正解として、配置の前後関係が正解と同じ向きになる割合で測った:
    //   0（なし）  全編 83.1% / 4-8s 39.7% / 36-39s 48.1%
    //   0.6s       全編 86.9% / 4-8s 46.3% / 36-39s 60.5%
    //   1.2s       全編 88.9% / 4-8s 53.7% / 36-39s 96.3%  ← 既定
    //
    // 4-8s（胸トラップ）が半分程度に留まるのは、この区間の anchor_z 自体が球を人より奥と
    // 誤推定しているため。平滑化はノイズを均すだけで系統誤差は消せない（D-004）。
    [Min(0f)] public float otherDepthGapSmoothingSeconds = 1.2f;

    // ⑨ で Else の深度を決めるとき、meta.bin の差をそのまま使うのではなく、
    // disparity から実距離の比を復元して使う。
    //
    // DepthCrafter は affine-invariant なので `disparity = a(t)/Z + b(t)`。bundle 側の
    // 背景ドリフト補正で b の 8 割は除去済み（2026-08-21、独立参照点で検証）なので、
    // 残る b を定数として扱えば、同一フレームの 2 物体の実距離の比が次式で求まる。
    //
    //     Z_other / Z_skeleton = (disp_skeleton − b) / (disp_other − b)
    //
    // keypoints も実距離の逆算も要らない（比を取ると相殺される）。
    //
    // 全編実測（bundle_human_shots_driftfix_test.svb、埋もれ率）:
    //   OFF（meta.bin の差をそのまま）  全編 28.0% / 4-8s 48.8% / 36-39s 1.2% / 41-42s 19.0%
    //   ON （実距離の比を復元）        全編 23.5% / 4-8s 40.5% / 36-39s 1.2% / 41-42s 14.3%
    // どの区間も悪化しない。
    //
    // 注意: 推定した実距離を「全編フィットの a」で disparity に戻す実装も試したが、
    // a(t) ≠ a のフレームで Else が奥へ寄り、36-39s が 1.2% → 12.3% に悪化した。
    // disparity へ戻さず実距離の比のまま使うこと。
    //
    // **既定 OFF。** 2026-08-21 に実装したが、主症状（5 秒付近の胸トラップでボールが
    // 人体を貫通する）は目視でまったく変わらなかった。動作自体はしている
    // （b 推定 0.3944、ratio 0.6950、ON/OFF でキャプチャが変わる）が、試算での改善幅が
    // 4-8s で 48.8% → 40.5% と小さく、見た目に出るレベルに達していない。
    public bool useMetricRatioForOtherDepth = false;

    // 上式の b。0 以下なら shot 先頭で自動推定する。
    // 自動推定は keypoints3d から実距離を逆算して `disparity = a/Z + b` を最小二乗で解く。
    public float depthAffineB = 0f;

    // ⑩ で「bundle が奥と言っていても手前へ押し出す」条件。Else の投影半径にこの係数を
    // 掛けた距離より画面上で近ければ、体のシルエット内部に深く入っていると見なして手前へ出す。
    // 0 で無効（常に bundle の前後関係に従う）。
    //
    // bundle が「奥」と言っているフレームで奥へ押し出すと、隠れたままで症状が直らない。
    // 一方この値を上げすぎると、実際に背中側にあるボール（40 秒台の前傾シーン）まで
    // 手前へ出してしまう。実機で見ながら決める値。
    [Min(0f)] public float penetrationFrontBias = 0f;

    // 診断: モデルの実ボーンと meta.bin の keypoints3d の投影位置の差を [BONEKP] に出す。
    // 「keypoints ベースの試算は合うのに実ボーンでの実装が効かない」原因の切り分け用。
    public bool logBoneVsKeypoint = false;
    [Min(0)] public int logBoneVsKeypointEveryNFrames = 30;

    // ⑨（Else を骨格 track の深度に追従させる補正）の適用結果を [DEPTH9] に出す。
    //
    // **⑨ 系を評価するときは必ずこれを使うこと。** [PLACE] は各 track の ApplyMetaTarget 内で
    // 出力されるが ⑨ は全 track の処理が終わったあとに走るため、[PLACE] には ⑨ の効果が
    // 含まれない。2026-08-25 に、この違いで試算と実測が大きく食い違った
    // （4-8s の符号一致が試算 39.7%→53.7% に対し [PLACE] 実測 92.3%→87.6%）。
    public bool logOtherDepthFollow = false;
    [Min(0)] public int logOtherDepthFollowEveryNFrames = 1;

    // `disparity = a/Z + b` の推定結果を [AFFINE] に出す。
    public bool logDepthAffineFit = false;

    // ⑩ が押し出したフレームを [PENET] に出す。
    public bool logPenetrationResolve = false;

    // ⑨ ⑧ で骨格モデルを動かしたあと、Else を「bundle が意図する深度差」を保つ位置へ追従させる。
    //
    // ⑧ は骨格を持つ track だけを動かすので、Else との深度差が bundle の意図から外れる。
    // 全編実測（bundle_human.svb、人 − 球の深度差）では、bundle の意図 81.8mm に対し
    // 現状 123.0mm、足上げ区間では 71.4mm に対し 237.0mm（3.3 倍）まで開いていた。
    // 胸トラップ区間では符号まで反転していた（意図 −59.5mm ＝ 球が奥 → 実際 +31.0mm）。
    //
    // ON にすると Else の深度を `骨格モデルの実配置深度 − meta.bin が示す差` に置き直す。
    // 実測では深度差が bundle の意図と一致し、前後関係の一致率も 91.6% → 99.8% になる。
    // 代償は Else の投影サイズで、median 4.7%・p10 で 13.7% 小さくなる。
    // ⑨ が「人がいる深度」として使う参照点。
    //
    // 既定だった Root は instance.transform.position だが、Renderpeople 等のスキャンモデルは
    // FBX の bind pose に原点オフセットが焼き込まれており、root が体の外に出る。
    // 16_Male_Eric では Hips がモデルローカルで z=+0.86m 固定（2026-08-25 実測、全 2156
    // フレームで変動 0.0000）で、表示スケール 0.2502 を掛けると体は root より 184.5mm 奥。
    // その結果 ⑨ が置く球は体より常に手前（100.0% のフレーム、中央値 164.3mm）になっていた。
    //
    // 4 種を実測して Hips を採用した（2026-08-26、球表面→最近傍ボーンの中央値）:
    //   Root 155.2mm / Hips 22.1mm / MeshCenter 22.8mm / MeshFront 88.8mm
    // 「anchor_z は可視表面の depth なので MeshFront が対応するはず」という読みは外れた。
    // intended は popout 圧縮空間での depth 差であって実距離の表面間距離ではないため。
    // Hips と MeshCenter はほぼ同点だが、Hips は姿勢で動かないぶん安定している
    // （参照点の 1f 変化 median 1.00mm 対 1.60mm、球の 1f 変化 p90 3.00mm 対 4.70mm）。
    //
    // Humanoid でない track（Animal 等）は自動的に Root にフォールバックする。
    public HumanDepthReferenceMode otherDepthSkeletonReference = HumanDepthReferenceMode.Hips;
    // ⑨ が Else を深度方向に動かしたぶん、見かけの大きさが変わらないようスケールを合わせるか。
    //
    // ⑨ は従来スケールを据え置いたまま位置だけ動かしていた。参照点を Hips にして移動量が
    // median 43.1mm → 163.7mm に増えた結果、球の見かけが 0.772 倍（23% 縮小）になった
    // （2026-08-26 実測）。配置パイプラインは「投影が bbox に一致する」ことを前提に
    // 組まれているので、深度を動かしたらスケールも追従させるのが筋。
    //
    // スケールは「掛ける」のではなく「代入」する。ApplyMetaTarget が毎 tick 位置を
    // 貼り直すため（フレーム内の otherZ の幅は 0.000mm）、掛けると 1 フレームで
    // 約 31 回累積してしまう。
    public bool matchOtherScaleToFollowedDepth = true;
    // 姿勢適用後に、Hips が「ルートを置いた位置」に来るようモデル全体をずらすか。
    //
    // ② ComputeTargetHeightMeters(bboxH, anchorZ) は「深度 anchorZ でモデルが bbox 高を
    // 張る」ようにスケールを決めるが、③ が置くのは root であって体ではない。
    // 原点オフセットを持つモデル（Renderpeople 等）では体が root より奥に出るため、
    // その前提が崩れる。16_Male_Eric では体が root より 171mm 奥で、実際に見える体は
    // 937mm（画面 1000mm・popout レンジ 650〜1000mm）に張り付いていた（2026-08-26 実測）。
    //
    // 2026-08-26 に実測して**棄却**した。ratio は 1 に近づくどころか 1.2968 → 1.4972 と
    // 悪化し、球と体の隙間も 16.2 → 52.9mm に広がった。体を手前に動かすと投影が大きく
    // なるので ratio は 1 から遠ざかり、⑧ が更に奥へ押し返す（移動量 172.9 → 258.0mm）。
    // スケールは ② ではなく RefineLockedScaleFromProjectedBones が投影実測で決め直して
    // いるため、「② の前提を成立させる」という読み自体が成り立っていなかった。
    //
    // 投影が bbox に一致するという条件は、モデルの世界サイズと bbox の見込み角で深度を
    // 一意に決めてしまう。root をどこに置いても ⑧ が同じ深度へ引き戻す。
    //
    // 球のクランプ率だけは 20.4% → 5.8% と改善するので、popout レンジを触るときに
    // 再評価する価値はある。それまで既定 false。
    public bool alignModelBodyToAnchorDepth = false;
    public bool logBodyAnchorAlign = false;
    public bool followOtherDepthToRefinedSkeleton = true;

    // RefineLockedScaleFromProjectedBones が狙う boneRatio。1.0 は「基準フレームで骨格の
    // 投影高さを bbox 高さにぴったり合わせる」。ただし boneRatio は姿勢で 1.0〜2.2 と動くため、
    // 基準フレーム（shot 先頭＝多くの場合は立位）で 1.0 に合わせても全区間の中央値は 1.2 前後に
    // 残る。1.0 未満にするとモデル全体が小さくなり、接触場面のめり込みは減るが立位で映像より
    // 小さくなる。最適値は素材依存なので実測して決める。
    [Min(0.5f)] public float projectedBoneRatioTarget = 1f;


    // 腕（上腕・前腕）の骨長も keypoints3d に合わせる。**既定 OFF。**
    //
    // 2026-08-19 の実測では、比率そのものは正しく合う（補正後の前腕/胴 0.537 = 映像 0.537）
    // 一方で boneRatio が 1.197 → 1.270 に悪化した。モデル全体が bboxWorldH の単一深度前提で
    // 約 1.2 倍に膨らんでいるため、腕の「比率」を正すと絶対長が 1.2 倍になり、手が上端から
    // 余計にはみ出す（boneTopDelta -47.9 → -60.7 px、手が topBone になるフレーム 9→13）。
    // 補正前は「腕が 27% 短い」ことが膨張を偶然打ち消していた。
    //
    // 膨張（docs/bundle-placement.md「根本原因: bboxWorldH の式が単一深度前提」）が解消されれば
    // 正しく効くようになるため、実装は残して既定 OFF にしてある。
    public bool enableHumanArmLengthCorrection = false;

    [Header("Human Bone Length")]
    // 表示モデルと元映像の脚の骨長比を合わせる。既定 Human モデルは胴で正規化した脚が
    // 映像より 8.3% 短く、足首が bbox 高さの約 10% 上にずれていた（2026-08-06 実測）。
    // モデル切り替え時は新しいインスタンスの生成時に自動で掛かる。
    //
    // **既定 OFF（2026-08-21 変更）。** 姿勢を keypoints3d に一致させることを最優先目標に
    // 据えて実測したところ、この補正が姿勢を崩していると判明した。
    //
    // 実ボーンと keypoints3d の投影位置の差（相対 dv、+ = モデルが下、docs/smpl-retargeting.md）:
    //   部位     ON      OFF
    //   右足   +17.5px   0.0px
    //   左足   +11.2px  -1.9px
    //   右手首  -5.7px  -0.6px
    //   左手首  -8.3px  -0.9px
    //   右膝    +3.7px +10.3px   ← 膝だけは ON の方が良い
    // 全体の RMS も 6.35% → 5.85% と OFF が良い。**膝を 10px 合わせるために足を 17px・
    // 手首を 8px ずらす**割の合わないトレードオフになっていた。
    //
    // 2026-08-06 当時の前提（足首が bbox 高さの 10% "上" にずれる）は既に成立していない。
    // 現在は補正 OFF でも足は Heel 基準でほぼ一致する。⑧ の深度補正が入って遠近感が
    // 正しくなったこと、bundle 側の depth 修正が進んだことで状況が変わった。
    //
    // 注意: この補正の本来の目的は「モデル固有のプロポーションを映像の人物に合わせる」ことで、
    // 姿勢一致とは別軸。OFF にすると身長・シルエットが変わる。実機で確認済み（2026-08-21）。
    public bool enableHumanBoneLengthCorrection = false;
    public bool logHumanBoneLengthCorrection = false;

    [Header("Human-Other Contact Correction")]
    public bool enableHumanOtherContactCorrection = false;
    // 診断用: どの部位にどれだけ吸着したか、補正が適用されない場合はその理由を出力する。
    public bool logHumanOtherContact = false;
    public int logHumanOtherContactEveryNFrames = 5;

    // 計測用: 配置したモデルを実際に画面へ再投影し、meta.bin の bbox とどれだけ一致するかを出す。
    // [PLACE] = 大きさ（投影高さ/bbox高さ）と位置（上端・下端のずれ）、[BONELEN] = 表示モデルの骨長。
    // 配置の検算に使う。手順は Docs/smpl-retargeting.md の「配置の実測方法」を参照。
    public bool logPlacementMeasurement = false;
    public int logPlacementMeasurementEveryNFrames = 30;

    // 計測: Human と Other の位置関係を「視線方向」と「画面平行方向」に分解して出す。
    // 「ボールが足に埋もれる」原因が深度不足なのか画面上の位置ずれなのかを切り分けるための
    // 観測専用フラグで、配置には一切影響しない。[GAP] を出力する。
    public bool logHumanOtherGap = false;
    public int logHumanOtherGapEveryNFrames = 15;

    // 計測: ボールと頭の高さ関係（[BALLHEAD]）。「深度を合わせてもボールが頭の上に浮く」
    // 症状を、画面上の位置と 3D 空間の高さの両方で切り分ける。
    public bool logBallHead = false;

    // 計測: 主要ボーンが bbox のどの高さにあるか（[BONEREL]）。
    // 「頭が低い」原因が全体スケール・胴の短さ・頭の小ささのどれかを切り分ける。
    public bool logBoneBBoxRelative = false;

    // 計測: meta.bin の keypoints3d と表示モデルのボーンを同じ eye pixel 空間へ投影し、
    // 部位ごとのずれを出す（[POSE]）。姿勢再現の誤差だけを抽出するための観測専用フラグで、
    // 配置には一切影響しない。[GAP] の lateralGap には「ボールが実際に体から離れている分」も
    // 含まれるため、そこから誤差成分を切り分けるのに使う。
    public bool logHumanPoseError = false;
    public int logHumanPoseErrorEveryNFrames = 30;
    [Min(0f)] public float humanOtherFullContactRadiusMultiplier = 1.25f;
    [Min(0f)] public float humanOtherReleaseRadiusMultiplier = 2f;
    [Min(0f)] public float humanOtherContactSurfacePaddingPixels = 2f;

    [Header("Audio")]
    // 音声を消す。バッチテストのように繰り返し再生する場面で使う。
    // 再生中に切り替えても効く。
    public bool mute = false;

    [Header("Runtime Controls")]
    public bool enableRuntimeControls = true;
    public GameObject runtimeControlsPrefab;

    [Header("Experiment")]
    // 被験者実験の StereoOnly 条件用。最初のフレームから normal mode
    // (source/pre_removal_stereo_video.mp4) で再生する。再生開始後に ToggleNormalMode で
    // 切り替えると、切り替わるまでの数フレームだけ置換モデルが見えてしまい条件が崩れる。
    public bool startInNormalMode = false;
    // false にすると Display（normal mode 切り替え）ボタンを生成しない。実験中に被験者が
    // 表示条件そのものを変えてしまうのを防ぐ。詳細は Docs/experiment-flow.md。
    public bool enableNormalModeToggleButton = true;

    [Header("Interactive Motion")]
    public bool enableInteractiveMotion = true;
    [FormerlySerializedAs("humanInteractiveClips")]
    public AnimationClip[] humanStaticGestureClips;
    public AnimationClip[] humanWalkClips;
    public AnimalGesturePose[] animalStaticGestureClips;
    public AnimalGesturePose[] animalWalkClips;
    public float interactiveMotionMinIntervalSeconds = 6f;
    public float interactiveMotionMaxIntervalSeconds = 14f;
    [FormerlySerializedAs("interactiveMotionDurationSeconds")]
    public float staticAnimationDurationSeconds = 5.5f;
    [FormerlySerializedAs("interactiveMotionBlendSeconds")]
    public float interactiveHandoffBlendSeconds = 0.8f;
    public float humanApproachStopDistanceMeters = 0.6f;
    public float humanWalkSpeedMetersPerSecond = 0.8f;
    public float animalApproachStopDistanceMeters = 0.5f;
    public float animalWalkSpeedMetersPerSecond = 0.5f;

    // popoutRangeMeters（Inspector 調整可）へ移行済み。
    private const float EpsilonMeters = 0.02f;
    private const float MinDistanceFromHeadMeters = 0.25f;
    private const float BaseHeight = 1f;
    private static readonly bool UseFrameReadySync = false;
    private static readonly bool SelectDisplayTrackFromClick = true;
    private const float DisplayTrackSelectThresholdPixels = 80f;

    private static readonly bool EnableSkeletonScaleCorrection = false;
    private const float SkeletonScaleMin = 0.2f;
    private const float SkeletonScaleMax = 5f;
    private const float SkeletonScaleRelativeMin = 0.75f;
    private const float SkeletonScaleRelativeMax = 1.25f;
    private static readonly bool StabilizePersonRootYaw = true;
    private const float PersonRootYawMaxDegreesPerSecond = 180f;
    private const float Smpl24RootRotateAlpha = 0.85f;
    private const float Smpl24LimbIkAlpha = 0.9f;
    private const float Smpl24SpineAlpha = 0.35f;
    private static readonly bool EnableHumanSmplMotion = true;
    // 2026-08-06 検証済み: この値を 1.0 にしても [PLACE] 計測の sizeRatio は
    // 小数第3位まで一切変わらなかった。ShouldUseSmplOnlyPose() 経路では姿勢の深さに
    // 効いていないので、姿勢の再現精度を調べる際にここを触っても無駄。
    private const float HumanSmplRotationAlpha = 0.65f;
    private static readonly bool HumanSmplFlipY = true;
    private static readonly bool EnableYawDepthDisambiguation = true;
    private const float YawDepthOffsetMeters = 0.045f;
    private const float YawDepthBlend = 1f;

    private static readonly bool EnableAnimalLimbApply = true;
    private static readonly bool StabilizeAnimalRootYaw = true;
    private const float AnimalRootRotateAlpha = 0.6f;
    private const float AnimalRootPitchRollBlend = 0.18f;
    private static readonly Vector3 AnimalModelForwardLocal = new Vector3(0f, 0f, -1f);
    private static readonly Vector3 AnimalModelUpLocal = Vector3.up;
    private static readonly bool DisableAnimalAnimatorController = true;
    private static readonly bool EnableAnimalDistalFreezeOnHighSkip = true;
    private const int AnimalDistalFreezeSkipThreshold = 6;

    private static readonly bool ForceScreensInFrontOfViewCamera = false;
    private static readonly bool ForceStationaryTrackingOrigin = true;
    private static readonly bool AlignModelToBBoxBottom = true;
    private const float ModelBottomExtraOffsetMeters = 0f;
    private static readonly bool BottomAlignVerticalOnly = true;

    private static readonly Vector2 ControlsBarOffsetMeters = Vector2.zero;
    private const float ControlsBarGapMeters = 0.06f;
    private const float ControlsBarForwardOffsetMeters = 0.01f;
    private static readonly Vector2 ControlsBarSizeMeters = new Vector2(0.6f, 0.16f);
    private static readonly bool EnablePauseHotkey = true;
    private const float RuntimeFovxMinDeg = 40f;
    private const float RuntimeFovxMaxDeg = 140f;
    private const float RuntimeFovxDefaultDeg = 90f;
    private const float RuntimeScreenDistanceMinMeters = 0.5f;
    private const float RuntimeScreenDistanceMaxMeters = 3.0f;
    private static readonly Vector2 SettingsPanelSizeMeters = new Vector2(0.78f, 0.5f);
    private static readonly Vector2 SettingsPanelOffsetMeters = Vector2.zero;
    private const float SettingsPanelGapMeters = 0.08f;
    private const float SettingsPanelForwardOffsetMeters = 0.01f;

    private VideoPlayer vp;
    private string modelModePlaybackVideoPath;
    private string normalModePlaybackVideoPath;
    private bool hasNormalModeVideo;
    private bool isNormalMode;
    private bool pendingModeSwitchResume;
    private double pendingModeSwitchTimeSeconds;
    private ManifestData manifest;
    private int lastFrameReadyFrame = -1;
    private string leftTexProp = "_MainTex";
    private string rightTexProp = "_MainTex";
    private Material leftMat;
    private Material rightMat;
    private Mesh quadMesh;
    private bool hasLockedPinholeBasis;
    private Vector3 lockedPinholeOrigin;
    private Quaternion lockedPinholeRotation = Quaternion.identity;
    private readonly List<XRInputSubsystem> xrInputSubsystems = new List<XRInputSubsystem>();
    private bool headPosePrimed;
    private Vector3 lastHeadPos;
    private Quaternion lastHeadRot = Quaternion.identity;
    private bool prevPrimaryButtonPressed;
    private bool appliedMute;
    private bool useRuntimeFovxOverride;
    private float runtimeFovxDeg;
    private Camera cachedViewCamera;

}

