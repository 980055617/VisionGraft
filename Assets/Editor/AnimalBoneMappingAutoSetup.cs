using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 新しい動物 prefab の骨名を自動検出して AnimalBoneMappingOverride を設定する。
/// Tools > Setup Animal Bone Mappings で実行。
/// 対応パターン:
///   Wolf系:   LegFL1/FL2/FLAnkle, LegFR1..., LegBL1..., LegBR1...
///   BearRig系: RigLFLeg1/2/Ankle, RigRFLeg1..., RigLBLeg1..., RigRBLeg1...
///   Donkey系:  L_UpperLeg/L_LowerLeg/L_Ankle, L_Back_UpperLeg..., R_Back_...
/// </summary>
public static class AnimalBoneMappingAutoSetup
{
    [MenuItem("Tools/Setup Animal Bone Mappings")]
    public static void SetupSelectedPrefabs()
    {
        Object[] selected = Selection.objects;
        if (selected.Length == 0)
        {
            EditorUtility.DisplayDialog("Bone Mapping Setup", "Prefab を選択してから実行してください。", "OK");
            return;
        }

        int updated = 0, skipped = 0;

        foreach (Object obj in selected)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
            {
                skipped++;
                continue;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { skipped++; continue; }

            bool changed = ApplyMapping(prefab, path);
            if (changed) updated++;
            else skipped++;
        }

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Bone Mapping Setup",
            $"完了\n更新: {updated}\nスキップ: {skipped}", "OK");
    }

    [MenuItem("Tools/Setup Animal Bone Mappings (All in Resources/Models/Animal)")]
    public static void SetupAllAnimalPrefabs()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Resources/Models/Animal" });
        int updated = 0, skipped = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { skipped++; continue; }

            bool changed = ApplyMapping(prefab, path);
            if (changed) updated++;
            else skipped++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[BoneMappingAutoSetup] 完了. updated={updated}, skipped={skipped}");
        EditorUtility.DisplayDialog("Bone Mapping Setup (All)",
            $"完了\n更新: {updated}\nスキップ: {skipped}", "OK");
    }

    static bool ApplyMapping(GameObject prefab, string path)
    {
        Transform[] bones = prefab.GetComponentsInChildren<Transform>(true);
        var boneNames = new HashSet<string>(bones.Select(b => b.name));

        var mapping = DetectMapping(boneNames);
        if (mapping == null)
        {
            Debug.Log($"[BoneMappingAutoSetup] パターン未検出: {prefab.name}");
            return false;
        }

        using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
        {
            GameObject root = scope.prefabContentsRoot;

            AnimalBoneMappingOverride comp = root.GetComponentInChildren<AnimalBoneMappingOverride>();
            if (comp == null)
                comp = root.AddComponent<AnimalBoneMappingOverride>();

            comp.spine = mapping.spine;
            comp.neck = mapping.neck;
            comp.head = mapping.head;
            comp.tailBase = mapping.tailBase;
            comp.tailMid = mapping.tailMid;
            comp.tailTip = mapping.tailTip;
            comp.frontLUpper = mapping.frontLUpper;
            comp.frontLLower = mapping.frontLLower;
            comp.frontLPaw = mapping.frontLPaw;
            comp.frontRUpper = mapping.frontRUpper;
            comp.frontRLower = mapping.frontRLower;
            comp.frontRPaw = mapping.frontRPaw;
            comp.rearLUpper = mapping.rearLUpper;
            comp.rearLLower = mapping.rearLLower;
            comp.rearLPaw = mapping.rearLPaw;
            comp.rearLToe = mapping.rearLToe;
            comp.rearRUpper = mapping.rearRUpper;
            comp.rearRLower = mapping.rearRLower;
            comp.rearRPaw = mapping.rearRPaw;
            comp.rearRToe = mapping.rearRToe;
        }

        Debug.Log($"[BoneMappingAutoSetup] 設定完了: {prefab.name} ({mapping.patternName})");
        return true;
    }

    // ──────────────────────────────────────────
    // パターン検出
    // ──────────────────────────────────────────

    class MappingResult
    {
        public string patternName;
        public string spine, neck, head;
        public string tailBase, tailMid, tailTip;
        public string frontLUpper, frontLLower, frontLPaw;
        public string frontRUpper, frontRLower, frontRPaw;
        public string rearLUpper, rearLLower, rearLPaw, rearLToe;
        public string rearRUpper, rearRLower, rearRPaw, rearRToe;
    }

    static MappingResult DetectMapping(HashSet<string> names)
    {
        MappingResult r;

        r = TryWolfPattern(names);    if (r != null) return r;
        r = TryBearRigPattern(names); if (r != null) return r;
        r = TryDonkeyPattern(names);  if (r != null) return r;
        r = TryGenericPattern(names); if (r != null) return r;

        return null;
    }

    // Wolf 系: LegFL1, LegFL2, LegFLAnkle / LegFR... / LegBL... / LegBR...
    static MappingResult TryWolfPattern(HashSet<string> n)
    {
        if (!n.Contains("LegFL1") || !n.Contains("LegBL1")) return null;

        return new MappingResult
        {
            patternName = "Wolf",
            spine    = Pick(n, "Spine1", "Spine"),
            neck     = Pick(n, "Neck"),
            head     = Pick(n, "Head"),
            tailBase = Pick(n, "Tail1", "TailBase", "tail_1"),
            tailMid  = Pick(n, "Tail2", "tail_2"),
            tailTip  = Pick(n, "Tail3", "Tail4", "tail_tip"),
            frontLUpper = "LegFL1",
            frontLLower = Pick(n, "LegFL2", "LegFLKnee"),
            frontLPaw   = Pick(n, "LegFLAnkle", "LegFL3"),
            frontRUpper = "LegFR1",
            frontRLower = Pick(n, "LegFR2", "LegFRKnee"),
            frontRPaw   = Pick(n, "LegFRAnkle", "LegFR3"),
            rearLUpper  = "LegBL1",
            rearLLower  = Pick(n, "LegBL2", "LegBLKnee"),
            rearLPaw    = Pick(n, "LegBLAnkle", "LegBL3"),
            rearLToe    = Pick(n, "LegBLToe", "LegBL4"),
            rearRUpper  = "LegBR1",
            rearRLower  = Pick(n, "LegBR2", "LegBRKnee"),
            rearRPaw    = Pick(n, "LegBRAnkle", "LegBR3"),
            rearRToe    = Pick(n, "LegBRToe", "LegBR4"),
        };
    }

    // BearRig 系: RigLFLeg1/2/Ankle, RigRFLeg1..., RigLBLeg1..., RigRBLeg1...
    static MappingResult TryBearRigPattern(HashSet<string> n)
    {
        if (!n.Contains("RigLFLeg1") || !n.Contains("RigLBLeg1")) return null;

        return new MappingResult
        {
            patternName = "BearRig",
            spine    = Pick(n, "RigSpine2", "RigSpine1", "RigSpine"),
            neck     = Pick(n, "RigNeck1", "RigNeck"),
            head     = Pick(n, "RigHead", "RigHead1"),
            tailBase = Pick(n, "RigTail1", "RigTailBase"),
            tailMid  = Pick(n, "RigTail2"),
            tailTip  = Pick(n, "RigTail3", "RigTail4"),
            frontLUpper = "RigLFLeg1",
            frontLLower = Pick(n, "RigLFLeg2", "RigLFLegKnee"),
            frontLPaw   = Pick(n, "RigLFLegAnkle", "RigLFLeg3"),
            frontRUpper = "RigRFLeg1",
            frontRLower = Pick(n, "RigRFLeg2", "RigRFLegKnee"),
            frontRPaw   = Pick(n, "RigRFLegAnkle", "RigRFLeg3"),
            rearLUpper  = "RigLBLeg1",
            rearLLower  = Pick(n, "RigLBLeg2", "RigLBLegKnee"),
            rearLPaw    = Pick(n, "RigLBLegAnkle", "RigLBLeg3"),
            rearLToe    = Pick(n, "RigLBLegToe", "RigLBLeg4"),
            rearRUpper  = "RigRBLeg1",
            rearRLower  = Pick(n, "RigRBLeg2", "RigRBLegKnee"),
            rearRPaw    = Pick(n, "RigRBLegAnkle", "RigRBLeg3"),
            rearRToe    = Pick(n, "RigRBLegToe", "RigRBLeg4"),
        };
    }

    // Donkey 系: L_UpperLeg/L_LowerLeg/L_Ankle, L_Back_UpperLeg, R_Back_UpperLeg ...
    static MappingResult TryDonkeyPattern(HashSet<string> n)
    {
        if (!n.Contains("L_UpperLeg") || !n.Contains("L_Back_UpperLeg")) return null;

        return new MappingResult
        {
            patternName = "Donkey",
            spine    = Pick(n, "Spine_02", "Spine_01", "Spine"),
            neck     = Pick(n, "Neck_01", "Neck"),
            head     = Pick(n, "Head", "Head_01"),
            tailBase = Pick(n, "Tail_01", "Tail_Base", "Tail1"),
            tailMid  = Pick(n, "Tail_02", "Tail2"),
            tailTip  = Pick(n, "Tail_03", "Tail_Tip", "Tail3"),
            frontLUpper = "L_UpperLeg",
            frontLLower = Pick(n, "L_LowerLeg", "L_Knee"),
            frontLPaw   = Pick(n, "L_Ankle", "L_Foot"),
            frontRUpper = Pick(n, "R_UpperLeg"),
            frontRLower = Pick(n, "R_LowerLeg", "R_Knee"),
            frontRPaw   = Pick(n, "R_Ankle", "R_Foot"),
            rearLUpper  = "L_Back_UpperLeg",
            rearLLower  = Pick(n, "L_Back_LowerLeg", "L_Back_Knee"),
            rearLPaw    = Pick(n, "L_Back_Ankle", "L_Back_Foot"),
            rearLToe    = Pick(n, "L_Back_Toe", "L_Back_Hoof"),
            rearRUpper  = "R_Back_UpperLeg",
            rearRLower  = Pick(n, "R_Back_LowerLeg", "R_Back_Knee"),
            rearRPaw    = Pick(n, "R_Back_Ankle", "R_Back_Foot"),
            rearRToe    = Pick(n, "R_Back_Toe", "R_Back_Hoof"),
        };
    }

    // 汎用パターン: 大文字L/R + Front/Back/Fore/Hind + Upper/Lower キーワード検索
    static MappingResult TryGenericPattern(HashSet<string> names)
    {
        // 前左上腕の候補を探す
        string flUpper = FindGenericLeg(names, isLeft: true, isFront: true, segment: 0);
        string brUpper = FindGenericLeg(names, isLeft: false, isFront: false, segment: 0);
        if (flUpper == null || brUpper == null) return null;

        return new MappingResult
        {
            patternName = "Generic",
            spine    = FindByTokens(names, "spine", "body"),
            neck     = FindByTokens(names, "neck"),
            head     = FindByTokens(names, "head"),
            tailBase = FindByTokens(names, "tail"),
            frontLUpper = flUpper,
            frontLLower = FindGenericLeg(names, true,  true,  1),
            frontLPaw   = FindGenericLeg(names, true,  true,  2),
            frontRUpper = FindGenericLeg(names, false, true,  0),
            frontRLower = FindGenericLeg(names, false, true,  1),
            frontRPaw   = FindGenericLeg(names, false, true,  2),
            rearLUpper  = FindGenericLeg(names, true,  false, 0),
            rearLLower  = FindGenericLeg(names, true,  false, 1),
            rearLPaw    = FindGenericLeg(names, true,  false, 2),
            rearRUpper  = brUpper,
            rearRLower  = FindGenericLeg(names, false, false, 1),
            rearRPaw    = FindGenericLeg(names, false, false, 2),
        };
    }

    // ──────────────────────────────────────────
    // ユーティリティ
    // ──────────────────────────────────────────

    static string Pick(HashSet<string> names, params string[] candidates)
    {
        foreach (string c in candidates)
            if (!string.IsNullOrEmpty(c) && names.Contains(c))
                return c;
        return null;
    }

    static string FindByTokens(HashSet<string> names, params string[] tokens)
    {
        foreach (string name in names)
        {
            string lower = name.ToLowerInvariant();
            if (tokens.Any(t => lower.Contains(t)))
                return name;
        }
        return null;
    }

    static string FindGenericLeg(HashSet<string> names, bool isLeft, bool isFront, int segment)
    {
        // segment 0=upper, 1=lower, 2=paw/ankle
        string[] sideL   = { "l_", "_l_", "left",  "lf", "bl", "fl" };
        string[] sideR   = { "r_", "_r_", "right", "rf", "br", "fr" };
        string[] front   = { "front", "fore", "fl", "rf" };
        string[] back    = { "back", "rear", "hind", "bl", "br" };
        string[] upper   = { "upper", "1", "thigh", "shoulder" };
        string[] lower   = { "lower", "2", "shin",  "forearm" };
        string[] paw     = { "ankle", "paw", "foot", "toe", "hoof", "3" };

        string[] sideTokens  = isLeft  ? sideL  : sideR;
        string[] limbTokens  = isFront ? front   : back;
        string[] segTokens   = segment == 0 ? upper : (segment == 1 ? lower : paw);

        string best = null;
        int bestScore = -1;

        foreach (string name in names)
        {
            string lower2 = name.ToLowerInvariant();
            int score = 0;
            if (sideTokens.Any(t => lower2.Contains(t)))  score++;
            if (limbTokens.Any(t => lower2.Contains(t)))  score++;
            if (segTokens.Any(t => lower2.Contains(t)))   score++;

            if (score > bestScore && score >= 2)
            {
                bestScore = score;
                best = name;
            }
        }

        return best;
    }
}
