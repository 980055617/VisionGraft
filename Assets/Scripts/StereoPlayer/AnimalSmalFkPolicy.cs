public static class AnimalSmalFkPolicy
{
    public static bool ShouldKeepBindPoseForJoint(int smalJoint)
    {
        return smalJoint >= 25 && smalJoint <= 31;
    }
}
