public static class AnimalSmalFkPolicy
{
    // Tail1/Tail2 (SMAL joints 25/26 = tailBase/tailMid) now have a registered geometric
    // correction (AnimalSmalFkApplier.SmalRestDirByJoint) and drive real body_pose motion.
    // Tail3+ (27-31 = tailTip and beyond) stay bind-pose-only: joint 27's own SMAL child
    // (28) has no corresponding Unity bone in the canonical rig, and 28-31 have no bone at
    // all (BONE MISSING virtual joints past what the rig tracks).
    public static bool ShouldKeepBindPoseForJoint(int smalJoint)
    {
        return smalJoint >= 27 && smalJoint <= 31;
    }
}
