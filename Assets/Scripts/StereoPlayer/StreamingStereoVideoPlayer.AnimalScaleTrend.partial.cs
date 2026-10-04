using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Animal の接近を scale でも追う（C4-T、2026-10-03、既定 OFF: animalScaleFollowApproachTrend）。
    //
    // 今は scale を shot 先頭で固定し、その後の大きさの変化を奥行き（⑧、τ 1.2 s）だけで追う。遠くから
    // 走ってくる犬は bbox が ×2.8〜3.5 になるのにモデルは ×1.6〜2.1 しか伸びず、見た目が bbox の 0.66 / 0.82 倍に
    // とどまる。奥行きだけで合わせるとモデルが目から 0.25 m まで寄る（Docs/bundle-placement.md「2026-10-02: Animal の接近」）。
    //
    // 倍率 F = 目標高（見切れを補った bbox 高）を log で ±W frame 平均した比 × anchorZ(t) / anchorZ(ロック)。
    // 「bbox 高 × 深度」の形なので奥行きと二重に数えない。姿勢の伸び（うずくまり・跳躍）でも bbox は変わるので、
    // SMAL block の transl z から作る 1/tz（AniMer が体を姿勢込みで当てはめた見かけの大きさ）も同じ向きに
    // 不感帯（±25%）を超えたときだけ効かせる。効かせる量は F と 1/tz の不感帯を超えた分の小さい方。
    // 先読みは meta.bin を読み込み時に走査した表で行う（runtime は先のフレームを既に持っている）。
    // 試算（A2 の c4t.py）: 接近 2 区間で骨格の大きさ比 0.60 / 0.83 → 0.92 / 0.94、体の深度の最小 0.53 m、
    // 接近以外の区間は ×0.99〜1.00（猫のうずくまり f1435〜1612 も ×1.00）。跳躍（f470〜500）は 1.17〜1.26。

    private sealed class ScaleTrendTrack
    {
        public float[] logTarget;   // log(見切れを補った目標高 px)。無ければ NaN
        public float[] negLogTz;    // −log(SMAL transl z)。無ければ NaN
        public float[] anchorZ01;   // 配置深度の元（毎回デコードし直す）。無ければ NaN
    }

    private readonly Dictionary<uint, ScaleTrendTrack> scaleTrendTracks = new Dictionary<uint, ScaleTrendTrack>();
    private int[] scaleTrendSegmentByFrame;
    private string scaleTrendMetaPath;
    // スケールをロックしたときの基準フレーム（TryResolveShotStartScaleReference が使った shot 先頭、無ければロックしたフレーム）。
    private readonly Dictionary<uint, int> scaleLockReferenceFrameByTrack = new Dictionary<uint, int>();
    private readonly Dictionary<uint, int> lastScaleTrendLogFrameByTrack = new Dictionary<uint, int>();

    private void EnsureScaleTrendTable()
    {
        if (!metaLoaded || scaleTrendMetaPath == metaFilePath)
        {
            return;
        }

        scaleTrendMetaPath = metaFilePath;
        scaleTrendTracks.Clear();
        // 実機ではこれが再生中の最初のフレームで走る。かかった時間を出して、固まりの大きさを確かめる。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int startFrame = GetCurrentPlaybackFrame();
        int numFrames = (int)metaHeader.numFrames;
        if (numFrames <= 0)
        {
            return;
        }

        // スケールのロックが外れる区切り（本物のカット）ごとに区間番号を振る。偽のカット
        // （keepScaleAcrossContinuousShotBoundary && IsFalseCutBoundary）ではロックが持ち越されるので区切らない。
        int shotCount = shotBoundaries.Count;
        var segmentByShot = new int[Mathf.Max(1, shotCount)];
        for (int s = 1; s < shotCount; s++)
        {
            bool keeps = keepScaleAcrossContinuousShotBoundary && IsFalseCutBoundary(s - 1, s);
            segmentByShot[s] = segmentByShot[s - 1] + (keeps ? 0 : 1);
        }

        scaleTrendSegmentByFrame = new int[numFrames];
        var buffer = new List<MetaObj>(16);
        for (int frame = 0; frame < numFrames; frame++)
        {
            int shot = shotBoundaries.ResolveShotIndex(frame);
            scaleTrendSegmentByFrame[frame] = shot >= 0 && shot < segmentByShot.Length ? segmentByShot[shot] : 0;

            bool hadHuman = humanSmplPosesMetaBin.ContainsKey(frame);
            bool hadAnimal = animalSmalPosesMetaBin.ContainsKey(frame);
            if (TryReadFrameObjectsRaw(frame, buffer))
            {
                for (int i = 0; i < buffer.Count; i++)
                {
                    MetaObj obj = buffer[i];
                    if (!IsCategoryAnimal(obj.categoryId) || obj.bboxH <= 0)
                    {
                        continue;
                    }

                    if (!scaleTrendTracks.TryGetValue(obj.trackId, out ScaleTrendTrack t))
                    {
                        t = new ScaleTrendTrack
                        {
                            logTarget = NewNaNArray(numFrames),
                            negLogTz = NewNaNArray(numFrames),
                            anchorZ01 = NewNaNArray(numFrames)
                        };
                        scaleTrendTracks[obj.trackId] = t;
                    }

                    float target = ResolveUnclippedTargetHeight(obj, obj.bboxH);
                    if (target > 0f)
                    {
                        t.logTarget[frame] = Mathf.Log(target);
                    }

                    t.anchorZ01[frame] = obj.anchorZ01;
                    if (TryGetAnimalSmalPose(frame, obj.trackId, out AnimalSmalPose smal) && smal.hasTransl && smal.transl.z > 0f)
                    {
                        t.negLogTz[frame] = -Mathf.Log(smal.transl.z);
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

        Debug.Log($"[SCALETREND] table frames={numFrames} animalTracks={scaleTrendTracks.Count} segments={(shotCount > 0 ? segmentByShot[shotCount - 1] + 1 : 1)}");
        Debug.Log($"[PRECOMPUTE] scaleTrendTable ms={stopwatch.Elapsed.TotalMilliseconds:F0} frames={numFrames} atFrame={startFrame}");
    }

    private static float[] NewNaNArray(int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = float.NaN;
        }

        return a;
    }

    // 同じ区間の中だけで ±halfWidth の平均（無い値は飛ばす）。
    private bool TryScaleTrendWindowMean(float[] values, int center, int halfWidth, out float mean)
    {
        mean = 0f;
        if (values == null || scaleTrendSegmentByFrame == null || center < 0 || center >= values.Length)
        {
            return false;
        }

        int segment = scaleTrendSegmentByFrame[center];
        float sum = 0f;
        int n = 0;
        for (int g = center - halfWidth; g <= center + halfWidth; g++)
        {
            if (g < 0 || g >= values.Length || scaleTrendSegmentByFrame[g] != segment || float.IsNaN(values[g]))
            {
                continue;
            }

            sum += values[g];
            n++;
        }

        if (n == 0)
        {
            return false;
        }

        mean = sum / n;
        return true;
    }

    // 平滑した目標高（px）。B1（refineLockedScaleAgainstSmoothedTarget）がロック時の基準に使う。
    private bool TryGetAnimalTrendSmoothedTargetHeight(uint trackId, int frame, out float targetPx)
    {
        targetPx = 0f;
        EnsureScaleTrendTable();
        if (!scaleTrendTracks.TryGetValue(trackId, out ScaleTrendTrack t) ||
            !TryScaleTrendWindowMean(t.logTarget, frame, Mathf.Max(1, animalScaleTrendWindowFrames), out float mean))
        {
            return false;
        }

        targetPx = Mathf.Exp(mean);
        return targetPx > 0f;
    }

    private void RecordScaleLockReferenceFrame(uint trackId, int frame)
    {
        scaleLockReferenceFrameByTrack[trackId] = frame;
    }

    // 不感帯の外に出た分だけを返す（x / clamp(x, 1/dz, dz)）。
    private static float ScaleTrendGate(float x, float deadZone)
    {
        return x / Mathf.Clamp(x, 1f / deadZone, deadZone);
    }

    // その frame の scale の倍率。animal 以外・フラグ OFF・材料が足りないときは 1。
    private float ResolveAnimalScaleTrendFactor(MetaObj obj, int frame, float lockedScale)
    {
        if (!animalScaleFollowApproachTrend || !IsCategoryAnimal(obj.categoryId))
        {
            return 1f;
        }

        EnsureScaleTrendTable();
        if (!scaleTrendTracks.TryGetValue(obj.trackId, out ScaleTrendTrack t) ||
            !scaleLockReferenceFrameByTrack.TryGetValue(obj.trackId, out int lockFrame) ||
            scaleTrendSegmentByFrame == null || frame < 0 || frame >= scaleTrendSegmentByFrame.Length ||
            lockFrame < 0 || lockFrame >= scaleTrendSegmentByFrame.Length ||
            scaleTrendSegmentByFrame[frame] != scaleTrendSegmentByFrame[lockFrame])
        {
            return 1f;
        }

        int w = Mathf.Max(1, animalScaleTrendWindowFrames);
        float dz = Mathf.Max(1.01f, animalScaleTrendDeadZone);
        if (!TryScaleTrendWindowMean(t.logTarget, frame, w, out float smF) ||
            !TryScaleTrendWindowMean(t.logTarget, lockFrame, w, out float smL) ||
            float.IsNaN(t.anchorZ01[lockFrame]))
        {
            return 1f;
        }

        float zLock = DecodeAnchorDepthMetersFromBundle(t.anchorZ01[lockFrame]);
        if (zLock <= 0.0001f || obj.anchorZ <= 0.0001f)
        {
            return 1f;
        }

        float F = Mathf.Exp(smF - smL) * obj.anchorZ / zLock;
        float T = float.NaN;
        if (TryScaleTrendWindowMean(t.negLogTz, frame, w, out float siF) &&
            TryScaleTrendWindowMean(t.negLogTz, lockFrame, w, out float siL))
        {
            T = Mathf.Exp(siF - siL);
        }

        float factor = 1f;
        if (!float.IsNaN(T) && ((F > dz && T > dz) || (F < 1f / dz && T < 1f / dz)))
        {
            float a = ScaleTrendGate(F, dz);
            float b = ScaleTrendGate(T, dz);
            factor = Mathf.Abs(Mathf.Log(a)) <= Mathf.Abs(Mathf.Log(b)) ? a : b;
        }

        // 1 フレームに何 tick 走っても track ごとに 1 行だけ出す。
        if (logAnimalScaleTrend &&
            (!lastScaleTrendLogFrameByTrack.TryGetValue(obj.trackId, out int lastLogged) || lastLogged != frame) &&
            (frame % Mathf.Max(1, logPlacementMeasurementEveryNFrames) == 0 || Mathf.Abs(factor - 1f) > 0.0001f))
        {
            lastScaleTrendLogFrameByTrack[obj.trackId] = frame;
            Debug.Log($"[SCALETREND] f={frame} track={obj.trackId} lock={lockFrame} F={F:F3} T={(float.IsNaN(T) ? "nan" : T.ToString("F3"))} " +
                      $"factor={factor:F3} lockedScale={lockedScale:F4} scale={lockedScale * factor:F4}");
        }

        return factor;
    }
}
