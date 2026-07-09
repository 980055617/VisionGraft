// All animal prefabs in Resources/Models/Animal/ use the canonical bone names below.
// See CONTEXT.md "Canonical animal rig bone names" and docs/adr/0001-canonical-animal-rig-bone-names.md.
public static class AnimalRigDefinition
{
    public static readonly AnimalBoneRule Spine = new AnimalBoneRule(
        new[] { "spine" },
        new[] { "spine", "body", "chest", "back" });

    public static readonly AnimalBoneRule Neck = new AnimalBoneRule(
        new[] { "neck" },
        new[] { "neck" });

    public static readonly AnimalBoneRule Head = new AnimalBoneRule(
        new[] { "head" },
        new[] { "head" });

    // FindBoneByTokens is called directly for tailBase — "tail_base" exact gets a score
    // bonus because token == name, so it wins over any bone that merely contains "tail".
    public static readonly string[] TailBaseTokens = { "tail_base", "tail" };

    public static readonly AnimalBoneRule TailMid = new AnimalBoneRule(
        new[] { "tail_mid" },
        new[] { "tail_mid" });

    public static readonly AnimalBoneRule TailTip = new AnimalBoneRule(
        new[] { "tail_tip" },
        new[] { "tail_tip" });

    public static readonly AnimalBoneRule LeftFrontUpper = new AnimalBoneRule(
        new[] { "front_l_upper" },
        new string[0]);

    public static readonly AnimalBoneRule LeftFrontLower = new AnimalBoneRule(
        new[] { "front_l_lower" },
        new string[0]);

    public static readonly AnimalBoneRule LeftFrontPaw = new AnimalBoneRule(
        new[] { "front_l_paw" },
        new string[0]);

    public static readonly AnimalBoneRule RightFrontUpper = new AnimalBoneRule(
        new[] { "front_r_upper" },
        new string[0]);

    public static readonly AnimalBoneRule RightFrontLower = new AnimalBoneRule(
        new[] { "front_r_lower" },
        new string[0]);

    public static readonly AnimalBoneRule RightFrontPaw = new AnimalBoneRule(
        new[] { "front_r_paw" },
        new string[0]);

    public static readonly AnimalBoneRule LeftRearUpper = new AnimalBoneRule(
        new[] { "rear_l_upper" },
        new string[0]);

    public static readonly AnimalBoneRule LeftRearLower = new AnimalBoneRule(
        new[] { "rear_l_lower" },
        new string[0]);

    public static readonly AnimalBoneRule LeftRearPaw = new AnimalBoneRule(
        new[] { "rear_l_paw" },
        new string[0]);

    public static readonly AnimalBoneRule LeftRearToe = new AnimalBoneRule(
        new[] { "rear_l_toe" },
        new string[0]);

    public static readonly AnimalBoneRule RightRearUpper = new AnimalBoneRule(
        new[] { "rear_r_upper" },
        new string[0]);

    public static readonly AnimalBoneRule RightRearLower = new AnimalBoneRule(
        new[] { "rear_r_lower" },
        new string[0]);

    public static readonly AnimalBoneRule RightRearPaw = new AnimalBoneRule(
        new[] { "rear_r_paw" },
        new string[0]);

    public static readonly AnimalBoneRule RightRearToe = new AnimalBoneRule(
        new[] { "rear_r_toe" },
        new string[0]);
}

public readonly struct AnimalBoneRule
{
    public readonly string[] exactNames;
    public readonly string[] tokens;

    public AnimalBoneRule(string[] exactNamesValue, string[] tokensValue)
    {
        exactNames = exactNamesValue;
        tokens = tokensValue;
    }
}
