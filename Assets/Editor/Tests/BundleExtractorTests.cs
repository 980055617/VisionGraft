using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;

public class BundleExtractorTests
{
    private string tempPath;

    [SetUp]
    public void SetUp()
    {
        tempPath = Path.GetTempFileName();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    private static ZipArchive CreateZipWithEntry(string entryName, byte[] content)
    {
        var ms = new MemoryStream();
        using (var writer = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = writer.CreateEntry(entryName);
            using Stream stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    [Test]
    public void Extract_WhenEntryExists_WritesFileAndReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello");
        using ZipArchive za = CreateZipWithEntry("data.bin", content);

        bool result = BundleExtractor.Extract(za, "data.bin", tempPath);

        Assert.That(result, Is.True);
        Assert.That(File.ReadAllBytes(tempPath), Is.EqualTo(content));
    }

    [Test]
    public void Extract_WhenEntryMissing_ReturnsFalse()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello");
        using ZipArchive za = CreateZipWithEntry("other.bin", content);

        bool result = BundleExtractor.Extract(za, "missing.bin", tempPath);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ExtractWithRequirement_Required_EntryPresent_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("data");
        using ZipArchive za = CreateZipWithEntry("file.bin", content);

        bool result = BundleExtractor.ExtractWithRequirement(za, "file.bin", tempPath, SpatialVideoBundleEntryRequirement.Required);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ExtractWithRequirement_Required_EntryMissing_ReturnsFalse()
    {
        byte[] content = Encoding.UTF8.GetBytes("data");
        using ZipArchive za = CreateZipWithEntry("other.bin", content);

        bool result = BundleExtractor.ExtractWithRequirement(za, "missing.bin", tempPath, SpatialVideoBundleEntryRequirement.Required);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ExtractWithRequirement_Optional_EntryMissing_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("data");
        using ZipArchive za = CreateZipWithEntry("other.bin", content);

        bool result = BundleExtractor.ExtractWithRequirement(za, "missing.bin", tempPath, SpatialVideoBundleEntryRequirement.Optional);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ExtractWithRequirement_Optional_EntryPresent_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("data");
        using ZipArchive za = CreateZipWithEntry("file.bin", content);

        bool result = BundleExtractor.ExtractWithRequirement(za, "file.bin", tempPath, SpatialVideoBundleEntryRequirement.Optional);

        Assert.That(result, Is.True);
    }
}
