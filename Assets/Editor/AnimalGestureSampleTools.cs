using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// One-shot generator for placeholder AnimalGesturePose assets, so Force Static/Dynamic has
// something to actually play before real hand-authored gestures exist. See "Animal tracks"
// in Docs/interactive-motion-events.md and Assets/Animations/InteractiveMotion/README.md.
public static class AnimalGestureSampleTools
{
    private const string WalkFolder = "Assets/Animations/InteractiveMotion/Animal/Walk";
    private const string StaticFolder = "Assets/Animations/InteractiveMotion/Animal/Static";

    [MenuItem("VisionGraft/Interactive Motion/Create Sample Animal Gesture Assets")]
    public static void CreateSampleAnimalGestureAssets()
    {
        AnimalGesturePose walkSample = CreateWalkSample();
        AnimalGesturePose staticSample = CreateStaticSample();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Created/updated sample Animal Walk and Static gesture assets in place. If " +
            "StreamingStereoVideoPlayer.animalWalkClips / animalStaticGestureClips already " +
            "reference these assets, the new curve values apply immediately - no re-assignment " +
            "needed. Note: on a SMAL track, Force Static only picks this gesture ~50% of the " +
            "time (the rest is FaceViewer) - press it a few times before concluding nothing moved.");
        Selection.objects = new Object[] { walkSample, staticSample };
    }

    // One Force Static round-trip to identify which local axis actually swings a paw fore-aft
    // on this model, instead of three (one per axis guess). Each leg gets a single large swing
    // on a different axis; whichever leg visibly swings forward/back (not up/down or
    // left/right) tells us which axis name to use for all four legs in the real Walk asset.
    [MenuItem("VisionGraft/Interactive Motion/Create Animal Leg Axis Calibration Asset")]
    public static void CreateLegAxisCalibrationAsset()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/_AxisCalibration.asset");
        // All four legs, same axis/amplitude/phase, slow (6s/cycle) and large (60deg) - checks
        // whether FrontLeftPaw/FrontRightPaw resolve and move at all (vs. only the rear legs
        // visibly swinging in the real walk asset).
        asset.duration = 6.0f;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontLeftPaw, right = BuildSineCurve(60f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildSineCurve(60f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RearLeftPaw, right = BuildSineCurve(60f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RearRightPaw, right = BuildSineCurve(60f, 0f, false) },
        };
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Updated _AxisCalibration.asset: all four paws, same 'right'-axis swing (60deg, " +
            "6s cycle). Force Static and report which legs move at all - this checks whether " +
            "FrontLeftPaw/FrontRightPaw resolve to a real bone on this model, separate from the " +
            "axis question.");
        Selection.objects = new Object[] { asset };
    }

    private static AnimalGesturePose CreateWalkSample()
    {
        EnsureDirectory(WalkFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(WalkFolder + "/SampleWalk.asset");
        asset.duration = 1.0f;
        // Diagonal (trot) gait: front-left + rear-right swing together, front-right + rear-left
        // swing on the opposite half of the cycle. Swinging the upper leg (thigh) carries the
        // whole leg rigidly - rotating only the paw (a leaf bone with little mesh below it on
        // this model) barely moved at all. The lower leg adds a knee bend during the
        // forward-swing half so the foot visibly lifts, instead of dragging stiff-legged.
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            MakeUpperLegCurve(AnimalGesturePoint.FrontLeftUpper, 0f),
            MakeLowerLegCurve(AnimalGesturePoint.FrontLeftLower, 0f),
            MakeUpperLegCurve(AnimalGesturePoint.RearRightUpper, 0f),
            MakeLowerLegCurve(AnimalGesturePoint.RearRightLower, 0f),
            MakeUpperLegCurve(AnimalGesturePoint.FrontRightUpper, 0.5f),
            MakeLowerLegCurve(AnimalGesturePoint.FrontRightLower, 0.5f),
            MakeUpperLegCurve(AnimalGesturePoint.RearLeftUpper, 0.5f),
            MakeLowerLegCurve(AnimalGesturePoint.RearLeftLower, 0.5f),
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    private static AnimalGesturePose CreateStaticSample()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/SampleHeadTiltAndTailWag.asset");
        asset.duration = 2.0f;
        // Exaggerated amplitude on a single axis each, for the same reason as MakeLegCurve -
        // makes it obvious from which axis actually moves the bone on this model.
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailTip, forward = BuildSineCurve(45f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, forward = BuildSineCurve(20f, 0f, false) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // Overwrites the asset's fields in place if it already exists (re-running this menu item
    // after changing the curve-building logic below should update the same asset, not leave a
    // stale one behind alongside an orphaned new one).
    private static AnimalGesturePose LoadOrCreateAsset(string path)
    {
        AnimalGesturePose existing = AssetDatabase.LoadAssetAtPath<AnimalGesturePose>(path);
        if (existing != null)
        {
            return existing;
        }

        AnimalGesturePose asset = ScriptableObject.CreateInstance<AnimalGesturePose>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    // 'right' confirmed (via Create Animal Leg Axis Calibration Asset) as the axis that swings
    // a leg cleanly fore-aft on this model - bone-local axis conventions are not guaranteed
    // consistent across models (see "Current Risk Notes" in Docs/DogMetaBoneMapping.md), so
    // re-run that calibration tool first if a different model's legs swing the wrong way.
    private static AnimalGesturePointCurve MakeUpperLegCurve(AnimalGesturePoint point, float phaseTurns)
    {
        return new AnimalGesturePointCurve
        {
            point = point,
            right = BuildSineCurve(25f, phaseTurns, clampPositive: false)
        };
    }

    // Bends during the same half of the cycle the upper leg is swinging forward, lifting the
    // foot clear of the ground; straightens during the back/stance half. Assumes the lower
    // leg's local 'right' axis matches the upper leg's (same kinematic chain, same bone roll
    // convention) - re-check with the calibration tool if a model's knee bends the wrong way.
    private static AnimalGesturePointCurve MakeLowerLegCurve(AnimalGesturePoint point, float phaseTurns)
    {
        return new AnimalGesturePointCurve
        {
            point = point,
            right = BuildSineCurve(30f, phaseTurns, clampPositive: true)
        };
    }

    private static AnimationCurve BuildSineCurve(float amplitude, float phaseTurns, bool clampPositive, int samples = 8)
    {
        AnimationCurve curve = new AnimationCurve();
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float value = Mathf.Sin((t + phaseTurns) * Mathf.PI * 2f) * amplitude;
            if (clampPositive)
            {
                value = Mathf.Max(0f, value);
            }
            curve.AddKey(t, value);
        }

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }
        return curve;
    }

    private static void EnsureDirectory(string assetDirectory)
    {
        if (!Directory.Exists(assetDirectory))
        {
            Directory.CreateDirectory(assetDirectory);
        }
    }
}
