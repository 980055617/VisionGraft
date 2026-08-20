using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: track instance/model state (Model.cs), bbox bottom-align cache (Playback partial),
    //             SMPL/SMAL smoothing state (HumanSmpl / AnimalPoseApplier)
    // Provides: shot boundary detection and the per-shot reset of carried-over track state

    private ShotBoundaries shotBoundaries = ShotBoundaries.Empty;
    private int lastAppliedShotIndex = -1;

    private void ApplyLoadedShotBoundaries(ShotBoundaries loadedShotBoundaries)
    {
        shotBoundaries = loadedShotBoundaries ?? ShotBoundaries.Empty;
        lastAppliedShotIndex = -1;
        Debug.Log($"[Shot] shots={shotBoundaries.Count}");
    }

    // 表示フレームが別の shot に入ったら、track ごとに持ち越している「前フレームからの
    // 連続性を前提にした状態」を捨てる。カットが変わると同じ trackId でもカメラ距離が
    // 変わり、bbox から求まる見かけサイズが正当に別の値になるが、スケールは
    // GetOrLockModelLocalScale で track ごとに初回ロックされるため、リセットしないと
    // 前 shot のサイズのまま新しい shot に貼り付いて極端な大きさで表示される。
    //
    // シーク時も shot index の変化として検出される。同一 shot 内へのシークは
    // カメラが連続しているためリセット不要。
    private void SyncShotBoundaryForFrame(int frame)
    {
        int shotIndex = shotBoundaries.ResolveShotIndex(frame);
        if (shotIndex == lastAppliedShotIndex)
        {
            return;
        }

        bool hasPreviousShot = lastAppliedShotIndex >= 0;
        lastAppliedShotIndex = shotIndex;
        if (!hasPreviousShot)
        {
            // bundle ロード直後の最初の 1 フレーム。持ち越している状態がないので何もしない。
            return;
        }

        Debug.Log($"[Shot] boundary crossed. frame={frame} shotIndex={shotIndex} startFrame={shotBoundaries.GetStartFrame(shotIndex)}");
        ResetPerShotTrackState();
    }

    // bundle の shot_boundary_policy.unity_guidance:
    //   "Do not interpolate or spring position/scale across a shot boundary for the same
    //    trackId; snap to the new shot's first-frame anchor instead."
    // モデルのボーン解決結果 (HumanoidRigCache / AnimalRigCache)、ユーザーが選んだモデル、
    // 手動 yaw キーフレームは shot とは無関係なので触らない。
    private void ResetPerShotTrackState()
    {
        // 主対象: 前 shot のカメラ距離で確定した表示スケール。
        lockedModelLocalScaleByTrack.Clear();
        // スケールを測り直したかどうかも shot ごと（GetOrLockModelLocalScale でも外れるが、
        // ロックを経由せず消えるケースに備えてここでもクリアする）。
        scaleRefinedByTrack.Clear();
        // ⑧ の深度補正比率も前 shot の値を引きずらせない。
        smoothedProjectedDepthRatioByTrack.Clear();

        // 位置・向きの平滑化。前 shot の値から補間すると新しいカットの先頭で滑り込む。
        smoothedJointsByTrack.Clear();
        personRootYawForwardByRoot.Clear();
        animalPoseApplier.ResetMotionState();
        ResetHumanSmplSmoothingForShotBoundary();

        // 前 shot の bbox を基準にした下端合わせのホールド。
        lastGoodBottomAlignArea.Clear();
        lastGoodBottomAlignVEye.Clear();

        ResetHumanOtherContactStateForShotBoundary();
    }
}
