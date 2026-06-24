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
    RearRightLower
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
}
