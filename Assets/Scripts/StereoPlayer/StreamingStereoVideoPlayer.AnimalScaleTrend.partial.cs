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
    // 表を bundle の読み込み時に作っている間だけ true（[PRECOMPUTE] の phase=load / play の区別）。
    private bool scaleTrendBuildingOnLoad;
    // animalScaleTrendRawLockReference の基準（ロックしたフレームの生の目標高の log）。track ごとに、ロックのフレームが変わったら作り直す。
    private readonly Dictionary<uint, int> scaleTrendRawReferenceFrameByTrack = new Dictionary<uint, int>();
    private readonly Dictionary<uint, float> scaleTrendRawReferenceLogTargetByTrack = new Dictionary<uint, float>();
    private readonly List<MetaObj> scaleTrendLockFrameObjects = new List<MetaObj>(16);

    // bundle の読み込み時（LoadMeta の CalibrateAnchorDepthRange の直後）に表を作る（2026-10-07）。C4-T か B1 が ON のときだけ。
    // 遅延で作ると、再生中の最初のフレーム（f0）で Editor のバッチでも 265〜320 ms 止まる（実機はさらに遅い）。読み込み中に移すだけで、
    // メインスレッドが止まること自体は変わらない。ここで失敗しても途中までの表が残らないよう、表を捨てて遅延の経路に任せる。
    private void PrecomputeAnimalScaleTrendTableOnLoad()
    {
        if (!animalScaleFollowApproachTrend && !refineLockedScaleAgainstSmoothedTarget)
        {
            return;
        }

        scaleTrendBuildingOnLoad = true;
        try
        {
            EnsureScaleTrendTable();
        }
        catch (System.Exception ex)
        {
            InvalidateAnimalScaleTrendTable();
            Debug.LogWarning($"[SCALETREND] precompute on load failed, built on first use instead: {ex.Message}");
        }
        finally
        {
            scaleTrendBuildingOnLoad = false;
        }
    }

    // bundle を読み直したら表・基準フレーム・生の基準を捨てる（2026-10-07）。展開先の meta.bin は bundle によらず同じパス（svb_cache/meta.bin）
    // なので、パスだけで判定すると前の bundle の表を使い続ける（同じプレーヤーで bundle を選び直したとき。試行ごとにシーンを読み直す実験の経路では
    // 起きない）。ロック（lockedModelLocalScaleByTrack）は読み直しでも残る既存の欠陥があり、次にロックし直すまで倍率は 1（C4-T OFF と同じ）になる。
    private void InvalidateAnimalScaleTrendTable()
    {
        scaleTrendMetaPath = null;
        scaleTrendTracks.Clear();
        scaleTrendSegmentByFrame = null;
        scaleLockReferenceFrameByTrack.Clear();
        lastScaleTrendLogFrameByTrack.Clear();
        scaleTrendRawReferenceFrameByTrack.Clear();
        scaleTrendRawReferenceLogTargetByTrack.Clear();
    }

    // 倍率の基準の目標高（log）。animalScaleTrendRawLockReference（既定 OFF、2026-10-07、新しい振る舞い）なら、ロックしたフレームの生の値:
    // ロックの基準（TryResolveShotStartScaleReference）と同じスパイクの修理後の object（TryReadFrameObjects）を、ロックのフレームで読み直して作る
    // （表示中の obj は displayMetadataFrame のもの。今は UseFrameReadySync が false 固定で同じフレームだが、ずれても表とロックのフレームに揃うよう読み直す）。
    // 下端の判定は shot の頭の状態（未判定）から（表の走査も shot の頭で状態を戻す）。ロックごとに 1 回だけ読んで覚える。
    // ロックの倍率は bbox 高そのもので合わせているので、基準もそのフレームの値に揃う。窓の平均（今の基準）は区間の頭で先のフレームだけになり、
    // 近づいてくる場面では生の値より大きく出る（bundle_animal f258: 92.2 px 対 73 px）。生の値が取れないときと、フラグ OFF のときは今どおり窓の平均。
    private bool TryResolveScaleTrendLockLogTarget(MetaObj obj, int lockFrame, ScaleTrendTrack t, int halfWidth, bool raw, out float logTarget)
    {
        logTarget = float.NaN;
        if (raw)
        {
            if (scaleTrendRawReferenceFrameByTrack.TryGetValue(obj.trackId, out int cachedFrame) && cachedFrame == lockFrame &&
                scaleTrendRawReferenceLogTargetByTrack.TryGetValue(obj.trackId, out float cached))
            {
                logTarget = cached;
            }
            else
            {
                if (TryReadFrameObjects(lockFrame, scaleTrendLockFrameObjects) &&
                    TryFindTrackObject(scaleTrendLockFrameObjects, obj.trackId, out MetaObj lockObj) && lockObj.bboxH > 0)
                {
                    float target = ResolveUnclippedTargetHeight(lockObj, lockObj.bboxH, ResolveBBoxBottomClipState(false, lockObj.bboxY + lockObj.bboxH));
                    if (target > 0f)
                    {
                        logTarget = Mathf.Log(target);
                    }
                }

                scaleTrendRawReferenceFrameByTrack[obj.trackId] = lockFrame;
                scaleTrendRawReferenceLogTargetByTrack[obj.trackId] = logTarget;
            }

            if (!float.IsNaN(logTarget))
            {
                return true;
            }
        }

        return TryScaleTrendWindowMean(t.logTarget, lockFrame, halfWidth, out logTarget);
    }

    // 倍率の基準の −log(SMAL transl z)。生の基準ならロックしたフレームの表の値（tz は bbox のスパイクの修理と関係ない）、無ければ窓の平均。
    private bool TryResolveScaleTrendLockNegLogTz(ScaleTrendTrack t, int lockFrame, int halfWidth, bool raw, out float negLogTz)
    {
        if (raw && t.negLogTz != null && lockFrame >= 0 && lockFrame < t.negLogTz.Length && !float.IsNaN(t.negLogTz[lockFrame]))
        {
            negLogTz = t.negLogTz[lockFrame];
            return true;
        }

        return TryScaleTrendWindowMean(t.negLogTz, lockFrame, halfWidth, out negLogTz);
    }

    private void EnsureScaleTrendTable()
    {
        if (!metaLoaded || scaleTrendMetaPath == metaFilePath)
        {
            return;
        }

        scaleTrendMetaPath = metaFilePath;
        scaleTrendTracks.Clear();
        // フラグが ON なら bundle の読み込み時に走る（PrecomputeAnimalScaleTrendTableOnLoad）。読み込みの後で ON にしたときだけ、
        // 再生中の最初の使用で走る。かかった時間を出して、固まりの大きさを確かめる。
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
        // 下端が切れているかの判定は走査用の状態で行い、再生中の track の状態（bboxBottomClippedByTrack）は書き換えない。shot の頭で戻す。
        var scanBottomClipped = new Dictionary<uint, bool>();
        int scanBottomClipShot = int.MinValue;
        for (int frame = 0; frame < numFrames; frame++)
        {
            int shot = shotBoundaries.ResolveShotIndex(frame);
            if (shot != scanBottomClipShot)
            {
                scanBottomClipped.Clear();
                scanBottomClipShot = shot;
            }
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

                    bool scanClipped = ResolveBBoxBottomClipState(
                        scanBottomClipped.TryGetValue(obj.trackId, out bool scanWasClipped) && scanWasClipped,
                        obj.bboxY + obj.bboxH);
                    scanBottomClipped[obj.trackId] = scanClipped;
                    float target = ResolveUnclippedTargetHeight(obj, obj.bboxH, scanClipped);
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
        Debug.Log($"[PRECOMPUTE] scaleTrendTable ms={stopwatch.Elapsed.TotalMilliseconds:F0} frames={numFrames} atFrame={startFrame} phase={(scaleTrendBuildingOnLoad ? "load" : "play")}");
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
        // animalScaleTrendRawLockReference（既定 OFF）: 基準はロックしたフレームの生の値。B1 が ON のときはロックが窓の平均に合っているので今どおり。
        bool rawLockReference = animalScaleTrendRawLockReference && !refineLockedScaleAgainstSmoothedTarget;
        if (!TryScaleTrendWindowMean(t.logTarget, frame, w, out float smF) ||
            !TryResolveScaleTrendLockLogTarget(obj, lockFrame, t, w, rawLockReference, out float smL) ||
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
            TryResolveScaleTrendLockNegLogTz(t, lockFrame, w, rawLockReference, out float siL))
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
                      $"factor={factor:F3} lockedScale={lockedScale:F4} scale={lockedScale * factor:F4}{(rawLockReference ? " ref=raw" : "")}");
        }

        return factor;
    }
}
