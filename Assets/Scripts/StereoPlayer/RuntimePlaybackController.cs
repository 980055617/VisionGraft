using UnityEngine.Video;

public static class RuntimePlaybackController
{
    public enum Command
    {
        None,
        Prepare,
        Play,
        Pause,
    }

    public static Command ResolveToggleCommand(bool isPlaying)
    {
        return isPlaying ? Command.Pause : Command.Play;
    }

    public static Command ResolvePauseForEditCommand(bool hasVideoPlayer, bool isPlaying)
    {
        return hasVideoPlayer && isPlaying ? Command.Pause : Command.None;
    }

    public static void ConfigureForApiPlayback(VideoPlayer videoPlayer)
    {
        if (videoPlayer == null)
        {
            return;
        }

        videoPlayer.source = VideoSource.Url;
        videoPlayer.isLooping = true;
        videoPlayer.renderMode = VideoRenderMode.APIOnly;
        videoPlayer.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
        videoPlayer.playbackSpeed = 1f;
        videoPlayer.sendFrameReadyEvents = true;
    }

    public static void Apply(VideoPlayer videoPlayer, Command command)
    {
        if (videoPlayer == null)
        {
            return;
        }

        if (command == Command.None)
        {
            return;
        }

        if (command == Command.Prepare)
        {
            videoPlayer.Prepare();
            return;
        }

        if (command == Command.Play)
        {
            videoPlayer.Play();
            return;
        }

        if (command == Command.Pause)
        {
            videoPlayer.Pause();
        }
    }

    public static void ApplySeekTarget(VideoPlayer videoPlayer, RuntimePlaybackTimeline.SeekTarget target)
    {
        if (videoPlayer == null)
        {
            return;
        }

        if (target.hasTime)
        {
            videoPlayer.time = target.timeSeconds;
            return;
        }

        if (target.hasFrame)
        {
            videoPlayer.frame = target.frame;
        }
    }
}
