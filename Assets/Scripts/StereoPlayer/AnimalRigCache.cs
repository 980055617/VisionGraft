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
    // FK が毎 tick 書き直さない骨（役に入らない正規名の骨・Animator の子の root）の bind の局所回転。ジェスチャはこれの上に足す
    // （今の回転に掛けると毎 tick 積み重なって回り続けた: Fox・Beaver の front_*_lower、00_Dog の Root 点。2026-10-04、査読役）。
    public readonly Dictionary<Transform, Quaternion> gestureBindLocal = new Dictionary<Transform, Quaternion>();
    // インタラクティブモーションで頭を視聴者へ向ける（2026-10-04）: SMAL の FK が首・頭を書き直した最後の frameCount と、
    // Apply がジェスチャを足す地点まで来た最後の frameCount（来なかった tick は向けない・足さない。HEAD と同じく Apply が早く返れば何もしない）。
    public int smalFkWrittenFrame = -1;
    public int gestureOverlayFrame = -1;
    // 頭ローカルの鼻先方向（頭の子孫の顔の骨から。AnimalPoseApplier.ResolveHeadNoseLocal）。
    public bool headNoseResolved;
    public bool headNoseValid;
    public Vector3 headNoseLocal;
    public string headNoseSource = string.Empty;
    // 頭のジェスチャを解剖学的な軸で回す写像（頭ローカル、AnimalPoseApplier.ResolveHeadGestureRemap、2026-10-05）。
    public bool headGestureRemapResolved;
    public bool headGestureRemapValid;
    public Quaternion headGestureRemap = Quaternion.identity;
    // 動物の動きの作り直し（2026-10-05、AnimalPoseApplier.EnsureGestureAnatomy）: 胴の鎖（spine の子から肩甲帯まで。FK が書かない骨）、
    // 肩甲骨・耳（FK が書かない骨）と、資産の anatomicalAxes で使う骨ごとの体の軸（骨ローカル。X = 体の右、Y = 体の上、Z = 体の前、頭は Z = 鼻）と
    // right の曲線の符号（+ で四肢は先が前、ほかは先が上）。
    public bool gestureAnatomyResolved;
    public readonly List<Transform> trunkChain = new List<Transform>();
    public Transform gestureScapulaLeft;
    public Transform gestureScapulaRight;
    public Transform gestureEarLeft;
    public Transform gestureEarRight;
    public readonly Dictionary<Transform, Quaternion> gestureAxesFrame = new Dictionary<Transform, Quaternion>();
    public readonly Dictionary<Transform, float> gestureSwingSign = new Dictionary<Transform, float>();
    // 非四足モード（AnimalPoseApplier.smalNonQuadrupedRig、2026-10-08、2026-10-09 に既定 ON）。キャッシュを作るとき 1 回だけ決める（ApplyNonQuadrupedRoles）。どちらも既定 false。
    // smalNoFrontLimbs: そのうち前肢の上の役が左右とも無いリグ（鳥）。SMAL FK の入口を spine と後肢の上 2 本で通す。脛 19/23 は smalNonQuadrupedHock だけで写し、
    //   全体の smalDriveCarpusHock・smalDriveFeet は見ない（前足・指は受け身のまま。AnimalSmalFkApplier の IsSmalCarpusHockDriven・IsSmalFootParentOnBodyFrame）。
    public bool smalNonQuadruped;
    public bool smalNoFrontLimbs;
    public bool ready;
}
