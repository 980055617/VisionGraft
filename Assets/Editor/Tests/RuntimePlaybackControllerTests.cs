using NUnit.Framework;
using UnityEngine;
using UnityEngine.Video;

public class RuntimePlaybackControllerTests
{
    [Test]
    public void ResolveToggleCommandPausesWhenCurrentlyPlaying()
    {
        Assert.That(
            RuntimePlaybackController.ResolveToggleCommand(isPlaying: true),
            Is.EqualTo(RuntimePlaybackController.Command.Pause));
    }

    [Test]
    public void ResolveToggleCommandPlaysWhenCurrentlyStopped()
    {
        Assert.That(
            RuntimePlaybackController.ResolveToggleCommand(isPlaying: false),
            Is.EqualTo(RuntimePlaybackController.Command.Play));
    }

    [Test]
    public void ResolvePauseForEditCommandPausesOnlyWhenVideoIsPlaying()
    {
        Assert.That(
            RuntimePlaybackController.ResolvePauseForEditCommand(hasVideoPlayer: true, isPlaying: true),
            Is.EqualTo(RuntimePlaybackController.Command.Pause));

        Assert.That(
            RuntimePlaybackController.ResolvePauseForEditCommand(hasVideoPlayer: true, isPlaying: false),
            Is.EqualTo(RuntimePlaybackController.Command.None));

        Assert.That(
            RuntimePlaybackController.ResolvePauseForEditCommand(hasVideoPlayer: false, isPlaying: true),
            Is.EqualTo(RuntimePlaybackController.Command.None));
    }

    [Test]
    public void ApplyIgnoresNullVideoPlayer()
    {
        Assert.DoesNotThrow(() => RuntimePlaybackController.Apply(null, RuntimePlaybackController.Command.Prepare));
        Assert.DoesNotThrow(() => RuntimePlaybackController.Apply(null, RuntimePlaybackController.Command.Play));
        Assert.DoesNotThrow(() => RuntimePlaybackController.Apply(null, RuntimePlaybackController.Command.Pause));
        Assert.DoesNotThrow(() => RuntimePlaybackController.Apply(null, RuntimePlaybackController.Command.None));
    }

    [Test]
    public void ConfigureForApiPlaybackSetsRuntimeVideoDefaults()
    {
        GameObject go = new GameObject("VideoPlayer", typeof(VideoPlayer));
        try
        {
            VideoPlayer videoPlayer = go.GetComponent<VideoPlayer>();

            RuntimePlaybackController.ConfigureForApiPlayback(videoPlayer);

            Assert.That(videoPlayer.source, Is.EqualTo(VideoSource.Url));
            Assert.That(videoPlayer.isLooping, Is.True);
            Assert.That(videoPlayer.renderMode, Is.EqualTo(VideoRenderMode.APIOnly));
            Assert.That(videoPlayer.timeUpdateMode, Is.EqualTo(VideoTimeUpdateMode.UnscaledGameTime));
            Assert.That(videoPlayer.playbackSpeed, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(videoPlayer.sendFrameReadyEvents, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplySeekTargetWritesTimeWhenTargetHasTime()
    {
        GameObject go = new GameObject("VideoPlayer", typeof(VideoPlayer));
        try
        {
            VideoPlayer videoPlayer = go.GetComponent<VideoPlayer>();
            RuntimePlaybackTimeline.SeekTarget target = new RuntimePlaybackTimeline.SeekTarget(
                true,
                1.25d,
                false,
                0L);

            RuntimePlaybackController.ApplySeekTarget(videoPlayer, target);

            Assert.That(videoPlayer.time, Is.EqualTo(1.25d).Within(0.0001d));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ApplySeekTargetWritesFrameWhenTargetHasFrame()
    {
        GameObject go = new GameObject("VideoPlayer", typeof(VideoPlayer));
        try
        {
            VideoPlayer videoPlayer = go.GetComponent<VideoPlayer>();
            RuntimePlaybackTimeline.SeekTarget target = new RuntimePlaybackTimeline.SeekTarget(
                false,
                0d,
                true,
                42L);

            RuntimePlaybackController.ApplySeekTarget(videoPlayer, target);

            Assert.That(videoPlayer.frame, Is.EqualTo(42L));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
