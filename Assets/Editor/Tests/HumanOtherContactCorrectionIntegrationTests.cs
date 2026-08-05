using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.Video;

public class HumanOtherContactCorrectionIntegrationTests
{
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const int ContactFrame = 300;
    private const int SpinStartFrame = 540;
    private const int SpinEndFrame = 720;
    private const float MaximumAddedCorrectionDeltaPerFramePixels = 20f;
    private const uint HumanTrackId = 0;
    private const uint OtherTrackId = 1;

    private static readonly int[] ReportedContactFrames =
    {
        600, 615, 624, 625, 630, 2105, 2110, 2112, 2115
    };
    private static readonly int[] ReportedContactWindowStarts = { 590, 2095 };
    private static readonly int[] ReportedContactWindowEnds = { 640, 2120 };

    private static readonly HumanBodyBones[] ContactFootBones =
    {
        HumanBodyBones.LeftFoot,
        HumanBodyBones.RightFoot,
        HumanBodyBones.LeftToes,
        HumanBodyBones.RightToes
    };

    private struct BodyContactProbeSegment
    {
        public readonly string name;
        public readonly HumanBodyBones start;
        public readonly HumanBodyBones end;
        public readonly float proxyRadiusToLength;

        public BodyContactProbeSegment(
            string name,
            HumanBodyBones start,
            HumanBodyBones end,
            float proxyRadiusToLength)
        {
            this.name = name;
            this.start = start;
            this.end = end;
            this.proxyRadiusToLength = proxyRadiusToLength;
        }
    }

    private struct BodyContactProbeMeasurement
    {
        public readonly float clearancePixels;
        public readonly float depthDeltaMeters;

        public BodyContactProbeMeasurement(
            float clearancePixels,
            float depthDeltaMeters)
        {
            this.clearancePixels = clearancePixels;
            this.depthDeltaMeters = depthDeltaMeters;
        }
    }

    private static readonly BodyContactProbeSegment[] BodyContactProbeSegments =
    {
        new BodyContactProbeSegment(
            "LeftFoot",
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            0.25f),
        new BodyContactProbeSegment(
            "RightFoot",
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes,
            0.25f),
        new BodyContactProbeSegment(
            "LeftShin",
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            0.12f),
        new BodyContactProbeSegment(
            "RightShin",
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            0.12f),
        new BodyContactProbeSegment(
            "LeftForearm",
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            0.12f),
        new BodyContactProbeSegment(
            "RightForearm",
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            0.12f),
        new BodyContactProbeSegment(
            "LeftUpperArm",
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            0.16f),
        new BodyContactProbeSegment(
            "RightUpperArm",
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            0.16f)
    };

    [UnityTest]
    public IEnumerator BundleHumanActualPlaybackPlacesBallOnSourceFacingSideOfFoot()
    {
        EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        yield return new EnterPlayMode();

        string failure = null;
        long measuredFrame = -1;
        float ballRadiusPixels = 0f;
        float nearestFootBonePixels = 0f;
        float footProxyRadiusPixels = 0f;
        float footCapsuleClearancePixels = 0f;
        float renderedBallRadiusPixels = 0f;
        float sourceFacingDistancePixels = 0f;
        Vector2 sourceContactDirection = Vector2.zero;
        Vector2 displayedContactDirection = Vector2.zero;
        StreamingStereoVideoPlayer player =
            Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        VideoPlayer videoPlayer = player != null ? player.GetComponent<VideoPlayer>() : null;
        if (player == null || videoPlayer == null)
        {
            failure = "SampleScene did not create StreamingStereoVideoPlayer and VideoPlayer.";
        }
        else
        {
            player.enableHumanOtherContactCorrection = true;
            float prepareDeadline = Time.realtimeSinceStartup + 30f;
            while (!videoPlayer.isPrepared && Time.realtimeSinceStartup < prepareDeadline)
            {
                yield return null;
            }

            if (!videoPlayer.isPrepared)
            {
                failure = "VideoPlayer did not prepare bundle_human.svb within 30 seconds.";
            }
            else
            {
                videoPlayer.time = 0d;
                videoPlayer.Play();
                float playbackDeadline = Time.realtimeSinceStartup + 20f;
                while (videoPlayer.frame < ContactFrame &&
                       Time.realtimeSinceStartup < playbackDeadline)
                {
                    yield return null;
                }

                videoPlayer.Pause();
                measuredFrame = videoPlayer.frame;
                player.DisplayModelTick();
                if (measuredFrame < ContactFrame)
                {
                    failure = "VideoPlayer did not reach frame 300.";
                }
                else if (!TryResolveOtherMetadataRadius(player, out ballRadiusPixels))
                {
                    failure = "Could not resolve the Other metadata radius.";
                }
                else if (!TryMeasureFootOtherPixelGap(player, out nearestFootBonePixels))
                {
                    failure = "Could not measure the foot-bone gap at the contact scene.";
                }
                else if (!TryMeasureRenderedOtherRadius(player, out renderedBallRadiusPixels))
                {
                    failure = "Could not measure the rendered Other radius.";
                }
                else if (!TryMeasureFootCapsuleClearance(
                             player,
                             renderedBallRadiusPixels,
                             out footProxyRadiusPixels,
                             out footCapsuleClearancePixels))
                {
                    failure = "Could not measure the projected Foot-Toes capsule.";
                }
                else if (!TryMeasureSourceFacingFootContact(
                             player,
                             out sourceContactDirection,
                             out displayedContactDirection,
                             out sourceFacingDistancePixels))
                {
                    failure = "Could not measure which side of the source-facing foot contains the ball.";
                }
            }
        }

        yield return new ExitPlayMode();

        TestContext.WriteLine(
            $"frame={measuredFrame} ballRadius={ballRadiusPixels:F1}px " +
            $"nearestFootBone={nearestFootBonePixels:F1}px " +
            $"footProxyRadius={footProxyRadiusPixels:F1}px " +
            $"capsuleClearance={footCapsuleClearancePixels:F1}px " +
            $"renderedBallRadius={renderedBallRadiusPixels:F1}px " +
            $"sourceDirection={sourceContactDirection} " +
            $"displayedDirection={displayedContactDirection} " +
            $"sourceFacingDistance={sourceFacingDistancePixels:F1}px");
        Assert.That(failure, Is.Null, failure);
        Assert.That(
            footCapsuleClearancePixels,
            Is.GreaterThanOrEqualTo(0f),
            "The ball disk overlaps the projected Foot-Toes capsule.");
        Assert.That(
            Vector2.Dot(sourceContactDirection, displayedContactDirection),
            Is.GreaterThan(0.5f),
            "The ball is close to a foot, but lies on the opposite side from the source-video contact.");
        Assert.That(
            sourceFacingDistancePixels,
            Is.GreaterThan(0f),
            "The ball center must lie beyond the source-facing end of the displayed foot.");
    }

    [UnityTest]
    public IEnumerator BundleHumanActualPlaybackDoesNotAddBallTeleportDuringFrame600Spin()
    {
        EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        yield return new EnterPlayMode();

        string failure = null;
        int consecutiveSampleCount = 0;
        long previousFrame = -1;
        Vector2 previousCorrectionOffset = Vector2.zero;
        float maximumCorrectionDeltaPixels = 0f;
        long maximumDeltaFrame = -1;
        StreamingStereoVideoPlayer player =
            Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        VideoPlayer videoPlayer = player != null ? player.GetComponent<VideoPlayer>() : null;
        if (player == null || videoPlayer == null)
        {
            failure = "SampleScene did not create StreamingStereoVideoPlayer and VideoPlayer.";
        }
        else
        {
            player.enableHumanOtherContactCorrection = true;
            float prepareDeadline = Time.realtimeSinceStartup + 30f;
            while (!videoPlayer.isPrepared && Time.realtimeSinceStartup < prepareDeadline)
            {
                yield return null;
            }

            if (!videoPlayer.isPrepared)
            {
                failure = "VideoPlayer did not prepare bundle_human.svb within 30 seconds.";
            }
            else
            {
                videoPlayer.time = 0d;
                videoPlayer.Play();
                float playbackDeadline = Time.realtimeSinceStartup + 35f;
                while (videoPlayer.frame <= SpinEndFrame &&
                       Time.realtimeSinceStartup < playbackDeadline)
                {
                    long currentFrame = videoPlayer.frame;
                    if (currentFrame >= SpinStartFrame &&
                        currentFrame <= SpinEndFrame &&
                        currentFrame != previousFrame)
                    {
                        player.DisplayModelTick();
                        if (!TryMeasureOtherCorrectionOffset(
                                player,
                                out Vector2 correctionOffset))
                        {
                            failure =
                                $"Could not measure the Other correction at frame {currentFrame}.";
                            break;
                        }

                        if (previousFrame >= SpinStartFrame &&
                            currentFrame == previousFrame + 1)
                        {
                            float delta = Vector2.Distance(
                                correctionOffset,
                                previousCorrectionOffset);
                            consecutiveSampleCount++;
                            if (delta > maximumCorrectionDeltaPixels)
                            {
                                maximumCorrectionDeltaPixels = delta;
                                maximumDeltaFrame = currentFrame;
                            }
                        }

                        previousFrame = currentFrame;
                        previousCorrectionOffset = correctionOffset;
                    }
                    yield return null;
                }

                videoPlayer.Pause();
                if (videoPlayer.frame <= SpinEndFrame && failure == null)
                {
                    failure = "VideoPlayer did not reach the end of the frame-600 spin window.";
                }
            }
        }

        yield return new ExitPlayMode();

        TestContext.WriteLine(
            $"frames={SpinStartFrame}-{SpinEndFrame} " +
            $"consecutiveSamples={consecutiveSampleCount} " +
            $"maxAddedCorrectionDelta={maximumCorrectionDeltaPixels:F1}px " +
            $"atFrame={maximumDeltaFrame}");
        Assert.That(failure, Is.Null, failure);
        Assert.That(
            consecutiveSampleCount,
            Is.GreaterThan(100),
            "The playback loop did not capture enough consecutive frames.");
        Assert.That(
            maximumCorrectionDeltaPixels,
            Is.LessThanOrEqualTo(MaximumAddedCorrectionDeltaPerFramePixels),
            "Human-Other correction introduced a one-frame ball teleport.");
    }

    [UnityTest]
    public IEnumerator BundleHumanActualPlaybackDoesNotEmbedBallInReportedContactScenes()
    {
        EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        yield return new EnterPlayMode();

        string failure = null;
        float maximumSurfaceErrorPixels = 0f;
        float maximumDepthErrorMeters = 0f;
        string maximumSurfaceErrorContact = null;
        string maximumDepthErrorContact = null;
        StreamingStereoVideoPlayer player =
            Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        VideoPlayer videoPlayer = player != null ? player.GetComponent<VideoPlayer>() : null;
        if (player == null || videoPlayer == null)
        {
            failure = "SampleScene did not create StreamingStereoVideoPlayer and VideoPlayer.";
        }
        else
        {
            player.enableHumanOtherContactCorrection = true;
            float prepareDeadline = Time.realtimeSinceStartup + 30f;
            while (!videoPlayer.isPrepared && Time.realtimeSinceStartup < prepareDeadline)
            {
                yield return null;
            }

            if (!videoPlayer.isPrepared)
            {
                failure = "VideoPlayer did not prepare bundle_human.svb within 30 seconds.";
            }
            else
            {
                int capturedFrameCount = 0;
                int nextTargetIndex = 0;
                for (int windowIndex = 0;
                     windowIndex < ReportedContactWindowStarts.Length &&
                     failure == null;
                     windowIndex++)
                {
                    videoPlayer.Pause();
                    videoPlayer.frame =
                        ReportedContactWindowStarts[windowIndex];
                    videoPlayer.Play();
                    long previousMeasuredFrame = -1;
                    float playbackDeadline =
                        Time.realtimeSinceStartup + 12f;
                    while (videoPlayer.frame <=
                               ReportedContactWindowEnds[windowIndex] &&
                           Time.realtimeSinceStartup < playbackDeadline)
                    {
                        long measuredFrame = videoPlayer.frame;
                        if (measuredFrame != previousMeasuredFrame &&
                            nextTargetIndex < ReportedContactFrames.Length &&
                            measuredFrame ==
                                ReportedContactFrames[nextTargetIndex])
                        {
                            player.DisplayModelTick();
                            if (!TryMeasureBodyContactProbe(
                                    player,
                                    out Dictionary<string, BodyContactProbeMeasurement>
                                        frameMeasurements,
                                    out string measurements))
                            {
                                failure =
                                    $"Could not measure body contact at frame {measuredFrame}.";
                                break;
                            }

                            string expectedContact;
                            BodyContactProbeMeasurement expectedMeasurement;
                            if (measuredFrame < 1000)
                            {
                                BodyContactProbeMeasurement leftFoot =
                                    frameMeasurements["LeftFoot"];
                                BodyContactProbeMeasurement rightFoot =
                                    frameMeasurements["RightFoot"];
                                bool useLeft =
                                    Mathf.Abs(leftFoot.clearancePixels) <=
                                    Mathf.Abs(rightFoot.clearancePixels);
                                expectedContact = useLeft
                                    ? "LeftFoot"
                                    : "RightFoot";
                                expectedMeasurement = useLeft
                                    ? leftFoot
                                    : rightFoot;
                            }
                            else
                            {
                                expectedContact = "RightForearm";
                                expectedMeasurement =
                                    frameMeasurements[expectedContact];
                            }

                            TestContext.WriteLine(
                                $"frame={measuredFrame} " +
                                $"target={ReportedContactFrames[nextTargetIndex]} " +
                                $"expected={expectedContact} " +
                                measurements);
                            float surfaceError =
                                Mathf.Abs(expectedMeasurement.clearancePixels);
                            float depthError =
                                Mathf.Abs(expectedMeasurement.depthDeltaMeters);
                            if (surfaceError > maximumSurfaceErrorPixels)
                            {
                                maximumSurfaceErrorPixels = surfaceError;
                                maximumSurfaceErrorContact =
                                    $"{expectedContact} frame={measuredFrame}";
                            }
                            if (depthError > maximumDepthErrorMeters)
                            {
                                maximumDepthErrorMeters = depthError;
                                maximumDepthErrorContact =
                                    $"{expectedContact} frame={measuredFrame}";
                            }

                            capturedFrameCount++;
                            nextTargetIndex++;
                        }

                        previousMeasuredFrame = measuredFrame;
                        yield return null;
                    }

                    videoPlayer.Pause();
                    if (videoPlayer.frame <=
                        ReportedContactWindowEnds[windowIndex])
                    {
                        failure =
                            $"VideoPlayer did not finish contact window " +
                            $"{ReportedContactWindowStarts[windowIndex]}-" +
                            $"{ReportedContactWindowEnds[windowIndex]}.";
                    }
                }

                if (failure == null &&
                    capturedFrameCount != ReportedContactFrames.Length)
                {
                    failure =
                        $"Captured {capturedFrameCount} of " +
                        $"{ReportedContactFrames.Length} reported frames.";
                }
            }
        }

        yield return new ExitPlayMode();

        TestContext.WriteLine(
            $"maximumSurfaceError={maximumSurfaceErrorPixels:F1}px " +
            $"at {maximumSurfaceErrorContact}; " +
            $"maximumDepthError={maximumDepthErrorMeters:F3}m " +
            $"at {maximumDepthErrorContact}");
        Assert.That(failure, Is.Null, failure);
        Assert.That(
            maximumSurfaceErrorPixels,
            Is.LessThanOrEqualTo(6f),
            "The displayed ball is not on the mapped body-part surface.");
        Assert.That(
            maximumDepthErrorMeters,
            Is.LessThanOrEqualTo(0.025f),
            "The displayed ball and mapped body part disagree in depth.");
    }

    private static bool TryMeasureBodyContactProbe(
        StreamingStereoVideoPlayer player,
        out Dictionary<string, BodyContactProbeMeasurement>
            contactMeasurements,
        out string measurements)
    {
        contactMeasurements =
            new Dictionary<string, BodyContactProbeMeasurement>();
        measurements = string.Empty;
        if (!TryResolveContactTestObjects(
                player,
                out GameObject human,
                out GameObject other,
                out _) ||
            !TryResolveProjection(
                player,
                out ManifestData manifest,
                out Quaternion worldToCam,
                out Vector3 camOrigin,
                out float fx,
                out float fy) ||
            !TryMeasureRenderedOtherRadius(player, out float ballRadiusPixels))
        {
            return false;
        }

        Vector3 otherCam = worldToCam * (other.transform.position - camOrigin);
        if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                otherCam,
                fx,
                fy,
                out Vector2 otherPixel))
        {
            return false;
        }

        Animator animator = human.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        var details = new List<string>();
        for (int i = 0; i < BodyContactProbeSegments.Length; i++)
        {
            BodyContactProbeSegment binding = BodyContactProbeSegments[i];
            Transform start = animator.GetBoneTransform(binding.start);
            Transform end = animator.GetBoneTransform(binding.end);
            if (start == null || end == null)
            {
                continue;
            }

            Vector3 startCam = worldToCam * (start.position - camOrigin);
            Vector3 endCam = worldToCam * (end.position - camOrigin);
            if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    startCam,
                    fx,
                    fy,
                    out Vector2 startPixel) ||
                !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    endCam,
                    fx,
                    fy,
                    out Vector2 endPixel))
            {
                continue;
            }

            float proxyRadiusPixels =
                Vector2.Distance(startPixel, endPixel) *
                binding.proxyRadiusToLength;
            float clearancePixels =
                DistanceToSegment(otherPixel, startPixel, endPixel) -
                ballRadiusPixels -
                proxyRadiusPixels;
            float depthDelta =
                otherCam.z - (startCam.z + endCam.z) * 0.5f;
            contactMeasurements[binding.name] =
                new BodyContactProbeMeasurement(
                    clearancePixels,
                    depthDelta);
            details.Add(
                $"{binding.name}={clearancePixels:F1}px/z{depthDelta:F3}");
        }

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        Transform leftShoulder =
            animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
        Transform rightShoulder =
            animator.GetBoneTransform(HumanBodyBones.RightShoulder);
        if (hips != null &&
            neck != null &&
            leftShoulder != null &&
            rightShoulder != null)
        {
            Vector3 hipsCam = worldToCam * (hips.position - camOrigin);
            Vector3 neckCam = worldToCam * (neck.position - camOrigin);
            Vector3 leftShoulderCam =
                worldToCam * (leftShoulder.position - camOrigin);
            Vector3 rightShoulderCam =
                worldToCam * (rightShoulder.position - camOrigin);
            if (PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    hipsCam,
                    fx,
                    fy,
                    out Vector2 hipsPixel) &&
                PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    neckCam,
                    fx,
                    fy,
                    out Vector2 neckPixel) &&
                PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    leftShoulderCam,
                    fx,
                    fy,
                    out Vector2 leftShoulderPixel) &&
                PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    rightShoulderCam,
                    fx,
                    fy,
                    out Vector2 rightShoulderPixel))
            {
                float torsoRadiusPixels =
                    Vector2.Distance(leftShoulderPixel, rightShoulderPixel) *
                    0.35f;
                float torsoClearancePixels =
                    DistanceToSegment(otherPixel, hipsPixel, neckPixel) -
                    ballRadiusPixels -
                    torsoRadiusPixels;
                float torsoDepthDelta =
                    otherCam.z - (hipsCam.z + neckCam.z) * 0.5f;
                contactMeasurements["Torso"] =
                    new BodyContactProbeMeasurement(
                        torsoClearancePixels,
                        torsoDepthDelta);
                details.Add(
                    $"Torso={torsoClearancePixels:F1}px/z{torsoDepthDelta:F3}");
            }
        }

        measurements =
            $"ball={otherPixel}/r{ballRadiusPixels:F1}px " +
            string.Join(" ", details);
        return contactMeasurements.ContainsKey("LeftFoot") &&
               contactMeasurements.ContainsKey("RightFoot") &&
               contactMeasurements.ContainsKey("RightForearm");
    }

    private static bool TryMeasureFootOtherPixelGap(
        StreamingStereoVideoPlayer player,
        out float gapPixels)
    {
        gapPixels = 0f;
        FieldInfo instancesField = typeof(StreamingStereoVideoPlayer).GetField(
            "trackInstances",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Dictionary<uint, GameObject> instances =
            instancesField != null
                ? instancesField.GetValue(player) as Dictionary<uint, GameObject>
                : null;
        if (instances == null ||
            !instances.TryGetValue(HumanTrackId, out GameObject human) ||
            !instances.TryGetValue(OtherTrackId, out GameObject other) ||
            human == null ||
            other == null)
        {
            return false;
        }

        Animator animator = human.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        FieldInfo manifestField = typeof(StreamingStereoVideoPlayer).GetField(
            "manifest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo leftScreenField = typeof(StreamingStereoVideoPlayer).GetField(
            "leftScreen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo pinholeBasisMethod = typeof(StreamingStereoVideoPlayer).GetMethod(
            "TryGetPinholeBasis",
            BindingFlags.Instance | BindingFlags.NonPublic);
        ManifestData manifest =
            manifestField != null ? manifestField.GetValue(player) as ManifestData : null;
        Transform screen =
            leftScreenField != null ? leftScreenField.GetValue(player) as Transform : null;
        if (manifest == null || screen == null || pinholeBasisMethod == null)
        {
            return false;
        }

        object[] basisArguments = { screen, Vector3.zero, Quaternion.identity };
        bool hasBasis = (bool)pinholeBasisMethod.Invoke(player, basisArguments);
        Vector3 camOrigin = (Vector3)basisArguments[1];
        Quaternion camRotation = (Quaternion)basisArguments[2];
        float fovxDeg = manifest.fovx_deg;
        if (!hasBasis ||
            !PinholePlacementSpace.TryResolveProjectionIntrinsics(
                manifest,
                fovxDeg > 0f,
                fovxDeg,
                out float fx,
                out float fy,
                out _,
                out _))
        {
            return false;
        }

        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                worldToCam * (other.transform.position - camOrigin),
                fx,
                fy,
                out Vector2 otherPixel))
        {
            return false;
        }

        float best = float.MaxValue;
        for (int i = 0; i < ContactFootBones.Length; i++)
        {
            Transform bone = animator.GetBoneTransform(ContactFootBones[i]);
            if (bone != null &&
                PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    worldToCam * (bone.position - camOrigin),
                    fx,
                    fy,
                    out Vector2 footPixel))
            {
                best = Mathf.Min(best, Vector2.Distance(footPixel, otherPixel));
            }
        }

        gapPixels = best;
        return best < float.MaxValue;
    }

    private static bool TryResolveOtherMetadataRadius(
        StreamingStereoVideoPlayer player,
        out float ballRadiusPixels)
    {
        ballRadiusPixels = 0f;
        if (!TryResolveContactTestObjects(
                player,
                out _,
                out _,
                out List<StreamingStereoVideoPlayer.MetaObj> frameObjects))
        {
            return false;
        }

        bool foundOther = false;
        for (int i = 0; i < frameObjects.Count; i++)
        {
            if (frameObjects[i].trackId == OtherTrackId)
            {
                ballRadiusPixels = (frameObjects[i].bboxW + frameObjects[i].bboxH) * 0.25f;
                foundOther = ballRadiusPixels > 0f;
                break;
            }
        }
        return foundOther;
    }

    private static bool TryMeasureFootCapsuleClearance(
        StreamingStereoVideoPlayer player,
        float ballRadiusPixels,
        out float selectedFootProxyRadiusPixels,
        out float clearancePixels)
    {
        selectedFootProxyRadiusPixels = 0f;
        clearancePixels = 0f;
        if (!TryResolveContactTestObjects(
                player,
                out GameObject human,
                out GameObject other,
                out _) ||
            !TryResolveProjection(
                player,
                out ManifestData manifest,
                out Quaternion worldToCam,
                out Vector3 camOrigin,
                out float fx,
                out float fy) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                worldToCam * (other.transform.position - camOrigin),
                fx,
                fy,
                out Vector2 otherPixel))
        {
            return false;
        }

        Animator animator = human.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        HumanBodyBones[] toes = { HumanBodyBones.LeftToes, HumanBodyBones.RightToes };
        float bestClearance = float.MaxValue;
        for (int i = 0; i < feet.Length; i++)
        {
            Transform foot = animator.GetBoneTransform(feet[i]);
            Transform toe = animator.GetBoneTransform(toes[i]);
            if (foot == null ||
                toe == null ||
                !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    worldToCam * (foot.position - camOrigin),
                    fx,
                    fy,
                    out Vector2 footPixel) ||
                !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    worldToCam * (toe.position - camOrigin),
                    fx,
                    fy,
                    out Vector2 toePixel))
            {
                continue;
            }

            float proxyRadius = Vector2.Distance(footPixel, toePixel) * 0.25f;
            float centerToSegment = DistanceToSegment(otherPixel, footPixel, toePixel);
            float currentClearance = centerToSegment - ballRadiusPixels - proxyRadius;
            if (currentClearance < bestClearance)
            {
                bestClearance = currentClearance;
                selectedFootProxyRadiusPixels = proxyRadius;
            }
        }

        clearancePixels = bestClearance;
        return bestClearance < float.MaxValue;
    }

    private static bool TryMeasureRenderedOtherRadius(
        StreamingStereoVideoPlayer player,
        out float renderedRadiusPixels)
    {
        renderedRadiusPixels = 0f;
        if (!TryResolveContactTestObjects(
                player,
                out _,
                out GameObject other,
                out _) ||
            !TryResolveProjection(
                player,
                out ManifestData manifest,
                out Quaternion worldToCam,
                out Vector3 camOrigin,
                out float fx,
                out float fy) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                worldToCam * (other.transform.position - camOrigin),
                fx,
                fy,
                out Vector2 otherPixel))
        {
            return false;
        }

        Renderer[] renderers = other.GetComponentsInChildren<Renderer>(true);
        float minU = float.MaxValue;
        float maxU = float.MinValue;
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        bool hasPoint = false;
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Bounds bounds = renderers[rendererIndex].bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                if (PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                        manifest,
                        worldToCam * (world - camOrigin),
                        fx,
                        fy,
                        out Vector2 cornerPixel))
                {
                    minU = Mathf.Min(minU, cornerPixel.x);
                    maxU = Mathf.Max(maxU, cornerPixel.x);
                    minV = Mathf.Min(minV, cornerPixel.y);
                    maxV = Mathf.Max(maxV, cornerPixel.y);
                    hasPoint = true;
                }
            }
        }

        renderedRadiusPixels = Mathf.Max(maxU - minU, maxV - minV) * 0.5f;
        return hasPoint;
    }

    private static bool TryMeasureOtherCorrectionOffset(
        StreamingStereoVideoPlayer player,
        out Vector2 correctionOffset)
    {
        correctionOffset = Vector2.zero;
        if (!TryResolveContactTestObjects(
                player,
                out _,
                out GameObject other,
                out List<StreamingStereoVideoPlayer.MetaObj> frameObjects) ||
            !TryResolveProjection(
                player,
                out ManifestData manifest,
                out Quaternion worldToCam,
                out Vector3 camOrigin,
                out float fx,
                out float fy) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                worldToCam * (other.transform.position - camOrigin),
                fx,
                fy,
                out Vector2 displayedOtherPixel))
        {
            return false;
        }

        for (int i = 0; i < frameObjects.Count; i++)
        {
            if (frameObjects[i].trackId != OtherTrackId)
            {
                continue;
            }

            Vector2 sourceOtherPixel = new Vector2(
                frameObjects[i].anchorU,
                frameObjects[i].anchorV);
            correctionOffset = displayedOtherPixel - sourceOtherPixel;
            return true;
        }
        return false;
    }

    private static bool TryMeasureSourceFacingFootContact(
        StreamingStereoVideoPlayer player,
        out Vector2 sourceDirection,
        out Vector2 displayedDirection,
        out float sourceFacingDistancePixels)
    {
        sourceDirection = Vector2.zero;
        displayedDirection = Vector2.zero;
        sourceFacingDistancePixels = 0f;
        if (!TryResolveContactTestObjects(
                player,
                out GameObject human,
                out GameObject other,
                out List<StreamingStereoVideoPlayer.MetaObj> frameObjects) ||
            !TryResolveProjection(
                player,
                out ManifestData manifest,
                out Quaternion worldToCam,
                out Vector3 camOrigin,
                out float fx,
                out float fy) ||
            !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                manifest,
                worldToCam * (other.transform.position - camOrigin),
                fx,
                fy,
                out Vector2 displayedOtherPixel))
        {
            return false;
        }

        bool hasHumanMetadata = false;
        bool hasOtherMetadata = false;
        StreamingStereoVideoPlayer.MetaObj humanMetadata = default;
        StreamingStereoVideoPlayer.MetaObj otherMetadata = default;
        for (int i = 0; i < frameObjects.Count; i++)
        {
            if (frameObjects[i].trackId == HumanTrackId)
            {
                humanMetadata = frameObjects[i];
                hasHumanMetadata = true;
            }
            else if (frameObjects[i].trackId == OtherTrackId)
            {
                otherMetadata = frameObjects[i];
                hasOtherMetadata = true;
            }
        }
        if (!hasHumanMetadata || !hasOtherMetadata)
        {
            return false;
        }

        Rect humanBounds = new Rect(
            humanMetadata.bboxX,
            humanMetadata.bboxY,
            humanMetadata.bboxW,
            humanMetadata.bboxH);
        Vector2 sourceOtherPixel = new Vector2(
            otherMetadata.anchorU,
            otherMetadata.anchorV);
        Vector2 closestSourceBoundsPixel = new Vector2(
            Mathf.Clamp(sourceOtherPixel.x, humanBounds.xMin, humanBounds.xMax),
            Mathf.Clamp(sourceOtherPixel.y, humanBounds.yMin, humanBounds.yMax));
        sourceDirection = sourceOtherPixel - closestSourceBoundsPixel;
        if (sourceDirection.sqrMagnitude <= 0.0001f)
        {
            sourceDirection = sourceOtherPixel - humanBounds.center;
        }
        if (sourceDirection.sqrMagnitude <= 0.0001f)
        {
            return false;
        }
        sourceDirection.Normalize();

        Animator animator = human.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        HumanBodyBones[] toes = { HumanBodyBones.LeftToes, HumanBodyBones.RightToes };
        bool hasFacingFoot = false;
        float bestFacingProjection = float.MinValue;
        Vector2 facingFootPixel = Vector2.zero;
        for (int i = 0; i < feet.Length; i++)
        {
            Transform foot = animator.GetBoneTransform(feet[i]);
            Transform toe = animator.GetBoneTransform(toes[i]);
            if (foot == null ||
                toe == null ||
                !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    worldToCam * (foot.position - camOrigin),
                    fx,
                    fy,
                    out Vector2 footPixel) ||
                !PinholePlacementSpace.TryProjectCamLocalToEyePixel(
                    manifest,
                    worldToCam * (toe.position - camOrigin),
                    fx,
                    fy,
                    out Vector2 toePixel))
            {
                continue;
            }

            float footProjection = Vector2.Dot(footPixel, sourceDirection);
            float toeProjection = Vector2.Dot(toePixel, sourceDirection);
            Vector2 candidatePixel =
                footProjection >= toeProjection ? footPixel : toePixel;
            float candidateProjection = Mathf.Max(footProjection, toeProjection);
            if (!hasFacingFoot || candidateProjection > bestFacingProjection)
            {
                hasFacingFoot = true;
                bestFacingProjection = candidateProjection;
                facingFootPixel = candidatePixel;
            }
        }
        if (!hasFacingFoot)
        {
            return false;
        }

        Vector2 displayedOffset = displayedOtherPixel - facingFootPixel;
        sourceFacingDistancePixels = Vector2.Dot(displayedOffset, sourceDirection);
        if (displayedOffset.sqrMagnitude <= 0.0001f)
        {
            return false;
        }
        displayedDirection = displayedOffset.normalized;
        return true;
    }

    private static bool TryResolveContactTestObjects(
        StreamingStereoVideoPlayer player,
        out GameObject human,
        out GameObject other,
        out List<StreamingStereoVideoPlayer.MetaObj> frameObjects)
    {
        human = null;
        other = null;
        frameObjects = null;
        FieldInfo instancesField = typeof(StreamingStereoVideoPlayer).GetField(
            "trackInstances",
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo frameObjectsField = typeof(StreamingStereoVideoPlayer).GetField(
            "metaFrameObjects",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Dictionary<uint, GameObject> instances =
            instancesField != null
                ? instancesField.GetValue(player) as Dictionary<uint, GameObject>
                : null;
        frameObjects =
            frameObjectsField != null
                ? frameObjectsField.GetValue(player) as List<StreamingStereoVideoPlayer.MetaObj>
                : null;
        return instances != null &&
               frameObjects != null &&
               instances.TryGetValue(HumanTrackId, out human) &&
               instances.TryGetValue(OtherTrackId, out other) &&
               human != null &&
               other != null;
    }

    private static bool TryResolveProjection(
        StreamingStereoVideoPlayer player,
        out ManifestData manifest,
        out Quaternion worldToCam,
        out Vector3 camOrigin,
        out float fx,
        out float fy)
    {
        manifest = null;
        worldToCam = Quaternion.identity;
        camOrigin = Vector3.zero;
        fx = 0f;
        fy = 0f;
        FieldInfo manifestField = typeof(StreamingStereoVideoPlayer).GetField(
            "manifest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo leftScreenField = typeof(StreamingStereoVideoPlayer).GetField(
            "leftScreen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo pinholeBasisMethod = typeof(StreamingStereoVideoPlayer).GetMethod(
            "TryGetPinholeBasis",
            BindingFlags.Instance | BindingFlags.NonPublic);
        manifest =
            manifestField != null ? manifestField.GetValue(player) as ManifestData : null;
        Transform screen =
            leftScreenField != null ? leftScreenField.GetValue(player) as Transform : null;
        if (manifest == null || screen == null || pinholeBasisMethod == null)
        {
            return false;
        }

        object[] basisArguments = { screen, Vector3.zero, Quaternion.identity };
        bool hasBasis = (bool)pinholeBasisMethod.Invoke(player, basisArguments);
        camOrigin = (Vector3)basisArguments[1];
        Quaternion camRotation = (Quaternion)basisArguments[2];
        float fovxDeg = manifest.fovx_deg;
        worldToCam = Quaternion.Inverse(camRotation);
        return hasBasis &&
               PinholePlacementSpace.TryResolveProjectionIntrinsics(
                   manifest,
                   fovxDeg > 0f,
                   fovxDeg,
                   out fx,
                   out fy,
                   out _,
                   out _);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f)
        {
            return Vector2.Distance(point, start);
        }

        float t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        return Vector2.Distance(point, start + segment * t);
    }
}
