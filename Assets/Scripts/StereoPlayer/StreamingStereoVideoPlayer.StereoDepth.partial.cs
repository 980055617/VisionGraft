using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// S（2026-10-16、新しい振る舞い・既定 OFF。Docs/tmp/roadmap_20261016/README.md の「直し S」）: 種別で分けない 1 つの決まり。
// 倍率をロックする track（人・動物）は、ロック（shot 先頭の U1・作り直し）ごとに k を 1 つ決める。⑨ が付けていない Else（同じフレームに人・動物が
// 一度も出ない track、今は車）はフレームごとに映像の焼き込まれた視差が示す奥行き Zv に置く。⑨ が付けている Else（ボール）は付いていく骨格の k に乗る（⑨）。
// 平面の絵は変えない: 目（ピンホールの原点）を中心に、位置・倍率と長さを持つ状態（動物の root の位置の平滑・人の根の奥行きの平滑）を同じ k 倍する。
// 書き換えるのは ApplyMetaTarget の値渡しの複製だけ。metaFrameObjects（⑨ が人とボールの差に使う）は popout のまま。
internal static class StereoVideoDepthContract
{
    // 生成側の splat の契約（D-017 A1）。manifest.json にはまだ無い（依頼 5-1）ので S のフラグの内側でだけ固定値で使う。
    // 実測（2026-10-16、G3、source/pre_removal_stereo_video.mp4 の NCC。実行時は読まない）: 動物のマスク全体で d = 20 − 40 z01 + 0.06 px（rms 1.22）。
    public const float MaxDisparityPx = 20f;
    public const float ReferenceWidthPx = 1280f;
    public const float ZeroDisparityNearness = 0.5f;

    // 焼き込まれた視差 u_L − u_R（基準の幅の px、＋ = 交差 = 画面より手前）。
    public static float BakedDisparityPx(float nearness)
    {
        return (Mathf.Clamp01(nearness) - ZeroDisparityNearness) * 2f * MaxDisparityPx;
    }

    // 映像の点の画面上の交差の隔たり = dPx·p − 2·plateOffset（p = 画面の幅 ÷ 基準の幅、板は ∓plateOffset、StereoScreenPlacement.cs:46-47・UV のずらしは無い Screens.cs:240-264）。
    // 目 ±ipd/2 から見た奥行き Z の点の隔たりは ipd·(D/Z − 1)。等しいとおいて Z を解く。
    public static bool TryPerceivedDepth(float dPx, float screenDistance, float screenWidth, float plateOffset, float ipd, out float depth)
    {
        depth = 0f;
        if (!(screenDistance > 0f) || !(screenWidth > 0f) || !(ipd > 0f) || float.IsNaN(dPx) || float.IsInfinity(dPx))
        {
            return false;
        }

        float denominator = ipd + dPx * (screenWidth / ReferenceWidthPx) - 2f * plateOffset;
        if (!(denominator > 1e-5f))
        {
            return false;   // 無限遠かその先（極）
        }

        depth = screenDistance * ipd / denominator;
        return !float.IsNaN(depth) && !float.IsInfinity(depth);
    }

    // 奥行き z のモデルの点の画面上の交差の隔たり（基準の幅の px）。
    public static float ModelDisparityPx(float z, float screenDistance, float screenWidth, float ipd)
    {
        return ipd * (screenDistance / Mathf.Max(1e-4f, z) - 1f) / (screenWidth / ReferenceWidthPx);
    }

    // 映像の点の画面上の交差の隔たり（基準の幅の px）。
    public static float VideoOnScreenDisparityPx(float dPx, float screenWidth, float plateOffset)
    {
        return dPx - 2f * plateOffset / (screenWidth / ReferenceWidthPx);
    }
}

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    public enum StereoDepthMatchPoint
    {
        Pivot = 0,       // (a) 配置が anchor に置く骨（U1 の中心）
        AabbCenter = 1,  // (b) レンダラーの AABB の中心（[PLACE] の depth と同じ量）
        Contract = 2,    // (c) AniMer の 6 点（kp7・18・10・11・12・13）に当たる見えている側の骨の中央値。骨の代理（D-017:330 の 7 点目と面の手前は入れていない）
    }

    // S の主スイッチ（2026-10-16、既定 OFF）。種別で分けない: 倍率をロックする track（人・動物）はロックごとに k を 1 つ、
    // ⑨ が付けていない Else（車）はフレームごとに Zv、⑨ が付けている Else（ボール）は ⑨ で付いていく骨格の k に乗る。
    public bool stereoDepthMatchVideo = false;
    // 合わせる点（骨格の track だけに効く）。Contract は AniMer の 6 点に当たる骨を持つリグだけ。持たないリグ（人・前肢の役が無い鳥）は AabbCenter（ログに mode=AabbCenter）。
    public StereoDepthMatchPoint stereoDepthMatchPoint = StereoDepthMatchPoint.AabbCenter;
    // A（2026-10-20、新しい振る舞い・既定 OFF。S が ON のときだけ効く）: ロックが決まった後は、⑧ が箱の大きさで奥行きを決める代わりに、合わせる点を毎フレーム Zv に置く。
    // 箱から奥行きを決めると、人が寝転がる・逆さになる・しゃがむと箱の形が変わったのを奥行きの変化と取り違える（S なしでも寝転がる場面で 129 cm、動画は 94 cm。
    // S はロックの 1 フレームで合わせるだけなので残りはその分ずれる。Docs/tmp/stereo_s_20261017/README.md の 10/20）。倍率はロックのまま（ショットに 1 つ）で、
    // 大きさは奥行きから決まる（箱には合わせない）。⑧ の平滑・下限・上限・2 回目の ⑦ はそのまま通す。種別で分けない。
    public bool stereoDepthFollowVideoEveryFrame = false;
    // S が ON のときだけ効く（2026-10-26）: 被験者実験の Model（既定1 ⇄ 既定2）で同じ shot の中でモデルを替えたとき、確定した k をそのまま使う。
    // 今までは作り直したロックで k を押した tick の Zv から測り直したので、押した時刻で奥行きが変わった（10/25 の確かめ役 V_Q3: サッカー f330 で
    // フレームごとの k 1.7〜1.95 に対して確定した k 1.31、奥行きが 30〜50% 跳ぶ見込み）。ピッカーの差し替え・表示し直しは今までどおり測り直す。
    public bool stereoDepthKeepsKAcrossModelPresetSwap = true;
    // S が ON のときだけ効く（2026-10-26）: 自分から動く動作の長さ（止まる距離・歩く/走る速さ・歩ける余地の下限・車の U ターンの半径の上下限・
    // モデルの差し替えでずらす上限）に、目のまわりに広げた倍率（骨格は k、⑨ が付けていない Else は Zv ÷ popout）を掛ける。S は片目の絵を変えずに
    // 両目の奥行きだけ動画に合わせるので、動作も片目の絵では S なしと同じに動く。掛けないと（10/26 の点検 wf_d7c6837b-4ca）、S で遠く大きくなった車が
    // メートルのままの 0.45 m まで来て画面の 25% を覆い（今 11%）動画の止まる時間が 5.9 s（今 3.25 s）に延び、人は止まる距離 0.6 m が近くなって
    // 歩いて近づき始め（サッカーの女性がボールの上に立つ）、猫の動作の途中の Model でキツネが目の前 0.2 m に出た（ずらす上限 0.5 m を超えた）。
    // 視聴者の体の近さの決まり（近さの下限 MinDistanceFromHeadMeters・画面の外で横を通る帯）は実際の頭との距離なので掛けない（倍率が 1 未満でも止まる距離は
    // 近さの下限より近くしない）。⑨ が付けている Else（ボール）は 1（ボールが走るのは人・動物が映っていないフレームだけで、そのときは ⑨ も S も掛からない）。
    public bool stereoDepthScalesMotionLengths = true;
    // 0 = 目の位置から読む（読めなければ 63 mm）。> 0 ならこの値（mm）。バッチは 63 を渡す。
    [Min(0f)] public float stereoDepthIpdOverrideMillimeters = 0f;
    // [STEREOZ] を出す（S が OFF でも k = 1 で同じ式の残差を出す）。
    public bool logStereoDepth = false;
    [Min(1)] public int logStereoDepthEveryNTicks = 30;

    private sealed class StereoDepthTrackState
    {
        public float k = 1f;
        public float kEntry = 1f;
        public bool settled;
        public bool pendingUndo;
        public bool disabledForLock;
        public string reason = "lock";
        public StereoDepthMatchPoint modeUsed = StereoDepthMatchPoint.Pivot;
        public int unsettledTicks;
        public int createdFrame = -1;
        public int keypointTicks;
    }

    private readonly Dictionary<uint, StereoDepthTrackState> stereoDepthByTrack = new Dictionary<uint, StereoDepthTrackState>();
    // この tick に S を掛けた track の、掛ける前の popout の深さ。tick の頭で消して付け直す。
    private readonly Dictionary<uint, float> stereoDepthPopoutThisTick = new Dictionary<uint, float>();
    // ⑨ が付けていない Else（車・風船）のこの tick の Zv ÷ popout（動作の長さの倍率、stereoDepthScalesMotionLengths）。tick の頭で消して付け直す。
    private readonly Dictionary<uint, float> stereoDepthElseRatioThisTick = new Dictionary<uint, float>();
    private readonly List<uint> stereoDepthTrackIdBuffer = new List<uint>(8);
    private readonly HashSet<uint> stereoDepthEligibleElseTracks = new HashSet<uint>();
    private bool stereoDepthElseEligibilityBuilt;
    private readonly List<Renderer> stereoDepthRendererBuffer = new List<Renderer>(32);
    private readonly float[] stereoDepthContractSamples = new float[6];
    private readonly List<XRNodeState> stereoDepthNodeStates = new List<XRNodeState>(16);
    private Transform stereoDepthLeftEyeAnchor;
    private Transform stereoDepthRightEyeAnchor;
    private float stereoDepthIpdCached = -1f;
    private float stereoDepthIpdReadAt = -10f;
    private string stereoDepthIpdSource = "none";
    private bool stereoDepthSettingsInitialized;
    private bool stereoDepthSnapMatch;
    private bool stereoDepthSnapAnyFlag;
    private StereoDepthMatchPoint stereoDepthSnapMode;
    private float stereoDepthSnapIpdOverride;
    private float stereoDepthSnapDistance;
    private float stereoDepthSnapWidth;
    private bool stereoDepthHasPendingUndo;
    private bool stereoDepthContractLogged;
    // S（2026-10-16）: この tick に骨格の姿勢がどの経路を通ったか（TryApplySkeleton の頭で None に戻す。記録だけ）。
    // DirectionOnly = 向きだけで決まる経路（人の SMPL FK、動物の SMAL FK で制御点なし）。目のまわりの k 倍で曲げが変わらないのはここだけ。
    // AbsoluteTarget = 絶対の目標へ向ける経路（人の SMPL の無いフレームの keypoint の配置と IK、動物の keypoint の経路・制御点の姿勢）。None = 骨に姿勢を書かなかった。
    private enum StereoDepthPosePath { None = 0, DirectionOnly = 1, AbsoluteTarget = 2 }
    private Transform stereoDepthPosePathRoot;
    private StereoDepthPosePath stereoDepthPosePath;
    // S（2026-10-16）: この tick の ⑧ の結果（RefineDepthFromProjectedBones が目標の深さを出した所で書く。記録だけで振る舞いは変えない）。
    private uint stereoDepthRefineTrack = uint.MaxValue;
    private int stereoDepthRefineTick = -1;
    private bool stereoDepthRefineClamped;
    private bool stereoDepthRefineEarlyOut;
    private float stereoDepthRefineMoveZ;
    private bool stereoDepthRefineFollow;
    // V（placementFollowVideoDepth、2026-10-26）: この tick の ⑧ が下限で止まった（比の深さより奥へ押した）か。記録だけ。
    private bool stereoDepthRefineFloored;

    private const float StereoDepthFallbackIpdMeters = 0.063f;
    // 目の位置の読みの妥当な範囲（係数ではない。外れたら次の読み方、最後は 63 mm）。
    private const float StereoDepthIpdMinMeters = 0.045f;
    private const float StereoDepthIpdMaxMeters = 0.085f;

    // ---- 契約・幾何 ----

    private bool IsStereoDepthContractAvailable(out string reason)
    {
        if (manifest == null) { reason = "noManifest"; return false; }
        if (!IsAnchorDepthLargerMeansFarther()) { reason = "noDepthPolicy"; return false; }
        if (manifest.eye_w != Mathf.RoundToInt(StereoVideoDepthContract.ReferenceWidthPx)) { reason = "eyeW"; return false; }
        if (experimentMonocular) { reason = "monocular"; return false; }
        // 画面と目の前提（目 = ピンホールの原点、画面はその正面 D）。0 でないと画面が目の方へ傾き（StereoScreenPlacement.cs:43-45）、原点も動く（Screens.cs:633）。
        if (screenOffsetMeters.sqrMagnitude > 1e-10f) { reason = "screenOffset"; return false; }
        if (leftScreen == null || rightScreen == null || !rightScreen.gameObject.activeSelf) { reason = "onePlate"; return false; }
        reason = "ok";
        return true;
    }

    private bool TryResolveStereoScreenGeometry(out float screenDistance, out float screenWidth, out float plateOffset)
    {
        screenDistance = Mathf.Max(0.001f, screenDistanceMeters);   // 面はピンホールの原点から正面へ（Screens.cs:341-352・630-635）
        screenWidth = 0f;
        plateOffset = 0f;
        Transform basis = leftScreen != null ? leftScreen : rightScreen;
        if (basis == null)
        {
            return false;
        }

        GetScreenSizeMeters(basis, out screenWidth, out _, out _);   // 表示している幅（fitScreenToFov なら 2·D·tan(fovx/2)）
        screenWidth = Mathf.Abs(screenWidth);
        bool twoPlates = leftScreen != null && rightScreen != null && rightScreen.gameObject.activeSelf;
        plateOffset = twoPlates ? StereoScreenEyeSeparationMeters : 0f;   // 板は ∓1 mm（Screens.cs:6・371-383）
        return screenWidth > 0.001f;
    }

    private bool TryResolveVideoDepthMeters(float z01, bool forAnimal, out float zv, out float dPx, out float ipd, out string ipdSource)
    {
        zv = 0f;
        dPx = 0f;
        ipd = 0f;
        ipdSource = "none";
        if (float.IsNaN(z01) || float.IsInfinity(z01) || !TryResolveStereoScreenGeometry(out float d, out float w, out float plate))
        {
            return false;
        }

        // 生の z01（NormalizeAnchorZ01 は通さない）。depth_policy のある bundle では nearness = 1 − z01（Manifest.partial.cs:88-91）。
        dPx = StereoVideoDepthContract.BakedDisparityPx(Z01ToNearness(Mathf.Clamp01(z01)));
        ipd = ResolveStereoDepthViewerIpdMeters(out ipdSource);
        if (!StereoVideoDepthContract.TryPerceivedDepth(dPx, d, w, plate, ipd, out zv))
        {
            return false;
        }

        // 極の近く（画面 3 m・fovx 70 では z01 0.965 以上で分母が 0 以下）と ⑧ の上限より奥は使わない。上限は ⑧ と同じ（新しい定数ではない）。
        return zv > MinDistanceFromHeadMeters && zv <= ResolveDepthUpperLimitMeters(d, forAnimal);
    }

    private float ResolveStereoDepthViewerIpdMeters(out string source)
    {
        if (stereoDepthIpdOverrideMillimeters > 0f)
        {
            source = "override";
            return stereoDepthIpdOverrideMillimeters * 0.001f;
        }

        float now = Time.unscaledTime;
        if (stereoDepthIpdCached > 0f && now - stereoDepthIpdReadAt < 1f)
        {
            source = stereoDepthIpdSource;
            return stereoDepthIpdCached;
        }

        stereoDepthIpdReadAt = now;
        float ipd = StereoDepthFallbackIpdMeters;
        string src = "fallback";
        if (TryReadEyeAnchorIpd(out float anchorIpd) && anchorIpd >= StereoDepthIpdMinMeters && anchorIpd <= StereoDepthIpdMaxMeters)
        {
            ipd = anchorIpd;
            src = "eyeAnchors";
        }
        else if (TryReadXrNodeIpd(out float nodeIpd) && nodeIpd >= StereoDepthIpdMinMeters && nodeIpd <= StereoDepthIpdMaxMeters)
        {
            ipd = nodeIpd;
            src = "xrNodes";
        }

        stereoDepthIpdCached = ipd;
        stereoDepthIpdSource = src;
        source = src;
        return ipd;
    }

    // OVRCameraRig の TrackingSpace の子 LeftEyeAnchor / RightEyeAnchor（OVRCameraRig.cs:300-346 が毎フレーム書く）。world の距離なのでリグの倍率も入る。
    // HMD が無い（バッチ）と両方が中央の目に重なって 0 になり、上の範囲で落ちる。
    private bool TryReadEyeAnchorIpd(out float ipd)
    {
        ipd = 0f;
        if (stereoDepthLeftEyeAnchor == null || stereoDepthRightEyeAnchor == null)
        {
            Camera cam = GetViewCamera();
            Transform space = cam != null ? cam.transform.parent : null;
            if (space == null)
            {
                return false;
            }

            stereoDepthLeftEyeAnchor = space.Find("LeftEyeAnchor");
            stereoDepthRightEyeAnchor = space.Find("RightEyeAnchor");
            if (stereoDepthLeftEyeAnchor == null || stereoDepthRightEyeAnchor == null)
            {
                return false;
            }
        }

        ipd = Vector3.Distance(stereoDepthLeftEyeAnchor.position, stereoDepthRightEyeAnchor.position);
        return true;
    }

    private bool TryReadXrNodeIpd(out float ipd)
    {
        ipd = 0f;
        InputTracking.GetNodeStates(stereoDepthNodeStates);
        bool hasL = false;
        bool hasR = false;
        Vector3 l = Vector3.zero;
        Vector3 r = Vector3.zero;
        for (int i = 0; i < stereoDepthNodeStates.Count; i++)
        {
            XRNodeState s = stereoDepthNodeStates[i];
            if (s.nodeType == XRNode.LeftEye)
            {
                hasL = s.TryGetPosition(out l);
            }
            else if (s.nodeType == XRNode.RightEye)
            {
                hasR = s.TryGetPosition(out r);
            }
        }

        if (!hasL || !hasR)
        {
            return false;
        }

        ipd = Vector3.Distance(l, r);
        return true;
    }

    // ---- 倍率 ----

    private float ResolveStereoDepthFactor(uint trackId)
    {
        return stereoDepthByTrack.TryGetValue(trackId, out StereoDepthTrackState st) ? st.k : 1f;
    }

    // V（placementFollowVideoDepth、2026-10-26、既定 OFF）: V の f も目のまわりの倍率なので 1/(k f)。状態が無ければ ÷ 1 で同じ値。
    private float ResolveStereoDepthRootFilterBetaScale(uint trackId)
    {
        return (stereoDepthByTrack.TryGetValue(trackId, out StereoDepthTrackState st) && st.k > 0f ? 1f / st.k : 1f) / ResolveVideoDepthFollowFactor(trackId);
    }

    private static bool IsUsableStereoDepthFactor(float k)
    {
        return !float.IsNaN(k) && !float.IsInfinity(k) && k > 0f;
    }

    // 動作の長さに掛ける倍率（stereoDepthScalesMotionLengths）。今この track の配置に掛かっている目のまわりの倍率: 骨格の track は S の状態の k
    // （確定の前は kEntry、外す待ちの間も掛けたまま。毎 tick 掛ける、ApplyStereoDepthAtTickStart）、⑨ が付けていない Else はこの tick の Zv ÷ popout。
    // どちらも無ければ 1（S が OFF・ロックで S を使わなかった track は k = 1）。発火の tick に 1 回読んで動作の間は持つ。
    // V（placementFollowVideoDepth、2026-10-26、既定 OFF）: V の f も掛ける（placementFollowVideoDepthScalesMotionLengths。S のこのスイッチとは別）。状態が無ければ 1 で今と同じ値。
    private float ResolveMotionLengthScale(uint trackId)
    {
        float videoDepth = ResolveVideoDepthFollowMotionLengthFactor(trackId);
        if (!stereoDepthScalesMotionLengths)
        {
            return videoDepth;
        }

        float k = 1f;
        if (stereoDepthByTrack.TryGetValue(trackId, out StereoDepthTrackState st))
        {
            k = st.k;
        }
        else if (stereoDepthMatchVideo && stereoDepthElseRatioThisTick.TryGetValue(trackId, out float ratio))
        {
            k = ratio;
        }

        return IsUsableStereoDepthFactor(k) ? k * videoDepth : videoDepth;
    }

    // 止まる距離（m）を動作の長さの倍率で伸ばす。倍率が 1 なら元の値そのまま。1 未満で縮めるときは、近さの下限（実際の頭との距離）より近くしない
    // （元の値がそれより近いときは元の値まで）。
    private static float ScaleMotionStopDistance(float stopMeters, float scale)
    {
        if (scale == 1f)
        {
            return stopMeters;
        }

        return Mathf.Max(stopMeters * scale, Mathf.Min(stopMeters, MinDistanceFromHeadMeters));
    }

    private float ResolveAnimalApproachStopDistance(InteractiveMotionState state)
    {
        return ScaleMotionStopDistance(animalApproachStopDistanceMeters, state != null ? state.motionLengthScale : 1f);
    }

    // [MOTION] の行の末尾。倍率が 1 のときは何も足さない（S が OFF のログは今と同じ）。
    private static string MotionLengthScaleLogSuffix(InteractiveMotionState state)
    {
        return state != null && state.motionLengthScale != 1f ? $" lengthScale={state.motionLengthScale:F4}" : string.Empty;
    }

    private bool IsTrackInInteractiveMotion(uint trackId)
    {
        return interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState motion) && motion != null &&
               motion.stage != InteractiveEventStage.Inactive;
    }

    // 骨格 track = 倍率をロックする track（ApplyReplaceableModelTransform の lockScale、Playback.partial.cs:676 と同じ式）。
    private bool IsStereoDepthSkeletonTrack(byte categoryId)
    {
        return IsCategoryPerson(categoryId) || IsCategoryAnimal(categoryId);
    }

    private string StereoDepthCategoryLabel(byte categoryId)
    {
        return IsCategoryAnimal(categoryId) ? "Animal" : IsCategoryPerson(categoryId) ? "Person" : "Other";
    }

    // この tick にこの instance の姿勢が通った経路（同じ ApplyMetaTarget の中で TryApplySkeleton の後に読む）。
    private StereoDepthPosePath ResolveStereoDepthPosePathThisTick(GameObject instance)
    {
        return instance != null && stereoDepthPosePathRoot == instance.transform ? stereoDepthPosePath : StereoDepthPosePath.None;
    }

    // ⑨ が働いているか（OtherDepth.partial.cs:256-261 と同じ条件）。
    private bool IsOtherDepthFollowActive()
    {
        return followOtherDepthToRefinedSkeleton && refineDepthFromProjectedBones;
    }

    // RefineDepthFromProjectedBones（Playback.partial.cs:1412 の後）が呼ぶ。記録だけ。
    private void NoteStereoDepthRefineResult(uint trackId, bool clamped, bool earlyOut, float moveZ, bool follow = false, bool floored = false)
    {
        stereoDepthRefineFloored = floored;
        stereoDepthRefineTrack = trackId;
        stereoDepthRefineTick = Time.frameCount;
        stereoDepthRefineClamped = clamped;
        stereoDepthRefineEarlyOut = earlyOut;
        stereoDepthRefineMoveZ = moveZ;
        stereoDepthRefineFollow = follow;
    }

    // A（stereoDepthFollowVideoEveryFrame）: ⑧（RefineDepthFromProjectedBones）がガードの前に呼ぶ。合わせる点を Zv に置く比（基準点の深さに掛ける倍率）。
    // ⑧ の移動は基準点の光線に沿った平行移動なので、どの点の深さも同じだけ動く: 目標の基準点の深さ = 基準点の深さ + (Zv − 合わせる点の深さ)。
    // ロックが決まる前・ロックで S を使わなかった（絶対の目標へ向ける経路）・外す待ち・モーション中・Zv が読めない tick は false（⑧ は箱の比のまま）。
    private bool TryResolveStereoFollowRatio(GameObject instance, MetaObj obj, Vector3 eye, Quaternion camRotation, float bodyZ, out float ratio)
    {
        ratio = 1f;
        if (!stereoDepthMatchVideo || !stereoDepthFollowVideoEveryFrame || instance == null || bodyZ <= 0.0001f ||
            !IsStereoDepthSkeletonTrack(obj.categoryId) ||
            !stereoDepthByTrack.TryGetValue(obj.trackId, out StereoDepthTrackState st) ||
            !st.settled || st.disabledForLock || st.pendingUndo || IsTrackInInteractiveMotion(obj.trackId) ||
            !IsStereoDepthContractAvailable(out _) ||
            !TryResolveVideoDepthMeters(obj.anchorZ01, IsCategoryAnimal(obj.categoryId), out float zv, out _, out _, out _))
        {
            return false;
        }

        StereoDepthMatchPoint used = st.modeUsed;
        if (!TryResolveStereoMatchDepth(instance, eye, camRotation, ref used, out float matchRaw, out float pivotZ))
        {
            return false;
        }

        // 手動倍率の track はロック（ApplyStereoDepthLockIfSettled）と同じく、配置の中心の骨のまわりの拡大を割り戻した点を合わせる。
        float manual = Mathf.Max(0.01f, EvaluateManualScaleForFrame(obj.trackId, GetCurrentPlaybackFrame()));
        bool manualScaled = Mathf.Abs(manual - ManualScaleDefault) > 0.001f;
        float match = manualScaled && used != StereoDepthMatchPoint.Pivot ? pivotZ + (matchRaw - pivotZ) / manual : matchRaw;
        ratio = (bodyZ + (zv - match)) / bodyZ;
        return IsUsableStereoDepthFactor(ratio);
    }

    // この tick にこの track の ⑧ が目標の深さを出したか（入口の判定・ガードで戻った tick は false）。
    private bool TryGetStereoDepthRefineThisTick(uint trackId, out bool clamped, out bool earlyOut, out float moveZ)
    {
        bool ok = stereoDepthRefineTick == Time.frameCount && stereoDepthRefineTrack == trackId;
        clamped = ok && stereoDepthRefineClamped;
        earlyOut = ok && stereoDepthRefineEarlyOut;
        moveZ = ok ? stereoDepthRefineMoveZ : 0f;
        return ok;
    }

    // V（placementFollowVideoDepth、2026-10-26）: この tick のこの track の ⑧ が下限（比の深さより奥へ押した）で止まったか。
    private bool TryGetStereoDepthRefineFloorThisTick(uint trackId)
    {
        return stereoDepthRefineTick == Time.frameCount && stereoDepthRefineTrack == trackId && stereoDepthRefineFloored;
    }

    private bool TryGetStereoDepthEye(out Vector3 eye)
    {
        return TryGetPinholeBasis(leftScreen != null ? leftScreen : rightScreen, out eye, out _);
    }

    // この track の長さを持つ状態を目のまわりに ratio 倍する（ロックの倍率・動物の root の位置の平滑・人の根の奥行きの平滑・必要なら配置）。
    // ⑧ の比・頭合わせの px・手動のキー・SMAL/SMPL の回転は無次元か px か回転、⑨ の差と保持は k で割った長さで持つので触らない。
    // 関節の world 位置の平滑（smoothedJointsByTrack、JointsSpace.partial.cs:21-52、Lerp）も触らない。中身は anchor（k 倍）+ R·jointCam（k 倍しない）で
    // 目のまわりの k 倍ではないが、SMAL の経路が読むのは関節どうしの差（共通の anchor が消える）だけ: rootYawFix の 1 回の判定（余裕 0.3、
    // AnimalSmalFkApplier.cs:546-569）と、既定 OFF の四肢の AimAt（線分の向き、AnimalPoseApplier.cs:1029）。平滑は shot の境界・差し替えで消える。
    // k が変わる間に隠れていた関節だけ前の anchor を持つ（判定には効かない程度）。ほかに読むのは人・動物の keypoint の経路（確定のときは SKIP、確定の後は数えるだけ）と
    // モーションの姿勢の保持（§2）。
    private void ApplyStereoDepthFactorChange(uint trackId, GameObject instance, Vector3 eye, float ratio, bool includeTransform)
    {
        if (lockedModelLocalScaleByTrack.TryGetValue(trackId, out Vector3 locked))
        {
            lockedModelLocalScaleByTrack[trackId] = locked * ratio;
        }

        // bottomFitBorderEdgesAsBounds（Playback.partial.cs、既定 OFF）: 上下とも切れた tick が保つ基準点のカメラ空間の高さも、目のまわりの一様な ratio 倍に付いていく
        // （カメラの原点 = 目なので、カメラ空間の座標がそのまま ratio 倍になる）。
        if (bottomFitHeldReferenceCamYByTrack.TryGetValue(trackId, out float heldReferenceY))
        {
            bottomFitHeldReferenceCamYByTrack[trackId] = heldReferenceY * ratio;
        }

        if (instance == null)
        {
            return;
        }

        Transform root = instance.transform;
        if (includeTransform)
        {
            TrackPlacementWriter.Apply(root, new TrackPlacementCommand(eye + (root.position - eye) * ratio, root.rotation, root.localScale * ratio));
        }

        animalPoseApplier.ScaleRootPositionFilterAbout(root, eye, ratio);   // 人の root には何もしない
        ScaleHumanSmplRootDepthState(instance, ratio);                       // 動物・Else には何もしない
    }

    // 人の SMPL の根の奥行きの平滑（HumanSmpl.partial.cs:661-691、半減期 0.35 s・動画フレームで進む）は、ピンホールの原点（= 目）からの前向きの距離の
    // 1 次遅れ（Mathf.Lerp、速さの項は無い）なので、状態を ratio 倍すれば ratio 倍の入力に対して出力も厳密に ratio 倍。同じ動画フレームの残りの tick は
    // 進まない（alpha = 0）ので、直さないと確定の後 ~0.35 s、前の k の奥行きから滑る（Hb の f0 で 1.7 %）。非表示のインスタンスも引く（inactive の Animator も探す）。
    // キャッシュは作らない（humanoidCaches を引くだけ、Model.cs:84）。初期化前（shot の境界の後・新しいインスタンス）は何もしない。
    private void ScaleHumanSmplRootDepthState(GameObject instance, float ratio)
    {
        Animator animator = instance != null ? instance.GetComponentInChildren<Animator>(true) : null;
        if (animator != null && animator.isHuman && humanoidCaches.TryGetValue(animator, out HumanoidRigCache cache))
        {
            ScaleHumanSmplRootDepthStateForCache(cache, ratio);
        }
    }

    private void ScaleHumanSmplRootDepthStateForCache(HumanoidRigCache cache, float ratio)
    {
        if (cache != null && humanSmplSmoothedRootInit.Contains(cache) && humanSmplSmoothedDepth.TryGetValue(cache, out float depth))
        {
            humanSmplSmoothedDepth[cache] = depth * ratio;
        }
    }

    // ---- tick の流れ（Playback.partial.cs） ----

    // DisplayModelTick の先頭。S の入力（主スイッチ・合わせる点・IPD の上書き・視聴距離・画面の幅）が変わったら k を外して状態を捨てる。
    private void SyncStereoDepthSettings()
    {
        float width = 0f;
        Transform basis = leftScreen != null ? leftScreen : rightScreen;
        if (basis != null)
        {
            GetScreenSizeMeters(basis, out width, out _, out _);
            width = Mathf.Abs(width);
        }

        bool anyFlag = stereoDepthMatchVideo || stereoFrameOccluder || logStereoDepth;
        bool changed = !stereoDepthSettingsInitialized ||
                       stereoDepthSnapMatch != stereoDepthMatchVideo ||
                       stereoDepthSnapMode != stereoDepthMatchPoint ||
                       Mathf.Abs(stereoDepthSnapIpdOverride - stereoDepthIpdOverrideMillimeters) > 1e-4f ||
                       Mathf.Abs(stereoDepthSnapDistance - screenDistanceMeters) > 1e-6f ||
                       Mathf.Abs(stereoDepthSnapWidth - width) > 1e-6f;
        if (!changed)
        {
            if (stereoDepthHasPendingUndo)
            {
                UndoStereoDepthStates("pending", false, true);
            }

            return;
        }

        bool first = !stereoDepthSettingsInitialized;
        bool hadAnyFlag = stereoDepthSnapAnyFlag;
        bool turnedOn = stereoDepthMatchVideo && (first || !stereoDepthSnapMatch);
        if (!first)
        {
            UndoStereoDepthStates("settings", false);
        }

        stereoDepthSettingsInitialized = true;
        stereoDepthSnapMatch = stereoDepthMatchVideo;
        stereoDepthSnapMode = stereoDepthMatchPoint;
        stereoDepthSnapIpdOverride = stereoDepthIpdOverrideMillimeters;
        stereoDepthSnapDistance = screenDistanceMeters;
        stereoDepthSnapWidth = width;
        stereoDepthSnapAnyFlag = anyFlag;
        if (turnedOn)
        {
            EnsureStereoDepthElseEligibility("toggle");
        }

        if (anyFlag || hadAnyFlag)
        {
            LogStereoDepthConfig(first ? "start" : "change");
        }
    }

    // 外す。force = false ならモーション中の track は外さず pendingUndo にする（k は掛けたまま、確定もしない。R2-4）。
    private void UndoStereoDepthStates(string reason, bool force, bool onlyPending = false)
    {
        stereoDepthHasPendingUndo = false;
        if (stereoDepthByTrack.Count == 0)
        {
            return;
        }

        bool hasEye = TryGetStereoDepthEye(out Vector3 eye);
        stereoDepthTrackIdBuffer.Clear();
        stereoDepthTrackIdBuffer.AddRange(stereoDepthByTrack.Keys);
        for (int i = 0; i < stereoDepthTrackIdBuffer.Count; i++)
        {
            uint id = stereoDepthTrackIdBuffer[i];
            StereoDepthTrackState st = stereoDepthByTrack[id];
            if (onlyPending && !st.pendingUndo)
            {
                continue;
            }

            if (!force && IsTrackInInteractiveMotion(id))
            {
                st.pendingUndo = true;
                stereoDepthHasPendingUndo = true;
                continue;
            }

            trackInstances.TryGetValue(id, out GameObject instance);
            if (Mathf.Abs(st.k - 1f) > 1e-6f)
            {
                if (hasEye)
                {
                    ApplyStereoDepthFactorChange(id, instance, eye, 1f / st.k, true);
                }
                else
                {
                    // 目が取れない（両方の板も頭も取れない）ときも、目を使わない状態は割り戻す。今は起きない: 状態は両方の板がある間だけ作られ、板は作り直されるだけで null にならない。
                    // ロックの倍率は次の本物のカットまで残るので必ず割る。人の根の奥行きの平滑は原点からの距離。式は上の経路と同じ（ratio = 1/k を掛ける）。
                    // 目のまわりの量（配置・動物の root の位置の平滑）はそのまま: 配置は次に置く tick に書き直され、平滑は短い過渡で戻る。
                    float ratio = 1f / st.k;
                    if (lockedModelLocalScaleByTrack.TryGetValue(id, out Vector3 locked))
                    {
                        lockedModelLocalScaleByTrack[id] = locked * ratio;
                    }

                    ScaleHumanSmplRootDepthState(instance, ratio);
                }
            }

            stereoDepthByTrack.Remove(id);
            Debug.Log($"[STEREOZ-UNDO] track={id} reason={reason} k={st.k:F4} settled={(st.settled ? 1 : 0)} force={(force ? 1 : 0)} eye={(hasEye ? 1 : 0)}");
        }
    }

    // ResetPerShotTrackState（本物のカット）。ロックと一緒に捨てる。
    private void ClearStereoDepthStatesForRealCut()
    {
        stereoDepthHasPendingUndo = false;
        if (stereoDepthByTrack.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, StereoDepthTrackState> kv in stereoDepthByTrack)
        {
            if (!kv.Value.settled)
            {
                Debug.Log($"[STEREOZ-NEVER] toShot={lastAppliedShotIndex} track={kv.Key} reason={kv.Value.reason} kEntry={kv.Value.kEntry:F4} unsettledTicks={kv.Value.unsettledTicks}");
            }
            else if (kv.Value.keypointTicks > 0)
            {
                Debug.Log($"[STEREOZ-KPT] toShot={lastAppliedShotIndex} track={kv.Key} keypointTicks={kv.Value.keypointTicks} k={kv.Value.k:F4}");
            }
        }

        stereoDepthByTrack.Clear();
    }

    // ApplyMetaTarget の先頭（:198 の後）。target は値渡しの複製。
    private void ApplyStereoDepthAtTickStart(ref MetaObj target)
    {
        stereoDepthPopoutThisTick.Remove(target.trackId);
        stereoDepthElseRatioThisTick.Remove(target.trackId);
        float popout = target.anchorZ;
        if (IsStereoDepthSkeletonTrack(target.categoryId))
        {
            if (!stereoDepthByTrack.TryGetValue(target.trackId, out StereoDepthTrackState st))
            {
                if (!stereoDepthMatchVideo || popout <= 0.001f || IsTrackInInteractiveMotion(target.trackId) ||
                    !IsStereoDepthContractAvailable(out _) ||
                    !TryResolveVideoDepthMeters(target.anchorZ01, IsCategoryAnimal(target.categoryId), out float zv, out _, out _, out _))
                {
                    return;
                }

                bool lockPending = !lockedModelLocalScaleByTrack.ContainsKey(target.trackId);
                float kEntry = lockPending ? zv / popout : 1f;
                if (!IsUsableStereoDepthFactor(kEntry))
                {
                    return;
                }

                st = new StereoDepthTrackState
                {
                    k = kEntry,
                    kEntry = kEntry,
                    reason = lockPending ? "lock" : "attach",
                    createdFrame = GetCurrentPlaybackFrame(),
                };
                stereoDepthByTrack[target.trackId] = st;
                // 平滑の状態が残っている root（視聴距離の変更でロックだけ外れたとき）は、これから掛ける倍率と同じだけずらす（動物の root の位置の平滑・人の根の奥行きの平滑）。shot の境界では平滑は消えている。
                if (lockPending && kEntry != 1f && trackInstances.TryGetValue(target.trackId, out GameObject existing) && existing != null &&
                    TryGetStereoDepthEye(out Vector3 eye))
                {
                    ApplyStereoDepthFactorChange(target.trackId, existing, eye, kEntry, false);
                }
            }

            // 状態がある間は毎 tick 掛ける（ロック・倍率・平滑が k 倍のままなので、掛けない tick は絵が 1/k になる。R1-1・R2-6）。
            stereoDepthPopoutThisTick[target.trackId] = popout;
            target.anchorZ = popout * st.k;
            return;
        }

        if (stereoDepthMatchVideo && IsStereoDepthElseTrack(target) && IsStereoDepthContractAvailable(out _) &&
            TryResolveVideoDepthMeters(target.anchorZ01, false, out float zvElse, out _, out _, out _))
        {
            // 車: 倍率は毎フレーム bbox から（Playback.partial.cs:676）、⑧ も通らない（:1240）ので、深さを Zv にするだけで絵は変わらない。
            stereoDepthPopoutThisTick[target.trackId] = popout;
            if (popout > 0.001f)
            {
                stereoDepthElseRatioThisTick[target.trackId] = zvElse / popout;
            }
            target.anchorZ = zvElse;
        }
    }

    // GetOrLockModelLocalScale（:2319・:2330）。同じ shot の中でロックを作り直した（モデルの差し替え・表示し直し・prefab の変更）。
    private void NoteStereoDepthLockCreated(uint trackId)
    {
        bool presetSwap = modelPresetSwapRelockTracks.Remove(trackId);
        if (presetSwap && stereoDepthKeepsKAcrossModelPresetSwap &&
            stereoDepthByTrack.TryGetValue(trackId, out StereoDepthTrackState kept) && kept.settled && !kept.pendingUndo)
        {
            // Model（既定1 ⇄ 既定2）で替えたロックは、確定した k をそのまま使う（押した tick の Zv で測り直さない）。新しいロックはこの tick の
            // anchorZ（= popout × k、ApplyStereoDepthAtTickStart）で作られるので k を含んでいる。
            Debug.Log($"[STEREOZ-KEEP] track={trackId} reason=modelPresetSwap k={kept.k:F6} frame={GetCurrentPlaybackFrame()} shot={lastAppliedShotIndex}");
            return;
        }

        if (stereoDepthByTrack.TryGetValue(trackId, out StereoDepthTrackState st) && st.settled && !st.pendingUndo)
        {
            st.settled = false;
            st.disabledForLock = false;
            st.reason = "relock";
            st.kEntry = st.k;
            st.unsettledTicks = 0;
        }
    }

    // ApplyMetaTarget の 2 回目の ⑦（:300-304）の後・ObserveInteractiveMotionDisplayedRoot（:305）の前。倍率が決まり、⑧ がこの tick の深さを
    // 決めた後で k を確定し、配置を目のまわりに m 倍する（⑧ の比の平滑はロックの作り直し・視聴距離・途中で ON では消えないので、⑧ の前で測ると表示の深さが
    // その比だけ Zv からずれる。人の f300〜f1800 で ×1.09〜1.15）。種別で分けない: 姿勢の経路と ⑧ の結果で決める。
    private void ApplyStereoDepthLockIfSettled(GameObject instance, ref MetaObj target, Transform screen)
    {
        if (instance == null || !IsStereoDepthSkeletonTrack(target.categoryId) ||
            !stereoDepthByTrack.TryGetValue(target.trackId, out StereoDepthTrackState st) ||
            !stereoDepthPopoutThisTick.TryGetValue(target.trackId, out float popout))
        {
            return;
        }

        StereoDepthPosePath path = ResolveStereoDepthPosePathThisTick(instance);
        if (st.settled)
        {
            if (!st.disabledForLock && path == StereoDepthPosePath.AbsoluteTarget)
            {
                st.keypointTicks++;   // 記録だけ（今の bundle では 0 のはず）
            }

            return;
        }

        if (st.pendingUndo)
        {
            return;
        }

        float manual = Mathf.Max(0.01f, EvaluateManualScaleForFrame(target.trackId, GetCurrentPlaybackFrame()));
        bool manualScaled = Mathf.Abs(manual - ManualScaleDefault) > 0.001f;
        bool scaleSettled = scaleRefinedByTrack.Contains(target.trackId) || !refineScaleFromProjectedBones || manualScaled;
        if (!scaleSettled || IsTrackInInteractiveMotion(target.trackId) || !TryGetPinholeBasis(screen, out Vector3 eye, out Quaternion camRotation))
        {
            st.unsettledTicks++;
            return;
        }

        if (path == StereoDepthPosePath.None)
        {
            // この tick は骨に姿勢を書いていない（骨は前の tick の回転のまま）。経路が分かる tick まで待つ（飛ばすとこのロックの間ずっと S が切れる）。
            st.unsettledTicks++;
            return;
        }

        if (path == StereoDepthPosePath.AbsoluteTarget)
        {
            // 絶対の目標へ向ける経路（keypoint・制御点・IK）は k 倍すると曲げが変わる（R1-11・R2-3）。このロックでは S を使わない。
            float kOld = st.k;
            if (Mathf.Abs(kOld - 1f) > 1e-6f)
            {
                ApplyStereoDepthFactorChange(target.trackId, instance, eye, 1f / kOld, true);
            }

            st.k = 1f;
            st.settled = true;
            st.disabledForLock = true;
            target.anchorZ = popout;
            Debug.Log($"[STEREOZ-SKIP] track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} frame={GetCurrentPlaybackFrame()} " +
                      $"shot={lastAppliedShotIndex} reason={(IsCategoryAnimal(target.categoryId) ? "noSmalFk" : "noSmplFk")} undoneK={kOld:F6}");
            return;
        }

        // ⑧ がこの tick に自分の比で深さを決めたか（下限 0.25 m・上限 3·D は絶対の長さなので、止まった tick の深さを k に入れない。入口・ガードで戻った tick も待つ）。
        bool refined = TryGetStereoDepthRefineThisTick(target.trackId, out bool refineClamped, out _, out float refineMoveZ);
        if (refineDepthFromProjectedBones && (!refined || refineClamped))
        {
            st.unsettledTicks++;
            return;
        }

        if (!IsStereoDepthContractAvailable(out _) ||
            !TryResolveVideoDepthMeters(target.anchorZ01, IsCategoryAnimal(target.categoryId), out float zv, out float dPx, out float ipd, out string ipdSource))
        {
            st.unsettledTicks++;
            return;
        }

        StereoDepthMatchPoint used = stereoDepthMatchPoint;
        if (!TryResolveStereoMatchDepth(instance, eye, camRotation, ref used, out float matchRaw, out float pivotZ))
        {
            st.unsettledTicks++;
            return;
        }

        // 手動倍率の track（U1 は走らない、Playback.partial.cs:1488-1493）は、配置の中心の骨のまわりの拡大を割り戻す（R2-11）。
        float match = manualScaled && used != StereoDepthMatchPoint.Pivot ? pivotZ + (matchRaw - pivotZ) / manual : matchRaw;
        float m = zv / Mathf.Max(0.001f, match);
        if (!IsUsableStereoDepthFactor(m))
        {
            st.unsettledTicks++;
            return;
        }

        float kBefore = st.k;
        if (Mathf.Abs(m - 1f) > 1e-6f)
        {
            ApplyStereoDepthFactorChange(target.trackId, instance, eye, m, true);
        }

        st.k = kBefore * m;
        st.settled = true;
        st.modeUsed = used;
        target.anchorZ = popout * st.k;

        TryResolveStereoScreenGeometry(out float plane, out float width, out float plate);
        trackPrefabSources.TryGetValue(target.trackId, out GameObject prefab);
        Debug.Log($"[STEREOZ-LOCK] track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} frame={GetCurrentPlaybackFrame()} shot={lastAppliedShotIndex} " +
                  $"mode={used} modeSet={stereoDepthMatchPoint} reason={st.reason} prefab={(prefab != null ? prefab.name : "?")} z01={target.anchorZ01:F4} " +
                  $"dPx={dPx:+0.000;-0.000} Zv={zv:F6} popoutZ={popout:F6} kEntry={st.kEntry:F6} matchRawZ={matchRaw:F6} matchZ={match:F6} pivotZ={pivotZ:F6} " +
                  $"pivotMinusAnchorZmm={(pivotZ - popout * kBefore) * 1000f:F1} moved8Mm={refineMoveZ * 1000f:+0.00;-0.00} manual={manual:F3} m={m:F6} k={st.k:F6} " +
                  $"ipdMm={ipd * 1000f:F1} ipdSrc={ipdSource} D={plane:F3} screenW={width:F5} pMm={width / StereoVideoDepthContract.ReferenceWidthPx * 1000f:F5} " +
                  $"plateMm={plate * 1000f:F1} unsettledTicks={st.unsettledTicks} sinceCreateFrames={GetCurrentPlaybackFrame() - st.createdFrame}");
    }

    // ---- 合わせる点 ----

    private bool TryResolveStereoMatchDepth(GameObject instance, Vector3 eye, Quaternion camRotation, ref StereoDepthMatchPoint mode, out float depth, out float pivotZ)
    {
        Quaternion toCam = Quaternion.Inverse(camRotation);
        pivotZ = (toCam * (ResolveAnchorPlacementPivotWorld(instance, out _) - eye)).z;
        depth = 0f;
        if (mode == StereoDepthMatchPoint.Contract)
        {
            if (TryResolveStereoContractDepth(instance, eye, toCam, out depth))
            {
                return depth > 0.001f;
            }

            mode = StereoDepthMatchPoint.AabbCenter;   // 役の骨が揃わないリグ（前肢の役が無い鳥など）は (b)。ログに mode=AabbCenter で出る
        }

        if (mode == StereoDepthMatchPoint.AabbCenter)
        {
            return TryResolveStereoAabbCenterDepth(instance, eye, toCam, out depth) && depth > 0.001f;
        }

        depth = pivotZ;
        return depth > 0.001f;
    }

    // (b): renderer.bounds の和の中心の camera z（TryGetRendererWorldBounds・[PLACE] の depth と同じ集め方。手で回すときの案内 manualYawGuideRoot だけ除く）。
    private bool TryResolveStereoAabbCenterDepth(GameObject instance, Vector3 eye, Quaternion toCam, out float depth)
    {
        depth = 0f;
        instance.GetComponentsInChildren(true, stereoDepthRendererBuffer);
        bool any = false;
        Bounds bounds = default;
        for (int i = 0; i < stereoDepthRendererBuffer.Count; i++)
        {
            Renderer r = stereoDepthRendererBuffer[i];
            if (r == null || !r.enabled || (manualYawGuideRoot != null && r.transform.IsChildOf(manualYawGuideRoot.transform)))
            {
                continue;
            }

            if (!any)
            {
                bounds = r.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        if (!any)
        {
            return false;
        }

        depth = (toCam * (bounds.center - eye)).z;
        return true;
    }

    // (c): 肩（kp12・13）と膝（kp10・11）の見えている側（目に近い方）を 2 回ずつ、尾の付け根（kp7）と頭（kp18）。6 つの中央値（s_matchpoint_c.py の proxy_of と同じ）。
    private bool TryResolveStereoContractDepth(GameObject instance, Vector3 eye, Quaternion toCam, out float depth)
    {
        depth = 0f;
        AnimalRigCache c = animalPoseApplier.PeekAnimalRigCache(instance);
        if (c == null || !c.ready || c.leftFrontUpper == null || c.rightFrontUpper == null ||
            c.leftRearLower == null || c.rightRearLower == null || c.tailBase == null || c.head == null)
        {
            return false;
        }

        float nf = Mathf.Min((toCam * (c.leftFrontUpper.position - eye)).z, (toCam * (c.rightFrontUpper.position - eye)).z);
        float nr = Mathf.Min((toCam * (c.leftRearLower.position - eye)).z, (toCam * (c.rightRearLower.position - eye)).z);
        float[] s = stereoDepthContractSamples;
        s[0] = nf;
        s[1] = nf;
        s[2] = nr;
        s[3] = nr;
        s[4] = (toCam * (c.tailBase.position - eye)).z;
        s[5] = (toCam * (c.head.position - eye)).z;
        System.Array.Sort(s);
        depth = 0.5f * (s[2] + s[3]);
        return true;
    }

    // ---- Else（車）の対象 ----

    private void InvalidateStereoDepthElseEligibility()
    {
        stereoDepthElseEligibilityBuilt = false;
        stereoDepthEligibleElseTracks.Clear();
    }

    private void PrecomputeStereoDepthElseEligibilityOnLoad()
    {
        if (stereoDepthMatchVideo)
        {
            EnsureStereoDepthElseEligibility("load");
        }
    }

    // 全フレームを読み、人・動物と同じフレームに一度も出ない Else の track を集める（⑨ は同じフレームの骨格に付けるので、その track は popout のまま）。
    // 読んだフレームの SMPL/SMAL の項目は、読む前に無かったものを消す（ShotBoundary.partial.cs:171-190 と同じ）。
    private bool EnsureStereoDepthElseEligibility(string phase)
    {
        if (stereoDepthElseEligibilityBuilt)
        {
            return true;
        }

        if (!metaLoaded || frameOffsets == null || frameOffsets.Length == 0)
        {
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var humanBefore = new HashSet<int>(humanSmplPosesMetaBin.Keys);
        var animalBefore = new HashSet<int>(animalSmalPosesMetaBin.Keys);
        var elseTracks = new HashSet<uint>();
        var shared = new HashSet<uint>();
        var buffer = new List<MetaObj>(16);
        try
        {
            for (int f = 0; f < frameOffsets.Length; f++)
            {
                if (!TryReadFrameObjectsRaw(f, buffer))
                {
                    continue;
                }

                bool hasSkeleton = false;
                for (int i = 0; i < buffer.Count; i++)
                {
                    if (IsCategoryPerson(buffer[i].categoryId) || IsCategoryAnimal(buffer[i].categoryId))
                    {
                        hasSkeleton = true;
                        break;
                    }
                }

                for (int i = 0; i < buffer.Count; i++)
                {
                    if (!IsCategoryOther(buffer[i].categoryId))
                    {
                        continue;
                    }

                    elseTracks.Add(buffer[i].trackId);
                    if (hasSkeleton)
                    {
                        shared.Add(buffer[i].trackId);
                    }
                }
            }
        }
        finally
        {
            RemoveFramesNotIn(humanSmplPosesMetaBin, humanBefore);
            RemoveFramesNotIn(animalSmalPosesMetaBin, animalBefore);
        }

        stereoDepthEligibleElseTracks.Clear();
        foreach (uint id in elseTracks)
        {
            if (!shared.Contains(id))
            {
                stereoDepthEligibleElseTracks.Add(id);
            }
        }

        stereoDepthElseEligibilityBuilt = true;
        Debug.Log($"[PRECOMPUTE] stereoElse phase={phase} frames={frameOffsets.Length} elseTracks=[{string.Join(",", elseTracks)}] " +
                  $"eligible=[{string.Join(",", stereoDepthEligibleElseTracks)}] ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        return true;
    }

    private bool IsStereoDepthElseTrack(MetaObj obj)
    {
        return IsCategoryOther(obj.categoryId) && EnsureStereoDepthElseEligibility("play") &&
               stereoDepthEligibleElseTracks.Contains(obj.trackId) && !FrameHasPersonOrAnimal();
    }

    // InteractiveMotion.partial.cs:1314（後で現れるときに歩いて戻る先。人・動物のシステムのフレームアウトだけが通る: :1108 → :1112 → :1186 → :1253）。
    // 状態がある間はロックと同じ k、無ければ Zv（(a) と同じ意味）。
    // V（placementFollowVideoDepth、2026-10-26、既定 OFF）: V の f も掛ける（状態が無ければ × 1 で同じ値）。
    private float ResolveStereoDepthFrameInDepth(MetaObj obj)
    {
        float videoDepth = ResolveVideoDepthFollowFactor(obj.trackId);
        if (stereoDepthByTrack.TryGetValue(obj.trackId, out StereoDepthTrackState st))
        {
            return obj.anchorZ * st.k * videoDepth;
        }

        if (stereoDepthMatchVideo && IsStereoDepthSkeletonTrack(obj.categoryId) && IsStereoDepthContractAvailable(out _) &&
            TryResolveVideoDepthMeters(obj.anchorZ01, IsCategoryAnimal(obj.categoryId), out float zv, out _, out _, out _))
        {
            return zv;
        }

        return obj.anchorZ * videoDepth;
    }

    // ---- ログ ----

    // ApplyMetaTarget の最後（:318 の後）。S が OFF でも k = 1 で同じ残差（モデルの画面上の隔たり − 映像の画面上の隔たり、＋ = モデルが手前）を出す。
    // Else の行は ⑨ の前の置き方（⑨ が付けるボールは own9=1、⑨ の後は [STEREOZ-FOLLOW]）。e8 = この tick の ⑧ が 0.1 mm 以内で動かなかった（2 回目の ⑦ は走らない）。
    private void LogStereoDepthIfEnabled(MetaObj target, GameObject instance, Transform screen, int frame)
    {
        if (!logStereoDepth || instance == null)
        {
            return;
        }

        bool isAnimal = IsCategoryAnimal(target.categoryId);
        bool isSkeleton = IsStereoDepthSkeletonTrack(target.categoryId);
        if ((!isSkeleton && !IsCategoryOther(target.categoryId)) || Time.frameCount % Mathf.Max(1, logStereoDepthEveryNTicks) != 0)
        {
            return;
        }

        if (!IsStereoDepthContractAvailable(out string contract))
        {
            if (!stereoDepthContractLogged)
            {
                stereoDepthContractLogged = true;
                Debug.Log($"[STEREOZ] contract={contract} (no Zv; nothing logged)");
            }

            return;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 eye, out Quaternion camRotation) ||
            !TryResolveStereoScreenGeometry(out float plane, out float width, out float plate))
        {
            return;
        }

        bool zvOk = TryResolveVideoDepthMeters(target.anchorZ01, isAnimal, out float zv, out float dPx, out float ipd, out string ipdSource);
        if (!zvOk)
        {
            dPx = StereoVideoDepthContract.BakedDisparityPx(Z01ToNearness(Mathf.Clamp01(target.anchorZ01)));
            ipd = ResolveStereoDepthViewerIpdMeters(out ipdSource);
        }

        float videoPx = StereoVideoDepthContract.VideoOnScreenDisparityPx(dPx, width, plate);
        Quaternion toCam = Quaternion.Inverse(camRotation);
        float aabbZ = TryResolveStereoAabbCenterDepth(instance, eye, toCam, out float aabb) ? aabb : -1f;
        float pivotZ = -1f;
        float contractZ = -1f;
        if (isSkeleton)
        {
            pivotZ = (toCam * (ResolveAnchorPlacementPivotWorld(instance, out _) - eye)).z;   // 人は Hips
            if (TryResolveStereoContractDepth(instance, eye, toCam, out float contractDepth))   // 人は取れない（-1）
            {
                contractZ = contractDepth;
            }
        }

        float nearestZ = TryResolveNearestProjectionBoneDepth(instance, eye, camRotation, out float nearest, out _) ? nearest : -1f;
        bool applied = stereoDepthPopoutThisTick.TryGetValue(target.trackId, out float popout);
        if (!applied)
        {
            // V（placementFollowVideoDepth、2026-10-26）: anchorZ に V の f が掛かっている tick は割り戻す（popoutZ は meta の popout のまま。状態が無ければ ÷ 1 で同じ値）。
            popout = target.anchorZ / ResolveVideoDepthFollowFactor(target.trackId);
        }

        stereoDepthByTrack.TryGetValue(target.trackId, out StereoDepthTrackState st);
        float kLog = st != null ? st.k : (applied && popout > 0.001f ? target.anchorZ / popout : 1f);
        string modeLog = st == null ? "-" : (st.settled ? st.modeUsed.ToString() : "unsettled");
        bool refined = TryGetStereoDepthRefineThisTick(target.trackId, out _, out bool earlyOut8, out float move8) && isSkeleton;
        string own9 = !isSkeleton && !applied && IsOtherDepthFollowActive() && FrameHasPersonOrAnimal() ? " own9=1" : "";
        // A の確かめ用（A のフラグが ON のときだけ足す。OFF の行は今までと同じ）: fol = この tick の ⑧ が Zv に合わせたか、cl = 下限・上限で止まったか。
        if (stereoDepthFollowVideoEveryFrame && isSkeleton)
        {
            bool cl8 = TryGetStereoDepthRefineThisTick(target.trackId, out bool clamped8, out _, out _) && clamped8;
            own9 += $" fol={(refined && stereoDepthRefineFollow ? 1 : 0)} cl={(cl8 ? 1 : 0)}";
        }
        // V の確かめ用（V のフラグが ON のときだけ足す。OFF の行は今までと同じ）: fv = この tick の配置に掛かっている V の f（k の上に掛かる）。
        if (placementFollowVideoDepth && isSkeleton)
        {
            own9 += $" fv={ResolveVideoDepthFollowFactor(target.trackId):F4}";
        }
        string Res(float z) => z > 0.001f
            ? (StereoVideoDepthContract.ModelDisparityPx(z, plane, width, ipd) - videoPx).ToString("+0.000;-0.000")
            : "na";
        Debug.Log($"[STEREOZ] tick={Time.frameCount} f={frame} shot={lastAppliedShotIndex} track={target.trackId} cat={StereoDepthCategoryLabel(target.categoryId)} " +
                  $"S={(applied ? 1 : 0)} mode={modeLog} k={kLog:F6} settled={(st != null && st.settled ? 1 : 0)} z01={target.anchorZ01:F4} " +
                  $"dPx={dPx:+0.000;-0.000} videoPx={videoPx:+0.000;-0.000} popoutZ={popout:F4} anchorZ={target.anchorZ:F4} Zv={(zvOk ? zv.ToString("F4") : "na")} " +
                  $"pivotZ={pivotZ:F4} aabbZ={aabbZ:F4} contractZ={contractZ:F4} nearestZ={nearestZ:F4} " +
                  $"resPivotPx={Res(pivotZ)} resAabbPx={Res(aabbZ)} resContractPx={Res(contractZ)} resAnchorPx={Res(target.anchorZ)} " +
                  $"kp={(isSkeleton && ResolveStereoDepthPosePathThisTick(instance) == StereoDepthPosePath.AbsoluteTarget ? 1 : 0)} " +
                  $"e8={(refined ? (earlyOut8 ? "1" : "0") : "na")} d8Mm={(refined ? (move8 * 1000f).ToString("+0.000;-0.000") : "na")} " +
                  $"ipdMm={ipd * 1000f:F1} ipdSrc={ipdSource}{own9}");
    }

    private void LogStereoDepthConfig(string reason)
    {
        IsStereoDepthContractAvailable(out string contract);
        TryResolveStereoScreenGeometry(out float d, out float w, out float plate);
        float ipd = ResolveStereoDepthViewerIpdMeters(out string ipdSource);
        string eligible = stereoDepthElseEligibilityBuilt ? "[" + string.Join(",", stereoDepthEligibleElseTracks) + "]" : "n/a";
        Debug.Log($"[STEREOZ-CONFIG] reason={reason} s={(stereoDepthMatchVideo ? 1 : 0)} mode={stereoDepthMatchPoint} follow9={(IsOtherDepthFollowActive() ? 1 : 0)} " +
                  $"occluder={(stereoFrameOccluder ? 1 : 0)} log={(logStereoDepth ? 1 : 0)} contract={contract} " +
                  $"maxDispPx={StereoVideoDepthContract.MaxDisparityPx:F0} refWidthPx={StereoVideoDepthContract.ReferenceWidthPx:F0} " +
                  $"zeroDn={StereoVideoDepthContract.ZeroDisparityNearness:F1} D={d:F3} screenW={w:F5} pMm={w / StereoVideoDepthContract.ReferenceWidthPx * 1000f:F5} " +
                  $"plateMm={plate * 1000f:F1} ipdMm={ipd * 1000f:F1} ipdSrc={ipdSource} elseEligible={eligible}");
    }

    // 実験の trial_state に足す（ON のものだけ。OFF なら空で、今の行と同じ）。
    public string StereoDepthTrialState
    {
        get
        {
            string s = string.Empty;
            if (stereoDepthMatchVideo) s += " stereo_depth=" + stereoDepthMatchPoint;
            if (stereoFrameOccluder) s += " stereo_frame=1";
            if (placementFollowVideoDepth) s += " video_depth_follow=1";
            return s;
        }
    }

    // ⑨（OtherDepth.partial.cs:335）の S の分岐（k ≠ 1 のときだけ呼ぶ）。差の平滑は k で割った長さ（S が無いときの長さ）で持ち、使うときに k を掛ける。
    // 同じ alpha の列なら平滑の状態は S なしの走りと同じ値になり、目標は k 倍になる（確定・作り直し・外すで k が tick ごとに変わっても成り立つ）。
    private float ResolveStereoFollowTargetZ(uint otherTrackId, float skeletonZ, float rawTargetZ, float kSkel)
    {
        return skeletonZ - kSkel * SmoothOtherDepthGap(otherTrackId, (skeletonZ - rawTargetZ) / kSkel);
    }

    // ⑨ の後のボールの置き方（[PLACE]・ダンプ・[STEREOZ] は ⑨ の前に書かれる）と映像のボールとの残差。uz・vz・sz（カメラ空間の x/z・y/z・倍率/z）は
    // 目のまわりの k 倍で変わらない量。logStereoDepth のときだけ。
    private void LogStereoDepthFollowIfEnabled(MetaObj other, uint skeletonTrack, float kSkel, Vector3 finalCam, Vector3 finalScale)
    {
        if (!logStereoDepth || Time.frameCount % Mathf.Max(1, logStereoDepthEveryNTicks) != 0 || finalCam.z <= 0.001f ||
            !IsStereoDepthContractAvailable(out _) || !TryResolveStereoScreenGeometry(out float plane, out float width, out float plate))
        {
            return;
        }

        float dPx = StereoVideoDepthContract.BakedDisparityPx(Z01ToNearness(Mathf.Clamp01(other.anchorZ01)));
        float ipd = ResolveStereoDepthViewerIpdMeters(out _);
        float videoPx = StereoVideoDepthContract.VideoOnScreenDisparityPx(dPx, width, plate);
        float res = StereoVideoDepthContract.ModelDisparityPx(finalCam.z, plane, width, ipd) - videoPx;
        Debug.Log($"[STEREOZ-FOLLOW] tick={Time.frameCount} f={GetCurrentFrameIndex()} track={other.trackId} skel={skeletonTrack} kSkel={kSkel:F6} " +
                  $"z01={other.anchorZ01:F4} dPx={dPx:+0.000;-0.000} videoPx={videoPx:+0.000;-0.000} finalZ={finalCam.z:F5} " +
                  $"uz={finalCam.x / finalCam.z:F6} vz={finalCam.y / finalCam.z:F6} sz={finalScale.x / finalCam.z:F6} resPx={res:+0.000;-0.000} ipdMm={ipd * 1000f:F1}");
    }

#if UNITY_EDITOR
    // バッチの左右の目の撮影（BatchPlaybackLogger.Stereo.cs）。画面の中心・軸（ピンホールの基底）・大きさ・板・IPD。
    internal bool TryGetStereoCaptureFrame(out Vector3 center, out Vector3 right, out Vector3 up, out Vector3 forward, out Vector2 size,
        out Renderer leftPlate, out Renderer rightPlate, out float ipd, out string ipdSource, out int eyeW, out int eyeH)
    {
        center = Vector3.zero;
        right = Vector3.right;
        up = Vector3.up;
        forward = Vector3.forward;
        size = Vector2.zero;
        leftPlate = null;
        rightPlate = null;
        ipd = 0f;
        ipdSource = "none";
        eyeW = 0;
        eyeH = 0;
        Transform basis = leftScreen != null ? leftScreen : rightScreen;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 ||
            !TryGetScreenFrame(out center, out _, out _, out size) || !TryGetPinholeBasis(basis, out _, out Quaternion camRotation))
        {
            return false;
        }

        right = camRotation * Vector3.right;
        up = camRotation * Vector3.up;
        forward = camRotation * Vector3.forward;
        leftPlate = leftScreen != null ? leftScreen.GetComponent<Renderer>() : null;
        rightPlate = rightScreen != null && rightScreen.gameObject.activeSelf ? rightScreen.GetComponent<Renderer>() : null;
        ipd = ResolveStereoDepthViewerIpdMeters(out ipdSource);
        eyeW = manifest.eye_w;
        eyeH = manifest.eye_h;
        return true;
    }

    // -captureStereoMarkers: 骨格の track ごとに、目から配置の中心の骨（人は Hips）への光線上の Zv の点と popout の点。
    internal void CollectStereoCaptureMarkerPoints(List<Vector3> zvPoints, List<Vector3> popoutPoints)
    {
        zvPoints.Clear();
        popoutPoints.Clear();
        if (!TryGetStereoDepthEye(out Vector3 eye) ||
            !TryGetPinholeBasis(leftScreen != null ? leftScreen : rightScreen, out _, out Quaternion camRotation))
        {
            return;
        }

        Vector3 forward = camRotation * Vector3.forward;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            if (!IsStereoDepthSkeletonTrack(obj.categoryId) || !trackInstances.TryGetValue(obj.trackId, out GameObject inst) || inst == null ||
                !inst.activeInHierarchy || !TryResolveVideoDepthMeters(obj.anchorZ01, IsCategoryAnimal(obj.categoryId), out float zv, out _, out _, out _))
            {
                continue;
            }

            Vector3 dir = ResolveAnchorPlacementPivotWorld(inst, out _) - eye;
            float along = Vector3.Dot(dir, forward);
            if (along <= 0.001f)
            {
                continue;
            }

            zvPoints.Add(eye + dir * (zv / along));
            popoutPoints.Add(eye + dir * (obj.anchorZ / along));
        }
    }
#endif
}
