using System.Collections.Generic;
using UnityEngine;

internal sealed class HumanoidRigCache
{
    public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
    public readonly Dictionary<HumanBodyBones, Quaternion> bindRotLocal = new Dictionary<HumanBodyBones, Quaternion>();
    public readonly Dictionary<HumanBodyBones, Quaternion> bindRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
    // Avatar の T ポーズ（avatar.humanDescription.skeleton の局所回転を Animator から積んだもの）の world 回転（2026-10-04）。
    // bindRotWorld と同じ時点・同じ root の回転で採る。fkReferenceFromAvatarTPose のとき FK の基準に使う。
    public readonly Dictionary<HumanBodyBones, Quaternion> tposeRotWorld = new Dictionary<HumanBodyBones, Quaternion>();
    // 指 30 本の T ポーズの局所回転（2026-10-08、J-12 の humanFingerRestFromAvatarTPose・humanFingersFromSmplHand 用）。tposeRotWorld[Humanoid の親]⁻¹ ×
    // tposeRotWorld[指] を最初に使うときに作る（StreamingStereoVideoPlayer.HumanFingers.partial.cs）。T ポーズを採っていなければ null のまま。
    public Dictionary<HumanBodyBones, Quaternion> fingerTposeRotLocal;
    // [FINGER] の行を状態が変わったときだけ出すための控え（0 = まだ、1 = 使った、2 = 今の経路のまま）。
    public int fingerRestLogState;
    public int fingerSmplHandLogState;
    // T-pose での canonical 親フレームから手ボーンの bindRotWorld への補正量。
    // 手のFK式: canonicalParent * smplLocal * handBindCorrection
    // smplLocal=identity のとき bindRotWorld[hand] に一致し、キャラクター間の軸差を吸収する。
    public readonly Dictionary<HumanBodyBones, Quaternion> handBindCorrection = new Dictionary<HumanBodyBones, Quaternion>();
    // Meshy の人の手のねじれの直し（2026-10-25、StreamingStereoVideoPlayer.humanHandRollFixMode、既定 0 = OFF）: 前腕の軸の回りに回す量
    // （度、左は負・右は正 = どちらも手のひらを後ろへ回す回内）と、muscles=0 で採った肘→手首の向き（手・前腕それぞれの局所）。
    // 表（humanHandRollFixTable）に無いモデルは 0 のまま（何もしない）。
    public float leftHandRollDeg;
    public float rightHandRollDeg;
    public Vector3 leftHandRollAxisLocal;
    public Vector3 rightHandRollAxisLocal;
    public Vector3 leftForearmRollAxisLocal;
    public Vector3 rightForearmRollAxisLocal;
    public string sourcePrefabName;
    public bool ready;
    // 追従の Hips の局所位置（2026-10-25、StreamingStereoVideoPlayer.humanTrackingRestoresHipsLocalPosition）。人のクリップ（自分から動く
    // 動作・画面の外への歩き）が初めて Hips に触る直前の値。追従は Hips の局所位置を書かないので、それまではこの値のまま変わらない。
    public bool hasHipsTrackLocalPosition;
    public Vector3 hipsTrackLocalPosition;

    // シルエット相当の投影点（2026-10-03、useSilhouetteProjectionExtent）。既定姿勢で一度だけ測る。
    // 頭頂 = Head の位置 + Head の回転 × crownLocalDir × crownDistance × lossyScale。
    // 足裏 = 各ボーン（Toes / Foot）の位置 + 回転 × soleLocalDir × soleDistance × lossyScale。
    // 距離はモデルのローカル単位（lossyScale で割った値）。
    public bool hasSilhouette;
    // 足先の代理点（2026-10-04、footAimAtToeTipProxy）。Foot ボーンのローカル座標（Resources/human_foot_tip.json を avatar 名で引く）。
    public string avatarName;
    public bool footTipResolved;
    public bool hasFootTip;
    public Vector3 leftFootTipLocal;
    public Vector3 rightFootTipLocal;
    public Vector3 silhouetteCrownLocalDir;
    public float silhouetteCrownDistance;
    public readonly List<SilhouetteSolePoint> silhouetteSoles = new List<SilhouetteSolePoint>(4);
    // 既定姿勢での頭頂〜足裏の縦の広がり（モデルのローカル単位、2026-10-10、humanScaleRefineCarryAcrossModels のシルエットの場合）。
    // 頭頂と足裏の代理点はメッシュの上端・下端に置くので、既定姿勢ではちょうど上端 − 下端になる。測れなければ 0。
    public float silhouetteExtentLocal;
}

internal struct SilhouetteSolePoint
{
    public HumanBodyBones bone;
    public Vector3 localDir;
    public float distance;
}
