using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Normal mode plays source/pre_removal_stereo_video.mp4 with no replaceable models or
    // proxy overlays. See Docs/adr/0003-normal-mode-playback-video.md for why this does not
    // violate the source/* runtime-placement restriction.

    public void ToggleNormalMode()
    {
        if (vp == null || !hasNormalModeVideo)
        {
            return;
        }

        SetNormalMode(!isNormalMode);
    }


    private void SetNormalMode(bool normalMode)
    {
        if (vp == null || normalMode == isNormalMode)
        {
            return;
        }

        if (normalMode && !hasNormalModeVideo)
        {
            return;
        }

        string targetUrl = normalMode ? normalModePlaybackVideoPath : modelModePlaybackVideoPath;
        if (string.IsNullOrEmpty(targetUrl))
        {
            return;
        }

        pendingModeSwitchResume = vp.isPlaying;
        pendingModeSwitchTimeSeconds = vp.time;

        isNormalMode = normalMode;
        if (isNormalMode)
        {
            HideAllTrackInstancesAndProxies();
            StopAllInteractiveMotion();
        }

        Debug.Log(
            $"[Mode] switch normal={isNormalMode} url={targetUrl} " +
            $"resume={pendingModeSwitchResume} t={pendingModeSwitchTimeSeconds:F2}");

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Pause);
        vp.prepareCompleted -= OnModeSwitchPrepared;
        vp.prepareCompleted += OnModeSwitchPrepared;
        vp.url = targetUrl;
        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Prepare);

        UpdateRuntimeModeUiState();
    }


    private void OnModeSwitchPrepared(VideoPlayer source)
    {
        source.prepareCompleted -= OnModeSwitchPrepared;
        Debug.Log($"[Mode] prepared {source.width}x{source.height} url={source.url}");
        source.time = pendingModeSwitchTimeSeconds;
        if (pendingModeSwitchResume)
        {
            RuntimePlaybackController.Apply(source, RuntimePlaybackController.Command.Play);
        }

        UpdatePauseButtonLabel();
    }


    private void HideAllTrackInstancesAndProxies()
    {
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value != null)
            {
                SceneObjectWriter.ApplyActive(kv.Value, false);
            }
        }

        foreach (KeyValuePair<uint, GameObject> kv in otherProxyBoxesByTrack)
        {
            if (kv.Value != null)
            {
                SceneObjectWriter.ApplyActive(kv.Value, false);
            }
        }
    }
}
