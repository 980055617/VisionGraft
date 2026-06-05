using NUnit.Framework;
using UnityEngine;

public class ManifestDataTests
{
    [Test]
    public void BundleManifestJsonDeserializesExistingFields()
    {
        const string json =
            "{" +
            "\"width\":3840," +
            "\"height\":1080," +
            "\"eye_w\":1920," +
            "\"eye_h\":1080," +
            "\"num_frames\":120," +
            "\"fps\":30.0," +
            "\"fovx_deg\":90.0," +
            "\"quant_pos_scale\":0.001," +
            "\"quant_joint_scale\":0.002," +
            "\"joints_space\":\"camera_xyz_root_relative\"," +
            "\"joints_source\":\"metrabs\"," +
            "\"camera_axes\":\"opencv\"," +
            "\"uv_origin\":\"top_left\"," +
            "\"fx_norm\":1.1," +
            "\"fy_norm\":1.2," +
            "\"cx\":0.5," +
            "\"cy\":0.6," +
            "\"fovy_deg\":60.0," +
            "\"fovy\":61.0" +
            "}";

        ManifestData manifest = JsonUtility.FromJson<ManifestData>(json);

        Assert.That(manifest.width, Is.EqualTo(3840));
        Assert.That(manifest.height, Is.EqualTo(1080));
        Assert.That(manifest.eye_w, Is.EqualTo(1920));
        Assert.That(manifest.eye_h, Is.EqualTo(1080));
        Assert.That(manifest.num_frames, Is.EqualTo(120));
        Assert.That(manifest.fps, Is.EqualTo(30.0f));
        Assert.That(manifest.fovx_deg, Is.EqualTo(90.0f));
        Assert.That(manifest.quant_pos_scale, Is.EqualTo(0.001f).Within(0.000001f));
        Assert.That(manifest.quant_joint_scale, Is.EqualTo(0.002f).Within(0.000001f));
        Assert.That(manifest.joints_space, Is.EqualTo("camera_xyz_root_relative"));
        Assert.That(manifest.joints_source, Is.EqualTo("metrabs"));
        Assert.That(manifest.camera_axes, Is.EqualTo("opencv"));
        Assert.That(manifest.uv_origin, Is.EqualTo("top_left"));
        Assert.That(manifest.fx_norm, Is.EqualTo(1.1f).Within(0.000001f));
        Assert.That(manifest.fy_norm, Is.EqualTo(1.2f).Within(0.000001f));
        Assert.That(manifest.cx, Is.EqualTo(0.5f));
        Assert.That(manifest.cy, Is.EqualTo(0.6f));
        Assert.That(manifest.fovy_deg, Is.EqualTo(60.0f));
        Assert.That(manifest.fovy, Is.EqualTo(61.0f));
    }
}
