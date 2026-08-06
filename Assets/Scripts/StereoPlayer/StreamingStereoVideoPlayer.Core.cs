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

    [Header("Human Bone Length")]
    // 表示モデルと元映像の脚の骨長比を合わせる。既定 Human モデルは胴で正規化した脚が
    // 映像より 8.3% 短く、足首が bbox 高さの約 10% 上にずれていた（2026-08-06 実測）。
    // モデル切り替え時は新しいインスタンスの生成時に自動で掛かる。
    public bool enableHumanBoneLengthCorrection = true;
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

