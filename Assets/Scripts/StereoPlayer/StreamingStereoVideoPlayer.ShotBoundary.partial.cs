using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: track instance/model state (Model.cs), bbox bottom-align cache (Playback partial),
    //             SMPL/SMAL smoothing state (HumanSmpl / AnimalPoseApplier)
    // Provides: shot boundary detection and the per-shot reset of carried-over track state

    private ShotBoundaries shotBoundaries = ShotBoundaries.Empty;
    private int lastAppliedShotIndex = -1;

    // 「短い shot」の上限（frame）。bundle_animal のカット検出の偽陽性（走る犬）は 2〜14 frame の shot が連続した。
    // 本物のカットで区切られた shot はこの bundle では最短 22 frame。
    private const int KeepScaleShortShotMaxFrames = 15;

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

        int previousShotIndex = lastAppliedShotIndex;
        lastAppliedShotIndex = shotIndex;
        if (previousShotIndex < 0)
        {
            // bundle ロード直後の最初の 1 フレーム。持ち越している状態がないので何もしない。
            return;
        }

        bool keepScale = keepScaleAcrossContinuousShotBoundary && IsFalseCutBoundaryChain(previousShotIndex, shotIndex);
        Debug.Log($"[Shot] boundary crossed. frame={frame} shotIndex={shotIndex} startFrame={shotBoundaries.GetStartFrame(shotIndex)} keepScale={keepScale}");
        // 記録だけ（2026-10-09、logPlacementMeasurement）: 前の shot で倍率の測り直しが一度も通らなかった track を出す。ResetPerShotTrackState が
        // scaleRefinedByTrack を消す前に呼ぶ。
        LogScaleRefineNeverPassedAndClear(previousShotIndex);
        ResetPerShotTrackState(keepScale);
    }

    // 2026-09-18: manifest の shot 境界のうち「偽のカット」（カット検出の偽陽性）とみなすもの。
    // 条件は 3 つとも必要:
    //   1. 順方向に隣の shot へ移った（戻りと 2 つ以上の飛びは除く。隣へ着地する順方向シークは通るが、
    //      3 の判定は境界の frame のデータで行うので結果は通し再生と同じ）
    //   2. 境界のどちらか側の shot が短い（KeepScaleShortShotMaxFrames 以下）。
    //      カット検出の偽陽性は動きの速い区間で短い shot を連発する。本物のカットは 1 秒以上の shot で区切られる
    //   3. 境界の frame（新しい shot の先頭）とその 1 つ前で、両方に写っている track の bbox が連続している
    // bbox の連続だけでは足りない: bundle_animal では本物のカット 151 / 801 / 1034 / 1435 なども
    // （被写体を中央に置く撮り方のため）bbox が連続と判定される。shot の長さだけでも足りない:
    // 本物のカット 1104 の次の shot は 13 frame。両方を要求すると、この bundle では
    // 280 / 290 / 520 / 1117 / 1127 / 1130 / 1144（目視で全部同じテイク）だけが残る。
    private bool IsFalseCutBoundary(int previousShotIndex, int shotIndex)
    {
        if (shotIndex != previousShotIndex + 1)
        {
            return false;
        }

        int totalFrames = (int)metaHeader.numFrames;
        bool previousShort = shotBoundaries.GetShotLength(previousShotIndex, totalFrames) <= KeepScaleShortShotMaxFrames;
        bool nextShort = shotBoundaries.GetShotLength(shotIndex, totalFrames) <= KeepScaleShortShotMaxFrames;
        if (!previousShort && !nextShort)
        {
            return false;
        }

        return IsShotBoundaryBBoxContinuous(shotBoundaries.GetStartFrame(shotIndex));
    }

    // 2026-10-07（falseCutChainKeepsScale、既定 OFF、既存の欠陥の直し）: 順方向に 2 つ以上の shot をまとめて越えた（コマ落ち・シーク）とき、
    // 間の境界がすべて偽のカット（IsFalseCutBoundary の 3 条件を境界ごとに、その境界のフレームで判定）なら同じテイクとみなして scale を持ち越す。
    // 今は隣の shot へ 1 つ進んだときしか認めないので、bundle_animal の 280 と 290（10 frame しか離れていない）を一度に越えると
    // f290 の bbox でロックし直し、同じ場面のモデルの大きさが着地のフレームによって ×2.5〜3.8 変わる（10/03 の撮影の回: frame=298 shotIndex=5
    // startFrame=290 keepScale=False）。戻る向きは今までどおり持ち越さない。C4-T の表の区切り（隣どうしの判定）はこれを使わない。
    // 種別は問わない（隣どうしの判定と同じ。animal だけにすると、同じ境界を 1 つずつ越えたときとまとめて越えたときで Human の扱いが食い違う）。
    // 今ある bundle で偽のカットが続くのは bundle_animal（280・290 と 1117・1127・1130・1144）だけで、human・car・tutorial は shot が 1 つ。
    private bool IsFalseCutBoundaryChain(int previousShotIndex, int shotIndex)
    {
        if (!falseCutChainKeepsScale || previousShotIndex < 0 || shotIndex <= previousShotIndex + 1)
        {
            return IsFalseCutBoundary(previousShotIndex, shotIndex);
        }

        for (int s = previousShotIndex + 1; s <= shotIndex; s++)
        {
            if (!IsFalseCutBoundary(s - 1, s))
            {
                return false;
            }
        }

        return true;
    }

    // 境界の前後（boundaryFrame-1 と boundaryFrame）で、両方に写っている track の bbox がすべて「同じ位置・同じ大きさ」なら true。
    // 両方に写っている track が無ければ false（新しい track は初回ロックなので判定は要らない）。
    private bool IsShotBoundaryBBoxContinuous(int boundaryFrame)
    {
        if (boundaryFrame <= 0 ||
            !TryReadFrameObjectsMemo(boundaryFrame - 1, out List<MetaObj> prev) ||
            !TryReadFrameObjectsMemo(boundaryFrame, out List<MetaObj> cur))
        {
            return false;
        }

        bool anyShared = false;
        for (int i = 0; i < cur.Count; i++)
        {
            MetaObj c = cur[i];
            if (!TryFindTrackObject(prev, c.trackId, out MetaObj p) || p.bboxH <= 0 || p.bboxW <= 0)
            {
                continue;
            }
            anyShared = true;
            float heightRatio = (float)c.bboxH / p.bboxH;
            float dx = (c.bboxX + c.bboxW * 0.5f) - (p.bboxX + p.bboxW * 0.5f);
            float dy = (c.bboxY + c.bboxH * 0.5f) - (p.bboxY + p.bboxH * 0.5f);
            float tolerance = 0.5f * Mathf.Max(p.bboxW, p.bboxH);
            if (heightRatio < 0.67f || heightRatio > 1.5f || Mathf.Abs(dx) > tolerance || Mathf.Abs(dy) > tolerance)
            {
                return false;
            }
        }
        return anyShared;
    }

    // bundle の shot_boundary_policy.unity_guidance:
    //   "Do not interpolate or spring position/scale across a shot boundary for the same
    //    trackId; snap to the new shot's first-frame anchor instead."
    // モデルのボーン解決結果 (HumanoidRigCache / AnimalRigCache)、ユーザーが選んだモデル、
    // 手動 yaw キーフレームは shot とは無関係なので触らない。
    private void ResetPerShotTrackState(bool keepScale = false)
    {
        if (!keepScale)
        {
            // 主対象: 前 shot のカメラ距離で確定した表示スケール。
            lockedModelLocalScaleByTrack.Clear();
            // スケールを測り直したかどうかも shot ごと（GetOrLockModelLocalScale でも外れるが、
            // ロックを経由せず消えるケースに備えてここでもクリアする）。
            scaleRefinedByTrack.Clear();
            // 補正倍率もここで捨てる。**モデル差し替えでは持ち越すが、shot 境界では持ち越さない。**
            // カットが変われば被写体の典型的な姿勢も変わるので、測り直すのが正しい。
            scaleRefineFactorByTrack.Clear();
            scaleRefineFactorPrefabByTrack.Clear();
            // animalPlaceClippedFromFullBody: 測り直しが倍率に入れた体全体の gain も同じ寿命（モデル差し替えでは持ち越し、shot 境界で捨てる）。
            animalFullBodyGainByTrack.Clear();
            // ⑧ の深度補正比率も前 shot の値を引きずらせない。
            smoothedProjectedDepthRatioByTrack.Clear();
        }
        // ⑨ の深度差の平滑化も shot をまたがせない。
        otherDepthGapByTrack.Clear();

        // 位置・向きの平滑化。前 shot の値から補間すると新しいカットの先頭で滑り込む。
        smoothedJointsByTrack.Clear();
        personRootYawForwardByRoot.Clear();
        animalPoseApplier.ResetMotionState();
        ResetHumanSmplSmoothingForShotBoundary();

        // 前 shot の bbox を基準にした下端合わせのホールド。
        lastGoodBottomAlignArea.Clear();
        lastGoodBottomAlignVEye.Clear();
        // 下端が切れているかのヒステリシスの状態も shot をまたがせない（偽のカットでも消す。bboxBottomClipHysteresis）。
        bboxBottomClippedByTrack.Clear();

        ResetHumanOtherContactStateForShotBoundary();
        // Else の連結順と向きの記憶も shot をまたがせない。
        ResetElseChainStateForShotBoundary();
        ResetElseFrameOutStateForShotBoundary();
    }
}
