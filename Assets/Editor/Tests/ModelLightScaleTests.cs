using NUnit.Framework;

// 2026-10-25: モデルの光を動画の下半分の明るさに合わせる式（StreamingStereoVideoPlayer.ModelLight.partial.cs）。
// S は 6 本の動画（video.mp4 の左目の下半分、線形の輝度の平均。Q1 の video_stats.py）。
public class ModelLightScaleTests
{
    [TestCase(0.3175f, 0.995f)]   // balloon
    [TestCase(0.2357f, 0.832f)]   // cat
    [TestCase(0.1836f, 0.717f)]   // dog
    [TestCase(0.1316f, 0.587f)]   // soccer
    [TestCase(0.0312f, 0.247f)]   // tv
    [TestCase(0.0297f, 0.240f)]   // car2
    public void ScaleForTheSixExperimentVideos(float lowerHalfLuminance, float expected)
    {
        float k = StreamingStereoVideoPlayer.ModelLightScaleFor(lowerHalfLuminance, 0.32f, 0.6f, 0.15f, 1f);

        Assert.That(k, Is.EqualTo(expected).Within(0.005f));
    }

    [Test]
    public void NeverBrighterThanTheSceneLight_AndNeverBelowTheFloor()
    {
        Assert.That(StreamingStereoVideoPlayer.ModelLightScaleFor(0.9f, 0.32f, 0.6f, 0.15f, 1f), Is.EqualTo(1f));
        Assert.That(StreamingStereoVideoPlayer.ModelLightScaleFor(0.0005f, 0.32f, 0.6f, 0.15f, 1f), Is.EqualTo(0.15f));
        Assert.That(StreamingStereoVideoPlayer.ModelLightScaleFor(0f, 0.32f, 0.6f, 0.15f, 1f), Is.EqualTo(1f));
    }
}
