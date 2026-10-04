using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // AimAt の目標を SMPL から作る（2026-10-03、既定 OFF: aimAtTargetFromSmpl / centeredSmplRotationFilter）。
    //
    // 今の AimAt は meta.bin の keypoints3d の 2 点間で四肢の向きを決める。その keypoints には生成側が
    // 因果 EMA（新しい値の重み 0.2）を掛けているので、速い動き（2 Hz）は 0.54 倍に縮み約 0.1 s 遅れる
    // （Docs/smpl-retargeting.md「2026-10-02: Human の動きが少ない」）。同じ bundle の SMPL block は平滑されていない。
    // keypoints は SMPL の当てはめから計算された点なので、四肢の区間の向きは
    // 「その関節の world 回転 G_j × 定数 d_rest」で表せる（生の keypoints で区間ごとのばらつき 0.06〜0.11°）。
    // d_rest は bundle から推定する（G_j⁻¹ × keypoint の区間方向の平均。EMA の遅れは平均で打ち消し合う）。
    //
    // AimAt は残す。FK の基準姿勢（muscles=0）が膝・肘の曲がった姿勢なので、FK だけでは四肢の向きが決まらない。
    // 差し替えるのは目標の向きだけ。

    private struct SmplAimSegment
    {
        public readonly string name;
        // 区間の向きを回す SMPL joint。Body25→SMPL24 の並べ替え後の keypoint の始点も同じ番号になる。
        public readonly int joint;
        public readonly int childKp;
        public readonly HumanBodyBones prox;
        public readonly HumanBodyBones dist;

        public SmplAimSegment(string name, int joint, int childKp, HumanBodyBones prox, HumanBodyBones dist)
        {
            this.name = name;
            this.joint = joint;
            this.childKp = childKp;
            this.prox = prox;
            this.dist = dist;
        }
    }

    // 並びは今の AimAt（TryApplySmplArmsFromJointPositions → TryApplySmplLegsFromJointPositions）と同じ。
    // 親の区間を先に回すので、子の区間は親が動いたあとの位置から目標へ向けられる。
    private static readonly SmplAimSegment[] SmplAimSegments =
    {
        new SmplAimSegment("LUpArm", SmplLeftShoulder, SmplLeftElbow, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
        new SmplAimSegment("LForeArm", SmplLeftElbow, SmplLeftWrist, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
        new SmplAimSegment("RUpArm", SmplRightShoulder, SmplRightElbow, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
        new SmplAimSegment("RForeArm", SmplRightElbow, SmplRightWrist, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
        new SmplAimSegment("LThigh", SmplLeftHip, SmplLeftKnee, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg),
        new SmplAimSegment("LShin", SmplLeftKnee, SmplLeftAnkle, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
        new SmplAimSegment("RThigh", SmplRightHip, SmplRightKnee, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
        new SmplAimSegment("RShin", SmplRightKnee, SmplRightAnkle, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
        new SmplAimSegment("LFoot", SmplLeftAnkle, SmplLeftFoot, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes),
        new SmplAimSegment("RFoot", SmplRightAnkle, SmplRightFoot, HumanBodyBones.RightFoot, HumanBodyBones.RightToes),
    };

    private const int SmplAimSegLeftForeArm = 1;
    private const int SmplAimSegRightForeArm = 3;

    // SMPL24 の番号 → Body25（HMR2 の 44 点の先頭 25 点）の番号。RemapHmr2Body25ToSmpl24 と同じ対応。
    private static readonly int[] Smpl24FromBody25 =
    {
        8, 12, 9, -1, 13, 10, -1, 14, 11, -1, 19, 22, 1, -1, -1, 0, 5, 2, 6, 3, 7, 4, -1, -1
    };

    // d_rest の推定に使うフレーム数の上限（等間隔に間引く）。2167 フレームの bundle_human で 1 フレームおき 3 程度。
    private const int SmplAimRestMaxSampleFrames = 720;
    // これより少ないサンプルの区間は推定が当てにならないので、その track は keypoint の AimAt のまま。
    // 推定誤差は 30 フレームで最大 14.5°、300 フレームで最大 3.9°（Docs/log-analysis/reinvestigation_20261002/H2 の s24）。
    private const int SmplAimRestMinSamples = 120;

    private readonly Dictionary<uint, Vector3[]> smplAimRestDirByTrack = new Dictionary<uint, Vector3[]>();
    private string smplAimRestDirMetaPath;

    private static readonly float[] CenteredSmplWeights5 = { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
    private static readonly float[] CenteredSmplWeights3 = { 0f, 1f / 4f, 2f / 4f, 1f / 4f, 0f };

    // track の区間ごとの d_rest（SMPL の関節フレームでの区間方向）。推定できなければ null。
    private Vector3[] GetSmplAimRestDirections(uint trackId)
    {
        if (!metaLoaded)
        {
            return null;
        }

        if (smplAimRestDirMetaPath != metaFilePath)
        {
            EstimateSmplAimRestDirections();
        }

        return smplAimRestDirByTrack.TryGetValue(trackId, out Vector3[] dirs) ? dirs : null;
    }

    // bundle 全体を等間隔に読み、person track ごとに c = (globalOrient × bodyFk[j])⁻¹ × 区間方向 を平均する。
    // runtime の world 回転は camRotation × globalOrient、keypoint は camRotation × jointsCam なので、
    // カメラ回転は両辺で打ち消し合い、カメラ座標のまま推定してよい。
    private void EstimateSmplAimRestDirections()
    {
        smplAimRestDirMetaPath = metaFilePath;
        smplAimRestDirByTrack.Clear();
        // 実機ではこれが再生中の最初のフレームで走る。かかった時間を出して、固まりの大きさを確かめる。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int startFrame = GetCurrentPlaybackFrame();
        int numFrames = (int)metaHeader.numFrames;
        if (numFrames <= 0)
        {
            return;
        }

        var sums = new Dictionary<uint, Vector3[]>();
        var counts = new Dictionary<uint, int[]>();
        var buffer = new List<MetaObj>(16);
        var bodyFk = new Quaternion[22];
        int step = Mathf.Max(1, numFrames / SmplAimRestMaxSampleFrames);
        int framesRead = 0;
        for (int frame = 0; frame < numFrames; frame += step)
        {
            // 再生中のフレームの SMPL を消さないよう、元から無かったフレームの分だけ読み終えたら捨てる。
            bool hadHuman = humanSmplPosesMetaBin.ContainsKey(frame);
            bool hadAnimal = animalSmalPosesMetaBin.ContainsKey(frame);
            bool read = TryReadFrameObjectsRaw(frame, buffer);
            if (read)
            {
                framesRead++;
                for (int i = 0; i < buffer.Count; i++)
                {
                    MetaObj obj = buffer[i];
                    if (!IsCategoryPerson(obj.categoryId) || !obj.hasSkeleton || obj.jointsCam == null ||
                        !TryGetHumanSmplPose(frame, obj.trackId, out HumanSmplPose pose) ||
                        !pose.hasGlobalOrient || pose.bodyPose == null)
                    {
                        continue;
                    }

                    AccumulateSmplBodyFk(pose, bodyFk);
                    if (!sums.TryGetValue(obj.trackId, out Vector3[] sum))
                    {
                        sum = new Vector3[SmplAimSegments.Length];
                        sums[obj.trackId] = sum;
                        counts[obj.trackId] = new int[SmplAimSegments.Length];
                    }

                    int[] count = counts[obj.trackId];
                    for (int s = 0; s < SmplAimSegments.Length; s++)
                    {
                        SmplAimSegment seg = SmplAimSegments[s];
                        if (!TryGetSmpl24KeypointCam(obj, seg.joint, out Vector3 a) ||
                            !TryGetSmpl24KeypointCam(obj, seg.childKp, out Vector3 b))
                        {
                            continue;
                        }

                        Vector3 u = b - a;
                        if (u.sqrMagnitude < 1e-8f)
                        {
                            continue;
                        }

                        Quaternion g = pose.globalOrient * bodyFk[seg.joint];
                        Vector3 c = Quaternion.Inverse(g) * u.normalized;
                        if (!IsFinite(c))
                        {
                            continue;
                        }

                        sum[s] += c;
                        count[s]++;
                    }
                }
            }

            if (!hadHuman)
            {
                humanSmplPosesMetaBin.Remove(frame);
            }

            if (!hadAnimal)
            {
                animalSmalPosesMetaBin.Remove(frame);
            }
        }

        foreach (KeyValuePair<uint, Vector3[]> kv in sums)
        {
            int[] count = counts[kv.Key];
            var dirs = new Vector3[SmplAimSegments.Length];
            bool ok = true;
            var sb = new System.Text.StringBuilder();
            for (int s = 0; s < SmplAimSegments.Length; s++)
            {
                if (count[s] < SmplAimRestMinSamples || kv.Value[s].sqrMagnitude < 1e-8f)
                {
                    ok = false;
                }
                else
                {
                    dirs[s] = kv.Value[s].normalized;
                }

                // 平均ベクトルの長さ（1 に近いほど揃っている）も出す。EMA の遅れで揃いが崩れる区間が分かる。
                float coherence = count[s] > 0 ? kv.Value[s].magnitude / count[s] : 0f;
                sb.Append($" {SmplAimSegments[s].name}=({dirs[s].x:F3},{dirs[s].y:F3},{dirs[s].z:F3}) n={count[s]} R={coherence:F3}");
            }

            if (ok)
            {
                smplAimRestDirByTrack[kv.Key] = dirs;
            }

            Debug.Log($"[SMPLAIM] rest track={kv.Key} framesRead={framesRead} step={step} use={ok}{sb}");
        }

        Debug.Log($"[PRECOMPUTE] smplAimRest ms={stopwatch.Elapsed.TotalMilliseconds:F0} framesRead={framesRead} atFrame={startFrame}");
    }

    // body_pose だけを world 空間で積む（TryApplyHumanSmplRotationOverlay の bodyFk と同じ式。平滑なし）。
    private static void AccumulateSmplBodyFk(HumanSmplPose pose, Quaternion[] bodyFk)
    {
        bodyFk[0] = Quaternion.identity;
        for (int i = 0; i < SmplJointTopologicalOrder.Length; i++)
        {
            int joint = SmplJointTopologicalOrder[i];
            TryGetHumanSmplLocalRotation(pose, joint, out Quaternion local);
            bodyFk[joint] = bodyFk[SmplJointParentArray[joint]] * local;
        }
    }

    // meta.bin の keypoint（カメラ座標・root 相対）を SMPL24 の番号で取り出す。
    // 25 点以上なら Body25 → SMPL24 の対応で読む（runtime の RemapHmr2Body25ToSmpl24 と同じ。可視フラグは見ない）。
    private static bool TryGetSmpl24KeypointCam(MetaObj obj, int smplIndex, out Vector3 p)
    {
        p = Vector3.zero;
        int count = obj.skeletonKpCount;
        int src = smplIndex;
        if (count >= 25)
        {
            if (smplIndex < 0 || smplIndex >= Smpl24FromBody25.Length)
            {
                return false;
            }

            src = Smpl24FromBody25[smplIndex];
        }
        else if (obj.jointsVis == null || smplIndex >= obj.jointsVis.Length || obj.jointsVis[smplIndex] == 0)
        {
            return false;
        }

        if (src < 0 || src >= count || obj.jointsCam == null || src >= obj.jointsCam.Length)
        {
            return false;
        }

        p = obj.jointsCam[src];
        return IsFinite(p);
    }

    // SMPL の回転を前後 2 フレームの中心 5tap [1,4,6,4,1]/16 で平均した姿勢を返す（遅れ 0）。
    // 窓は shot 境界と track の途切れをまたがない。両側で使える半径に揃える（片側だけだと位相がずれる）。
    // 半径 1 なら [1,2,1]/4、半径 0 なら元の値。
    private HumanSmplPose BuildCenteredSmplPose(int frame, uint trackId, HumanSmplPose center)
    {
        if (center.bodyPose == null || !center.hasGlobalOrient)
        {
            return center;
        }

        int shot = shotBoundaries.ResolveShotIndex(frame);
        var window = new HumanSmplPose[5];
        var has = new bool[5];
        window[2] = center;
        has[2] = true;
        for (int k = -2; k <= 2; k++)
        {
            if (k == 0)
            {
                continue;
            }

            int f = frame + k;
            if (f < 0 || f >= (int)metaHeader.numFrames || shotBoundaries.ResolveShotIndex(f) != shot)
            {
                continue;
            }

            // 前後のフレームはふつう RepairIsolatedBBoxSpikes が先読み済み。無ければここで読む。
            if (!TryGetHumanSmplPose(f, trackId, out HumanSmplPose p) &&
                (!TryReadFrameObjectsMemo(f, out _) || !TryGetHumanSmplPose(f, trackId, out p)))
            {
                continue;
            }

            if (!p.hasGlobalOrient || p.bodyPose == null || p.bodyPose.Length != center.bodyPose.Length)
            {
                continue;
            }

            window[k + 2] = p;
            has[k + 2] = true;
        }

        if (!has[1])
        {
            has[0] = false;
        }

        if (!has[3])
        {
            has[4] = false;
        }

        float[] weights;
        if (has[0] && has[1] && has[3] && has[4])
        {
            weights = CenteredSmplWeights5;
        }
        else if (has[1] && has[3])
        {
            weights = CenteredSmplWeights3;
        }
        else
        {
            return center;
        }

        HumanSmplPose result = center;
        result.globalOrient = AverageSmplWindow(window, weights, -1, center.globalOrient);
        result.bodyPose = new Quaternion[center.bodyPose.Length];
        for (int i = 0; i < center.bodyPose.Length; i++)
        {
            result.bodyPose[i] = AverageSmplWindow(window, weights, i, center.bodyPose[i]);
        }

        return result;
    }

    // 符号を中心に揃えた重み付き和を正規化する（小さな角度の範囲では Slerp の平均と同じ）。
    private static Quaternion AverageSmplWindow(HumanSmplPose[] window, float[] weights, int bodyIndex, Quaternion center)
    {
        float x = 0f, y = 0f, z = 0f, w = 0f;
        for (int k = 0; k < 5; k++)
        {
            if (weights[k] <= 0f)
            {
                continue;
            }

            Quaternion q = bodyIndex < 0 ? window[k].globalOrient : window[k].bodyPose[bodyIndex];
            if (!IsFinite(q))
            {
                q = center;
            }

            float sign = Quaternion.Dot(q, center) < 0f ? -1f : 1f;
            x += weights[k] * sign * q.x;
            y += weights[k] * sign * q.y;
            z += weights[k] * sign * q.z;
            w += weights[k] * sign * q.w;
        }

        float mag = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        if (mag < 1e-6f)
        {
            return center;
        }

        return new Quaternion(x / mag, y / mag, z / mag, w / mag);
    }

    // AimAt（SMPL の目標版）。目標の向き = worldGlobalOrient × bodyFk[j] × d_rest。
    // worldGlobalOrient と bodyFk は直前の TryApplyHumanSmplRotationOverlay が積んだもの（平滑の掛かった回転）。
    private bool TryApplySmplLimbsFromSmplTarget(HumanoidRigCache cache, Vector3[] restDirs, Vector3[] jointsWorld, byte[] vis, int frame)
    {
        if (restDirs == null ||
            !humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState state) ||
            state == null || !state.hasLastFk)
        {
            return false;
        }

        bool log = logSmplAimTarget && frame % Mathf.Max(1, logSmplAimTargetEveryNFrames) == 0 && frame != lastSmplAimLogFrame;
        System.Text.StringBuilder sb = log ? new System.Text.StringBuilder() : null;
        for (int s = 0; s < SmplAimSegments.Length; s++)
        {
            SmplAimSegment seg = SmplAimSegments[s];
            Vector3 targetDir = state.lastWorldGlobalOrient * (state.smplFk[seg.joint] * restDirs[s]);
            if (IsFootSegment(seg.prox))
            {
                ApplyFootSegmentDirection(cache, seg.prox, seg.dist, targetDir);
            }
            else
            {
                ApplyHumanoidSegmentDirection(cache, seg.prox, seg.dist, targetDir);
            }

            if (sb != null)
            {
                // keypoint の区間方向（今の AimAt の目標）との角度。生成側の EMA の遅れがここに出る。
                float diff = -1f;
                if (TrackedJointPoints.TryGet(jointsWorld, vis, seg.joint, out Vector3 a) &&
                    TrackedJointPoints.TryGet(jointsWorld, vis, seg.childKp, out Vector3 b) &&
                    (b - a).sqrMagnitude > 1e-8f)
                {
                    diff = Vector3.Angle(targetDir, b - a);
                }

                sb.Append($" {seg.name}={diff:F1}");
            }
        }

        if (sb != null)
        {
            lastSmplAimLogFrame = frame;
            Debug.Log($"[SMPLAIM] f={frame} vsKeypoint{sb}");
        }

        return true;
    }

    private int lastSmplAimLogFrame = -1;

    // 手の FK（SMPL の目標版）。前腕の向きを keypoint の肘→手首ではなく SMPL の目標から取る。
    // keypoint のままだと、前腕は SMPL で動くのに手だけ EMA のぶん遅れる。
    private void TryApplyHandFkAfterSmplAimAt(HumanoidRigCache cache, Vector3[] restDirs)
    {
        if (!EnableHumanSmplMotion || cache == null || !cache.ready || restDirs == null) return;
        if (!humanSmplRetargetStateByCache.TryGetValue(cache, out HumanSmplRetargetState state) || state == null) return;
        if (!state.smoothingInitialized || !state.hasLastFk) return;
        if (TryApplyHandFromForearmFrame(cache)) return;

        Vector3 leftArm = state.lastWorldGlobalOrient * (state.smplFk[SmplLeftElbow] * restDirs[SmplAimSegLeftForeArm]);
        Vector3 rightArm = state.lastWorldGlobalOrient * (state.smplFk[SmplRightElbow] * restDirs[SmplAimSegRightForeArm]);
        ApplyHandFkWithCanonicalArmDirection(cache, state, HumanBodyBones.LeftHand, 20, leftArm);
        ApplyHandFkWithCanonicalArmDirection(cache, state, HumanBodyBones.RightHand, 21, rightArm);
    }
}
