using System;
using System.Collections.Generic;
using UnityEngine;

// One of the points an AnimalControlWorldData track moves while a tracked animal is visible.
// Curves are authored against these names, not literal bone paths, so one asset plays back
// the same way on any model that AnimalRigDefinition can resolve - the rig-mapping problem
// (see CONTEXT.md "Animal SMAL motion") is solved once, upstream of this data.
public enum AnimalGesturePoint
{
    Root,
    HeadTip,
    TailTip,
    FrontLeftPaw,
    FrontRightPaw,
    RearLeftPaw,
    RearRightPaw,
    // Upper leg ("thigh") bones - rotating these swings the whole leg rigidly, which reads as
    // an actual stride. Rotating only the paw (above) moves just the foot and is barely visible
    // on a model whose paw bone has little mesh hanging below it.
    FrontLeftUpper,
    FrontRightUpper,
    RearLeftUpper,
    RearRightUpper,
    FrontLeftLower,
    FrontRightLower,
    RearLeftLower,
    RearRightLower,
    // 2026-10-05 追加（資産は int で保存されるので必ず末尾に足す）。動物の動きの作り直し用:
    // Trunk = 胴（背骨の根 spine から肩甲帯までの中間の骨の鎖に配って回す。AnimalGesturePosePlayer の胴の分岐）、
    // Neck = 首の役の骨、TailBase / TailMid = 尾の付け根・中ほどの役の骨、LeftEar / RightEar = 耳（頭の子孫で名前に ear）、
    // FrontLeftScapula / FrontRightScapula = 前脚の役の上腕の親（肩甲骨。親が背骨・胴の鎖ならなし）。
    Trunk,
    Neck,
    TailBase,
    TailMid,
    LeftEar,
    RightEar,
    FrontLeftScapula,
    FrontRightScapula,
    // 2026-10-18 追加（あくび。資産は int で保存されるので末尾に足す）: Jaw = 頭の子で名前に jaw を含む最初の骨（上顎（upper）と末端（_end）は除く。
    // Labrador の Jaw、Lynx の fJaw。AnimalPoseApplier.EnsureGestureAnatomy）。FK が書かない骨（smalDriveJaw が OFF のとき）なので、毎 tick bind の局所へ戻してから足す
    // （smalDriveJaw が ON のときは、顎の点を持つ身ぶりの間だけ bind の局所を土台に回すので、その間は追従の口の開きを使わない）。
    // anatomicalAxes の right は − で口が開く（+ は顎先が上 = 閉じる側）。
    Jaw
}

[Serializable]
public class AnimalGesturePointCurve
{
    public AnimalGesturePoint point;

    // Evaluated over [0, 1] gesture time. Values are degrees of additive local rotation
    // applied on top of the bone's pose for this frame (right/up/forward = local X/Y/Z), so
    // they read the same on any model regardless of size or pose source (SMAL or animal
    // control targets) - see AnimalGesturePosePlayer.ApplyToRigCache.
    public AnimationCurve right = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f));
    public AnimationCurve up = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f));
    public AnimationCurve forward = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f));
}

// A static animation authored as data instead of code: a set of per-point offset curves over
// AnimalControlWorldData's existing control points (root/head/tail/paws). See "Animal pose
// preset" in CONTEXT.md and the "Testing" / "Animal tracks" sections of
// Docs/interactive-motion-events.md. Create via Assets > Create > VisionGraft > Animal Gesture
// Pose, or drop into Assets/Animations/InteractiveMotion/Animal/ for auto-assignment.
[CreateAssetMenu(fileName = "AnimalGesturePose", menuName = "VisionGraft/Animal Gesture Pose")]
public class AnimalGesturePose : ScriptableObject
{
    public float duration = 2f;
    public List<AnimalGesturePointCurve> pointCurves = new List<AnimalGesturePointCurve>();
    // 2026-10-05 追加（どちらも既定 false で、古い資産の再生は変わらない）。
    // onRoleBones: 四肢の点を役の骨（脚の割り当ての表で付け替えた、解剖学的な上腕・前腕・手 / 大腿・下腿・足）に乗せる。false なら
    // プレイヤーの animalGestureOnCanonicalLimbs のとおり（既定は正規名の骨。古い資産は正規名の骨の局所軸で作ってある）。
    public bool onRoleBones;
    // anatomicalAxes: 曲線を骨の局所軸ではなく体の軸で読む。right = 体の横軸まわり（+ で四肢は先が体の前へ、胴・首・頭・尾・耳は先が上へ）、
    // up = 体の上まわり（world の上から見て時計回り）、forward = 体の長軸まわり（頭は鼻の軸まわり）。骨ごとの軸は bind の姿勢で決める
    // （AnimalPoseApplier.EnsureGestureAnatomy）。左右の脚の骨の局所軸が鏡映のモデル（Labrador・Lynx など 52 体中 15 体）でも左右で意味がそろう。
    public bool anatomicalAxes;
    // 2026-10-17 追加（身震い v3）。胴の点（Trunk）を胴の鎖（spine の子から肩甲帯まで）に配る累積の形。x = 鎖の中の位置（spine の子 = 1/n … 肩甲帯 = 1）、
    // 値 = その骨までに回る割合（肩甲帯で 1 なら、胴の点の曲線の値がそのまま骨盤に対する肩甲帯の回転になる）。キーが 2 つ以上のときだけ使う。
    // 既定はキー無し = 従来の形（sin(πx) + 0.3x を最大 1 にそろえたもの。鎖 5 本なら肩甲帯に 26.5%）。今の資産（前足 v4b の胴 4° など）の再生は変わらない。
    public AnimationCurve trunkProfile = new AnimationCurve();
}
