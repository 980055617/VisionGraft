public static class AnimalDefSpineFallbackPolicy
{
    public static bool ShouldReplaceCanonicalSpineWithDefSpineChain(bool hasTailBase, int defSpineBoneCount)
    {
        return !hasTailBase && defSpineBoneCount >= 4;
    }
}
