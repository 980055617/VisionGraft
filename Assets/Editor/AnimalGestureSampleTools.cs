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

    // ---- 動物の動きの作り直しの候補（2026-10-05、Docs/interactive-motion-events.md「動物の動きの棚卸し」）----
    // 実物の動物の文献値に合わせた、仮の資産の作り直し。候補は Candidates/ に書く（Static/ に置くと OnValidate の自動割り当てに拾われる）。
    // 採用されたら同じ曲線を Static/ の同じパスへ書く（同じ GUID なので両シーンと次の APK に一度に効く）。バッチ:
    //   Unity.exe -batchmode -quit -projectPath ... -executeMethod AnimalGestureSampleTools.CreateRemakeCandidateAssets
    // どれも anatomicalAxes（right = 体の横軸まわり + で先が前・上、up = 体の上まわり、forward = 体の長軸・鼻の軸まわり）。
    // 時間は秒で書いて、資産の duration で割って 0..1 にする。段の長さは max(1.5 s, duration) なので duration を段の長さにそろえる（引き伸ばされない）。
    private const string CandidatesFolder = "Assets/Animations/InteractiveMotion/Animal/Candidates";

    [MenuItem("VisionGraft/Interactive Motion/Create Remake Candidate Gesture Assets")]
    public static void CreateRemakeCandidateAssets()
    {
        EnsureDirectory(CandidatesFolder);
        var created = new List<Object>
        {
            CreateHeadShakeEarFlapCandidate(),
            CreateHeadShakeNoCandidate(),
            CreateHeadTiltCandidate(),
            CreateBodyShakeCandidate(),
            CreatePawRaiseCandidate(),
            CreatePawOfferCandidate(),
            CreatePawLiftPointerCandidate(),
            CreatePawOfferBentCandidate(),
            CreatePawPointerElbowFlexCandidate(),
            CreatePawOfferElbowFlexCandidate(),
        };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        foreach (Object o in created)
        {
            Debug.Log($"[GESTURE-CANDIDATE] {AssetDatabase.GetAssetPath(o)}");
        }
    }

    // 身震いの作り直し v3 の候補（v3・v3s）だけを書く（2026-10-17）。CreateRemakeCandidateAssets は採用済みの候補も書き直すので分けた。バッチ:
    //   Unity.exe -batchmode -quit -projectPath ... -executeMethod AnimalGestureSampleTools.CreateBodyShakeV3CandidateAssets
    // 資産の trunkProfile を読むプレイヤー（AnimalGesturePose.trunkProfile・AnimalGesturePosePlayer.ApplyTrunk の 2026-10-17 の変更）が先に入っていること。
    [MenuItem("VisionGraft/Interactive Motion/Create Body Shake v3 Candidate Assets")]
    public static void CreateBodyShakeV3CandidateAssets()
    {
        EnsureDirectory(CandidatesFolder);
        var created = new List<Object>
        {
            // v3: 肩甲帯 ±16°・頭 ±28°・首 ±6°（頭が肩の 1.75 倍）
            CreateBodyShakeV3Candidate("SampleBodyShake_v3", 28f, 6f, 16f),
            // v3s: 胴を強め、頭を肩と同じに（肩甲帯 ±22°・頭 ±22°・首 ±4°）
            CreateBodyShakeV3Candidate("SampleBodyShake_v3s", 22f, 4f, 22f),
        };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        foreach (Object o in created)
        {
            Debug.Log($"[GESTURE-CANDIDATE] {AssetDatabase.GetAssetPath(o)}");
        }
    }

    // 新しい身ぶりの候補（2026-10-18、ユーザー 10/17「ほかにいいアニメーションがあればそれも追加してほしい」）。今のプレイヤーのまま動く「視線をそらす」と、
    // 撮影で比べる基準（点を持たない資産）を書く。どれも Candidates/ に書く（Static/ に置くと OnValidate の自動割り当てに拾われる）。バッチ:
    //   Unity.exe -batchmode -quit -projectPath ... -executeMethod AnimalGestureSampleTools.CreateNewGestureCandidateAssets
    // あくび（SampleYawn_v1・SampleYawnWide_v1）は顎の点（AnimalGesturePoint.Jaw）を足すプレイヤーの変更が要るので CreateYawnCandidateAssets に分けた（その変更と同じ版でだけ足す）。
    [MenuItem("VisionGraft/Interactive Motion/Create New Gesture Candidate Assets")]
    public static void CreateNewGestureCandidateAssets()
    {
        EnsureDirectory(CandidatesFolder);
        var created = new List<Object>
        {
            CreateLookAwayCandidate(),
            CreateStillReferenceAsset(),
        };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        foreach (Object o in created)
        {
            Debug.Log($"[GESTURE-CANDIDATE] {AssetDatabase.GetAssetPath(o)}");
        }
    }

    // あくびの候補（SampleYawn_v1 = 顎 44°、SampleYawnWide_v1 = 顎 52°）だけを書く（2026-10-18）。顎の点（AnimalGesturePoint.Jaw・AnimalRigCache.gestureJaw・
    // EnsureGestureAnatomy の顎）を足すプレイヤーの変更が先に入っていること（無いとこのファイルがコンパイルできない）。バッチ:
    //   Unity.exe -batchmode -quit -projectPath ... -executeMethod AnimalGestureSampleTools.CreateYawnCandidateAssets
    [MenuItem("VisionGraft/Interactive Motion/Create Yawn Candidate Assets")]
    public static void CreateYawnCandidateAssets()
    {
        EnsureDirectory(CandidatesFolder);
        var created = new List<Object>
        {
            CreateYawnCandidate("SampleYawn_v1", 44f),
            CreateYawnCandidate("SampleYawnWide_v1", 52f),
        };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        foreach (Object o in created)
        {
            Debug.Log($"[GESTURE-CANDIDATE] {AssetDatabase.GetAssetPath(o)}");
        }
    }

    // 首を振る（犬の耳を払う頭振り）: 鼻の軸まわりのロール ±35°・5 Hz・4 周期（0.8 s）、0.15 s 待ってから 0.1 s で立ち上げ、最後の 1.5 周期で減衰。
    // ヨー ±9° を 1/4 周期ずらして足し（楕円の振り）、耳を 1/8 周期遅れで ±30° ばたつかせる。犬の頭振りは「頭が左右に向く、または回転して耳が上下に繰り返し動く」
    // （den Uijl et al. 2017 PLoS ONE 12:e0188481）。周波数は推定（Labrador の身震い 4.5±0.25 Hz、Dickerson et al. 2012 より頭は小さく速い）。
    private static AnimalGesturePose CreateHeadShakeEarFlapCandidate()
    {
        const float duration = 1.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleHeadShake_v2.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        var osc = new Oscillation { hz = 5f, onset = 0.15f, attack = 0.1f, sustainCycles = 2.5f, decayCycles = 1.5f };
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve
            {
                point = AnimalGesturePoint.HeadTip,
                forward = BuildOscillationCurve(duration, osc, 35f, 0f),
                up = BuildOscillationCurve(duration, osc, 9f, 0.25f),
            },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.LeftEar, right = BuildOscillationCurve(duration, osc, 30f, -0.125f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RightEar, right = BuildOscillationCurve(duration, osc, -30f, -0.125f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 首を振る（別案: 人の「いいえ」に近い横振りを速く）: 頭の上まわりのヨー ±22°・2.5 Hz・2.5 周期、0.2 s 待ってから立ち上げ・減衰。
    // 今の資産（1 往復を 1.5 s）よりずっと速い。ユーザーが以前「首を振っていていい」と言った横振りを残したい場合の候補。
    private static AnimalGesturePose CreateHeadShakeNoCandidate()
    {
        const float duration = 1.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleHeadShake_v2no.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        var osc = new Oscillation { hz = 2.5f, onset = 0.2f, attack = 0.15f, sustainCycles = 1.5f, decayCycles = 1.0f };
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, up = BuildOscillationCurve(duration, osc, 22f, 0f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 首をかしげて尾を振る: 鼻の軸まわりに片側（右耳が下がる側）へ 25° を 0.3 s（ease-out）で傾け、1.0 s 保持（0.7 Hz・±2.5° のごく小さな揺れ）、
    // 0.4 s で戻して 0.3 s 静止。犬の首かしげは片側へ傾けて止める、傾ける側は個体ごとに一貫（Sommese et al. 2022 Anim Cogn 25:701）。角度・保持は推定。
    // 尾は付け根から全体を 2.0 Hz で振る（犬の尾振り 1.8±0.1 Hz、横の振れは尾の長さの ±0.75 倍、Martin et al. 2024）。付け根 ±30°、中ほど ±20°（1/8 周期遅れ）、
    // 先 ±25°（1/4 周期遅れ）、保持の間（0.3〜1.3 s）に振り、中心は右へ 5°（Quaranta et al. 2007 Curr Biol。量は推定）。耳は傾けている間に少し立てる。
    private static AnimalGesturePose CreateHeadTiltCandidate()
    {
        const float duration = 2.0f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleHeadTiltAndTailWag_v2.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        // 体の長軸（鼻の軸）まわりの + は頭の上が左へ傾く（左耳が下がる）向きなので、右耳を下げるのは −
        AnimationCurve tilt = BuildHoldCurve(duration, 0f, 0.3f, 1.0f, 0.4f, -25f, 2.5f, 0.7f);
        AnimationCurve earPerk = BuildHoldCurve(duration, 0f, 0.3f, 1.0f, 0.4f, 12f, 0f, 0f);
        var wag = new Oscillation { hz = 2.0f, onset = 0.3f, attack = 0.15f, sustainCycles = 1.4f, decayCycles = 0.6f };
        // 体の上まわりの + は尾の先が左へ動く向きなので、右へ 5° の中心は −5
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, forward = tilt },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.LeftEar, right = earPerk },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RightEar, right = earPerk },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailBase, up = BuildOscillationCurve(duration, wag, 30f, 0f, -5f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailMid, up = BuildOscillationCurve(duration, wag, 20f, -0.125f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailTip, up = BuildOscillationCurve(duration, wag, 25f, -0.25f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 体を振る（濡れた犬の身震い）: 胴（背骨の鎖）を体の長軸まわりに ±28°・4.5 Hz、立ち上げ 1 周期・持続 3 周期・減衰 1.5 周期（約 1.2 s）。
    // Labrador の身震いは 4.5±0.25 Hz、背骨の振れ約 30°（Dickerson, Mills & Hu 2012 J R Soc Interface 9:3208）。胴の鎖の累積は肩甲帯で 3 割を残す
    // （AnimalGesturePosePlayer.ApplyTrunk）。頭は 1/8 周期遅れで鼻の軸まわりに ±35°、耳は 1/4 周期遅れで ±30°、尾は付け根 ±25°（1/4 遅れ）・中ほど ±20°（3/8 遅れ）。
    // 首振り（5 Hz・0.8 s、頭だけ）と見分けがつくよう、胴・肩・尾まで振る。
    private static AnimalGesturePose CreateBodyShakeCandidate()
    {
        const float duration = 1.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleBodyShake_v2.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        var osc = new Oscillation { hz = 4.5f, onset = 0.1f, attack = 1f / 4.5f, sustainCycles = 3f, decayCycles = 1.5f };
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Trunk, forward = BuildOscillationCurve(duration, osc, 28f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, forward = BuildOscillationCurve(duration, osc, 35f, -0.125f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.LeftEar, right = BuildOscillationCurve(duration, osc, 30f, -0.25f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RightEar, right = BuildOscillationCurve(duration, osc, -30f, -0.25f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailBase, up = BuildOscillationCurve(duration, osc, 25f, -0.25f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailMid, up = BuildOscillationCurve(duration, osc, 20f, -0.375f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 前足を上げる: 右前肢の上腕を前へ +40°、前腕を +50°（前下へ）、手根を −60°（足先を垂らす）。上げ 0.4 s、保持 1.4 s（揺らさない）、下げ 0.4 s、静止 0.3 s。
    // paw lift は「前足 1 本をゆっくり上げ、すぐ下ろすか短く保持」（Vieira de Castro et al. 2020 PLoS ONE 15:e0225023）。手を振るのは人の仕草なので揺らさない。
    // 役の骨（解剖学的な上腕・前腕・手）に体の軸で掛けるので、左右の脚の局所軸が鏡映のモデルでも「前へ上げる」になる。角度は推定。
    private static AnimalGesturePose CreatePawRaiseCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v2.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.4f, 1.4f, 0.4f, 10f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.4f, 1.4f, 0.4f, 40f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.4f, 1.4f, 0.4f, 50f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.4f, 1.4f, 0.4f, -60f, 0f, 0f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 前足を上げる（お手の形、v3）: 上腕を前へ +30°、前腕を +10°（前下へ）、手根を −35°（足先を少し垂らす）、肩甲骨 +8°。前腕は鉛直から約 40° 前（水平より約 50° 下）。
    // v2（上腕 +40°・前腕 +50°）は前腕が水平より上まで上がった（2026-10-05 の R3 の拡大）。上げ 0.45 s、保持 1.3 s（揺らさない）、下げ 0.45 s、静止 0.3 s。角度は推定。
    private static AnimalGesturePose CreatePawOfferCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v3.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 8f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 30f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 10f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, -35f, 0f, 0f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 前足を上げる（片足上げ、v4）: 上腕を前へ +22°、肘を畳んで前腕の先を後ろへ −55°、手根を −70°（足先を後ろへ巻く）、肩甲骨 +5°。猟犬が前足を上げて止まる形。
    // paw lift（前足 1 本を上げ、すぐ下ろすか短く保持。Vieira de Castro et al. 2020）を肘・手根を畳む形で作った（形は推定）。上げ 0.35 s、保持 1.4 s、下げ 0.45 s。
    private static AnimalGesturePose CreatePawLiftPointerCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v4.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 5f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 22f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, -55f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, -70f, 0f, 0f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 前足を上げる（肘を曲げたお手、v5）: 上腕を前へ +70°（肘が胸の高さへ上がる）、肘を畳んで前腕を −40°（前腕は鉛直から約 30° 前へ垂れる）、手根を −45°（足先を垂らす）、肩甲骨 +10°。
    // 胴を体の長軸まわりに +5°（左が下がる = 支える左前足の側へ体重を移す）。保持中は上腕 ±2.5°・手根 ±4° を 0.9 Hz でわずかに揺らす。
    // 判定役 2 人が v2・v3 を「肘も手首も曲がらない棒の脚で、体も動かず人形のよう」、v4 を「関節は自然だが上げ幅が小さく視聴者へ向かない」と見たので（2026-10-05）、両方を合わせた。角度は推定。
    private static AnimalGesturePose CreatePawOfferBentCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v5.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Trunk, forward = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 5f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 10f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 70f, 2.5f, 0.9f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, -40f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, -45f, 4f, 0.9f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // ---- 前脚の関節の曲げる向き（2026-10-05、v4・v5 で間違えた）----
    // 犬・猫の前脚の肘は、曲げると前腕の先が前へ出る（肘の角は前側に開き、屈曲はそれを閉じる。歩きの遊脚でも前腕は前へ振れる）。手根は曲げると足先が後ろへ折れる。
    // 後脚は逆で、膝を曲げると下腿の先が後ろへ、飛節を曲げると中足の先が前へ。anatomicalAxes の right（+ で先が前）では 肘の屈曲 = +、手根の屈曲 = −、膝の屈曲 = −、飛節の屈曲 = +。
    // v4（肘 −55°）・v5（肘 −40°）は肘を「曲げる = −」と取り違え、bind で約 17° 曲がっている肘が約 38°・23° 逆に反っていた（Labrador の上腕・前腕の角度から計算）。

    // 前足を上げる（片足上げ・肘を正しく曲げた版、v4b）: 猟犬が指すときの前脚。上腕 +20°（ほぼ鉛直）、肘を +60° 曲げて前腕をほぼ水平に前へ、手根を −85° 曲げて足先を垂らす。
    // 肩甲骨 +5°、胴 +4°（支える左前足の側へ）。上げ 0.35 s、保持 1.4 s（上腕・手根を 0.9 Hz でわずかに揺らす）、下げ 0.45 s。角度は推定。
    private static AnimalGesturePose CreatePawPointerElbowFlexCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v4b.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Trunk, forward = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 4f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 5f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 20f, 2f, 0.9f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, 60f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.35f, 1.4f, 0.45f, -85f, 4f, 0.9f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 前足を上げる（肘を正しく曲げたお手、v5b）: 上腕 +45°（肘が前へ出る）、肘を +25° 曲げて前腕を前下へ（水平より約 24° 下）、手根を −60° 曲げて足先を垂らす。
    // 肩甲骨 +8°、胴 +5°（支える左前足の側へ）。上げ 0.45 s、保持 1.3 s（上腕・手根を 0.9 Hz でわずかに揺らす）、下げ 0.45 s。v2（上腕 +40°・肘 +50°）より低く、肘の曲がりが見える形。角度は推定。
    private static AnimalGesturePose CreatePawOfferElbowFlexCandidate()
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SamplePawRaise_v5b.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Trunk, forward = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 5f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 8f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightUpper, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 45f, 2.5f, 0.9f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightLower, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, 25f, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightPaw, right = BuildHoldCurve(duration, 0f, 0.45f, 1.3f, 0.45f, -60f, 4f, 0.9f) },
        };
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // ---- 身震いの作り直し v3（2026-10-17）----
    // ユーザー（10/17）「身震いなんかやりすぎじゃない？本当に動物がする動き？」「身ぶるいモットうまく作って」。v2 は判定役 2 人とも「胴の回転が見えず、首振り（＋尾振り）に見える」。
    // v2 は胴の鎖の中ほどだけを ±28° ねじり（肩甲帯 7.4°・骨盤 0°）、軸が背中の線なので腹だけが振れ、頭（world で ±40°）と耳（±30°）が目立った。v3 は:
    // - 肩甲帯（胴の鎖の最後の骨）を girdleDeg 回す。胴の点の曲線を「骨盤に対する肩甲帯の回転」にし、鎖への配り方を trunkProfile（(0,0)(0.6,1.2)(1,1) の折れ線、
    //   胴の中ほどが肩甲帯の 1.2 倍）で決める
    // - 肩甲骨を肩甲帯と同じだけ逆に回して前脚の向きを保つ（足先は肩甲骨の根の移動だけ動く。移植の予測で Labrador 0.6 cm・Lynx 0.9 cm、v2 は 2.0・4.8 cm）。
    //   上腕は回さない（肩甲骨の無い 00_Dog で前脚が振れないように）。骨盤・後脚・尾の付け根は回さない（後ろ足は動かない）
    // - 頭 → 首 → 肩甲帯 → 耳 → 尾の付け根 → 中ほど → 先の順に遅れる波（頭 0・首 1/16・肩甲帯 2/16・耳 4/16・尾 6/16・8/16・10/16 周期、1/16 周期 = 14 ms。
    //   遅れは推定）。頭は world で headDeg（鼻の軸まわり）
    // - 首の world のロールは neckDeg に抑える。視聴者へ向けた頭は首・胴のロールを受け継ぐので、頭の向きが体の前から ψ ずれていると鼻が上下に振れる。
    //   肩甲帯 16° で首も 16° 回すと ψ 57° で ±13°、首 6° なら ±8°（v2 は ±6°。移植、scratchpad の w1017shake/final/nod_neck_final.py）
    // - 各部位 3 周期（立ち上げ 0.75・持続 1.25・減衰 1）、0.45 s から（視聴者が体の前から 80° より外のとき、体の向き替えが最初の 0.4〜0.6 s にある。
    //   0.45 s なら向き替えが 0.45 s 以内（134° まで）のときは重ならない）。振る前に鼻を 8° 下げて構え（0.15〜0.40 s）、振り終えてから戻す（1.15〜1.45 s）
    // 周波数は Labrador の実測 4.5 Hz（Dickerson, Mills & Hu 2012 J R Soc Interface 9:3208。4〜260 kg の動物は 4〜6 Hz）。四本足で立って振り、足は地面に着いたまま
    // （同論文。立ってから振るのはイベントの to_default の段が済ませている）。頭から尾へ伝わる順はマウスの高速度撮影（Halberstadt & Geyer 2013）と犬についての
    // 研究者の談話（2010 の報道）だけが出典で、振幅・遅れ・周期数・構えは推定。値は world でのロール（頭は鼻の軸、ほかは体の長軸、+ で上が体の左へ）で決め、
    // 局所の曲線は「その部位 − 親の部位」にする（体の長軸まわりの回転は足し算で重なる）。
    // **この資産は trunkProfile を読むプレイヤーと同じビルドでしか出さない**。古いプレイヤーは trunkProfile を読まず肩甲帯に 26.5% しか届かないので、
    // 肩甲骨の逆回し（100%）が勝って前脚が逆に約 12° 傾き、足先が滑る。
    private static AnimalGesturePose CreateBodyShakeV3Candidate(string assetName, float headDeg, float neckDeg, float girdleDeg)
    {
        const float duration = 1.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/" + assetName + ".asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.trunkProfile = BuildLinearCurve(new Vector2(0f, 0f), new Vector2(0.6f, 1.2f), new Vector2(1f, 1f));
        var w = new ShakeWave { hz = 4.5f, start = 0.45f, attackCycles = 0.75f, sustainCycles = 1.25f, decayCycles = 1f };
        float Head(float t) => ShakeValue(w, headDeg, 0f, t);
        float Neck(float t) => ShakeValue(w, neckDeg, 1f / 16f, t);
        float Girdle(float t) => ShakeValue(w, girdleDeg, 2f / 16f, t);
        // 尾の world での横振り（度、体の上まわり）。付け根 → 中ほど → 先と遅れる。符号は肩甲帯が上を左へ傾けたとき尾の先が右へ出る側
        float TailBase(float t) => -ShakeValue(w, 12f, 6f / 16f, t);
        float TailMid(float t) => -ShakeValue(w, 20f, 8f / 16f, t);
        float TailEnd(float t) => -ShakeValue(w, 26f, 10f / 16f, t);
        // 耳（耳の根の骨）は頭のロールに 1/4 周期遅れて左右交互にばたつく（+ で耳の先が上。首振り v2 と同じ形で小さく）
        float Ear(float t) => ShakeValue(w, 14f, 4f / 16f, t);
        AnimationCurve S(System.Func<float, float> f) => BuildSampledCurve(duration, w.hz, f);
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Trunk, forward = S(Girdle) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Neck, forward = S(t => Neck(t) - Girdle(t)) },
            new AnimalGesturePointCurve
            {
                point = AnimalGesturePoint.HeadTip,
                forward = S(t => Head(t) - Neck(t)),
                right = BuildHoldCurve(duration, 0.15f, 0.25f, 0.75f, 0.30f, -8f, 0f, 0f),
            },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.LeftEar, right = S(Ear) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RightEar, right = S(t => -Ear(t)) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailBase, up = S(TailBase) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailMid, up = S(t => TailMid(t) - TailBase(t)) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailTip, up = S(t => TailEnd(t) - TailMid(t)) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontLeftScapula, forward = S(t => -Girdle(t)) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.FrontRightScapula, forward = S(t => -Girdle(t)) },
        };
        WarnDuplicatePoints(asset);
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 身震いの波。部位の値 = 包絡 × amplitude × sin(2π·hz·u)、u = t − start − lagCycles / hz（秒）。包絡（立ち上げ attackCycles・持続 sustainCycles・
    // 減衰 decayCycles、smoothstep）は全部位で同じ形を lagCycles / hz だけ遅らせる: 頭が先に始まって先に終わり、尾が最後に始まって最後に終わる。
    private struct ShakeWave
    {
        public float hz;
        public float start;
        public float attackCycles;
        public float sustainCycles;
        public float decayCycles;
    }

    private static float ShakeValue(ShakeWave w, float amplitude, float lagCycles, float t)
    {
        float period = 1f / Mathf.Max(0.1f, w.hz);
        float u = t - w.start - lagCycles * period;
        float attack = w.attackCycles * period;
        float sustain = w.sustainCycles * period;
        float decay = w.decayCycles * period;
        if (u <= 0f || u >= attack + sustain + decay)
        {
            return 0f;
        }

        float env = u < attack ? Mathf.SmoothStep(0f, 1f, u / Mathf.Max(1e-4f, attack))
            : u <= attack + sustain ? 1f
            : 1f - Mathf.SmoothStep(0f, 1f, (u - attack - sustain) / Mathf.Max(1e-4f, decay));
        return env * amplitude * Mathf.Sin(2f * Mathf.PI * w.hz * u);
    }

    // 秒の関数 f(t) を BuildOscillationCurve と同じ細かさ（1 周期 16 キー以上、最低でも 1/72 s ごと）でキーにする。
    private static AnimationCurve BuildSampledCurve(float duration, float hz, System.Func<float, float> f)
    {
        float period = 1f / Mathf.Max(0.1f, hz);
        float step = Mathf.Min(period / 16f, 1f / 72f);
        var curve = new AnimationCurve();
        for (float t = 0f; t <= duration + 1e-5f; t += step)
        {
            curve.AddKey(new Keyframe(Mathf.Clamp01(t / duration), f(t)));
        }

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }

        return curve;
    }

    // 折れ線の曲線（trunkProfile 用）。接線を隣の線分の傾きにし、さらに Linear にする（Inspector で触っても滑らかに直されない）。
    private static AnimationCurve BuildLinearCurve(params Vector2[] points)
    {
        var curve = new AnimationCurve();
        for (int i = 0; i < points.Length; i++)
        {
            float inSlope = i > 0 ? (points[i].y - points[i - 1].y) / Mathf.Max(1e-6f, points[i].x - points[i - 1].x) : 0f;
            float outSlope = i + 1 < points.Length ? (points[i + 1].y - points[i].y) / Mathf.Max(1e-6f, points[i + 1].x - points[i].x) : 0f;
            curve.AddKey(new Keyframe(points[i].x, points[i].y, i > 0 ? inSlope : outSlope, i + 1 < points.Length ? outSlope : inSlope));
        }

        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }

        return curve;
    }

    // 点は 1 つにつき 1 行にする（ApplyToRigCache は行ごとに bone.localRotation = 土台 × 回転 を書くので、同じ点が 2 行あると最後の行だけが効く）。
    private static void WarnDuplicatePoints(AnimalGesturePose asset)
    {
        var seen = new HashSet<AnimalGesturePoint>();
        foreach (AnimalGesturePointCurve c in asset.pointCurves)
        {
            if (c != null && !seen.Add(c.point))
            {
                Debug.LogError($"[GESTURE-CANDIDATE] DUPLICATE-POINT {asset.name}: 点 {c.point} が 2 行ある（最後の行だけが効く）");
            }
        }
    }

    // ---- 新しい身ぶりの候補（2026-10-18）----
    // どれも首・頭・耳（あくびは顎も）だけを回すので、足先は動かない（脚・胴・root・肩甲骨の点を持たない）。0.45 s までは何もしない
    // （立ち上がりの名残で胴・首がまだ回っている間と、視聴者が体の前から 80° より外のときの体の向き替え 0.4〜0.6 s に重ねない）。
    // 頭・首の曲線は「視聴者へ向けた頭」からのずれ（頭を向ける層の後に足される）。軸は anatomicalAxes: right + = 先が上（頭は鼻が上、首は頭が上、顎は顎先が上 = 閉じる側）、
    // up + = 体の右へ向く（上から見て時計回り）、forward + = 頭の上が体の左へ。耳は up（耳の根を鉛直まわりに回す = 耳の向きを変える）だけを使う
    // （耳の right は Labrador の垂れ耳では耳たぶを持ち上げ、Lynx の立ち耳では起こす側で、猫の「耳を前へ」と犬で向きがそろわない。移植 w1017gest/design/ear_right_check.py）。

    // 視線をそらして戻す（SampleLookAway_v1）: 立ち上がって視聴者を見てから 0.80 s おき、頭と首を合わせて体の左へ 30°（首 12°・頭 18°、頭を向ける層と同じ首 4 割）を
    // 0.80 s から 0.30 s で向け（ease-out、向き始めが速い）、0.65 s 保持し、0.40 s で戻す（2.15 s で終わる）。耳（耳の根）は 0.65 s から 0.15 s で −25° 先に向け、
    // 頭が向くにつれて戻して world では目標（体の左 30°）を向いたままにする（頭に対して 0 になるのは頭が向き終わる 1.10 s。その後は頭と一緒に戻る）。
    // 出典: 見知らぬ人が中立に近づくと犬の 94%（威嚇的なら 81%）が頭を横へ向けた（head turning、Pedretti et al. 2023 Anim Cogn 26:943、53 頭。定義は補遺で未確認）。
    // 犬の視線の行き来は 2 秒以内に顔と対象を見比べる形で記録される（gaze alternation、Commauda et al. 2026 Animals 16:760）。見知らぬ人の直視は猫には脅威になりうる
    // （2022 AAFP/ISFM の猫の扱いの指針）。猫は音の方を向くとき耳が目・頭より先に動く（Populin & Yin 1998 J Neurosci 18:4233 の要旨。遅れの数値は未確認）。
    // 角度・速さ・保持・耳の先行（約 0.14 s）・向き（体の左）は推定。向きは資産で決まる: 視聴者が体の右にいれば体の前へ戻る側、左にいれば肩越しにそれる側
    // （静的イベントは体を回して視聴者を体の前から 80° 以内に収めるので最大で体の前から約 110°、歩けずに体を回さないイベントだけ最大約 130°）。
    // 頭が視聴者より高く鼻が下を向いているときは、頭の上まわりに回すので頭が少し傾く（移植の予測で Lynx の立ち f1685 で 5.6°）。
    private static AnimalGesturePose CreateLookAwayCandidate()
    {
        const float duration = 2.25f;
        const float total = -30f;
        const float neckShare = 0.4f;
        const float onset = 0.80f, rise = 0.30f, hold = 0.65f, fall = 0.40f;
        const float earLead = -25f, earOnset = 0.65f, earRise = 0.15f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleLookAway_v1.asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        // 耳の根（頭に対して）: 頭が動く前に目標の側へ先に向き（最大 earLead）、頭が来るにつれて「目標 − 頭」へ戻る（耳の world の向き = 頭 + 耳 = 目標）。
        // 頭が向き終わった後は 0（頭と一緒に戻る。戻りでもう一度先に向けると垂れ耳が 2 回ばたつく）
        float Ear(float t)
        {
            if (t <= earOnset || t >= onset + rise)
            {
                return 0f;
            }

            float head = total * HoldShape(t, onset, rise, hold, fall);
            return Mathf.SmoothStep(0f, 1f, (t - earOnset) / earRise) * Mathf.Clamp(total - head, earLead, 0f);
        }

        AnimationCurve ear = BuildSampledCurve(duration, 1f, Ear);
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Neck, up = BuildHoldCurve(duration, onset, rise, hold, fall, total * neckShare, 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, up = BuildHoldCurve(duration, onset, rise, hold, fall, total * (1f - neckShare), 0f, 0f) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.LeftEar, up = ear },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.RightEar, up = ear },
        };
        WarnDuplicatePoints(asset);
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 撮影で比べる基準（SampleStillRef_v1）: 点を 1 つも持たない 2.5 s の資産。立ち上がって視聴者へ頭を向けたまま止まるので、同じ場面の
    // 「身ぶりを足す前の姿勢」が tick ごとに取れる（新しい身ぶりの撮影から引く）。撮影専用で、シーンの一覧（animalStaticGestureClips）には入れない。
    private static AnimalGesturePose CreateStillReferenceAsset()
    {
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/SampleStillRef_v1.asset");
        asset.duration = 2.5f;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        asset.pointCurves = new List<AnimalGesturePointCurve>();
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // BuildHoldCurve と同じ形（揺れなし）を 0..1 で返す（秒）: onset まで 0、rise 秒で 1 へ（ease-out）、hold 秒 1、fall 秒で 0 へ（smoothstep）、その後 0。
    private static float HoldShape(float t, float onset, float rise, float hold, float fall)
    {
        float holdStart = onset + rise;
        float holdEnd = holdStart + hold;
        if (t <= onset)
        {
            return 0f;
        }

        if (t < holdStart)
        {
            float u = (t - onset) / Mathf.Max(1e-4f, rise);
            return 1f - (1f - u) * (1f - u);
        }

        if (t <= holdEnd)
        {
            return 1f;
        }

        return t < holdEnd + fall ? 1f - Mathf.SmoothStep(0f, 1f, (t - holdEnd) / Mathf.Max(1e-4f, fall)) : 0f;
    }

    // あくび（SampleYawn_v1 = 顎 44°、SampleYawnWide_v1 = 顎 52°）: 顎を 0.45 s から 0.95 s で開け（smoothstep）、0.30 s 開けたまま、0.70 s で閉じる（2.40 s で閉じ終わる）。
    // 開く間に鼻先を 10° 上げ（首 3°・頭 7°、顎より少し後に上げ始める）、顎と同じ 2.40 s に下ろす。
    // 出典: 犬のあくびは 2.04 ± 0.59 s（272 回・198 頭）、Labrador は 2.21 ± 0.46 s（12 頭）。口が閉じた最後の瞬間から再び閉じるまで。「吸気とともに大きく開け、
    // 短い頂点の後、短い呼気で受動的に閉じる」（Gallup, Moscatello & Massen 2019 Curr Zool 66:401）ので閉じる方を短くした。猫は平均 1.97 s（Gallup et al. 2016、報道からの孫引き）。
    // メーカーの Lynx のあくび（idle_stretching_01 の中）は顎 81°・開き 0.62 s・閉じ 0.46 s（10〜90%）で、その間に頭が上がる。メーカーは Labrador にあくびを付けておらず、
    // 物をくわえる動きで顎を 41〜49° 開けている（唇の骨は回さない）。44° はその範囲の中、52° は少し外（口角の伸びを撮影で確かめる）。開く角度・時間の配分・鼻の上げ幅は推定。
    // 目は細めない・耳は動かさない（まぶたの点が無い。耳は犬と猫で倒す向きの軸がそろわない）。
    // **顎の点（AnimalGesturePoint.Jaw）を足すプレイヤーの変更と同じ版でしか出さない**（古いプレイヤーには顎の点が無く、鼻を上げて下ろすだけになる。顎の骨が無いモデル 00_Dog でも同じ）。
    private static AnimalGesturePose CreateYawnCandidate(string assetName, float jawDeg)
    {
        const float duration = 2.5f;
        AnimalGesturePose asset = LoadOrCreateAsset(CandidatesFolder + "/" + assetName + ".asset");
        asset.duration = duration;
        asset.onRoleBones = true;
        asset.anatomicalAxes = true;
        // 顎は right の − で開く（+ は顎先が上 = 閉じる側）
        float Jaw(float t) => -jawDeg * SmoothPulse(t, 0.45f, 0.95f, 0.30f, 0.70f);
        float Neck(float t) => 3f * SmoothPulse(t, 0.50f, 0.90f, 0.30f, 0.70f);
        float Head(float t) => 7f * SmoothPulse(t, 0.55f, 0.90f, 0.25f, 0.70f);
        AnimationCurve S(System.Func<float, float> f) => BuildSampledCurve(duration, 1f, f);
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Jaw, right = S(Jaw) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.Neck, right = S(Neck) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, right = S(Head) },
        };
        WarnDuplicatePoints(asset);
        EditorUtility.SetDirty(asset);
        return asset;
    }

    // 0..1 のなめらかなこぶ（秒）: start まで 0、rise 秒で 1 へ（smoothstep）、hold 秒 1、fall 秒で 0 へ（smoothstep）、その後 0。
    private static float SmoothPulse(float t, float start, float rise, float hold, float fall)
    {
        if (t <= start)
        {
            return 0f;
        }

        float u = t - start;
        if (u < rise)
        {
            return Mathf.SmoothStep(0f, 1f, u / Mathf.Max(1e-4f, rise));
        }

        u -= rise;
        if (u <= hold)
        {
            return 1f;
        }

        u -= hold;
        return u < fall ? 1f - Mathf.SmoothStep(0f, 1f, u / Mathf.Max(1e-4f, fall)) : 0f;
    }

    // 振動の形（秒）。onset だけ待ってから attack で立ち上げ、sustainCycles 周期持続し、decayCycles 周期で 0 へ減衰する（包絡は smoothstep）。
    private struct Oscillation
    {
        public float hz;
        public float onset;
        public float attack;
        public float sustainCycles;
        public float decayCycles;
    }

    // amplitude·env(t)·sin(2π·hz·(t − onset) + 2π·phaseCycles) + offset·env(t)。72 Hz で描ける細かさ（1 周期 16 キー以上、最低でも 1/72 s ごと）でキーを打つ。
    private static AnimationCurve BuildOscillationCurve(float duration, Oscillation o, float amplitude, float phaseCycles, float offset = 0f)
    {
        float period = 1f / Mathf.Max(0.1f, o.hz);
        float sustainEnd = o.onset + o.attack + o.sustainCycles * period;
        float end = sustainEnd + o.decayCycles * period;
        float step = Mathf.Min(period / 16f, 1f / 72f);
        var curve = new AnimationCurve();
        for (float t = 0f; t <= duration + 1e-5f; t += step)
        {
            float env;
            if (t <= o.onset || t >= end) { env = 0f; }
            else if (t < o.onset + o.attack) { env = Mathf.SmoothStep(0f, 1f, (t - o.onset) / Mathf.Max(1e-4f, o.attack)); }
            else if (t <= sustainEnd) { env = 1f; }
            else { env = 1f - Mathf.SmoothStep(0f, 1f, (t - sustainEnd) / Mathf.Max(1e-4f, end - sustainEnd)); }
            float value = env * (amplitude * Mathf.Sin(2f * Mathf.PI * (o.hz * (t - o.onset) + phaseCycles)) + offset);
            curve.AddKey(new Keyframe(Mathf.Clamp01(t / duration), value));
        }

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }

        return curve;
    }

    // 立ち上げ・保持・戻しの曲線（秒）: onset 待ち → rise 秒で peak へ（ease-out）→ hold 秒保持（wobble の揺れを乗せる）→ fall 秒で 0 へ（smoothstep）。
    private static AnimationCurve BuildHoldCurve(float duration, float onset, float rise, float hold, float fall, float peak, float wobbleAmplitude, float wobbleHz)
    {
        float step = 1f / 72f;
        var curve = new AnimationCurve();
        for (float t = 0f; t <= duration + 1e-5f; t += step)
        {
            float value;
            float holdStart = onset + rise;
            float holdEnd = holdStart + hold;
            if (t <= onset) { value = 0f; }
            else if (t < holdStart) { float u = (t - onset) / Mathf.Max(1e-4f, rise); value = peak * (1f - (1f - u) * (1f - u)); }
            else if (t <= holdEnd)
            {
                float wobble = wobbleAmplitude * Mathf.Sin(2f * Mathf.PI * wobbleHz * (t - holdStart));
                float fade = Mathf.Min(1f, (t - holdStart) / 0.2f, (holdEnd - t) / 0.2f);
                value = peak + wobble * Mathf.Max(0f, fade);
            }
            else if (t < holdEnd + fall) { value = peak * (1f - Mathf.SmoothStep(0f, 1f, (t - holdEnd) / Mathf.Max(1e-4f, fall))); }
            else { value = 0f; }
            curve.AddKey(new Keyframe(Mathf.Clamp01(t / duration), value));
        }

        for (int i = 0; i < curve.length; i++)
        {
            curve.SmoothTangents(i, 0f);
        }

        return curve;
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
        // 頭は 'up'（Labrador の局所 Y ≈ 鼻、解剖学的な写像では鼻の軸まわり）。'forward' だと首振りと同じ軸で横振りになっていた（2026-10-05、資産も同じく直した）。
        asset.pointCurves = new List<AnimalGesturePointCurve>
        {
            new AnimalGesturePointCurve { point = AnimalGesturePoint.TailTip, forward = BuildSineCurve(45f, 0f, false) },
            new AnimalGesturePointCurve { point = AnimalGesturePoint.HeadTip, up = BuildSineCurve(20f, 0f, false) },
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
