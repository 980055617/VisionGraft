using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// One-time tool: renames bones in newly-added "50+ Animated Animals" package
// prefabs to the canonical names AnimalRigDefinition expects (ADR-0001).
// Unpacks each target prefab completely (breaking its link to the source FBX
// prefab, same end state as Dog.prefab) then renames Transforms by exact name.
// See docs/animal-bone-rename-mapping.md for the per-species mapping rationale.
public static class AnimalRigBoneRenamer
{
    private class Mapping
    {
        public string prefabPath;
        public Dictionary<string, string> renames;
    }

    // 0_Dog.prefab is intentionally absent here - it was validated/renamed in an earlier
    // session and is already canonical; re-running unpack+rename on it is unnecessary.
    private static readonly Mapping[] Mappings =
    {
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/01_Wolf.prefab",
            renames = new Dictionary<string, string>
            {
                { "Pelvis", "spine" },
                { "Neck3", "neck" },
                { "Head", "head" },
                { "Tail1", "tail_base" },
                { "Tail2", "tail_mid" },
                { "Tail4", "tail_tip" },
                { "LegFL1", "front_l_upper" },
                { "LegFL2", "front_l_lower" },
                { "LegFL3", "front_l_paw" },
                { "LegFR1", "front_r_upper" },
                { "LegFR2", "front_r_lower" },
                { "LegFR3", "front_r_paw" },
                { "LegBL1", "rear_l_upper" },
                { "LegBL2", "rear_l_lower" },
                { "LegBL3", "rear_l_paw" },
                { "LegBLAnkle", "rear_l_toe" },
                { "LegBR1", "rear_r_upper" },
                { "LegBR2", "rear_r_lower" },
                { "LegBR3", "rear_r_paw" },
                { "LegBRAnkle", "rear_r_toe" },
            },
        },
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/02_WildBoar.prefab",
            renames = new Dictionary<string, string>
            {
                { "Hips", "spine" },
                { "Neck03", "neck" },
                { "Head", "head" },
                { "Tail01", "tail_base" },
                { "Tail02", "tail_mid" },
                { "Tail04", "tail_tip" },
                { "LeftArm", "front_l_upper" },
                { "LeftForeArm", "front_l_lower" },
                { "LeftHand", "front_l_paw" },
                { "RightArm", "front_r_upper" },
                { "RightForeArm", "front_r_lower" },
                { "RightHand", "front_r_paw" },
                { "LeftUpLeg", "rear_l_upper" },
                { "LeftLeg", "rear_l_lower" },
                { "LeftFoot", "rear_l_paw" },
                { "LeftToeBase", "rear_l_toe" },
                { "RightUpLeg", "rear_r_upper" },
                { "RightLeg", "rear_r_lower" },
                { "RightFoot", "rear_r_paw" },
                { "RightToeBase", "rear_r_toe" },
            },
        },
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/03_Buffalo.prefab",
            renames = new Dictionary<string, string>
            {
                { "Pelvis", "spine" },
                { "Neck3", "neck" },
                { "Head", "head" },
                { "Tail1", "tail_base" },
                { "Tail2", "tail_mid" },
                { "Tail4", "tail_tip" },
                { "LegFL1", "front_l_upper" },
                { "LegFL2", "front_l_lower" },
                { "LegFL3", "front_l_paw" },
                { "LegFR1", "front_r_upper" },
                { "LegFR2", "front_r_lower" },
                { "LegFR3", "front_r_paw" },
                { "LegBL1", "rear_l_upper" },
                { "LegBL2", "rear_l_lower" },
                { "LegBL3", "rear_l_paw" },
                { "LegBLAnkle", "rear_l_toe" },
                { "LegBR1", "rear_r_upper" },
                { "LegBR2", "rear_r_lower" },
                { "LegBR3", "rear_r_paw" },
                { "LegBRAnkle", "rear_r_toe" },
            },
        },
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/04_Lion.prefab",
            renames = new Dictionary<string, string>
            {
                { "Hips", "spine" },
                { "Neck03", "neck" },
                { "Head", "head" },
                { "Tail01", "tail_base" },
                { "Tail04", "tail_mid" },
                { "Tail07", "tail_tip" },
                { "LeftArm", "front_l_upper" },
                { "LeftForeArm", "front_l_lower" },
                { "LeftHand", "front_l_paw" },
                { "RightArm", "front_r_upper" },
                { "RightForeArm", "front_r_lower" },
                { "RightHand", "front_r_paw" },
                { "LeftUpLeg", "rear_l_upper" },
                { "LeftLeg", "rear_l_lower" },
                { "LeftFoot", "rear_l_paw" },
                { "LeftToeBase", "rear_l_toe" },
                { "RightUpLeg", "rear_r_upper" },
                { "RightLeg", "rear_r_lower" },
                { "RightFoot", "rear_r_paw" },
                { "RightToeBase", "rear_r_toe" },
            },
        },
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/05_Horse.prefab",
            renames = new Dictionary<string, string>
            {
                { "BN_Spine_00_02", "spine" },
                { "BN_Neck_03_012", "neck" },
                { "BN_Head_00_016", "head" },
                { "BN_Tail_00_061", "tail_base" },
                { "BN_Tail_02_063", "tail_mid" },
                { "BN_Tail_04_065", "tail_tip" },
                { "BN_L_UpperArm_039", "front_l_upper" },
                { "BN_l_Forearm_040", "front_l_lower" },
                { "BN_L_Hand_041", "front_l_paw" },
                { "BN_R_UpperArm_044", "front_r_upper" },
                { "BN_R_Forearm_045", "front_r_lower" },
                { "BN_R_Hand_046", "front_r_paw" },
                { "BN_L_Thing_051", "rear_l_upper" },
                { "BN_L_Calf_052", "rear_l_lower" },
                { "BN_L_HorseLink_053", "rear_l_paw" },
                { "BN_L_Foot_054", "rear_l_toe" },
                { "BN_R_Thing_056", "rear_r_upper" },
                { "BN_R_Calf_057", "rear_r_lower" },
                { "BN_R_HorseLink_058", "rear_r_paw" },
                { "BN_R_Foot_00", "rear_r_toe" },
            },
        },
    };

    // ---- Shared rig-shape helpers for the second batch (docs/animal-bone-rename-mapping.md) ----
    // "LegXX" shape (Buffalo/Wolf-style): Root>Pelvis>{LegBL.../LegBR..., Spine1>Spine2>Chest>
    // {LegFLCollarbone>LegFL..., LegFRCollarbone>LegFR..., Neck.../Head}, Tail...}.
    // Front/rear leg names are identical across every model using this shape - only neck/tail
    // segment counts vary between species.
    private static Dictionary<string, string> LegStyleFull(string neckKey, string tailBase, string tailMid, string tailTip)
    {
        var d = new Dictionary<string, string>
        {
            { "Pelvis", "spine" },
            { "Head", "head" },
            { "LegFL1", "front_l_upper" }, { "LegFL2", "front_l_lower" }, { "LegFL3", "front_l_paw" },
            { "LegFR1", "front_r_upper" }, { "LegFR2", "front_r_lower" }, { "LegFR3", "front_r_paw" },
            { "LegBL1", "rear_l_upper" }, { "LegBL2", "rear_l_lower" }, { "LegBL3", "rear_l_paw" }, { "LegBLAnkle", "rear_l_toe" },
            { "LegBR1", "rear_r_upper" }, { "LegBR2", "rear_r_lower" }, { "LegBR3", "rear_r_paw" }, { "LegBRAnkle", "rear_r_toe" },
        };
        if (neckKey != null) d[neckKey] = "neck";
        if (tailBase != null) d[tailBase] = "tail_base";
        if (tailMid != null) d[tailMid] = "tail_mid";
        if (tailTip != null) d[tailTip] = "tail_tip";
        return d;
    }

    // "Rig" shape variant A (Bear1.0/BearWITHFUR/FoxWITHFUR-style): RigRoot>RigPelvis>
    // {RigLBLeg1>RigLBLeg2>RigLBLegAnkle>RigLBLegDigit11, RigSpine1>RigSpine2>RigChest>
    // {RigLFLegCollarbone>RigLFLeg1>RigLFLeg2>RigLFLegAnkle, RigNeck.../RigHead}, RigTail...}.
    // Front has only 2 real segments before a terminal Ankle (paw=Ankle); rear has the same
    // 2 segments plus a Digit11 past the Ankle, giving a real 4th (toe) joint.
    private static Dictionary<string, string> RigStyleA(string neckKey, string tailBase, string tailMid, string tailTip)
    {
        var d = new Dictionary<string, string>
        {
            { "RigPelvis", "spine" },
            { "RigHead", "head" },
            { "RigLFLeg1", "front_l_upper" }, { "RigLFLeg2", "front_l_lower" }, { "RigLFLegAnkle", "front_l_paw" },
            { "RigRFLeg1", "front_r_upper" }, { "RigRFLeg2", "front_r_lower" }, { "RigRFLegAnkle", "front_r_paw" },
            { "RigLBLeg1", "rear_l_upper" }, { "RigLBLeg2", "rear_l_lower" }, { "RigLBLegAnkle", "rear_l_paw" }, { "RigLBLegDigit11", "rear_l_toe" },
            { "RigRBLeg1", "rear_r_upper" }, { "RigRBLeg2", "rear_r_lower" }, { "RigRBLegAnkle", "rear_r_paw" }, { "RigRBLegDigit11", "rear_r_toe" },
        };
        if (neckKey != null) d[neckKey] = "neck";
        if (tailBase != null) d[tailBase] = "tail_base";
        if (tailMid != null) d[tailMid] = "tail_mid";
        if (tailTip != null) d[tailTip] = "tail_tip";
        return d;
    }

    // "Rig" shape variant C (Moose1.0/Pronghorn1.0/Elk1.0/Doe1.0-style): same skeleton as
    // RigStyleA but with a real 3rd leg segment (RigXFLeg3/RigXBLeg3) before a terminal Ankle,
    // and no Digit past the rear Ankle - so rear's 4th slot (toe) is the Ankle itself instead
    // of a separate Digit bone, and front's paw is the 3rd real segment (Ankle stays undriven).
    private static Dictionary<string, string> RigStyleC(string tailBase, string tailTip)
    {
        return new Dictionary<string, string>
        {
            { "RigPelvis", "spine" },
            { "RigNeck3", "neck" },
            { "RigHead", "head" },
            { "RigLFLeg1", "front_l_upper" }, { "RigLFLeg2", "front_l_lower" }, { "RigLFLeg3", "front_l_paw" },
            { "RigRFLeg1", "front_r_upper" }, { "RigRFLeg2", "front_r_lower" }, { "RigRFLeg3", "front_r_paw" },
            { "RigLBLeg1", "rear_l_upper" }, { "RigLBLeg2", "rear_l_lower" }, { "RigLBLeg3", "rear_l_paw" }, { "RigLBLegAnkle", "rear_l_toe" },
            { "RigRBLeg1", "rear_r_upper" }, { "RigRBLeg2", "rear_r_lower" }, { "RigRBLeg3", "rear_r_paw" }, { "RigRBLegAnkle", "rear_r_toe" },
            { tailBase, "tail_base" }, { tailTip, "tail_tip" },
        };
    }

    // "Lion/WildBoar" shape (Reference>Hips>{LeftPelvis>LeftUpLeg>LeftLeg>LeftFoot>LeftToeBase,
    // Spine>Spine1-4>{LeftShoulder*>LeftArm>LeftForeArm>LeftHand, Neck01>Neck02>Neck03>Head},
    // Tail01...}). Front/rear/neck names are identical across every model using this shape
    // (always exactly 3 neck segments) - only tail segment count varies.
    private static Dictionary<string, string> LionStyleFull(string tailBase, string tailMid, string tailTip)
    {
        var d = new Dictionary<string, string>
        {
            { "Hips", "spine" },
            { "Neck03", "neck" },
            { "Head", "head" },
            { "LeftArm", "front_l_upper" }, { "LeftForeArm", "front_l_lower" }, { "LeftHand", "front_l_paw" },
            { "RightArm", "front_r_upper" }, { "RightForeArm", "front_r_lower" }, { "RightHand", "front_r_paw" },
            { "LeftUpLeg", "rear_l_upper" }, { "LeftLeg", "rear_l_lower" }, { "LeftFoot", "rear_l_paw" }, { "LeftToeBase", "rear_l_toe" },
            { "RightUpLeg", "rear_r_upper" }, { "RightLeg", "rear_r_lower" }, { "RightFoot", "rear_r_paw" }, { "RightToeBase", "rear_r_toe" },
        };
        if (tailBase != null) d[tailBase] = "tail_base";
        if (tailMid != null) d[tailMid] = "tail_mid";
        if (tailTip != null) d[tailTip] = "tail_tip";
        return d;
    }

    // Same as LionStyleFull but the rear foot branches into LeftFootThumb/LeftFootToeBase
    // instead of continuing straight to a bare "LeftToeBase" (AmericanMink/EuropeanBadger).
    private static Dictionary<string, string> LionStyleThumbFoot(string tailBase, string tailMid, string tailTip)
    {
        var d = new Dictionary<string, string>
        {
            { "Hips", "spine" },
            { "Neck03", "neck" },
            { "Head", "head" },
            { "LeftArm", "front_l_upper" }, { "LeftForeArm", "front_l_lower" }, { "LeftHand", "front_l_paw" },
            { "RightArm", "front_r_upper" }, { "RightForeArm", "front_r_lower" }, { "RightHand", "front_r_paw" },
            { "LeftUpLeg", "rear_l_upper" }, { "LeftLeg", "rear_l_lower" }, { "LeftFoot", "rear_l_paw" }, { "LeftFootToeBase", "rear_l_toe" },
            { "RightUpLeg", "rear_r_upper" }, { "RightLeg", "rear_r_lower" }, { "RightFoot", "rear_r_paw" }, { "RightFootToeBase", "rear_r_toe" },
        };
        if (tailBase != null) d[tailBase] = "tail_base";
        if (tailMid != null) d[tailMid] = "tail_mid";
        if (tailTip != null) d[tailTip] = "tail_tip";
        return d;
    }

    private static readonly Mapping[] RemainingMappings =
    {
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/06_AmericanMink.prefab", renames = LionStyleThumbFoot("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/07_Badger.prefab", renames = LegStyleFull("Neck2", "Tail1", "Tail2", "Tail3") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/08_Bear1.0.prefab", renames = RigStyleA("RigNeck2", "RigTail1", null, "RigTail2") },
        new Mapping
        {
            prefabPath = "Assets/Resources/Models/Animal/09_Bear2.0.prefab",
            renames = new Dictionary<string, string>
            {
                { "Pelvis", "spine" }, { "Neck2", "neck" }, { "Head", "head" },
                { "Tail1", "tail_base" }, { "Tail2", "tail_tip" },
                { "LegFL1", "front_l_upper" }, { "LegFL2", "front_l_lower" }, { "LegFL3", "front_l_paw" },
                { "LegFR1", "front_r_upper" }, { "LegFR2", "front_r_lower" }, { "LegFR3", "front_r_paw" },
                { "LegBL1", "rear_l_upper" }, { "LegBL2", "rear_l_lower" }, { "LegBLAnkle", "rear_l_paw" }, { "LegBLDigit11", "rear_l_toe" },
                { "LegBR1", "rear_r_upper" }, { "LegBR2", "rear_r_lower" }, { "LegBRAnkle", "rear_r_paw" }, { "LegBRDigit11", "rear_r_toe" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/10_BearWITHFUR.prefab", renames = RigStyleA("RigNeck2", "RigTail1", null, "RigTail2") },
        new Mapping
        {
            // Unique rig (Reallusion-style "butt_C0_x" chain hangs the tail off Hips, not a
            // separate Pelvis; rear foot has no separate toe joint).
            prefabPath = "Assets/Resources/Models/Animal/11_Beaver.prefab",
            renames = new Dictionary<string, string>
            {
                { "Hips", "spine" }, { "Neck02", "neck" }, { "Head", "head" },
                { "Tail01", "tail_base" }, { "Tail03", "tail_mid" }, { "Tail05", "tail_tip" },
                { "LeftArm", "front_l_upper" }, { "LeftForeArm", "front_l_lower" }, { "LeftHand", "front_l_paw" },
                { "RightArm", "front_r_upper" }, { "RightForeArm", "front_r_lower" }, { "RightHand", "front_r_paw" },
                { "LeftUpLeg", "rear_l_upper" }, { "LeftLeg", "rear_l_lower" }, { "LeftFoot", "rear_l_paw" },
                { "RightUpLeg", "rear_r_upper" }, { "RightLeg", "rear_r_lower" }, { "RightFoot", "rear_r_paw" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/12_BighornSheep.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/13_Bloodhund.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/14_BoarV2.prefab", renames = LegStyleFull("Neck2", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/15_Deer.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail06") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/17_Deer1.0.prefab", renames = RigStyleA("RigNeck3", "RigTail1", null, "RigTail2") },
        new Mapping
        {
            // Unique Reallusion/iClone-style ("RL_BoneRoot", l_femur/l_forearm) rig - lowest
            // confidence of this batch; verify visually before trusting front_l_paw=l_wrist.
            prefabPath = "Assets/Resources/Models/Animal/16_Deer1.prefab",
            renames = new Dictionary<string, string>
            {
                { "Hips", "spine" }, { "neck_1", "neck" }, { "head", "head" },
                { "tailio", "tail_base" },
                { "l_humer", "front_l_upper" }, { "l_elbow", "front_l_lower" }, { "l_wrist", "front_l_paw" },
                { "r_humer", "front_r_upper" }, { "r_elbow", "front_r_lower" }, { "r_wrist", "front_r_paw" },
                { "l_femur", "rear_l_upper" }, { "l_knee", "rear_l_lower" }, { "l_ankle", "rear_l_paw" }, { "l_ball", "rear_l_toe" },
                { "r_femur", "rear_r_upper" }, { "r_knee", "rear_r_lower" }, { "r_ankle", "rear_r_paw" }, { "r_ball", "rear_r_toe" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/18_DeerV2.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/19_Doe1.0.prefab", renames = RigStyleC("RigTail1", "RigTail2") },
        new Mapping
        {
            // Unique rig (Reallusion "SK_Donkey" naming: L_Hip/L_Back_UpperLeg/.../L_Back_Hoof,
            // L_Scapula/L_Shoulder/L_UpperLeg/.../L_Hoof).
            prefabPath = "Assets/Resources/Models/Animal/20_Donkey.prefab",
            renames = new Dictionary<string, string>
            {
                { "Pelvis", "spine" }, { "Neck", "neck" }, { "Head", "head" },
                { "Tail_01", "tail_base" }, { "Tail_02", "tail_mid" }, { "Tail_04", "tail_tip" },
                { "L_UpperLeg", "front_l_upper" }, { "L_LowerLeg", "front_l_lower" }, { "L_Ankle", "front_l_paw" },
                { "R_UpperLeg", "front_r_upper" }, { "R_LowerLeg", "front_r_lower" }, { "R_Ankle", "front_r_paw" },
                { "L_Back_UpperLeg", "rear_l_upper" }, { "L_Back_LowerLeg", "rear_l_lower" }, { "L_Back_Ankle", "rear_l_paw" }, { "L_Back_Hoof", "rear_l_toe" },
                { "R_Back_UpperLeg", "rear_r_upper" }, { "R_Back_LowerLeg", "rear_r_lower" }, { "R_Back_Ankle", "rear_r_paw" }, { "R_Back_Hoof", "rear_r_toe" },
            },
        },
        new Mapping
        {
            // Unique rig, but same shape family as Horse (L_Thigh/L_Calf/L_HorseLink/L_Foot,
            // no BN_ prefix).
            prefabPath = "Assets/Resources/Models/Animal/21_Donkey1.0.prefab",
            renames = new Dictionary<string, string>
            {
                { "Pelvis", "spine" }, { "Neck2", "neck" }, { "Head", "head" },
                { "Tail01", "tail_base" }, { "Tail03", "tail_mid" }, { "Tail05", "tail_tip" },
                { "L_UpperArm", "front_l_upper" }, { "L_Forearm", "front_l_lower" }, { "L_Hand", "front_l_paw" },
                { "R_UpperArm", "front_r_upper" }, { "R_Forearm", "front_r_lower" }, { "R_Hand", "front_r_paw" },
                { "L_Thigh", "rear_l_upper" }, { "L_Calf", "rear_l_lower" }, { "L_HorseLink", "rear_l_paw" }, { "L_Foot", "rear_l_toe" },
                { "R_Thigh", "rear_r_upper" }, { "R_Calf", "rear_r_lower" }, { "R_HorseLink", "rear_r_paw" }, { "R_Foot", "rear_r_toe" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/22_Elk1.0.prefab", renames = RigStyleC("RigTail1", "RigTail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/23_EuropeanBadger.prefab", renames = LionStyleThumbFoot("Tail01", "Tail02", "Tail04") },
        new Mapping
        {
            // Same front/rear/spine shape as LionStyleFull, but 4 neck segments (Neck04, not
            // Neck03) and 8 tail segments.
            prefabPath = "Assets/Resources/Models/Animal/24_Fox.prefab",
            renames = new Dictionary<string, string>
            {
                { "Hips", "spine" }, { "Neck04", "neck" }, { "Head", "head" },
                { "Tail01", "tail_base" }, { "Tail05", "tail_mid" }, { "Tail08", "tail_tip" },
                { "LeftArm", "front_l_upper" }, { "LeftForeArm", "front_l_lower" }, { "LeftHand", "front_l_paw" },
                { "RightArm", "front_r_upper" }, { "RightForeArm", "front_r_lower" }, { "RightHand", "front_r_paw" },
                { "LeftUpLeg", "rear_l_upper" }, { "LeftLeg", "rear_l_lower" }, { "LeftFoot", "rear_l_paw" }, { "LeftToeBase", "rear_l_toe" },
                { "RightUpLeg", "rear_r_upper" }, { "RightLeg", "rear_r_lower" }, { "RightFoot", "rear_r_paw" }, { "RightToeBase", "rear_r_toe" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/25_FoxV2.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/26_FoxWITHFUR.prefab", renames = RigStyleA("RigNeck2", "RigTail1", "RigTail3", "RigTail5") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/28_Goat.prefab", renames = LionStyleFull("Tail01", "Tail02", "Tail04") },
        new Mapping
        {
            // Unique rig (Reallusion "SK_Goat": hub is Spine_01 itself, not a separate Pelvis;
            // rear leg L_Hip/L_UpperLeg/L_LowerLeg/L_Hoof exactly fills all 4 rear slots).
            prefabPath = "Assets/Resources/Models/Animal/29_Goat1.prefab",
            renames = new Dictionary<string, string>
            {
                { "Spine_01", "spine" }, { "Neck_03", "neck" }, { "Head", "head" },
                { "Tail_01", "tail_base" }, { "Tail_02", "tail_tip" },
                { "L_Front_UpperLeg", "front_l_upper" }, { "L_Front_LowerLeg", "front_l_lower" }, { "L_Front_Hoof", "front_l_paw" },
                { "R_Front_UpperLeg", "front_r_upper" }, { "R_Front_LowerLeg", "front_r_lower" }, { "R_Front_Hoof", "front_r_paw" },
                { "L_Hip", "rear_l_upper" }, { "L_UpperLeg", "rear_l_lower" }, { "L_LowerLeg", "rear_l_paw" }, { "L_Hoof", "rear_l_toe" },
                { "R_Hip", "rear_r_upper" }, { "R_UpperLeg", "rear_r_lower" }, { "R_LowerLeg", "rear_r_paw" }, { "R_Hoof", "rear_r_toe" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/31_GrayWolf.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/33_Hare.prefab", renames = LionStyleFull("Tail01", "Tail02", "Tail03") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/34_Hyena.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/36_LabradorDog.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/37_Lioness.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/38_LionessV2.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail4", "Tail6") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/39_Lynx.prefab", renames = LionStyleFull("Tail01", "Tail02", "Tail04") },
        new Mapping
        {
            // Unique rig, lowercase c_/l_/r_ naming, no distinct neck bone at all (c_back5
            // connects straight to c_head) - c_back5 reused as "neck" for at least some flex.
            // Lowest confidence entry in this batch; verify visually.
            prefabPath = "Assets/Resources/Models/Animal/40_Mammoth.prefab",
            renames = new Dictionary<string, string>
            {
                { "c_back1", "spine" }, { "c_back5", "neck" }, { "c_head", "head" },
                { "c_tail1", "tail_base" }, { "c_tail2", "tail_mid" }, { "c_tail4", "tail_tip" },
                { "l_shoulder", "front_l_upper" }, { "l_elbow", "front_l_lower" }, { "l_wrist", "front_l_paw" },
                { "r_shoulder", "front_r_upper" }, { "r_elbow", "front_r_lower" }, { "r_wrist", "front_r_paw" },
                { "l_hip", "rear_l_upper" }, { "l_knee", "rear_l_lower" }, { "l_ankle", "rear_l_paw" },
                { "r_hip", "rear_r_upper" }, { "r_knee", "rear_r_lower" }, { "r_ankle", "rear_r_paw" },
            },
        },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/41_Moose.prefab", renames = LionStyleFull("Tail01", null, "Tail02") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/42_Moose1.0.prefab", renames = RigStyleC("RigTail1", "RigTail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/43_MooseF.prefab", renames = LegStyleFull("Neck3", "Tail1", null, "Tail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/44_MooseM.prefab", renames = LegStyleFull("Neck3", "Tail1", null, "Tail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/45_MountainGoat.prefab", renames = LegStyleFull("Neck3", "Tail1", "Tail2", "Tail4") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/47_Pronghorn1.0.prefab", renames = RigStyleC("RigTail1", "RigTail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/48_Puma.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/49_Rabbit.prefab", renames = LegStyleFull("Neck1", "Tail1", null, "Tail2") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/50_Racoon.prefab", renames = LionStyleFull("Tail01", "Tail04", "Tail07") },
        new Mapping { prefabPath = "Assets/Resources/Models/Animal/51_Warthog.prefab", renames = LegStyleFull("Neck2", "Tail1", "Tail2", "Tail4") },
    };

    [MenuItem("Tools/VisionGraft/Rename Animal Bones To Canonical (Priority Species)")]
    public static void RenameAll()
    {
        foreach (var mapping in Mappings)
        {
            RenameOne(mapping);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[AnimalRigBoneRenamer] Done.");
    }

    // Birds (Goose/Guineafowl/Pheasant) and Kangaroo are intentionally excluded from
    // RemainingMappings - their real topology is bipedal/winged, not the SMAL 4-leg
    // shape, the same reason Velociraptor is out of scope (see animal-model-roadmap memory).
    [MenuItem("Tools/VisionGraft/Rename Remaining Animal Bones To Canonical")]
    public static void RenameRemaining()
    {
        foreach (var mapping in RemainingMappings)
        {
            RenameOne(mapping);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[AnimalRigBoneRenamer] Remaining batch done.");
    }

    private static void RenameOne(Mapping mapping)
    {
        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(mapping.prefabPath);
        if (prefabAsset == null)
        {
            Debug.LogError("[AnimalRigBoneRenamer] Prefab not found: " + mapping.prefabPath);
            return;
        }

        // Instantiated into whatever scene is currently active (the default untitled
        // scene in batch mode, or the user's open scene when run via the Editor menu)
        // and destroyed again immediately below - never saved as part of any scene.
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        var remaining = new HashSet<string>(mapping.renames.Keys);
        ApplyRenames(instance.transform, mapping.renames, remaining);

        if (remaining.Count > 0)
        {
            Debug.LogWarning("[AnimalRigBoneRenamer] " + mapping.prefabPath +
                " - bones not found (skipped): " + string.Join(", ", remaining));
        }

        PrefabUtility.SaveAsPrefabAsset(instance, mapping.prefabPath);
        Object.DestroyImmediate(instance);

        Debug.Log("[AnimalRigBoneRenamer] Renamed " + mapping.prefabPath);
    }

    private static void ApplyRenames(Transform t, Dictionary<string, string> renames, HashSet<string> remaining)
    {
        string originalName = t.name;
        if (renames.TryGetValue(originalName, out string canonical))
        {
            t.name = canonical;
            remaining.Remove(originalName);
        }
        for (int i = 0; i < t.childCount; i++)
        {
            ApplyRenames(t.GetChild(i), renames, remaining);
        }
    }
}
