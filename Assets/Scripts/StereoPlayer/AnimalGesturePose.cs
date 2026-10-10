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
    FrontRightScapula
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
}
