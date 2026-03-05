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
    }


    private IEnumerator Start()
    {
        vp = GetComponent<VideoPlayer>();
        if (vp == null)
        {
            yield break;
        }

        vp.source = VideoSource.Url;
        vp.isLooping = true;
        vp.renderMode = VideoRenderMode.APIOnly;
        vp.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
        vp.playbackSpeed = 1f;
        vp.sendFrameReadyEvents = true;
        vp.frameReady += (player, frame) =>
        {
            if (frame < 0)
            {
                lastFrameReadyFrame = -1;
            }
            else if (frame > int.MaxValue)
            {
                lastFrameReadyFrame = int.MaxValue;
            }
            else
            {
                lastFrameReadyFrame = (int)frame;
            }
            ApplyVideoFrameTexture(player);
        };

        vp.prepareCompleted += OnPrepared;

        yield return EnsureBundleAndPrepareVideo();
    }


    private void OnPrepared(VideoPlayer source)
    {
        float w = source.width;
        float h = source.height;

        EnsureScreensExist();
        SetupScreensAndMaterials();

        if (w <= 0 || h <= 0)
        {
            vp.Play();
            return;
        }

        float perEyeWidth = sideBySide ? w * 0.5f : w;
        float aspect = perEyeWidth / h;
        Vector3 screenScale = new Vector3(aspect * baseHeight, baseHeight, 1f);

        if (leftScreen != null)
        {
            leftScreen.localScale = screenScale;
        }

        if (rightScreen != null)
        {
            rightScreen.localScale = screenScale;
        }

        PlaceScreens();
        EnsureRuntimeControls();

        if (spawnTestModelOnPrepared)
        {
            TrySpawnTestModel();
        }

        vp.Play();
        UpdatePauseButtonLabel();
        vp.prepareCompleted -= OnPrepared;
    }


    private void LateUpdate()
    {
        if (!forceScreensInFrontOfViewCamera)
        {
            return;
        }

        Camera cam = GetViewCamera();
        if (cam == null)
        {
            return;
        }

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Vector3 screenPos = camPos + camFwd * screenDistanceMeters;
        Quaternion screenRot = Quaternion.LookRotation((camPos - screenPos).normalized, Vector3.up);

        if (leftScreen != null)
        {
            leftScreen.position = screenPos;
            leftScreen.rotation = screenRot;
        }

        if (rightScreen != null)
        {
            rightScreen.position = screenPos;
            rightScreen.rotation = screenRot;
        }
    }


    private void Update()
    {
        if (TryPick(out PickResult pick))
        {
            pickedPixel = pick.pixel;
            hasPickedPixel = true;
            pickedScreen = pick.screen;
            TrySelectFollowTrackFromPick(pick);
            TrySpawnOrMoveTestModel(pick);
        }

        FollowTick();
        DetectRuntimeRecenterFallback();
        HandleRuntimePauseInput();
        RefreshRuntimeSettingsPerFrame();
        UpdateRuntimeProgressUi();
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
        RecenterScreensToCurrentFacing();
    }


    private void DetectRuntimeRecenterFallback()
    {
        if (forceScreensInFrontOfViewCamera)
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
        if (forceScreensInFrontOfViewCamera)
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

