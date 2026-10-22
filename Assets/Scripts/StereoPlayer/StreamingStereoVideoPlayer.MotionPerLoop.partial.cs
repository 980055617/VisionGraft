using System.Collections.Generic;
using UnityEngine;

// Random モーションを「動画 1 周に 1 回」にする（2026-10-23 ユーザー「一周に一回でいいかな」）。時刻の決め方は RandomMotionLoopSchedule。
// ON のとき、track ごとの 12〜24 秒の時計（interactiveMotionMin/MaxIntervalSeconds）は使わない:
//   1. 周の頭（最初の再生と loopPointReached の後）で、その周に動き始めるフレームをくじで決める
//   2. そのフレームを過ぎたら、その tick に動ける track（人・動物、人や動物が居ないときの車。今まで発火できた条件と同じ）から
//      1 つをくじで選んで動かす。動ける track が居なければ、居る tick まで待つ（周が終わるまで居なければその周は動かない）
//   3. 一時停止・Motion OFF から戻った直後の 2 秒（ResumeGraceSeconds）は動かさない（戻したらすぐまた止まる、を避ける。今までと同じ）
// 練習（チュートリアル）は短い動画で必ず 1 回見せるので、今までの秒数の時計のまま（ApplyPendingExperimentTrialRequest で OFF にする）。
// 実験の TrialScene で ON（シーンの値）。コードの既定は OFF（自由に見る TestScene・バッチは今までどおり）。
public partial class StreamingStereoVideoPlayer
{
    [Header("Random Motion: once per loop (2026-10-23)")]
    public bool randomMotionOncePerLoop = false;
    // 周の頭と終わりのこの秒数には動き始めない。
    [Min(0f)] public float randomMotionLoopStartMarginSeconds = 2f;
    [Min(0f)] public float randomMotionLoopEndMarginSeconds = 2f;

    private readonly RandomMotionLoopSchedule randomMotionLoop = new RandomMotionLoopSchedule();
    private readonly List<KeyValuePair<uint, MetaObj>> randomMotionLoopCandidates = new List<KeyValuePair<uint, MetaObj>>();
    private bool randomMotionLoopPending = true;
    private bool randomMotionLoopPausedLastTick;
    private float randomMotionLoopGraceUntil = -1f;

    // 動ける体（人・動物、enableElseInteractiveMotion なら人や動物の居ないときの車）が映っているフレーム（昇順）。
    // くじはこの中から引く。読み込み時に meta.bin を全フレーム読んで作る（ON のときだけ。30 秒 = 900 フレーム）。
    private int[] randomMotionEligibleFrames;

    private void PrecomputeRandomMotionEligibleFramesOnLoad()
    {
        randomMotionEligibleFrames = null;
        int numFrames = (int)metaHeader.numFrames;
        if (!randomMotionOncePerLoop || numFrames <= 0)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new List<MetaObj>(16);
        var frames = new List<int>(numFrames);
        for (int f = 0; f < numFrames; f++)
        {
            if (!TryReadFrameObjects(f, buffer))
            {
                continue;
            }

            for (int i = 0; i < buffer.Count; i++)
            {
                byte category = buffer[i].categoryId;
                if (IsCategoryPerson(category) || IsCategoryAnimal(category) ||
                    (enableElseInteractiveMotion && IsCategoryOther(category)))
                {
                    frames.Add(f);
                    break;
                }
            }
        }

        randomMotionEligibleFrames = frames.ToArray();
        Debug.Log($"[MOTION] per-loop eligible frames {randomMotionEligibleFrames.Length}/{numFrames} ({stopwatch.ElapsedMilliseconds}ms)");
    }

    // UpdateInteractiveMotionSchedule から（ON のとき、その track が今 動ける状態なら）。
    private void CollectRandomMotionLoopCandidate(uint trackId, MetaObj obj)
    {
        randomMotionLoopCandidates.Add(new KeyValuePair<uint, MetaObj>(trackId, obj));
    }

    // loopPointReached（周の終わり）。
    private void NoteRandomMotionLoopEnded()
    {
        if (!randomMotionOncePerLoop)
        {
            return;
        }

        if (randomMotionLoop.LoopIndex >= 0 && !randomMotionLoop.Fired)
        {
            Debug.Log($"[MOTION] per-loop missed loop={randomMotionLoop.LoopIndex} trigger={randomMotionLoop.TriggerFrame}（動ける track が居なかった）");
        }

        randomMotionLoopPending = true;
    }

    // その tick の配置が全部済んだ後に 1 回。
    private void ResolveRandomMotionOncePerLoop(int frame)
    {
        if (!randomMotionOncePerLoop)
        {
            randomMotionLoopCandidates.Clear();
            return;
        }

        try
        {
            if (randomMotionLoopPending && metaHeader.numFrames > 0)
            {
                float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
                // 最初の周は今が周の頭。loopPointReached の後は巻き戻りを待つ。
                bool firstLoop = randomMotionLoop.LoopIndex < 0;
                randomMotionLoop.BeginLoop((int)metaHeader.numFrames, fps,
                    randomMotionLoopStartMarginSeconds, randomMotionLoopEndMarginSeconds, Random.value,
                    randomMotionEligibleFrames, firstLoop);
                randomMotionLoopPending = false;
                Debug.Log($"[MOTION] per-loop begin loop={randomMotionLoop.LoopIndex} trigger={randomMotionLoop.TriggerFrame} " +
                          $"({randomMotionLoop.TriggerFrame / fps:F2}s / {metaHeader.numFrames / fps:F2}s) frame={frame} " +
                          $"eligible={(randomMotionEligibleFrames != null ? randomMotionEligibleFrames.Length : -1)}");
            }

            // 止まっていても今のフレームは見せる（周の頭で巻き戻ったことを覚えるため）。
            bool due = randomMotionLoop.Observe(frame);
            if (!enableInteractiveMotion || vp == null || !vp.isPlaying)
            {
                randomMotionLoopPausedLastTick = true;
                return;
            }

            float now = GetRuntimeTickContext().now;
            if (randomMotionLoopPausedLastTick)
            {
                randomMotionLoopPausedLastTick = false;
                randomMotionLoopGraceUntil = now + ResumeGraceSeconds;
            }

            if (!due || randomMotionLoopCandidates.Count == 0 ||
                IsRandomInteractiveMotionInProgress() || now < randomMotionLoopGraceUntil)
            {
                return;
            }

            // 候補をくじの順に試す（車は視聴者が近すぎると走る経路が組めず、始まらないことがある）。
            int count = randomMotionLoopCandidates.Count;
            int first = Random.Range(0, count);
            for (int k = 0; k < count; k++)
            {
                KeyValuePair<uint, MetaObj> candidate = randomMotionLoopCandidates[(first + k) % count];
                StartRandomInteractiveMotion(candidate.Key, candidate.Value, now);
                if (GetOrCreateInteractiveMotionState(candidate.Key).stage == InteractiveEventStage.Inactive)
                {
                    continue;
                }

                randomMotionLoop.MarkFired();
                Debug.Log($"[MOTION] per-loop fired loop={randomMotionLoop.LoopIndex} track={candidate.Key} frame={frame} " +
                          $"trigger={randomMotionLoop.TriggerFrame} candidates={count}");
                return;
            }
        }
        finally
        {
            randomMotionLoopCandidates.Clear();
        }
    }
}
