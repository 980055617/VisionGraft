public static class AnimalRigDefinition
{
    public static readonly AnimalBoneRule Neck = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.007", "neck", "DEF-spine.010" },
        new[] { "neck" });

    public static readonly AnimalBoneRule Head = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.009", "head.001", "DEF-spine.011" },
        new[] { "head" });

    public static readonly AnimalBoneRule Spine = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3", "body" },
        new[] { "body", "spine", "chest", "back" });

    public static readonly string[] TailBaseTokens = { "tail.002", "tail" };

    public static readonly AnimalBoneRule LeftFrontUpper = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_L.001", "arm.001.L", "DEF-front_thigh.L" },
        new[] { "arm.001.l", "def-front_thigh.l" });

    public static readonly AnimalBoneRule LeftFrontLower = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_L.002", "arm.002.L", "DEF-front_shin.L" },
        new[] { "arm.002.l", "def-front_shin.l" });

    public static readonly AnimalBoneRule LeftFrontPaw = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_L.003", "arm.003.L", "DEF-front_foot.L" },
        new[] { "arm.003.l", "def-front_foot.l", "def-front_toe.l" });

    public static readonly AnimalBoneRule RightFrontUpper = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_R.001", "arm.001.R", "DEF-front_thigh.R" },
        new[] { "arm.001.r", "def-front_thigh.r" });

    public static readonly AnimalBoneRule RightFrontLower = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_R.002", "arm.002.R", "DEF-front_shin.R" },
        new[] { "arm.002.r", "def-front_shin.r" });

    public static readonly AnimalBoneRule RightFrontPaw = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3_R.003", "arm.003.R", "DEF-front_foot.R" },
        new[] { "arm.003.r", "def-front_foot.r", "def-front_toe.r" });

    public static readonly AnimalBoneRule LeftRearUpper = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_L.001", "foot.001.L", "DEF-thigh.L" },
        new[] { "foot.001.l", "foot.002.l", "def-thigh.l" });

    public static readonly AnimalBoneRule LeftRearLower = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_L.002", "foot.002.L", "DEF-shin.L" },
        new[] { "foot.002.l", "foot.003.l", "def-shin.l" });

    public static readonly AnimalBoneRule LeftRearPaw = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_L.003", "foot.003.L", "DEF-foot.L" },
        new[] { "foot.003.l", "foot.004.l", "def-foot.l", "def-toe.l" });

    public static readonly AnimalBoneRule RightRearUpper = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_R.001", "foot.001.R", "DEF-thigh.R" },
        new[] { "foot.001.r", "foot.002.r", "def-thigh.r" });

    public static readonly AnimalBoneRule RightRearLower = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_R.002", "foot.002.R", "DEF-shin.R" },
        new[] { "foot.002.r", "foot.003.r", "def-shin.r" });

    public static readonly AnimalBoneRule RightRearPaw = new AnimalBoneRule(
        new[] { "\u30dc\u30fc\u30f3.001_R.003", "foot.003.R", "DEF-foot.R" },
        new[] { "foot.003.r", "foot.004.r", "def-foot.r", "def-toe.r" });
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
