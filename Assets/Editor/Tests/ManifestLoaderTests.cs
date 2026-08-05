using System.IO;
using NUnit.Framework;

public class ManifestLoaderTests
{
    private string tempFile;

    [SetUp]
    public void SetUp()
    {
        tempFile = Path.GetTempFileName();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }
    }

    private const string ValidJson =
        "{\"quant_pos_scale\":0.001,\"quant_joint_scale\":0.002,\"joints_space\":\"camera_xyz_root_relative\"}";

    [Test]
    public void TryLoad_ValidManifest_ReturnsTrueAndPopulatesData()
    {
        File.WriteAllText(tempFile, ValidJson);

        bool result = ManifestLoader.TryLoad(tempFile, out ManifestData manifest);

        Assert.That(result, Is.True);
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest.quant_pos_scale, Is.EqualTo(0.001f).Within(0.000001f));
        Assert.That(manifest.quant_joint_scale, Is.EqualTo(0.002f).Within(0.000001f));
    }

    [Test]
    public void TryLoad_ManifestWithShots_PopulatesShotBoundaries()
    {
        File.WriteAllText(tempFile,
            "{\"quant_pos_scale\":0.001,\"quant_joint_scale\":0.002," +
            "\"joints_space\":\"camera_xyz_root_relative\",\"shots\":[[0,258],[258,338]]}");

        bool result = ManifestLoader.TryLoad(tempFile, out _, out ShotBoundaries shots);

        Assert.That(result, Is.True);
        Assert.That(shots.Count, Is.EqualTo(2));
        Assert.That(shots.ResolveShotIndex(257), Is.EqualTo(0));
        Assert.That(shots.ResolveShotIndex(258), Is.EqualTo(1));
    }

    // shots を持たない旧 bundle でもロード自体は成功し、全編 1 shot 扱いになる。
    [Test]
    public void TryLoad_ManifestWithoutShots_ReturnsEmptyShotBoundaries()
    {
        File.WriteAllText(tempFile, ValidJson);

        bool result = ManifestLoader.TryLoad(tempFile, out _, out ShotBoundaries shots);

        Assert.That(result, Is.True);
        Assert.That(shots.HasShots, Is.False);
    }

    [Test]
    public void TryLoad_InvalidManifest_ReturnsEmptyShotBoundaries()
    {
        File.WriteAllText(tempFile,
            "{\"quant_pos_scale\":0.0,\"quant_joint_scale\":0.002," +
            "\"joints_space\":\"camera_xyz_root_relative\",\"shots\":[[0,258],[258,338]]}");

        bool result = ManifestLoader.TryLoad(tempFile, out _, out ShotBoundaries shots);

        Assert.That(result, Is.False);
        Assert.That(shots.HasShots, Is.False);
    }

    [Test]
    public void TryLoad_FileMissing_ReturnsFalse()
    {
        bool result = ManifestLoader.TryLoad(tempFile + "_doesnotexist", out ManifestData manifest);

        Assert.That(result, Is.False);
        Assert.That(manifest, Is.Null);
    }

    [Test]
    public void TryLoad_ZeroQuantPosScale_ReturnsFalse()
    {
        File.WriteAllText(tempFile,
            "{\"quant_pos_scale\":0.0,\"quant_joint_scale\":0.002,\"joints_space\":\"camera_xyz_root_relative\"}");

        bool result = ManifestLoader.TryLoad(tempFile, out _);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryLoad_ZeroQuantJointScale_ReturnsFalse()
    {
        File.WriteAllText(tempFile,
            "{\"quant_pos_scale\":0.001,\"quant_joint_scale\":0.0,\"joints_space\":\"camera_xyz_root_relative\"}");

        bool result = ManifestLoader.TryLoad(tempFile, out _);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryLoad_WrongJointsSpace_ReturnsFalse()
    {
        File.WriteAllText(tempFile,
            "{\"quant_pos_scale\":0.001,\"quant_joint_scale\":0.002,\"joints_space\":\"world\"}");

        bool result = ManifestLoader.TryLoad(tempFile, out _);

        Assert.That(result, Is.False);
    }
}
