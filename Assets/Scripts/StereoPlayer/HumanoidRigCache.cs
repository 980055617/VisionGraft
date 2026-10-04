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
    // T-pose での canonical 親フレームから手ボーンの bindRotWorld への補正量。
    // 手のFK式: canonicalParent * smplLocal * handBindCorrection
    // smplLocal=identity のとき bindRotWorld[hand] に一致し、キャラクター間の軸差を吸収する。
    public readonly Dictionary<HumanBodyBones, Quaternion> handBindCorrection = new Dictionary<HumanBodyBones, Quaternion>();
    public bool ready;

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
}

internal struct SilhouetteSolePoint
{
    public HumanBodyBones bone;
    public Vector3 localDir;
    public float distance;
}
