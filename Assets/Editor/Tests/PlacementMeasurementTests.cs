using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Video;

// 配置の実測。bundle_human.svb を実際に再生し、配置したモデルを画面へ再投影して
// meta.bin の bbox とどれだけ一致するかを [PLACE] ログに出す。
// アサートは最小限で、目的はログを取ること（大きさと位置のどちらがどれだけずれているかの把握）。
public class PlacementMeasurementTests
{
    private const string TestScenePath = "Assets/Scenes/TestScene.unity";
    private const int TargetFrame = 2100;

    [UnityTest]
    [Timeout(300000)]
    public IEnumerator MeasurePlacementAgainstBundleBBox()
    {
        EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
        yield return new EnterPlayMode();

        string failure = null;
        long reachedFrame = -1;
        StreamingStereoVideoPlayer player =
            Object.FindFirstObjectByType<StreamingStereoVideoPlayer>();
        VideoPlayer videoPlayer = player != null ? player.GetComponent<VideoPlayer>() : null;
        if (player == null || videoPlayer == null)
        {
            failure = "TestScene did not create StreamingStereoVideoPlayer and VideoPlayer.";
        }
        else
        {
            // バッチで何度も再生するため音は切る。
            player.mute = true;
            // 素の配置を測りたいので接触補正は切る。計測ログは有効にする。
            player.enableHumanOtherContactCorrection = false;
            player.logPlacementMeasurement = true;
            player.logPlacementMeasurementEveryNFrames = 30;
            // Human と Other の位置関係（[GAP]）。埋もれの原因が深度方向か画面平行方向かを分ける。
            player.logHumanOtherGap = true;
            player.logHumanOtherGapEveryNFrames = 30;
            // 姿勢再現の誤差（[POSE]）。[GAP] の lateralGap から誤差成分を切り分けるために測る。
            player.logHumanPoseError = true;
            player.logHumanPoseErrorEveryNFrames = 30;
            player.logHumanBoneLengthCorrection = true;
            player.logAnchorDepthRange = true;
            player.logBallHead = true;
            player.logBoneBBoxRelative = true;

            float prepareDeadline = Time.realtimeSinceStartup + 60f;
            while (!videoPlayer.isPrepared && Time.realtimeSinceStartup < prepareDeadline)
            {
                yield return null;
            }

            if (!videoPlayer.isPrepared)
            {
                failure = "VideoPlayer did not prepare the bundle within 60 seconds.";
            }
            else
            {
                videoPlayer.time = 0d;
                videoPlayer.Play();
                float playbackDeadline = Time.realtimeSinceStartup + 200f;
                while (videoPlayer.frame < TargetFrame &&
                       Time.realtimeSinceStartup < playbackDeadline)
                {
                    yield return null;
                }

                videoPlayer.Pause();
                reachedFrame = videoPlayer.frame;
            }
        }

        yield return new ExitPlayMode();

        TestContext.WriteLine($"reachedFrame={reachedFrame} (target {TargetFrame})");
        Assert.That(failure, Is.Null, failure);
        Assert.That(
            reachedFrame,
            Is.GreaterThan(0L),
            "Playback did not advance, so no placement measurement was collected.");
    }
}
