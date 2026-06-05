public enum SpatialVideoBundleEntryRequirement
{
    Required,
    Optional
}

public static class SpatialVideoBundleEntries
{
    public static bool ShouldContinueAfterExtraction(
        SpatialVideoBundleEntryRequirement requirement,
        bool extracted)
    {
        return extracted || requirement == SpatialVideoBundleEntryRequirement.Optional;
    }
}
