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
        AnimalGesturePose headTailSample = CreateHeadTailSample();
        AnimalGesturePose pawWaveSample = CreatePawRaiseSample();
        AnimalGesturePose headShakeSample = CreateHeadShakeSample();
        AnimalGesturePose bodyShakeSample = CreateBodyShakeSample();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Created/updated sample Animal Walk and Static gesture assets in place. If " +
            "StreamingStereoVideoPlayer.animalWalkClips / animalStaticGestureClips already " +
            "reference these assets, the new curve values apply immediately - no re-assignment " +
            "needed. On a SMAL track, Force Static always turns toward the viewer and now also " +
            "always picks one of the four Static assets at random - press it a few times to see " +
            "each one.");
        Selection.objects = new Object[] { walkSample, headTailSample, pawWaveSample, headShakeSample, bodyShakeSample };
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

    private static AnimalGesturePose CreateHeadTailSample()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/SampleHeadTiltAndTailWag.asset");
        asset.duration = 2.0f;
        // 'forward' confirmed by you as a good axis for head/tail motion on this model.
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailTip, forward = BuildSineCurve(45f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, forward = BuildSineCurve(20f, 0f, false) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // Raises and waves one front leg, like a "paw" trick - reuses the same upper/lower leg
    // points and confirmed 'right' axis as the walk gait, just held in a lifted position
    // (offset, not a zero-centered sine) with a faster small wave on top.
    private static AnimalGesturePose CreatePawRaiseSample()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/SamplePawRaise.asset");
        asset.duration = 2.5f;
        AnimationCurve upperLift = BuildHoldAndWaveCurve(35f, 8f);
        AnimationCurve lowerBend = BuildHoldAndWaveCurve(40f, 6f);
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = upperLift },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = lowerBend },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // Quick double head-shake (faster, smaller-period than the head tilt above) on the same
    // confirmed 'forward' axis.
    private static AnimalGesturePose CreateHeadShakeSample()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/SampleHeadShake.asset");
        asset.duration = 1.2f;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, forward = BuildSineCurve(25f, 0f, false, samples: 16) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // Quick whole-body wobble, like shaking off water - rotates the rig root itself rather
    // than a limb. Composes additively on top of whatever rotation FaceViewer/tracking already
    // assigned this frame.
    private static AnimalGesturePose CreateBodyShakeSample()
    {
        EnsureDirectory(StaticFolder);
        AnimalGesturePose asset = LoadOrCreateAsset(StaticFolder + "/SampleBodyShake.asset");
        asset.duration = 1.0f;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Root, up = BuildSineCurve(10f, 0f, false, samples: 16) },
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

    // Rises to holdAmplitude over the first 20% of the curve, wiggles by waveAmplitude while
    // held up through the middle, then eases back to 0 over the last 20% - a "raise, wave,
    // lower" envelope rather than a symmetric sine.
    private static AnimationCurve BuildHoldAndWaveCurve(float holdAmplitude, float waveAmplitude, int samples = 24)
    {
        AnimationCurve curve = new AnimationCurve();
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float rise = Mathf.Sin(Mathf.Clamp01(t / 0.2f) * Mathf.PI * 0.5f);
            float fall = Mathf.Sin(Mathf.Clamp01((1f - t) / 0.2f) * Mathf.PI * 0.5f);
            float envelope = Mathf.Min(rise, fall);
            float wave = Mathf.Sin(t * Mathf.PI * 2f * 3f) * waveAmplitude;
            float value = (holdAmplitude + wave) * envelope;
            curve.AddKey(t, value);
        }

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }
        return curve;
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
