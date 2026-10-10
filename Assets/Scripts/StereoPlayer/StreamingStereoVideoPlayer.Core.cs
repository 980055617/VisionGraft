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

    // 画面をヘッドセットのトラッキング原点（機械が持っている正面）に置く。
    //
    // **向きだけを原点から取る。位置は頭（目）のまま。**
    // 位置まで原点にしたら「human の体勢が変わった」（2026-09-07 実機）。
    // 詳細は Screens.cs の ResolveScreenAnchor。
    public bool useTrackingOriginForScreenFacing = true;

    // 画面と pinhole の基準点を「最初の目の位置」で固定する。**既定 ON。**
    // OFF にすると毎回そのときの頭の位置になり、Screen Dist を動かすたびに
    // 画面とモデルが頭へ寄ってくる（2026-09-07 実機指摘の挙動）。
    // 詳細は Screens.cs の ResolveScreenAnchor。
    public bool lockScreenAnchorPosition = true;

    // 自前のポインタの線を描くか。**既定 OFF。**
    // ISDK の白い線と 2 本出て始点も違うので紛らわしい（2026-09-07 実機指摘）。
    // OFF でも掴む判定はこの線と同じ姿勢で行うので、操作は変わらない。
    public bool showPointerRay;

    // 頭が大きく動いたときに画面を追従させる。**既定 OFF。**
    // 詳細は StreamingStereoVideoPlayer.Core.partial.cs の該当箇所。
    public bool autoRecenterScreensOnHeadMove;

    private const float AutoRecenterHeadMoveMeters = 0.35f;
    private const float AutoRecenterHeadTurnDegrees = 35f;

    // UI の見かけの大きさを Screen Dist に依らせない。
    // 詳細は StreamingStereoVideoPlayer.UI.Settings.partial.cs の PinRuntimeUiDistance。
    public bool pinRuntimeUiDistance = true;

    // 2.0m（画面の既定距離と同じ）にしたら「1 でも 3 でも小さい」と言われた。
    // 距離を固定しただけでは足りず、**近づけないと使える大きさにならない**。
    // 手が届く範囲より少し先の 1.2m にする（2026-09-07）。
    [Min(0.25f)] public float runtimeUiDistanceMeters = 1.2f;

    // UI の配置計算で基準にする画面距離。既定の screenDistanceMeters と同じ値。
    // 詳細は UI.Settings.partial.cs の NormalizeScreenSizeForUi。
    private const float RuntimeUiReferenceScreenDistanceMeters = 2f;

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

    // AimAt の目標を meta.bin の SMPL（平滑なし）から作る。**既定 OFF（2026-10-03、試作）。**
    // keypoints3d は生成側で因果 EMA（新しい値の重み 0.2）済みで、速い動き（2 Hz）が 0.54 倍に縮み
    // 約 0.1 s 遅れる。区間の向きを「関節の world 回転 × bundle から推定した rest 方向」で作る。
    // AimAt 自体は残す（目標だけ差し替える）。詳細は HumanSmplAim.partial.cs。
    public bool aimAtTargetFromSmpl = false;
    // SMPL の回転の時間平滑を、Slerp の EMA（半減期 0.05 s、遅れ約 40 ms）から前後 2 フレームの
    // 中心 5tap [1,4,6,4,1]/16（遅れ 0）に替える。**既定 OFF。** aimAtTargetFromSmpl と組で使う
    // （片方だけだと四肢と胴の遅れがずれる）。
    public bool centeredSmplRotationFilter = false;
    // 診断: SMPL の目標と keypoint の目標の角度を [SMPLAIM] に出す。
    public bool logSmplAimTarget = false;
    [Min(1)] public int logSmplAimTargetEveryNFrames = 1;


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
    // Generic リグの投影からスキンのウェイトが 0 のボーン（IK ターゲット・attach 等）を外す。
    // **既定 OFF（2026-10-03、試作）。** 詳細は ProjectionWeights.partial.cs。
    public bool excludeUnweightedProjectionBones = false;

    // スケールロック・⑧・⑦ が測る投影の上端・下端を、Humanoid ではシルエット相当（眼・Jaw を外し、
    // 頭頂と足裏の代理点を足す）にする。**既定 OFF（2026-10-03、試作）。** bbox は髪の上〜靴底なので、
    // 関節で測ると骨格が人より 1.13〜1.20 倍大きく置かれ、その量がリグと姿勢で変わる。
    // aimAtTargetFromSmpl と同時か後に使う（単独だと、大きさで隠れていた動きの縮みが目立つ）。
    // 詳細は SilhouetteExtent.partial.cs。
    public bool useSilhouetteProjectionExtent = false;
    // 診断: 代理点の距離と、Editor では BakeMesh で測ったメッシュの上端・下端を [SILHOUETTE] に 1 回出す。
    public bool logSilhouetteExtent = false;

    // ⑧ が合わせる相手を「bbox 高」から「見切れを補った推定全高」に変えるか。
    // bbox は可視部分だけなので、見切れフレームで bbox 高に合わせるとモデルが縮む。
    // 詳細は ResolveUnclippedTargetHeight のコメント。
    public bool extendTargetHeightForClippedBBox = true;
    // 見切れた animal（上端か下端が切れたフレーム）の大きさを、meta.bin の SMAL block の transl z と keypoints3d から出した「体全体の像の高さ」でも
    // 見積もり、上の外挿（ResolveUnclippedTargetHeight）より大きければそちらに合わせる。**新しい振る舞い・既定 OFF（2026-10-07、ユーザーの「一部しか
    // 映っていないカットで何かいい方法ないの」）。** 増えた分は shot 先頭の倍率の測り直し（RefineLockedScaleFromProjectedBones）で倍率に入れ、⑧ の目標も
    // 同じ比で上げる。⑧ が目標を上げるのは測り直しが倍率に入れた track だけ（手動倍率で測り直しを飛ばした・ガードで測り直せなかった・shot 先頭が
    // 見切れていなかった track は今と同じ）。bundle_animal で変わるのは shot 2・11・13（犬の顔の寄り）・23・24・25・27。見切れていないフレーム、
    // 上の外挿の方が大きい shot 1・20 は変わらない。背骨を anchor に置いたまま大きくなるので頭は目へ寄る（shot 2 の最も近い骨 0.33 → 0.25 m）。
    // 詳細は ResolveAnimalFullBodyTargetGain。
    public bool animalPlaceClippedFromFullBody = false;
    // 上の副選択（既定 OFF）: 左右が切れて上の外挿を諦めていたフレーム（bbox が画面の左端か右端に付く）だけに効かせる。bundle_animal では shot 2・11・13
    // だけが変わり、外挿が効いていた shot 23・24・25・27 は OFF と同じ（shot 24 は大きくなる代わりに顔が元の猫の顔から離れるので、それを採らないとき用）。
    public bool animalPlaceClippedFromFullBodyWidthClippedOnly = false;

    // 外挿の上限（bbox 高の何倍まで許すか）。1 フレームの推定ミスで暴れないための保護。
    // 1.6 は実測で決めた（2026-08-27、bundle_animal）。1.6 / 2.0 / 3.0 を振ったところ
    // 全体の誤差 median が 11.1% / 12.5% / 12.5%、欠損率 45% 超の帯では期待 2.00 に対し
    // 1.81 / 2.43 / 2.59 で、1.6 が最も近い。上限なしだと外挿が効きすぎて逆に大きくなる。
    public float maxClippedHeightExtrapolation = 1.6f;

    // 1 フレームだけ孤立して跳ねた bbox（マスクの漏れ等）を前後フレームの中央値で置き換える。**既定 ON。**
    // 次のフレームを先読みして判定するので遅延は無く、2 フレーム以上続く変化には一切触らない
    // （Meta.cs の RepairIsolatedBBoxSpikes）。
    // 実測（2026-09-11）: 旧 dog クリップ track 1 の f24 で bbox 高 271→572→274（上端だけ 300px 飛ぶ、
    // 下端と幅は不変 = マスクが上に漏れた）。⑧ の fast track が即座に深度を寄せ、犬が 1 フレームだけ
    // 約 2 倍に見えた（実機報告「一瞬大きくなって戻る」）。実験用 bundle にも同種が 4 件ある:
    // human ボール f584（×1.29）、train f1052 / f1054（×1.42 / ×1.65）・f365（×1.67）。
    public bool rejectIsolatedBBoxSpikes = true;

    // ⑧ が発動する ratio（投影高 ÷ 目標高）の下限。スケール再ロック側の
    // MinProjectedBoneRatioForScaleRefine とは独立に振れるようにした。
    // 0.2 は実測で決めた（2026-08-27）。0.4 のままだと shot 内で被写体の見かけが 3 倍に
    // なる場面（bundle_animal 29.9〜32.7s）でガードに張り付き、**最も補正が要るフレームで
    // ⑧ が何もしなくなる**（32 秒台の sizeRatio が 0.416）。0.2 にすると 0.692 まで戻る。
    // animal の全体誤差・揺れ、human の boneRatio/球との距離/姿勢一致はすべて不変。
    public float depthRefineMinRatio = 0.2f;
    // animalScaleRefineMinRatioFromDepthRefine（2026-10-09、既存の欠陥の直し・既定 OFF・animal のみ・新しい定数なし）: shot の頭の倍率の測り直し
    //   （RefineLockedScaleFromProjectedBones）の比の下限を、0.4 から ⑧ の下限（上の depthRefineMinRatio）に揃える。今の既定では比が 0.2〜0.4 に出る
    //   モデル（Lion・Mink・EuropeanBadger・Racoon）で測り直しが shot の間一度も通らないことがあり、その間 ⑧ が小さいモデルを目の前へ引き寄せる
    //   （4 体で 20 shot、遅れて通った shot 7 つ。遅れた shot は ⑧ の平滑化が測り直す前の比から始まり、体が目の前から約 1.5 秒かけて奥へ滑る）。
    //   ON で 4 体とも全 shot が最初の tick に通る。実験の Labrador・Lynx は全 shot で最初から通るので変わらない（860 行一致）。
    //   倍率が変わったら ⑧ の平滑化を始め直す案も撮ったが、これを ON にすると働く場面が残らず何も変えなかったので入れていない。
    //   詳細は Docs/tmp/roster_20261009/README.md の 11。
    public bool animalScaleRefineMinRatioFromDepthRefine = false;
    // animalBottomFitUsesBoneDepth（2026-10-09、既存の欠陥の直し・既定 OFF・animal のみ・新しい定数なし）: ⑦ の下端合わせ（FitDisplayedModelToBBox）で、
    //   一番下の骨の投影で合わせるときの px → m の換算を、レンダラーの AABB の中心の深さではなく、その骨自身の深さで行う。骨はその骨の深さで投影して
    //   いるので、AABB の深さで割り戻すと移動量が「AABB の深さ ÷ 骨の深さ」倍にずれる（31_GrayWolf の伏せで 2.5 倍、体が 0.5 m 浮く。⑧ に目の前へ
    //   寄せられた 06_AmericanMink で 50 倍、体が頭の上へ飛ぶ）。上端合わせ（下端が切れているとき）はメッシュの投影上端なので今のまま。
    //   詳細は Docs/tmp/roster_20261009/README.md の 11-4。
    public bool animalBottomFitUsesBoneDepth = false;

    // ⑧ の平滑化を速める相対誤差のしきい値。lo 以下は従来どおりの平滑化、hi 以上で
    // ほぼ即座に追従する。1.0 に設定すると実質無効（従来の挙動）。
    // 0.15 は実測（2026-08-27、animal）。0.30 / 0.15 / 0.08 を振り、0.15 が
    // 全体誤差 median 10.1%（従来 11.2%）で最良、揺れも 9mm（従来 8mm）と僅差。
    // human でも回帰なし（boneRatio 0.978→0.971、姿勢一致 5.30% で同値、揺れ 6.0mm 同値）。
    public float depthRefineFastTrackLow = 0.15f;
    public float depthRefineFastTrackHigh = 0.60f;
    // 速追従を animal にも掛けるか。**既定 OFF（2026-09-18）。** animal は姿勢で骨格の投影高が急に変わる
    // （振り向き・頭の上下）ので、相対誤差 0.6 超で即時追従すると 1 frame で奥行きが 0.43 → 0.74 m 飛び、
    // scale 固定のぶんが見かけの大きさの跳ねになる（実機でユーザー指摘、bundle_animal f60 / f81 / f1212〜1218）。
    // OFF なら animal は τ の平滑化だけで動く。human は従来どおり速追従あり。
    public bool depthRefineFastTrackForAnimal = false;

    // Animal の接近を scale でも追う（C4-T）。**既定 OFF（2026-10-03、試作）。10/1 の「⑧・範囲・shot 内の scale は変えない」
    // を開き直す変更。** 詳細は AnimalScaleTrend.partial.cs。
    public bool animalScaleFollowApproachTrend = false;
    // 目標高と 1/tz を平均する窓の半幅（frame）。15 = ±0.5 s。
    [Min(1)] public int animalScaleTrendWindowFrames = 15;
    // 不感帯（倍率）。1.25 = ±25% の内側は scale を動かさず ⑧ に任せる。
    [Min(1.01f)] public float animalScaleTrendDeadZone = 1.25f;
    // animal の ⑧ の平滑の時定数（秒）。0 以下なら projectedDepthSmoothingSeconds と同じ（従来）。C4-T の試算は 0.6。
    public float animalDepthRefineSmoothingSeconds = 0f;
    // スケールのロック時の測り直し（RefineLockedScaleFromProjectedBones）の基準を、bbox 高ではなく
    // ±animalScaleTrendWindowFrames で平滑した目標高にする（B1、animal のみ）。**既定 OFF。**
    public bool refineLockedScaleAgainstSmoothedTarget = false;
    // C4-T の倍率の基準（ロックしたフレームの値）を、±animalScaleTrendWindowFrames の窓の平均ではなく、そのフレームの生の値にする
    // （2026-10-07、**新しい振る舞い・既定 OFF**。係数・しきい値は足さない）。ロックの倍率は bbox 高そのもので合わせている
    // （RefineLockedScaleFromProjectedBones、B1 OFF）ので、基準もそれに揃える。窓の平均のままだと、区間の頭では窓が先のフレームだけになり、
    // 近づいてくる場面では基準が生の値より大きく出る（bundle_animal f258 で 92.2 px 対 73 px）。B1（上）はロックの方を窓の平均に合わせて
    // この食い違いを消すが、倍率が動かない区間のロックまで変える（shot 1 で ×1.59 など）。こちらは倍率の基準だけを動かすので、倍率が 1 の区間は
    // 何も変わらない。B1 と同時に ON にしたときは効かない（ロックが窓の平均に合っているので、基準も今どおり窓の平均）。
    // bundle_animal で倍率が 1 でなくなるのは f258〜337（×1.01〜2.23）・f427〜533（〜×1.38）・f898〜981（〜×1.83）・f1104〜1145（〜×1.18）・
    // f1435〜1612（×0.96〜1.00）。詳細は AnimalScaleTrend.partial.cs の TryResolveScaleTrendLockLogTarget。
    public bool animalScaleTrendRawLockReference = false;
    // モーションの受け渡し（HandoffBlend）で scale も位置・回転と同じ重みで混ぜる（animal のみ、2026-10-07、既存の欠陥の直し）。**既定 OFF。**
    // イベント中は配置が止まって scale は発火した tick の値のまま、動画は音のフェード（0.5 s）の間だけ進んでから止まる。受け渡しの最初の tick で
    // 追従がその時点の scale を書くので、C4-T が ON だと 1 tick で跳ねる（接近中に発火すると実機で最大 ×1.46、録画のバッチで最大 ×1.14）。
    public bool animalHandoffBlendsScale = false;
    // 偽のカットを 2 つ以上まとめて順方向に越えた（コマ落ち・シーク）ときも、間の境界がすべて偽のカットなら scale のロックを持ち越す
    // （2026-10-07、既存の欠陥の直し）。**既定 OFF。** 今は隣の shot へ 1 つ進んだときしか偽のカットと認めず、280・290 を一度に越えると
    // f290 の bbox でロックし直して、同じ場面のモデルの大きさが着地のフレームによって ×2.5〜3.8 変わる（10/03 の撮影の回で実際に起きた）。
    // 隣どうしの判定（keepScaleAcrossContinuousShotBoundary）と同じく種別を問わない。今ある bundle で偽のカットが続くのは bundle_animal だけ。
    // ShotBoundary.partial.cs の IsFalseCutBoundaryChain。
    public bool falseCutChainKeepsScale = false;
    // animal の ⑧ の奥行きの上限（m）。0 以下ならスクリーン距離（従来）。C4-T の試算は 2.0
    // （動画の犬は自分の視差でスクリーンより奥 1.03〜1.25 m に見えている）。
    public float animalDepthRefineMaxDepthMeters = 0f;
    // 診断: C4-T の倍率を [SCALETREND] に出す（倍率が 1 でないフレームと、logPlacementMeasurementEveryNFrames ごと）。
    public bool logAnimalScaleTrend = false;

    // 奥行きの上限（スクリーン距離）を外す。**既定 OFF（2026-10-03、試作、ユーザー指示）。**
    // 上限は 3 月の設計（モデルをスクリーンの手前に飛び出させる、当時はスクリーンが奥のモデルを隠した）の名残で、
    // 8/20 にスクリーンを背景描画（ZWrite Off）にしてからは描画上の理由が無い。人が「スクリーンより奥」に写る区間
    // （bundle_human f240〜899）では ⑧ が深度を上限に貼り付け、モデルが 1 割前後大きく見えていた。
    // ⑧（人・動物）・⑨（Else の追従）・めり込み解消・最初の配置の 4 か所を同時に外す（片方だけ外すと人とボールがずれる）。
    public bool allowDepthBehindScreen = false;
    // 外したときの安全上限（スクリーン距離の倍数）。⑧ の比のガード（3.0）より奥へは行かない目安。
    [Min(1f)] public float depthBehindScreenMaxFactor = 3f;
    // animal の ⑧ の深度の基準を root ではなく骨格（投影に使うボーン）の重心にする。**既定 OFF（2026-10-03、試作）。**
    // ⑧ は「投影高 ∝ 1/深度」で z × ratio と一手で動かすので、root が体から離れていると狙いを越える
    // （39_Lynx は root が体の約 0.5 m 奥にあると見ると、上限あり・なしの両方の結果が説明できる）。Humanoid は従来どおり Hips。
    public bool animalDepthReferenceFromBody = false;

    // 検証用（既定 空 = OFF、2026-10-04）: 姿勢と配置が全部終わった時点の骨の world の位置・回転を、tick ごとに
    // このファイルへ JSON 1 行で書き出す（BoneWorldDump.partial.cs）。割り当て（SMPL / SMAL → モデルの骨）の再調査で、
    // オフラインの移植と runtime の骨の向きを突き合わせるため。Debug.Log には出さない（負荷で動画が飛ぶ）。
    public string boneWorldDumpPath = "";
    // 書き出す動画フレームの区間（例 "590-660,830-880"）。空なら全フレーム。
    public string boneWorldDumpWindows = "";

    // 足の AimAt の点を揃える（2026-10-04、調査役 HM の F1、既定 OFF）。FootTip.partial.cs を参照。
    // footAimAtToeTipProxy = 骨側も「Foot → 足先の代理点」にする（AimAt は残す）、skipFootAimAt = 足だけ AimAt を掛けない。
    public bool footAimAtToeTipProxy = false;
    public bool skipFootAimAt = false;
    // UpperChest の無いモデル（A・B 系 12 体）で、Chest を spine3（joint 9）までの累積で書く（2026-10-04、HM の F2、既定 OFF）。
    // 今は Chest を joint 6 で書いたきりで、spine3 の曲げ（p95 16°）が胸から上に入らない。
    public bool chestUsesSpine3WhenNoUpperChest = false;
    // FK の基準を Avatar の T ポーズにする（HM の F3 (a) / LM の H-1）と、手を前腕の枠と SMPL の手首で FK する
    // （HM の F3 (e) / LM の H-2）。2026-10-04、既定 OFF。H-1 だけだと手と前腕の相対ねじれが悪化するので組で使う。
    public bool fkReferenceFromAvatarTPose = false;
    public bool handFkFromForearmFrame = false;
    // 人の指（2026-10-08、全関節の監査 J-12。どれも既定 OFF、新しい振る舞い）。今は指 30 本を毎 tick muscles=0（1 関節 34〜37° 曲がった半握り）に戻し、
    // SMPL の 22・23（手）は読むが使わない。詳細・予測・副作用は HumanFingers.partial.cs。採否は絵で。
    // humanFingerRestFromAvatarTPose: 指 30 本だけ Avatar の T ポーズ（ほぼまっすぐ）の局所回転に戻す。前提「SMPL のゼロ姿勢の手は平ら」は未確認
    //   （この PC に SMPL のモデルファイルが無い）。T ポーズが採れない（Quest で humanDescription が読めない等）ときは今の muscles=0 のまま。
    //   T ポーズは Humanoid のキャッシュを作るときにだけ採るので、起動時（-setFields）に入れる。副作用: 指は倍率のロック・⑦・⑧ の投影に入っているので、
    //   指が投影の上端・下端になるフレームでモデルの置き方が変わる（Eric の移植で変わったフレームの投影の高さ p50 +1.6%、最大 +7.2%）。
    // humanFingersFromSmplHand（humanFingerRestFromAvatarTPose が前提）: 指の付け根（人差し指〜小指）を SMPL の 22・23 の平滑後の回転で曲げる（係数なし）。
    //   R22 / R23 はほぼ一定（1 フレームの変化 p50 0.4°）で、本物の動きでなく HMR2 の事前分布の可能性がある。
    // humanFingersFromSmplHandThumb: humanFingersFromSmplHand を親指の付け根にも掛ける（SMPL の手の関節が親指を動かすかは未確認なので分けた）。
    public bool humanFingerRestFromAvatarTPose = false;
    public bool humanFingersFromSmplHand = false;
    public bool humanFingersFromSmplHandThumb = false;
    // 30 fps の姿勢を tick の時刻（vp.clockTime）で隣り合う 2 フレームから補間する（2026-10-04、調査役 TM の案 A を
    // 反論役 C-TM の修正つきで、既定 OFF）。Human は centeredSmplRotationFilter と組のときだけ。PoseInterpolation.partial.cs を参照。
    public bool interpolatePoseBetweenFrames = false;
    // 足の AimAt の目標を「SMPL の足首の回転 × そのモデルの T ポーズの足の向き」にする（2026-10-04、反論役 C-HM の案 3、既定 OFF）。
    // FootTip.partial.cs を参照。T ポーズ（Avatar の skeleton）を使うので、モデルの立ち足（ヒールの形）が保たれる。
    public bool footAimAtModelRestDirection = false;
    // HMR2 の向き（crop 基準）を crop の中心への視線の向きへ回す（2026-10-04、調査役 HM の F4、既定 OFF）。ViewRay.partial.cs を参照。
    public bool alignSmplToViewRay = false;
    // Animal の視線の向き（2026-10-04、反論役 C-DM、既定 OFF）。meta.bin の SMAL の globalOrient は AniMer の切り抜きカメラ基準
    // （AniMer の pred_smal_params と 0.00〜0.03° で一致）なので、Human の alignSmplToViewRay と同じ R_ray を掛ける。
    public bool alignSmalToViewRay = false;
    // Animal の根（globalOrient）だけを前後 2 フレームの中心 5tap で平滑する（2026-10-04、C-DM、既定 OFF）。根の tick ごとの EMA
    // （smalSmoothHalfLifeSec 0.12）は走り・跳躍の胴の上下を約半分に削り、0 にすると 1 tick で最大 39° 跳ぶ。-smalHalfLife 0 と組で使う。
    public bool centeredSmalRootFilter = false;
    // Animal の body_pose（SMAL 関節 1〜34）も前後 2 フレームの中心 5tap で平滑する（2026-10-04、第 3 ラウンド、既定 OFF）。
    // bundle は全フレームを先に読めるので、中心の窓でも表示は遅れない。tick ごとの EMA（smalBodyPoseSmoothHalfLifeSec、既定は根と同じ 0.12）は
    // 走りの脚の振れを削って遅らせ、0 にすると 30 fps の推定の揺れがそのまま出る（R3 の書き出し: 走りで 1 tick の最大 46〜63°）。
    // smalBodyPoseSmoothHalfLifeSec=0 と組で使う。PoseInterpolation.partial.cs の BuildCenteredSmalBodyPose。
    public bool centeredSmalBodyPoseFilter = false;
    // 首の中間の骨（cache.neck と背骨の間の Neck01・Neck02 など）に首の回転を配る（2026-10-04、C-DM）。AnimalSmalFkApplier を参照。
    // **既定 ON（2026-10-04 第 3 ラウンド、bodyFrameNeckHead と組）。** 猫のうずくまりの頭の下がりがデータ比 33% → 73%。52 体中 44 体に
    // 首の鎖があり、鎖の骨に脚がぶら下がるリグは無い（反論役 DEC の dec05）。
    public bool smalDriveNeckChain = true;
    // 方向の転写（2026-10-04 第 3 ラウンド、調査役 FKQ の (c)、既定 OFF）。SMAL の骨の向きそのものに合わせる（bind の形は捨てる）。
    // 詳細は AnimalSmalFkApplier の同名フィールド。採否はメッシュのねじれを含めて絵で。
    public bool smalAbsoluteDirection = false;
    public bool smalAbsoluteDirectionHeadTailOnly = false;
    // 尾の鎖（2026-10-07、担当 B「猫の尾が元動画のように見えて動く」）。詳細は AnimalSmalFkApplier の同名フィールド。
    // smalTailFullChain（既定 OFF、新しい振る舞い）: 尾の骨すべてを SMAL の尾の関節 25〜31 の向きへ（方向の転写。左右の反転も直る。Tail03 の 74° の
    //   曲げは捨てる）。掛かるのは smalTailFullChainModels に載ったモデルだけ（既定 "Lynx"。犬の尾は変えない）。
    //   副作用: shot の先頭の倍率の測り直し（RefineLockedScaleFromProjectedBones）は、確定前の小さい倍率（Lynx shot 23 で 0.293。確定後の約 0.57 倍）で
    //   アンカーに置いた骨の投影を測る。この配置では Lynx の尾の先が最上の骨（shot 23 で 6 px 上）なので、尾を立てると倍率が下がる。
    //   既存の方向の転写で実測 0.5126 → 0.4904（−4.3%）。鎖は移植で ×0.934（約 0.479）。確定後の姿勢では尾は最上の骨にならない。
    // smalTailFullChainModels: カンマ区切りの prefab 名（先頭の「数字_」は無視）。例 "Lynx,Puma"。"LabradorDog" を足すと犬の尾も変わる（向きが中央 21〜28°）。
    // smalTailMatchSmalLength（既定 OFF、新しい振る舞い）: 尾の長さの比を SMAL に合わせる（smalTailFullChain と組）。smalTailMatchSmalLengthModels に
    //   載ったモデルだけ（既定 "Lynx" = 1.93 倍）。倍率に上限は無く、尾の短いモデルは 6〜11 倍になるので、足すときは倍率の表を見てから。
    //   メッシュが骨の間で引き伸ばされる。Lynx shot 23 の倍率は移植で ×0.855（約 0.438）。
    // bodyFrameTail（既定 OFF）: 比較用（写像の切り替え）。尾の付け根・中ほどだけ体の写像で。左右の向きだけ変わり、仰角は今のまま。全モデルに効く。
    // smalAbsoluteDirection + smalAbsoluteDirectionHeadTailOnly は Lynx では尾だけだが、当てはめ済みの頭の Labrador は首・頭も変わるので尾の直しには使わない。
    public bool smalTailFullChain = false;
    public string smalTailFullChainModels = "Lynx";
    public bool smalTailMatchSmalLength = false;
    public string smalTailMatchSmalLengthModels = "Lynx";
    // 動物の胴・首・頭・口（2026-10-08、全関節の監査の J-09・J-04・J-21、担当 I3）。詳細は AnimalSmalFkApplier の同名フィールド。
    // どれも既定 OFF の新しい振る舞いで、採否は絵で決める。尾の鎖の既定の姿勢の混ぜ（J-23）はフラグなしの欠陥の直しで、smalTailFullChain が OFF なら何も変わらない。
    // smalTrunkChordFromSmal（J-09、係数なし）: SMAL の背骨 1〜6 の弦の振りを胴の骨（Spine〜Spine3）に弧長で配り、肩甲帯（Spine4）を A6 で回す（体の写像）。
    //   10/04 の AM の案 D（胴の弦）の再提案で、C-DM は D について「方向は正しいが効果は小さい」と判定している。肩甲帯に A6 を掛ける部分は 10/04 に評価されていない。
    //   予測（監査の移植）: き甲 p50 25 → 7 px（犬）・30 → 8 px（猫）。猫が頭を下げる動きの不足（J-10）も、単独のフラグは出さずこれで確かめる
    //   （傾き 0.62 → 0.99。ただし全フレームで頭がデータより約 8° 低くなる。C-DM の「A6 系は猫で行き過ぎる」と同じ向き）。
    //   **胴の曲がりはほぼ肩甲帯の関節（Spine3→Spine4）1 か所に集まる**（反論役の移植: p50 17°・p95 35〜38°・最大 60°。Spine〜Spine3 の各関節は p95 2〜6°）。
    //   き甲・胸のメッシュに折れ目が出るおそれがあるので、横からの絵で確かめる。shot の先頭の倍率と ⑧ への影響は未予測（[SCALEFIX]・[DEPTH8] で比べる）。
    // smalHeadNoseAim（J-04 (1)）: 頭の鼻を SMAL の鼻（新しい定数 n_smal = 頭の座標で前から 15° 下、データから較正）へ最小回転で（ロールは B のまま）。
    //   犬の鼻の上向き 目→鼻 +21.9° → +1.4°（頭→鼻の定義では +15.9° → −4.7°）。照準はモデルごとにほぼ一定の回転（犬 20.8°・猫 6.1°）で、実質は頭の bind の
    //   向きの回し直し（否定リストの「bind を共通基準へ」の系統）。イベントの既定の姿勢へ戻す間は (1 − 重み) で弱める。
    // smalNeckFullChain + smalNeckFullChainModels（J-04 (2)）: 首の骨（Neck01 → Neck02 → neck、終点は頭）を SMAL の 6→15→16 の折れ線の同じ弧長の区間の向きへ
    //   （尾の鎖と同じ仕組み・写像。bind の首の形は捨てる。頭の向きは B のまま）。名簿は既定 空。**犬（"LabradorDog"）だけに使う想定で、猫には入れない**
    //   （き甲→頭が犬 +25.9 → −8.6°、猫 −2.6 → −22.7° と行き過ぎる）。倍率は犬の多くの shot で ×1.08〜1.35（一次近似）。
    // smalDriveJaw（J-21）: 頭の子で名前に jaw を含む骨（上顎 UpperJaw は除く）を SMAL の口（関節 32）で回す（B と同じ体の写像）。耳は入れない。
    public bool smalTrunkChordFromSmal = false;
    public bool smalHeadNoseAim = false;
    public bool smalNeckFullChain = false;
    public string smalNeckFullChainModels = "";
    public bool smalDriveJaw = false;
    public bool bodyFrameTail = false;

    // Animal の首・頭・四肢を根と同じ体の写像で共役する（2026-10-04、調査役 AM の B / B′ / C）と、受け身の骨を
    // 実際の Unity の親に付ける（A2）。詳細は AnimalSmalFkApplier の同名フィールド。
    // **bodyFrameNeckHead だけ既定 ON（2026-10-04 第 3 ラウンド）。** 伏せの寄り（f338-410）で今の頭（Labrador の当てはめ済みの頭）は鼻が真上を
    // 向くが、B は頭がおもちゃへ下がる。猫（Lynx）も B で頭が猫と同じく下がる（頭の誤差 39° → 8°）。ただし B は Labrador の鼻を SMAL より常に
    // 約 36° 上げる（bind の差。座りで 10〜15° 上げすぎ）。ほかは既定 OFF。
    public bool bodyFrameNeckHead = true;
    public bool bodyFrameKeepFittedHead = false;
    public bool bodyFrameLimbs = false;
    public bool bodyFrameLimbsFrontAndTailOnly = false;
    // 後肢だけを体の写像で（2026-10-07、実機の再確認「40 秒からの猫の後ろ脚が交差」）。詳細は AnimalSmalFkApplier の同名フィールド。
    // 既定 ON（未 push、採否は絵と実機で）。bodyFrameLimbs = true なら後肢はもともと体の写像なので、これは何も変えない。
    public bool bodyFrameRearLimbs = true;
    // 脚の残り（2026-10-08、全関節の監査 J-05・J-06・J-08・J-20）。詳細は AnimalSmalFkApplier の同名フィールド。すべて既定 OFF、採否は絵で。
    // 以下の画素は移植の予測（監査 audit_joints/AL と I2 の predict_I2.py、視聴者の目、⑦⑧ の置き直し前）。
    // bodyFrameFrontLimbs（既存の欠陥の直し、式は既存）: 前肢だけを体の写像で（前脚の横の成分の左右の反転を直す。尾は変えない）。16_Deer1 にも掛かる。
    //   Labrador の伏せ f341-410 で脚の最下点が p50 +157 px 動き、⑦⑧ の置き直しで位置と大きさが変わる（D-009 の刺激）。⑧ の幅は最大 1.33%（10/07 の bodyFrameLimbs の実測）。
    // smalDriveCarpusHock（新しい振る舞い、係数なし）: 手根・飛節の曲げを写す（Beaver・Fox は除外）。Lynx shot 20 で最下点が p50 +29〜34 px 動く。
    //   0.12 s の平滑のもとで中手の 1 動画フレームのゆれは前腕よりやや大きい（p95 4.53 対 4.23°）。平滑 0 の組み合わせでは大きい（18.1 対 15.1°、監査 AL_verify/v13）。
    // smalDriveFeet（新しい振る舞い）: 前足・後足（指）の曲げを写す。smalDriveCarpusHock と組で使う。
    // smalAbsoluteDirectionLegsOnly（新しい振る舞い、式は既存の smalAbsoluteDirection）: 方向の転写を脚だけに（尾・首・頭は変えない）。
    //   Lynx の肘の曲がりすぎ（+52/+43°、データ −12/−15°）を消すが、bind の脚の形（モデルの立ち姿）は捨てる。Lynx の最下点が p50 −24 px。
    //   イベントの既定の姿勢でも SMAL の rest の脚の形になる（監査 J-23 と同じ型）。
    public bool bodyFrameFrontLimbs = false;
    public bool smalDriveCarpusHock = false;
    public bool smalDriveFeet = false;
    public bool smalAbsoluteDirectionLegsOnly = false;
    public bool passiveBoneUnityParent = false;

    // ⑧ の平滑化を tick ではなく**動画フレーム**で刻む。**既定 ON。**
    //
    // OFF（従来）だと表示レートで結果が変わる。実測（2026-09-09）:
    // バッチ 15.5 tick/フレームでは収束するのに、実機 72Hz の 2.4 tick/フレームでは
    // 追いつかず `ratio` が 1.17 前後で固定される（モデルが常に 17% 大きい）。
    // コマ落ちすればさらに変わるので、被験者実験の交絡要因になる
    // （Docs/experiment-flow.md）。
    // 詳細は Playback.partial.cs の SmoothProjectedDepthRatio。
    public bool smoothDepthPerVideoFrame = true;

    // Human の姿勢・root 深度の平滑化も動画フレームで刻む。**既定 ON。**
    // ⑧ だけ直しても、姿勢が tick 依存だと投影スパンが変わって ⑧ の入力が揺れる。
    // 詳細は HumanSmpl.partial.cs の ResolveSmoothingSeconds。
    public bool smoothPerVideoFrame = true;

    // 下端が画面外に切れているフレームで、⑦ の基準を bbox 下端から bbox 上端に切り替えるか。
    // 切れた下端に合わせると下半身を画面内へ持ち上げてしまうため。上下とも切れている
    // フレームは従来どおり下端合わせにフォールバックする。
    public bool alignTopWhenBottomClipped = true;

    // 「下端が画面の下端で切れているか」の判定にヒステリシスを付ける（2026-10-06、実機のちらつきの直し）。⑦（合わせる基準: 上端 / 下端）と
    // ⑧（目標高: 外挿 / bboxH）は同じ 1 つの判定（IsBBoxBottomClipped）を使う。下で切れた動物の bbox の下端は 717〜720 で 1〜3 px 揺れ、
    // `>= eye_h` だと 1 フレームごとに基準が入れ替わって高さが跳んでいた（bundle_animal で 42 回、shot 1・11・20・24 の中だけ、6〜223 mm。
    // Docs/tmp/devcheck_20261006/README.md の 7-1）。下端 >= eye_h - Enter で「切れている」に入り、< eye_h - Leave で抜ける。
    // 状態は track ごとで、shot の切れ目（偽のカットを含む）で消す。
    // **2 / 4 はこの bundle に合わせた値。** manifest の placement_observation_policy.edge_margin_px = 4 に合わせた 4 px・5 px では
    // shot 14 の f1097〜1099 に今は無い 2 フレームの切り替えができる。抜ける側の 4 が取り決めの 4 px と同じなのは偶然。
    public bool bboxBottomClipHysteresis = true;
    public int bboxBottomClipEnterMarginPixels = 2;
    public int bboxBottomClipLeaveMarginPixels = 4;

    // 測定 B（2026-08-28、診断専用）: SMAL の曲げ（body_pose の寄与）を当てず、
    // bind pose を globalOrient で回しただけの姿勢にする。[ANIMALKP] を有無で比べて
    // 「形状・bind pose の不一致」と「jointFrameMap のロール未拘束」を切り分ける。
    // **シリアライズされる公開フィールドにしてあるのは、非シリアライズの実行時
    // フィールドに書くと play mode に持ち越されないため**（過去に同じ罠を踏んだ）。
    public bool disableSmalBendForDiag;

    // 親の曲げを子へ積む。既定 OFF。
    // 詳細は AnimalSmalFkApplier.accumulateSmalParentBend。
    public bool accumulateSmalParentBend = true;

    // SMAL body_pose の平滑の半減期（秒）。詳細は AnimalSmalFkApplier.smalSmoothHalfLifeSec。
    // 既定 0.12 は従来の定数と同じ（2026-10-02 に検証用に外へ出しただけで挙動は変えていない）。
    public float smalSmoothHalfLifeSec = 0.12f;
    // body_pose（SMAL 関節 1〜34）だけの平滑の半減期（秒）。**負なら smalSmoothHalfLifeSec と同じ（従来）。**
    // 2026-10-03 の試作: 根（worldFk0、体全体の向き）は 0.12 のまま、脚の形だけ 0.03 にする。
    // 走りの脚の振れを 0.12 は 3 割前後しか残さない（歩様 2.3〜3.3 Hz）。詳細は AnimalSmalFkApplier.smalBodyPoseSmoothHalfLifeSec。
    public float smalBodyPoseSmoothHalfLifeSec = -1f;
    // 36_LabradorDog の脚の骨の割り当てを 1 本上へ直す（LionStyleFull リネームで SMAL の肩・股関節が肘・膝に当たり、
    // 上腕・大腿が一度も動いていなかった）。**既定 OFF（2026-10-03、試作）。** Labrador だけに効く。
    // 同じリネームの 39_Lynx に入れると前肢の 2 軸基底が縮退して曲げの平面が約 89° 回るので対象外。
    public bool fixLabradorLegMapping = false;
    // 脚の骨の割り当ての修正（2026-10-04、第 3 ラウンドの調査役 MAP）。AnimalLegMappingFix.Table（prefab 名から数字の接頭辞を外した名前で
    // 完全一致）の組を新しい animal インスタンスに付ける。21 体は脚の骨が 1 本下にずれ、2 体は肉球・つま先の親が食い違っていた。
    // 27_GermanShepherd は尾の付け根の役が首の手前の骨に当たっていた（DEF-spine のフォールバックの前後の取り違え）ので、尾の行もある（2026-10-04）。
    // fixLabradorLegMapping はこの表の LabradorDog 行と同じ（表が優先）。**animalFrontLimbBodyLateralSecondary と同時に使う**
    // （割り当てだけ直すと首の副軸で Moose・Goat・Mink・Fox の前肢上が前後逆、Lynx・Racoon・EuropeanBadger が縮退）。
    // **既定 ON（2026-10-04、ユーザー「一応入れよう」）。** ゲート（MAP の map_gate_corr、θ = 矢状面の角の相関）で表の 22 体すべて
    // 予測どおり +0.95 以上（後肢は予測の範囲）、解剖学的な上腕・大腿が動き出すことを確かめた。実験の Labrador・Lynx の後脚は
    // これで元動画と同じ向きに振れる（修正前は θ −1.00）。
    public bool fixAnimalLegMapping = true;
    // 前肢（SMAL 7, 8, 11, 12）の 2 軸 jointFrameMap の副軸を、首（neck → head の bind 方向）から体の横（SMAL の体の左 ↔ Unity の体の右）に
    // 替える（F2、MAP）。首の bind 方向が後ろ向き（48_Puma: 前肢が前後逆）・横向き（Hyena 等: 26〜74° 回る）・上腕と反平行（GSD 等: 縮退）の
    // モデルで写像が壊れていた。体の横は 52 体すべての前肢関節で縮退しない。首が健全なモデルでの差は中央 3.3°。
    // **既定 ON（2026-10-04、fixAnimalLegMapping と同時）。** ゲート: 48_Puma は今の既定で前肢 θ −1.00 → +1.00、27_GermanShepherd 等の
    // 縮退していた 6 体と Hyena・MountainGoat も +1.00、健全な Labrador は F2 の有無で θ の差 0.00。16_Deer1 は F2 で悪くなったので一度対象から外した
    // （AnimalLegMappingFix.FrontLimbBodyLateralExcluded）が、測ったときは背骨の役が骨盤に付いて胴が止まっていた。2026-10-09 に背骨の役を body に直し
    // （AnimalLegMappingFix.Table）、除外も外した（Docs/tmp/roster_20261009/README.md の 7-2）。
    public bool animalFrontLimbBodyLateralSecondary = true;
    // インタラクティブモーションの動物の四肢のジェスチャ（PawRaise・SampleWalk）を、脚の割り当ての表で付け替えた役の骨ではなく
    // 正規名の骨（front_r_upper など、実験で使っていた骨）に乗せる（2026-10-04、査読役の指摘 A と M1 の絵）。資産は正規名の骨の局所軸で
    // 作ってあり、表で役が 1 本上の骨（解剖学的な上腕）へ移ると、PawRaise が「肉球を上げる」から「肩で脚を後ろへ振る」になった。
    // **既定 ON**（fixAnimalLegMapping と組。表に無いモデルは何も変わらない）。OFF にすると表の役の骨に乗る。
    public bool animalGestureOnCanonicalLimbs = true;
    // 頭のジェスチャ（HeadShake・HeadTiltAndTailWag の頭の点）を、頭の骨の局所軸ではなくモデルの解剖学的な軸（頭の上・鼻・体の右）で回す（2026-10-05、**既定 ON**）。
    // 資産は Labrador の局所軸の意味（局所 Z = 頭の上）で作ってあり、Lynx は局所 Z が頭の上から 45〜48° 傾いていて、首振りが斜め（頷き混じり）になった。
    // Labrador は 10° 前後しか変わらない。鼻（顔の骨）か体の右が取れないモデルは従来どおり。詳細は AnimalPoseApplier.ResolveHeadGestureRemap。
    public bool animalGestureAnatomicalHeadAxes = true;
    // インタラクティブモーションの動物の頭を、ジェスチャ中と歩いて近づく間は視聴者へ向ける（2026-10-04、ユーザー「ユーザーを見て振ってほしい」）。
    // ジェスチャは発火した瞬間の姿勢を土台にするので、B（bodyFrameNeckHead）では座っている瞬間に発火すると上を向いたまま視聴者へ歩いてきた。
    // 頭の鼻先（頭の子孫の顔の骨: 鼻 → 舌 → 唇、無ければ当てはめ済みの頭。animal_head_aim.json は耳や角を指すので使わない）を視聴者の頭の位置へ向ける回転を、首に animalLookAtViewerNeckShare、残りを頭に配り、
    // その上にジェスチャ（首振りなど）を足す。イベントの開始から animalLookAtViewerBlendSeconds で入る。**既定 ON**。姿勢追従（イベントの外）には効かない。
    public bool animalGestureLookAtViewer = true;
    // 向け方を体の向き基準にする（2026-10-05、**既定 ON**）。視聴者が体の前にいるか（体の前 = TryGetCurrentNoseWorldDirection の水平、FaceViewer・歩きと同じ）で
    // 重みを落とし（animalLookAtViewerBodyMaxYawDegrees を超えたら 40° かけて 0）、頭の目標は体の前から ±animalLookAtViewerBodyMaxYawDegrees・
    // 仰角 ±animalLookAtViewerMaxPitchDegrees に収め、横の回転は体の前を通す（体の前からの方位の差で作る）。歩いて戻る始めの animalLookAtViewerBlendSeconds で抜く。
    // 頭の向き基準（OFF、2026-10-04 の方式）では、伏せの犬は頭を体の真横へ向けたまま凍結しているので、体が視聴者を向くと頭が視聴者から 100〜180° それ、
    // 「視聴者が後ろ」と判定して重みを落とした・回す量の上限 90° で 20〜25° 残った（M8 の実測、Docs/interactive-motion-events.md）。
    public bool animalLookAtViewerBodyRelative = true;
    public float animalLookAtViewerBodyMaxYawDegrees = 100f;
    public float animalLookAtViewerMaxPitchDegrees = 60f;
    // 以下 2 つは頭の向き基準（animalLookAtViewerBodyRelative = false）のときだけ使う: 回す角度の上限（縦横合わせて）と、
    // 鼻と視聴者の水平の角度の上限（超えたら 40° かけて重み 0）。140° の根拠にしたダンプ（2026-10-05）は、イベントが終わった後のハンドオフの tick だった
    // （BoneWorldDump は追従の経路でしか書かない）。イベント中を測り直すと（M8）、静止の伏せで向ける前の頭は最大 123.5° それていて 100° なら重みは 0.37 まで落ち、
    // 歩いたとき（145〜180°）は 140° でも落ちた。
    public float animalLookAtViewerMaxDegrees = 90f;
    public float animalLookAtViewerMaxYawDegrees = 140f;
    public float animalLookAtViewerNeckShare = 0.4f;
    public float animalLookAtViewerBlendSeconds = 0.35f;

    // 向きの切り分け用。詳細は AnimalSmalFkApplier.forceRootYawFix。
    public int forceRootYawFix;

    // 頭を連鎖から外す。詳細は AnimalSmalFkApplier.excludeHeadFromChain。
    // **既定 false（2026-09-11 再変更）。**下の headAimFromModelForward と必ずセット。
    public bool excludeHeadFromChain;

    // 頭の照準を当てはめ表から与える。詳細は AnimalSmalFkApplier.headAimFromModelForward。
    // **既定 true（2026-09-11 再変更）。**上の excludeHeadFromChain = false と必ずセット。
    // ここが実効値。AnimalSmalFkApplier 側の同名フィールドは
    // StreamingStereoVideoPlayer.PosePipeline が毎フレーム上書きするので、
    // **applier 側の既定だけ変えても効かない**（2026-09-11 に踏んだ）。
    public bool headAimFromModelForward = true;

    // 頭を体レベルのフレーム写像で解く。詳細は AnimalSmalFkApplier.headUseBodyFrameMap。
    // 既定 false。実測で悪化した。
    public bool headUseBodyFrameMap;

    // jointFrameMap のロールを「同じ肢のもう 1 本」で拘束する 2 軸版を使う（2026-08-28）。
    // 既定 false（従来の FromToRotation）。A/B で効果を確認してから既定を決める。
    public bool useTwoAxisJointFrameMap = true;

    // 頭（SMAL joint 16）に body_pose を当てるか。既定 ON。
    // 詳細は AnimalSmalFkApplier.enableAnimalHeadPose。
    public bool enableAnimalHeadPose = true;

    // VR で選んだモデルと手動 yaw を動画ごと・track ごとに覚える。
    //
    // **既定 ON。** これが製品として期待される挙動で、OFF を既定にすると
    // 「Inspector で ON にし忘れると黙って機能しない」という、まさにこのプロジェクトで
    // 何度も踏んだ罠になる。
    //
    // 計測を汚さないための対策は既定値ではなく BatchPlaybackLogger 側に置いた。
    // バッチは -remember true を明示しない限りこれを OFF にする（測定の再現性のため）。
    public bool rememberTrackCustomization = true;

    // 対象を掴んで手首をひねると回る。既定 ON。
    // 実機で誤爆するようなら Inspector で切れるようにしてある。
    public bool enableGrabRotate = true;

    // 手動の回転（掴んで回す・保存した向き）と bind（2026-10-08、全関節の監査 J-01・J-18、担当 I1）。humanBindRootRelative・humanFollowManualRotation・animalBindWithoutManualRotation の 3 つは 2026-10-09 にユーザーが採用して既定 ON
    // （humanFollowManualRotationAllAxes だけ既定 OFF）。
    // humanBindRootRelative（既存の欠陥の直し）: 人の bind（muscles=0 の姿勢・handBindCorrection・シルエット・T ポーズ）を採る間だけ、Animator の
    //   Transform の world 回転を単位にして、採った後に戻す（GetOrBuildHumanoidCache の HumanBindRootScope）。今は配置が root に書いた回転（手動の
    //   回転を含む）のまま採り、root の回転が bind に 2 回入る（HumanPoseHandler の Get/Set の往復と推定。調整役の撮影 queue_hx1: 60° 回した後に
    //   作り直すと bind が +y まわり 120.00°。FK は worldGO × bodyFk × bind で root の回転を使わないので、腰の線の向きが f620 で約 160°・f900 で
    //   約 120° ずれ、肩と腰の線が 31.6° ねじれた）。「inv(root) × bind で持つ」形では 2θ のうち θ が残るので、採る間だけ単位にする。
    //   配置の回転（pinhole の基底 × prefab の回転）が単位なら（バッチ、人 16 体の prefab）、手動の回転が無いときは今と同じ値。
    //   効くのはキャッシュを作るときだけなので、起動時（再生前）に入れる（途中で入れても、モデルを作り直すまで今のキャッシュのまま）。
    // humanFollowManualRotation（新しい振る舞い。humanBindRootRelative と組でだけ効く）: 手動の回転を SMPL の FK の根（平滑の後の worldGO）の左と
    //   keypoints（AimAt・足の高さ合わせの目標）の両方に掛け、人の体を手動の回転に付いて回す。今は回しても root だけが回り、体は回らない
    //   （queue_hx1: 60° 回しても腰の線の向きは 162.2° のまま）。既定は yaw だけ（動物の FK と同じ。掴む手首の傾きで体が傾かない）。
    //   平滑の後に掛けるので遅れずに回る（動物は根の平滑 0.12 s の分だけ遅れる。an_yaw の f310 で +51.8°）。
    // humanFollowManualRotationAllAxes: humanFollowManualRotation で pitch・roll も掛ける（配置と同じ 3 軸）。
    // animalBindWithoutManualRotation（既存の欠陥の直し）: 動物のリグのキャッシュ（bind）を採る間だけ、インスタンスの root を手動の回転を除いた
    //   配置の回転（pinhole の基底 × prefab の回転）に置き、採った後に戻す（AnimalPoseApplier.PrebuildRigCacheWithoutManualRotation）。今は手動の回転の
    //   まま bind を採り、SMAL の FK が root の yaw をもう一度掛ける（queue_syn1 の an_yawswap: f600 の作り直しで bind が全 20 骨とも +y まわり
    //   60.000°、体の向きが回さない走り＋129.5°（p50）。回したまま作り直さない an_yaw は＋60.0°）。手動の回転が無いフレームでは呼ばない（今と同じ経路）。
    public bool humanBindRootRelative = true;
    public bool humanFollowManualRotation = true;
    public bool humanFollowManualRotationAllAxes = false;
    public bool animalBindWithoutManualRotation = true;

    // バッチ検証専用の手動 yaw 注入。"track:deg" をカンマ区切りで書く（例: "0:90,1:-45"）。
    // 実機の手動回転は VR の UI からしか操作できず、Editor では再現できない。
    // 回転経路（ApplyManualTrackYawOffset → prefab 補正の合成）を batchmode で
    // 目視確認するための入口。空文字なら何もしないので、通常再生には影響しない。
    public string batchManualYawSpec = "";

    // 同じくバッチ検証専用の手動スケール注入。"track:倍率" をカンマ区切りで（例: "1:2.0"）。
    public string batchManualScaleSpec = "";

    // バッチ検証専用。再生中にモデルを差し替える。"track:frame:index" をカンマ区切りで。
    // 「途中でキャラを替えたら大きさが変わった」の再現用。実機ではピッカーからしか
    // できない操作なので、Editor で同じ経路（RecreateTrackInstanceForModelSelection）を踏む。
    public string batchSwapModelSpec = "";
    // 検証用（batchmode のみ、2026-10-02）: animal の骨の割り当て（AnimalBoneMappingOverride）を上書きする。
    // 書式 "<prefab 名に含む文字列>|frontLUpper=LeftShoulder01;frontLLower=front_l_upper;..."（| が無ければ全 animal）。
    // 空なら何もしない。適用は ApplyAnimalBoneOverride（Playback.partial.cs）。
    public string batchAnimalBoneOverrideSpec = "";

    // バッチ検証専用。設定パネルを開いた状態で始める。
    // パネルの配置は目で見るしか確認できず、実機では VR に入らないと開けない。
    // 過去に Home / Bundle ボタンを枠外に置いた事故があるので、撮って確かめる口を用意する。
    public bool batchOpenSettingsOnStart;

    // 同じくバッチ検証専用。モデルピッカーを開いた状態で始める。
    public bool batchOpenModelPickerOnStart;

    // 同じくバッチ検証専用。パネルの要素を canvas 座標で書き出す。
    public bool batchDumpPanelLayout;

    // 同じくバッチ検証専用。ピッカーを開いたときに表示するページ（0 起点）。
    // 目的のモデルが 2 ページ目にあると、バッチではクリックできず確認できないため。
    public int batchModelPickerPage;

    // モデルパネルをどちらのタブで開くか。"edit" で編集タブ。
    public string batchModelPickerTab;

    // キーの前後送りが使うフレーム直指定シークの検証用。
    // EditMode テストでは clip の無い VideoPlayer しか作れず、frame を書いても
    // -1 のままなので確かめられない（RuntimePlaybackControllerTests の既存失敗 2 件はこれ）。
    // 実動画での確認はバッチでやるしかない。
    public int batchSeekTestFrame = -1;

    // SMAL FK のあとに四肢を keypoint の位置へ向ける（Human の AimAt に相当）。
    // 既定 false。A/B で確認してから既定を決める。docs/smpl-retargeting.md 参照。
    public bool enableAnimalKeypointAimAt;

    // 横方向のずれを測る診断ログ [HPOS]。配置には影響しない。
    public bool logHorizontalPlacement = false;

    // Animal 版の姿勢一致診断 [ANIMALKP]。human の logBoneVsKeypoint に対応する。
    public bool logAnimalBoneVsKeypoint = false;
    public bool refineDepthFromProjectedBones = true;

    // shot 境界の前後で bbox が連続している（同じ track が同じ位置・同じ大きさで写っている）なら、
    // 「偽のカット」とみなして scale のロックと ⑧ の比を持ち越す。**既定 ON（2026-09-18）。**
    // bundle_animal の 1104 / 1117 / 1127 / 1130 / 1144 / 1146 は 1.4 秒に 6 回の境界で、2〜14 frame の shot が
    // 連続する（走る犬をカット検出が拾った偽陽性）。境界ごとに再ロックすると大きさが往復した。
    // 姿勢の平滑化などのリセットは従来どおり行う（連続なら 1 frame の再初期化で見えない）。
    public bool keepScaleAcrossContinuousShotBoundary = true;

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

    // Else の連結配置（B + C、2026-09-11）。遠方で隣り合う Else モデルどうしが 3D で刺さる
    // 問題への対処。popout は約 1/800 のジオラマだが、モデルは「その深度で bbox の見かけに
    // なる大きさ」で置くのでジオラマ縮尺の約 7 倍あり、bundle の深度差（遠方で数 mm）では
    // 車体（全長 80〜140 mm）を端と端で接して置けない。深度信号を良くしても届かない
    // （理想でも 10〜24 mm。docs/bundle-placement.md「bundle_train 遠方で 1 両目と 2 両目の
    // 前後が出ない」）。
    //
    // B: 奥の Else を自分の視線に沿って、手前の Else と中心間距離が (全長の和)/2 になる所まで
    //    奥へ動かす。scale を深度に比例させるので絵の位置・大きさは変わらず、立体視と遮蔽だけが変わる。
    // C: 連結した Else の向きを、隣との中心を結ぶ方向（進行方向）に合わせる。
    //    連結中とその後は手動回転（yaw/pitch/roll キー）を使わない。
    // 前後の順は配置深度で決め、差が elseChainDepthTieMeters 以内なら bbox 高（大きい＝手前）で
    // 決めた順を track ペアごとに記憶して以後は変えない（車両は追い越さない）。
    //
    // **2026-09-18 に既定 OFF。** train 用の機能で、train は研究対象から退役した。car では別レーンの
    // 2 台（深度 0.73 / 0.835）を列車扱いして yaw 112° を与え、連結が解けた後も残るので、prefab に
    // 焼いた正面向き 180° と合わさって 292°（横向きで少し後ろ寄り）になった。実機でユーザーが
    // 「車が逆を向いている」と指摘。train を見るときは Inspector かバッチの -elseChain true で戻す。
    public bool enableElseChainPlacement = false;
    [Min(0f)] public float elseChainDepthTieMeters = 0.02f;
    // 隣と見なす中心間距離の上限（接触距離の何倍か）。これより離れていれば向きも触らない。
    [Min(1f)] public float elseChainNeighborFactor = 2f;
    // 連結で決めた向きに足す角度。モデルのどちらの端が先頭かは prefab 次第で、
    // 06_DieselLocomotive はキャブ（先頭）が +Z 端（実機で確認、2026-09-11: 通過時に先頭が
    // 進行方向の反対を向いていた）。0 なら −Z 端が先頭の扱いになる。
    public float elseChainHeadingOffsetDeg = 180f;
    // 連結の向きの 1 frame あたりの変化量の上限（度）。通過中に後続が奥へ押し出されると連結方向が
    // 視線側へ傾き、先頭車が視聴者の方を向いてしまう（f885〜935 で 108° → 161°）。その暴走を鈍らせる。
    [Min(0f)] public float elseChainMaxYawStepDeg = 1f;
    // 押し出し倍率 k の 1 frame あたりの変化量の上限。通過中は端合わせと押し出しが互いに影響して
    // k が 1.0 ↔ 2.1 を往復した（f920〜945）。往復を鈍らせる。0 で無効。
    [Min(0f)] public float elseChainMaxPushStep = 0.05f;
    public bool logElseChainPlacement = false;

    // Else の frame out 継続（2026-09-11）。画面の端に掛かった Else は「可視部分の中心」ではなく
    // 見切れる直前の幅から真の中心に置き、scale も直前の値で固定する。track が消えた後は
    // 直前の速度で進め続け、**視界から映らなくなったら**非表示にする（2026-09-18 ユーザー要望。
    // それまでは全長 × 1.2 進んだら消していた）。距離（全長 × elseFrameOutCoastLengths）と 180 frame は安全弁。
    // データは画面の外を表さない（bbox は可視部分のみ、anchor はその中心で hold される）ので
    // 外挿になる。StreamingStereoVideoPlayer.ElseFrameOut.partial.cs。
    public bool enableElseFrameOutContinuation = true;
    [Min(0.1f)] public float elseFrameOutCoastLengths = 6f;
    // 上と同じことを画面の上下端でも行う（2026-09-17）。car clip（D-014）は 5 台とも下端から出る。
    // OFF だと下端に掛かった frame は「見切れなし」扱いで、bbox 高（105 → 7 px）に追従して
    // モデルが下端に向かって縮み、track が消えた瞬間に消える。左右の見切れが同時にあるときは左右を優先。
    // enableElseFrameOutContinuation が OFF ならこれも効かない。
    public bool enableElseVerticalFrameOutContinuation = true;
    // 縦の frame out で、端に掛かる前の 3D の動き（横・奥行き）と大きさの変化率を端に掛かっている間と慣性で続ける
    // （2026-09-18、ユーザー要望「真下に落ちるのではなく、近づきながら」）。OFF なら従来どおり真下へ、大きさ固定。
    public bool elseFrameOutFollowMotion = true;

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

    // 計測用: 描画されるメッシュの頂点そのものを投影した外接矩形を [MESH2D] に出す（2026-09-25）。
    // [PLACE] の sizeRatio は Renderer.bounds（world 軸の箱）の 8 隅の投影なので、体の前後の広がりぶん
    // 必ず過大に出る。boneRatio は骨格（頭の関節〜つま先の関節）なので髪や靴を含まない。
    // 「見た目のシルエット」が bbox からどれだけはみ出しているかは、この頂点投影でしか測れない。
    // SkinnedMeshRenderer.BakeMesh を使うので重い。[PLACE] と同じ間隔でだけ走る。
    public bool logMeshProjection = false;

    // [MESH2D] の切り分け用: SkinnedMeshRenderer ごとに可視状態・rootBone・倍率 1 での大きさを [MESH2D-PART] に出す。
    // 行数が多い（レンダラ数 × 計測フレーム数）ので、原因調査のときだけ。バッチは -meshParts true で入れる。
    public bool logMeshProjectionParts = false;

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
    // 被験者実験の StereoOnly / Monocular 条件用。最初のフレームから normal mode
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
    public float interactiveMotionMinIntervalSeconds = 12f;
    public float interactiveMotionMaxIntervalSeconds = 24f;
    [FormerlySerializedAs("interactiveMotionDurationSeconds")]
    public float staticAnimationDurationSeconds = 2.4f;
    [FormerlySerializedAs("interactiveMotionBlendSeconds")]
    public float interactiveHandoffBlendSeconds = 0.8f;
    public float humanApproachStopDistanceMeters = 0.6f;
    public float humanWalkSpeedMetersPerSecond = 0.8f;
    public float animalApproachStopDistanceMeters = 0.5f;
    public float animalWalkSpeedMetersPerSecond = 0.5f;
    // ランダムのイベントを今の表示位置から始める（2026-10-05、既存の不具合の直し）。起点に使っていた livePosition は bbox の面積の門
    // （直前に通った面積の 50% 以上のときだけ更新、基準の面積は shot をまたいで戻らない）を通った位置で、倍率の違う shot では門が閉じたまま、
    // Labrador の track 0 は 1146 フレーム中 551 で最長 8 s 古い位置になり、開始の 1 tick で体が 18〜19 cm 跳んでいた。門つきの位置はフレームアウトの起点用に残す。
    public bool interactiveRandomOriginFromDisplayed = true;
    // 動物の向き替えに時間の緩急を付ける（2026-10-05）。FaceViewer は 30〜61° を 0.4〜0.6 s、歩きの段の始めは 1 秒に 360° を目安に 0.2〜0.45 s、
    // ハンドオフは smoothstep。これまでは root の向きを 1 tick で切り替え、SMAL の根の平滑が回転だけを遅らせて骨の位置は root に即座についていくので、
    // 体が 10〜24 cm 瞬間移動していた。段の長さ（動画の停止時間）は変えない。
    public bool animalInteractiveTurnEasing = true;
    // 静的イベントで体を回さずに頭で視聴者を向く角度（2026-10-05、動物の動きの作り直し）。体の前と視聴者の向きの差のうち、この角度までは体を回さず
    // 頭を視聴者へ向ける層（animalGestureLookAtViewer、±100°）に任せ、超えた分だけ体を回す。0 なら従来どおり体ごと視聴者へ向き直る。
    // 足を踏み替えずにその場で回るので、伏せ（Labrador f380）は 61° 回って前脚の先が床を 44 cm 掃き、Lynx（track 1、f2025）は 42° で前足が 20〜32 cm 滑っていた
    // （R3・R4 の record_pose.csv）。頭を向ける層が働かないイベント（システムのフレームアウト、層を切ったとき）は従来どおり体ごと回す。
    public float animalStaticHeadOnlyTurnDegrees = 80f;
    // 発火の判定を、その tick の配置と表示位置の記録の後に行う（2026-10-06、既存の不具合の直し）。前に置いていたので、track が再び現れた
    // 最初の tick（ループ・シーク）と shot の切れ目の最初の tick では、前の出番の位置・倍率・凍結姿勢からイベントが始まっていた。
    // 実験の 12〜24 s 間隔でも、track 0 は 32.5 s・track 1 は 38.2 s 画面から居なくなるので、2 周目以降の f0・f1146 ではほぼ必ず
    // 最初の tick で発火する。イベントの開始は 1 tick 遅れる。位置の効果は interactiveRandomOriginFromDisplayed が ON のときだけ。
    public bool interactiveScheduleAfterPlacement = true;
    // 歩いて近づくときに止まる距離を、root ではなく体（スキンの骨のうち視聴者の目に一番近い点）で測る（2026-10-06、既存の不具合の直し）。
    // root は体の中心ではない。SMAL FK の根の骨は root から一定の位置にあり（Lynx (0.007, 0.736, -0.316)·s）、カメラを向いた動物では
    // 頭が root より「骨盤から頭まで + 0.316·s」前に出る。root で 0.5 m に止めていたので、寄りの場面の Lynx（f1206、倍率 0.51）は
    // 頭が目の面を 6 cm、鼻が 15 cm 越えて視聴者の頭に埋まった（Docs/tmp/devcheck_20261006/README.md の 7-3）。
    // 止まる距離は animalApproachStopDistanceMeters をそのまま使う（目と最も近い骨の 3D 距離）。各点は行き先の向きに回してから測る。
    // 歩ける距離が interactiveApproachMinTravelMeters 未満なら歩かない: animalApproachNoRoomFallsBackToStatic が true ならその場の
    // 動き（静的イベント）に切り替え、false なら発火せずに次の機会へ送る（車と同じ）。0.10 m と切り替えの形はユーザーの判断待ち。
    // 距離を保証するのは歩き終えた時点の姿勢だけ（その後の視聴者を見る層・ジェスチャは点を近づけうる）。
    public bool animalApproachByNearestPoint = true;
    public float interactiveApproachMinTravelMeters = 0.10f;
    public bool animalApproachNoRoomFallsBackToStatic = true;
    // 動物のランダムのイベントを既定の姿勢（prefab の bind の立ち姿）に戻してから動かす（2026-10-07、ユーザーの要望「human みたいにデフォルトのポーズに
    // 戻してからやる方がいい。動物だと変な体勢の時にアニメーションが起こることを避けたい」）。今は発火した tick の SMAL を凍結して FK に通し、その曲がりの上に
    // 身ぶりを足していた。発火から animalDefaultPoseBlendSeconds の段 to_default で FK の入力を既定の姿勢へ混ぜ（AnimalSmalFkApplier.smalDefaultPoseWeight）、
    // 混ぜ終えてから身ぶり・歩きを始める。秒数が負なら interactiveHandoffBlendSeconds（シーン値 0.45 s）を使い、0 なら最初の tick で切り替える（Human のクリップの
    // 切り替え方）。足の高さは変えない（root は凍結のまま。下端が見える shot では足が数 cm 沈む・浮く）。歩くかどうかは混ぜ終えた姿勢の骨で決める。
    // 動画の停止は to_default の分だけ延びる（実験ログに to_default の行）。SMAL の track だけ（keypoint の代替経路・Human・Else は変わらない）。
    public bool animalEventFromDefaultPose = true;
    // 脚の基準姿勢を skin 姿勢に（2026-10-08、全関節の監査 J-03、新しい振る舞い・既定 OFF）。詳細は AnimalPoseApplier の同名フィールド。
    // smalLegReferenceSkinPose: smalLegReferenceSkinPoseModels に載ったモデルだけ、リグのキャッシュを作るとき（モデルを置いた最初の姿勢適用）に
    //   脚の鎖（肩甲骨 → 脚の役 → 指）の局所回転を skin 姿勢（メッシュが歪まない姿勢）へ一度だけ書く。以後の bind（相対の転写の基準・上の
    //   animalEventFromDefaultPose の既定の姿勢・ジェスチャの肩甲骨の土台）が skin の左右対称な立ち姿の脚になる。39_Lynx の prefab の既定姿勢は
    //   左右非対称で、左前足が常に右より後ろにあった（前の球節の前後差 体長比 モデル −0.220 対データ +0.073）。
    //   キャッシュを作るときに 1 回だけ読むので、途中で切り替えても次にモデルを作り直すまで変わらない（バッチの -setFields は再生前に入るので効く）。
    //   副作用（棚卸しの FK の静的な予測、Lynx、倍率 1 の root 座標）: 前の球節の前後差 −0.264 → −0.013、肩の支点 −0.097 → −0.010、体長 +3.6%。
    //   「前の球節が約 2.5 cm 下がる・指が既定姿勢の床より最大 2.4 cm 下に出る」は bind 姿勢（静的）の値。再生中は最下点の骨が替わり、shot の先頭の
    //   倍率と ⑦ は shot によって上下どちらにも動く（反論役の目安 ×0.92〜×1.10、未確定）。後脚も skin 姿勢に入るので、後脚の下腿が約 17° 起き、
    //   後ろ足が毎フレーム p50 約 0.09〜0.10 root 動く（後脚はもともと左右対称なので、非対称の直しの外の変化。keypoints に近づく向き）。
    //   胴の鎖と局所位置は書かないので skin 姿勢そのものにはならない（肩の低さは変わらない。四肢は真の skin 姿勢より体の下へ約 7° 寄る）。
    // smalLegReferenceSkinPoseModels: prefab 名のカンマ区切り（先頭の「数字_」は無視）。既定は空 = どのモデルにも掛けない。例 "Lynx"。
    //   05_Horse・20_Donkey は skin 姿勢自体が左右非対称なので入れない（Resources/animal_leg_skin_pose.json にも焼いていない）。
    public bool smalLegReferenceSkinPose = false;
    public string smalLegReferenceSkinPoseModels = "";
    // 非四足モード（2026-10-08、鳥 3 体とカンガルーを SMAL の FK で。新しい振る舞い。2026-10-09 にユーザーが絵を見て採用し、bool はすべて既定 ON = 撮影の B2）。詳細は AnimalPoseApplier の同名フィールド。
    // smalNonQuadrupedRig: 主スイッチ。名簿のモデルだけ、リグのキャッシュを作るとき（モデルを置いたとき）に、役を AnimalNonQuadrupedRig.Table の行で付け替え
    //   （鳥 spine=Pelvis・neck=Neck3・尾の役なし、カンガルー spine=Hips・neck=Neck02）、体の前を「頭 → 顔の骨（鼻・顎）」の水平の向きにし、前肢の無い鳥も
    //   SMAL FK の入口を通す。カンガルーの尾 Tail01 は bind のまま（腰に剛体）。モードは作るときに決まるので、切り替えは Change Model まで効かない。
    //   ON でも名簿の外（実験の Labrador・Lynx を含む）は何も変わらない。犬の track では鳥・カンガルーの首が約 20° 後ろへ反り、くちばしが約 15° 上を向く
    //   一定の偏りが出る（B は bind からの相対なので、犬が首を SMAL の rest より高く保つ分がそのまま乗る。動きではない。35_Kangaroo の実測）。
    //   イベント（向き直し・歩いて近づく・既定の姿勢・ジェスチャの首の点・カンガルーの頭を視聴者へ向ける）の挙動も変わる。（2026-10-09 に、モード＋脛＋基準姿勢＋直立の組で採用）
    // smalNonQuadrupedRigModels: 名簿（prefab 名のカンマ区切り、先頭の「数字_」は無視）。表に行のあるモデルだけ効く。
    // smalNonQuadrupedHock: 鳥の脛（関節 19/23）を手根・飛節と同じ写し方で。前肢の無いリグ（鳥）だけ。鳥の脛はこの切り替えだけで決まり、全体の
    //   smalDriveCarpusHock（判断待ち）を ON にしても動かない。鳥の前足・指は smalDriveFeet が ON でも受け身のまま。カンガルーには効かない。毎 tick 読む。
    // smalNonQuadrupedReferencePose: 鳥の基準姿勢（Resources/animal_reference_pose.json。2026-10-09 に Assets/Editor/AnimalLegSkinPoseBaker で焼いた）をキャッシュを作るとき 1 回だけ書く。
    //   表・行が無い・合わないなら何もしない（ログはファイルが無いことは再生ごとに 1 回、行の理由はモデルごとに 1 回）。
    // smalNonQuadrupedUprightRoot: 体の根を上下まわりだけにする（イベントの既定の姿勢と同じ式）。犬が横に寝る・座るときに一緒に倒れない。毎 tick 読む。
    public bool smalNonQuadrupedRig = true;
    public string smalNonQuadrupedRigModels = "Goose,Guineafowl,Pheasant,Kangaroo";
    public bool smalNonQuadrupedHock = true;
    public bool smalNonQuadrupedReferencePose = true;
    public bool smalNonQuadrupedUprightRoot = true;
    // （次の animalDefaultPoseBlendSeconds は上の animalEventFromDefaultPose の説明の続き）
    public float animalDefaultPoseBlendSeconds = -1f;
    // 歩けずにその場の動きへ切り替えたイベントは体を回さない（2026-10-07、10/06 の直しの食い違いの直し）。歩ける距離の判定は「視聴者へ向き直った姿勢が
    // もう止まる距離の内側」と見て歩きを捨てたのに、切り替え先の静的イベントが同じ向き直りを確かめずに行い、横の近い視聴者の顔を猫の頭が通り越した
    // （実機 f1853、向き直った後の最も近い骨が目から 0.10 m）。頭を視聴者へ向ける層は今までどおり。
    public bool animalNoRoomStaticKeepsHeading = true;
    // Else（車などの剛体）の自発的な動き（2026-09-25、ユーザー指示）: 走って視聴者の手前まで近づき、
    // 弧を描いて U ターンし、走って戻る。その場回転は使わない（車らしくないため）。
    // 向きは前後のホイール（FL/FR/RL/RR の子）から決める。ホイールが無いモデルは向きを変えずに滑る。
    // 走行中はホイールを進んだ距離ぶん回す。U ターンの半径は 0 なら車体の長さから決める。
    // Random イベントで動画を止める前後の音量フェード（秒）。0 なら即時（以前の挙動）。
    // 音が急に消えるのが驚かれる（2026-09-28、実機のユーザー指摘）ので、止める前にこの時間で 0 へ落とし、
    // 再開後に同じ時間で戻す。モデルの動きはフェードの開始と同時に始まる（絵はあと fade 秒だけ動く）。
    public float interactiveMotionAudioFadeSeconds = 0.5f;
    public bool enableElseInteractiveMotion = true;
    public float elseApproachStopDistanceMeters = 0.45f;
    public float elseDriveSpeedMetersPerSecond = 0.35f;
    [Min(0f)] public float elseTurnRadiusMeters = 0f;

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
    // 高さは RuntimeControlsDefaultCanvasHeight と同じ比率で持つ（0.16 * 440/300）。
    // 揃えないとボタンの見かけの大きさが変わる。
    private static readonly Vector2 ControlsBarSizeMeters = new Vector2(0.6f, 0.235f);
    private static readonly bool EnablePauseHotkey = true;
    private const float RuntimeFovxMinDeg = 40f;
    private const float RuntimeFovxMaxDeg = 140f;
    private const float RuntimeFovxDefaultDeg = 90f;
    private const float RuntimeScreenDistanceMinMeters = 0.5f;
    private const float RuntimeScreenDistanceMaxMeters = 3.0f;
    // 高さは canvas 340px × (0.615/640 m/px)。canvas を詰めたので板も同じ比で縮める。
    // 比を変えると文字だけ拡縮して読みにくくなる。
    private static readonly Vector2 SettingsPanelSizeMeters = new Vector2(0.78f, 0.327f);
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

