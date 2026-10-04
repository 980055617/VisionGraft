using System.Collections.Generic;
using UnityEngine;

internal sealed class AnimalRigCache
{
    public Transform root;
    public Transform neck;
    public Transform head;
    public Transform spine;
    public Transform tailBase;
    public Transform leftFrontUpper;
    public Transform leftFrontLower;
    public Transform leftFrontPaw;
    public Transform rightFrontUpper;
    public Transform rightFrontLower;
    public Transform rightFrontPaw;
    public Transform leftRearUpper;
    public Transform leftRearLower;
    public Transform leftRearPaw;
    public Transform leftRearToe;
    public Transform rightRearUpper;
    public Transform rightRearLower;
    public Transform rightRearPaw;
    public Transform rightRearToe;
    public Transform tailMid;
    public Transform tailTip;
    // bind pose で「首→頭」が体の正中面からどれだけ横へ振れているか（度）。
    // **bind 時に一度だけ測る。**毎フレーム測ると、その時点の頭の向きを打ち消してしまい
    // 頭が横を向けなくなる（2026-09-06 にその実装をして気づいた）。
    public float bindHeadYawDegrees;
    public bool hasBindHeadYaw;

    public Vector3 modelForwardLocal;
    public Vector3 modelUpLocal;
    // Plain bind-time world direction from spine to neck (neck.position - spine.position,
    // captured once - no aim-child/pivot heuristics). Rotating this by a candidate root
    // delta predicts where the neck should end up; comparing that prediction against a
    // real per-frame keypoint reference is how the root yaw fix gets decided per model
    // (see AnimalSmalFkApplier, ADR-0002).
    public Vector3 spineToNeckBindDirWorld;
    public readonly Dictionary<Transform, Vector3> bindDirLocal = new Dictionary<Transform, Vector3>();
    public readonly Dictionary<Transform, Quaternion> bindRotLocal = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, Quaternion> bindRotWorld = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, Transform> aimChildByBone = new Dictionary<Transform, Transform>();
    // 首の中間の骨（smalDriveNeckChain、2026-10-04）の bind の局所回転。最初に見たとき（まだ誰も書いていない）に控える。
    public readonly Dictionary<Transform, Quaternion> neckChainBindLocal = new Dictionary<Transform, Quaternion>();
    // 捕捉時の world での「体の右」（F2、animalFrontLimbBodyLateralSecondary の副軸の Unity 側。2026-10-04、MAP）。
    // modelForwardLocal を水平にしたものと modelUpLocal から Cross(up, 前) で作り、root.TransformDirection で捕捉時の root の回転を含める
    // （boneBindWorld と同じ系。Labrador は捕捉時に root が 1.4° 回っている）。
    public Vector3 bodyRightBindWorld;
    // 四肢のジェスチャ（AnimalGesturePosePlayer）を乗せる正規名の骨（front_r_upper など）。脚の割り当ての表（AnimalBoneMappingOverride）で
    // 役の骨が正規名の骨と違うモデルだけ入る（2026-10-04）。ジェスチャの資産は正規名の骨の局所軸で作ってあり、表で役が 1 本上の骨へ移ると
    // 同じ局所回転が別の向きに効く（PawRaise が「肉球を上げる」から「肩で脚を後ろへ振る」になった。Labrador・Lynx、M1）。
    public readonly Dictionary<AnimalGesturePoint, Transform> gestureCanonicalLimbs = new Dictionary<AnimalGesturePoint, Transform>();
    public bool ready;
}
