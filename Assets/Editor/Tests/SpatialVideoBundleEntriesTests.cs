using NUnit.Framework;

public class SpatialVideoBundleEntriesTests
{
    [Test]
    public void ShouldContinueAfterExtractionRejectsMissingRequiredEntry()
    {
        Assert.That(
            SpatialVideoBundleEntries.ShouldContinueAfterExtraction(
                SpatialVideoBundleEntryRequirement.Required,
                false),
            Is.False);
    }

    [Test]
    public void ShouldContinueAfterExtractionAcceptsMissingOptionalEntry()
    {
        Assert.That(
            SpatialVideoBundleEntries.ShouldContinueAfterExtraction(
                SpatialVideoBundleEntryRequirement.Optional,
                false),
            Is.True);
    }

    [Test]
    public void ShouldContinueAfterExtractionAcceptsExtractedEntries()
    {
        Assert.That(
            SpatialVideoBundleEntries.ShouldContinueAfterExtraction(
                SpatialVideoBundleEntryRequirement.Required,
                true),
            Is.True);

        Assert.That(
            SpatialVideoBundleEntries.ShouldContinueAfterExtraction(
                SpatialVideoBundleEntryRequirement.Optional,
                true),
            Is.True);
    }
}
