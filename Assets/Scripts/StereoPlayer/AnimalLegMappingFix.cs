using System.Collections.Generic;

// 脚の骨の割り当てを、解剖学的な骨に直す表（2026-10-04、第 3 ラウンドの調査役 MAP の監査）。末尾に尾の役を直す行（GermanShepherd）もある。
//
// 動物モデルのリネーム（Assets/Editor/AnimalRigBoneRenamer.cs）で、21 体は脚の骨が 1 本下にずれていた（上腕・大腿が一度も
// 駆動されず、後脚は走りで元動画と前後逆に振れる）。判定は骨の矢状面の傾き・球節の高さ・区間長で、対照の 00_Dog・
// 27_GermanShepherd を正しく「OK」と判定することを確かめてある。36_LabradorDog の行は 10/03 の LabradorLegMappingFixSpec と同じ。
//
// **一般則（1 本上へ）では書けない**ので、モデルごとに骨名を明示する: Beaver・Fox は 1 本上が Roll の骨（上腕の途中）、
// 受け身の骨（肉球・つま先）は「SMAL の親関節の回転 × Unity の親に対する局所」で置くので、親が食い違うとその分ねじれる。
// 値は AnimalBoneMappingOverride のフィールド名 = 骨名 の組（;区切り）。
//
// キーは prefab 名から先頭の "数字_" を外した名前で、**完全一致**で引く（番号は振り直されることがある。部分一致だと
// "Lion" が "Lioness"・"LionessV2"、"Deer" が "DeerV2"、"Goat" が "MountainGoat"、"Moose" が "MooseF" にも当たる）。
//
// 前肢の副軸（animalFrontLimbBodyLateralSecondary）と組で使う。割り当てだけを直すと、首を副軸にした 2 軸の写像が
// Moose・Goat・Mink・Fox の前肢上で前後逆になり、Lynx・Racoon・EuropeanBadger では縮退する（MAP の map10）。
public static class AnimalLegMappingFix
{
    // 前肢の副軸を体の横にする F2（animalFrontLimbBodyLateralSecondary）を使わないモデル（キーは Key() と同じ）。2026-10-09 から空。
    // 16_Deer1 は 2026-10-04 に F2 で前肢上の θ（矢状面の角の相関）が悪くなった（+0.22〜+0.89 → −0.04〜−0.66、R6c / R7）として入れていたが、
    // その測定は背骨の役が骨盤（Hips）に付いて胴が止まったダンプで取られていた。背骨の役を body に直す（下の Table の行）と、F2 込みで
    // 動物 52 体の一覧の確認の全項目が合格した（不合格 54 → 0、Docs/tmp/roster_20261009/README.md の 7-2）。ユーザーが絵を見て採用（2026-10-09）。
    public static readonly HashSet<string> FrontLimbBodyLateralExcluded = new HashSet<string>(System.StringComparer.Ordinal)
    {
    };

    // prefab 名から先頭の「数字_」を外す（"36_LabradorDog" → "LabradorDog"）。数字が無い、または数字の後が "_" でなければそのまま。
    public static string Key(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
        {
            return string.Empty;
        }

        int i = 0;
        while (i < prefabName.Length && prefabName[i] >= '0' && prefabName[i] <= '9')
        {
            i++;
        }

        return i > 0 && i < prefabName.Length && prefabName[i] == '_' ? prefabName.Substring(i + 1) : prefabName;
    }

    // AnimalBoneMappingOverride のフィールドへの代入。**反射（GetField / SetValue）を使わない**（2026-10-04、査読の指摘 C:
    // 反射は Quest（IL2CPP）で初めて走る経路で、ストリップされると例外も出ずに割り当てだけ黙って効かなくなる。実機で確かめられないので無くした）。
    public static bool IsOverrideKey(string key)
    {
        return TrySetOverrideField(null, key, null);
    }

    // ov が null なら名前の確認だけ。知らないキーなら false。
    public static bool TrySetOverrideField(AnimalBoneMappingOverride ov, string key, string value)
    {
        switch (key)
        {
            case "spine": if (ov != null) { ov.spine = value; } return true;
            case "neck": if (ov != null) { ov.neck = value; } return true;
            case "head": if (ov != null) { ov.head = value; } return true;
            case "tailBase": if (ov != null) { ov.tailBase = value; } return true;
            case "tailMid": if (ov != null) { ov.tailMid = value; } return true;
            case "tailTip": if (ov != null) { ov.tailTip = value; } return true;
            case "frontLUpper": if (ov != null) { ov.frontLUpper = value; } return true;
            case "frontLLower": if (ov != null) { ov.frontLLower = value; } return true;
            case "frontLPaw": if (ov != null) { ov.frontLPaw = value; } return true;
            case "frontRUpper": if (ov != null) { ov.frontRUpper = value; } return true;
            case "frontRLower": if (ov != null) { ov.frontRLower = value; } return true;
            case "frontRPaw": if (ov != null) { ov.frontRPaw = value; } return true;
            case "rearLUpper": if (ov != null) { ov.rearLUpper = value; } return true;
            case "rearLLower": if (ov != null) { ov.rearLLower = value; } return true;
            case "rearLPaw": if (ov != null) { ov.rearLPaw = value; } return true;
            case "rearLToe": if (ov != null) { ov.rearLToe = value; } return true;
            case "rearRUpper": if (ov != null) { ov.rearRUpper = value; } return true;
            case "rearRLower": if (ov != null) { ov.rearRLower = value; } return true;
            case "rearRPaw": if (ov != null) { ov.rearRPaw = value; } return true;
            case "rearRToe": if (ov != null) { ov.rearRToe = value; } return true;
            default: return false;
        }
    }

    private const string Lion =
        "frontLUpper=LeftShoulder02;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
        "frontRUpper=RightShoulder02;frontRLower=front_r_upper;frontRPaw=front_r_lower;" +
        "rearLUpper=LeftPelvis;rearLLower=rear_l_upper;rearLPaw=rear_l_lower;rearLToe=rear_l_paw;" +
        "rearRUpper=RightPelvis;rearRLower=rear_r_upper;rearRPaw=rear_r_lower;rearRToe=rear_r_paw";

    // Labrador だけ階層が 1 段浅い（上腕 = LeftShoulder01）。
    private const string LabradorDog =
        "frontLUpper=LeftShoulder01;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
        "frontRUpper=RightShoulder01;frontRLower=front_r_upper;frontRPaw=front_r_lower;" +
        "rearLUpper=LeftPelvis;rearLLower=rear_l_upper;rearLPaw=rear_l_lower;rearLToe=rear_l_paw;" +
        "rearRUpper=RightPelvis;rearRLower=rear_r_upper;rearRPaw=rear_r_lower;rearRToe=rear_r_paw";

    // Roll の骨あり: paw = lower の直下の Roll 骨、toe = その子（受け身の骨の親を一致させる。省くと Fox は正規名 rear_l_toe が
    // 拾われて親が違い 120〜131° 回る。Beaver の LeftLegRoll は bind の局所回転が 143°）。
    private const string RollStyle =
        "frontLUpper=LeftShoulder02;frontLLower=front_l_upper;frontLPaw=LeftArmRoll;" +
        "frontRUpper=RightShoulder02;frontRLower=front_r_upper;frontRPaw=RightArmRoll;" +
        "rearLUpper=LeftPelvis;rearLLower=rear_l_upper;rearLPaw=LeftUpLegRoll;rearLToe=rear_l_lower;" +
        "rearRUpper=RightPelvis;rearRLower=rear_r_upper;rearRPaw=RightUpLegRoll;rearRToe=rear_r_lower";

    public static readonly Dictionary<string, string> Table = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        // LionStyleFull（上腕 = LeftShoulder02）
        { "Bloodhund", Lion }, { "Deer", Lion }, { "Goat", Lion }, { "GrayWolf", Lion }, { "Hare", Lion }, { "Lioness", Lion },
        { "Lynx", Lion }, { "Moose", Lion }, { "Puma", Lion }, { "Racoon", Lion },
        { "LabradorDog", LabradorDog },
        // LionStyleThumbFoot・個別（同じ命名）
        { "AmericanMink", Lion }, { "EuropeanBadger", Lion }, { "WildBoar", Lion }, { "Lion", Lion },
        { "Beaver", RollStyle }, { "Fox", RollStyle },
        {
            "Donkey",
            "frontLUpper=L_Shoulder;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
            "frontRUpper=R_Shoulder;frontRLower=front_r_upper;frontRPaw=front_r_lower;" +
            "rearLUpper=L_Hip;rearLLower=rear_l_upper;rearLPaw=rear_l_lower;rearLToe=rear_l_paw;" +
            "rearRUpper=R_Hip;rearRLower=rear_r_upper;rearRPaw=rear_r_lower;rearRToe=rear_r_paw"
        },
        // 前肢だけ 1 本下
        {
            "Horse",
            "frontLUpper=BN_L_Clavicle_038;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
            "frontRUpper=BN_R_Clavicle_043;frontRLower=front_r_upper;frontRPaw=front_r_lower"
        },
        // 21_Donkey1.0（前肢を L_Clavicle から 1 本上へ）は、監査の決め手が球節の高さの一致だけで（矢状面の傾きは ±2° で引き分け）、
        // 表でいちばん確度が低い。絵で確かめるまで入れない（反論役 DEC、2026-10-04）。試すときはバッチの -animalBoneOverride で
        // "21_Donkey1.0|frontLUpper=L_Clavicle;frontLLower=front_l_upper;frontLPaw=front_l_lower;frontRUpper=R_Clavicle;frontRLower=front_r_upper;frontRPaw=front_r_lower"。
        {
            "Goat1",
            "frontLUpper=L_Shoulder;frontLLower=front_l_upper;frontLPaw=front_l_lower;" +
            "frontRUpper=R_Shoulder;frontRLower=front_r_upper;frontRPaw=front_r_lower"
        },
        // 肉球・つま先だけ（上の 2 本は正しい）
        { "Deer1.0", "frontLPaw=RigLFLeg3;frontRPaw=RigRFLeg3;rearLPaw=RigLBLeg3;rearRPaw=RigRBLeg3;rearLToe=rear_l_paw;rearRToe=rear_r_paw" },
        // 二足だが prefab の上書き（AnimalBoneMappingOverride の上腕・大腿）で SMAL FK の入口を通り、SMAL FK で動いている（2026-10-08 に訂正。以前ここに
        // 「SMAL の対象外」と書いていたのは誤り: bk0_kangaroo.log に rootYawFix decided・MODEL spineName=Spine）。Change Model で選べる。toe の親の食い違い（171° / 91°）だけ直す。
        // 腰（Hips を誰も書かずに残るねじれ）と尾は、非四足モード（AnimalNonQuadrupedRig、2026-10-09 に既定 ON）で直す。
        { "Kangaroo", "rearLToe=LeftFootBase;rearRToe=RightFootBase" },
        // 尾（脚ではない。2026-10-04、S1 のダンプ）: 27_GermanShepherd は役が見つからないときの DEF-spine のフォールバック
        // （AnimalPoseApplier、全モデル共通の animalModelForwardLocal で背骨を前後に並べて最後尾を tailBase にする）が、このリグでは
        // 前後を逆に取り、首の手前の DEF-spine.009 を尾の付け根にしていた（尾のデータで首の付け根が bind から p95 42° 振られ、
        // 本物の尾 DEF-spine.003 → .002 → .001 → spine は動かなかった）。spine は今と同じ DEF-spine.004 を必ず明示する:
        // tailBase が見つかると AnimalDefSpineFallbackPolicy が DEF-spine の並びで spine を置き換えなくなり、
        // "spine" という名前の骨（このリグでは尾の先）が背骨の役になる。
        { "GermanShepherd", "spine=DEF-spine.004;tailBase=DEF-spine.003;tailMid=DEF-spine.002;tailTip=DEF-spine.001" },
        // 背骨（脚ではない。2026-10-09、動物 52 体の一覧の確認）: 16_Deer1 は背骨の役が体全体の根（body）でなく骨盤（Hips）に付いていた
        // （AnimalRigBoneRenamer の Hips→spine）。SMAL FK は体の向きを背骨の役にだけ書くので、後ろ半分だけ回り、胸・肩・首の付け根は prefab の
        // 向きに止まっていた（体の向き・左右の後脚・関節の反転・伏せで頭が折れる、はどれもこれ）。body に付け替え、上の F2 の除外も外した。
        // バッチの -animalBoneOverride "16_Deer1|spine=body" で撮って全項目合格（Docs/tmp/roster_20261009/README.md の 7-2）、ユーザーが採用。
        { "Deer1", "spine=body" },
    };
}
