public static class AnimalAimDirectionPolicy
{
    public static bool ShouldUseChildPivotDirection(bool hasRegisteredAimChild, bool isLimbBone)
    {
        return hasRegisteredAimChild || isLimbBone;
    }
}
