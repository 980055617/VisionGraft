using System.Collections.Generic;
using System.Text.RegularExpressions;

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
    // 前肢の副軸を体の横にする F2（animalFrontLimbBodyLateralSecondary）を使わないモデル（キーは Key() と同じ）。
    // 16_Deer1（Reallusion 系のリグ）は F2 で前肢上の θ（矢状面の角の相関）が今より悪くなった（今 +0.22〜+0.89 → F2 −0.04〜−0.66、
    // 2026-10-04 の R6c / R7。MAP の移植は逆の予測で、このリグでは移植の前提が合わない）。後肢は F2 に関係なくばらつく（別の問題、未調査）。
    public static readonly HashSet<string> FrontLimbBodyLateralExcluded = new HashSet<string>(System.StringComparer.Ordinal)
    {
        "Deer1",
    };

    public static string Key(string prefabName)
    {
        return string.IsNullOrEmpty(prefabName) ? string.Empty : Regex.Replace(prefabName, @"^\d+_", string.Empty);
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
        // 二足（SMAL の対象外）だが Change Model で選べる。toe の親の食い違い（171° / 91°）だけ直す
        { "Kangaroo", "rearLToe=LeftFootBase;rearRToe=RightFootBase" },
        // 尾（脚ではない。2026-10-04、S1 のダンプ）: 27_GermanShepherd は役が見つからないときの DEF-spine のフォールバック
        // （AnimalPoseApplier、全モデル共通の animalModelForwardLocal で背骨を前後に並べて最後尾を tailBase にする）が、このリグでは
        // 前後を逆に取り、首の手前の DEF-spine.009 を尾の付け根にしていた（尾のデータで首の付け根が bind から p95 42° 振られ、
        // 本物の尾 DEF-spine.003 → .002 → .001 → spine は動かなかった）。spine は今と同じ DEF-spine.004 を必ず明示する:
        // tailBase が見つかると AnimalDefSpineFallbackPolicy が DEF-spine の並びで spine を置き換えなくなり、
        // "spine" という名前の骨（このリグでは尾の先）が背骨の役になる。
        { "GermanShepherd", "spine=DEF-spine.004;tailBase=DEF-spine.003;tailMid=DEF-spine.002;tailTip=DEF-spine.001" },
    };
}
