using System.Collections;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.XR;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: shared runtime fields in Core.cs, Bundle/UI/Screens partials
    // Provides: Awake/OnEnable/OnDisable/OnDestroy/Start/OnPrepared/Update/LateUpdate and recenter flow

    private void OnEnable()
    {
        SubscribeRecenterEvents();
    }


    private void OnDisable()
    {
        UnsubscribeRecenterEvents();
    }


    private void OnDestroy()
    {
        UnsubscribeRecenterEvents();
        UnsubscribeVideoPlayerEvents();
        UnbindRuntimeControls();
        DisposeInteractiveMotion();
    }


    private IEnumerator Start()
    {
        vp = GetComponent<VideoPlayer>();
        if (vp == null)
        {
            yield break;
        }

        RuntimePlaybackController.ConfigureForApiPlayback(vp);
        vp.frameReady -= OnVideoFrameReady;
        vp.frameReady += OnVideoFrameReady;
        vp.prepareCompleted -= OnPrepared;
        vp.prepareCompleted += OnPrepared;

        if (showBundlePickerOnStart)
        {
            yield return RunBundlePickerFlowAndPrepareVideo();
        }
        else
        {
            yield return EnsureBundleAndPrepareVideo();
        }
    }


    private void OnPrepared(VideoPlayer source)
    {
        float w = source.width;
        float h = source.height;

        EnsureScreensExist();
        SetupScreensAndMaterials();

        if (w <= 0 || h <= 0)
        {
            RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
            return;
        }

        float perEyeWidth = manifest != null && manifest.eye_w > 0 ? manifest.eye_w : w * 0.5f;
        float aspect = perEyeWidth / h;
        Vector3 screenScale = new Vector3(aspect * BaseHeight, BaseHeight, 1f);

        if (leftScreen != null)
        {
            ScreenTransformWriter.ApplyLocalScale(leftScreen, screenScale);
        }

        if (rightScreen != null)
        {
            ScreenTransformWriter.ApplyLocalScale(rightScreen, screenScale);
        }

        PlaceScreens();
        EnsureRuntimeControls();

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Play);
        UpdatePauseButtonLabel();
        vp.prepareCompleted -= OnPrepared;
    }


    private void UnsubscribeVideoPlayerEvents()
    {
        if (vp == null)
        {
            return;
        }

        vp.frameReady -= OnVideoFrameReady;
        vp.prepareCompleted -= OnPrepared;
    }


    private void OnVideoFrameReady(VideoPlayer player, long frame)
    {
        lastFrameReadyFrame = RuntimePlaybackTimeline.NormalizeFrameReadyFrame(frame);
        ApplyVideoFrameTexture(player);
    }


    private void LateUpdate()
    {
        if (!ForceScreensInFrontOfViewCamera)
        {
            return;
        }

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            return;
        }

        StereoScreenPlacement.ForcedPose pose = StereoScreenPlacement.ResolveForcedInFrontPose(
            cam.transform.position,
            cam.transform.forward,
            screenDistanceMeters);

        if (leftScreen != null)
        {
            ScreenTransformWriter.ApplyPose(leftScreen, pose.position, pose.rotation);
        }

        if (rightScreen != null)
        {
            ScreenTransformWriter.ApplyPose(rightScreen, pose.position, pose.rotation);
        }
    }


    private void Update()
    {
        StreamingStereoUpdateFlow.Decision decision = StreamingStereoUpdateFlow.Resolve(bundlePickerActive);
        if (decision.updateBundlePickerPlacement)
        {
            UpdateBundlePickerTick();
            return;
        }

        if (decision.updateRuntimePlayback)
        {
            UpdateRuntimePlaybackTick();
        }
    }


    private void UpdateBundlePickerTick()
    {
        UpdateBundlePickerPlacement();
    }


    private void UpdateRuntimePlaybackTick()
    {
        UpdatePickingTick();
        DisplayModelTick();
        DetectRuntimeRecenterFallback();
        HandleRuntimePauseInput();
        RefreshRuntimeSettingsPerFrame();
        UpdateRuntimeProgressUi();
    }


    private void UpdatePickingTick()
    {
        if (TryPick(out PickResult pick))
        {
            TrySelectDisplayTrackFromPick(pick);
        }
    }


    private RuntimeClock.TickContext GetRuntimeTickContext()
    {
        return RuntimeClock.ResolveTickContext(Time.time, Time.deltaTime, Time.frameCount);
    }


    private float GetRuntimeUnscaledDeltaTime()
    {
        return Time.unscaledDeltaTime;
    }


    private void SubscribeRecenterEvents()
    {
        UnsubscribeRecenterEvents();
        SubsystemManager.GetSubsystems(xrInputSubsystems);
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            TryApplyPreferredTrackingOriginMode(xr);
            xr.trackingOriginUpdated += OnTrackingOriginUpdated;
        }
    }


    private void UnsubscribeRecenterEvents()
    {
        for (int i = 0; i < xrInputSubsystems.Count; i++)
        {
            XRInputSubsystem xr = xrInputSubsystems[i];
            if (xr == null)
            {
                continue;
            }

            xr.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }

        xrInputSubsystems.Clear();
    }


    private void OnTrackingOriginUpdated(XRInputSubsystem _)
    {
        if (ForceStationaryTrackingOrigin)
        {
            for (int i = 0; i < xrInputSubsystems.Count; i++)
            {
                XRInputSubsystem xr = xrInputSubsystems[i];
                if (xr != null)
                {
                    TryApplyPreferredTrackingOriginMode(xr);
                }
            }
        }

        RecenterScreensToCurrentFacing();
    }


    private void TryApplyPreferredTrackingOriginMode(XRInputSubsystem xr)
    {
        if (!ForceStationaryTrackingOrigin || xr == null)
        {
            return;
        }

        TrackingOriginModeFlags supported = xr.GetSupportedTrackingOriginModes();
        if ((supported & TrackingOriginModeFlags.Device) == 0)
        {
            return;
        }

        xr.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
    }


    private void DetectRuntimeRecenterFallback()
    {
        if (ForceScreensInFrontOfViewCamera)
        {
            headPosePrimed = false;
            return;
        }

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            headPosePrimed = false;
            return;
        }

        if (!headPosePrimed)
        {
            headPosePrimed = true;
            lastHeadPos = head.position;
            lastHeadRot = head.rotation;
            return;
        }

        float deltaPos = Vector3.Distance(lastHeadPos, head.position);
        float deltaRotDeg = Quaternion.Angle(lastHeadRot, head.rotation);
        if (deltaPos > 0.35f || deltaRotDeg > 35f)
        {
            RecenterScreensToCurrentFacing();
        }

        lastHeadPos = head.position;
        lastHeadRot = head.rotation;
    }


    private void RecenterScreensToCurrentFacing()
    {
        if (ForceScreensInFrontOfViewCamera)
        {
            return;
        }

        if (leftScreen == null && rightScreen == null)
        {
            return;
        }

        PlaceScreens();
    }
}

