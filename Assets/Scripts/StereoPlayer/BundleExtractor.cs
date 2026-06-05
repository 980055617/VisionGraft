using System.IO;
using System.IO.Compression;

public static class BundleExtractor
{
    public static bool Extract(ZipArchive archive, string entryName, string outputPath)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName);
        if (entry == null)
        {
            return false;
        }

        using (Stream entryStream = entry.Open())
        using (var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            entryStream.CopyTo(outStream);
        }
        return true;
    }

    public static bool ExtractWithRequirement(ZipArchive archive, string entryName, string outputPath, SpatialVideoBundleEntryRequirement requirement)
    {
        bool extracted = Extract(archive, entryName, outputPath);
        return SpatialVideoBundleEntries.ShouldContinueAfterExtraction(requirement, extracted);
    }
}
