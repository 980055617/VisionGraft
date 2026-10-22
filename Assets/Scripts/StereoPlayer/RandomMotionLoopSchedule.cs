using System.Collections.Generic;
using UnityEngine;

// 動画 1 周（頭から終わりまで）に 1 回だけ Random モーションを起こす時刻の決め方（2026-10-23）。
// ユーザー「一周に一回でいいかな」「（前に言った）確率どんどん増えるのは却下？」への形:
//   「まだ動いていなければ時間とともに確率が上がり、周の終わりまでに必ず 1 回動く」は、周の中の時刻を一様に 1 つ
//   くじで選ぶのと同じ分布（残り時間 r に対して 1 / r で上がっていく確率）。なので周の頭でくじを 1 回引き、その時刻を待つ。
// くじは「動ける体（人・動物、人や動物の居ないときの車）が映っているフレーム」から引く（eligibleFrames。読み込み時に meta.bin から数える）。
// 周全体から引くと、体が前半にしか映らない動画で後半を引いた周は動かない（2026-10-23 の点検）。
// 時刻は動画のフレーム（再生位置）で数える。一時停止・モーション中は動画が進まないので、止まっていた時間は数に入らない。
// 秒数の時計（12〜24 秒、track ごと）と違い、映る数・見る長さに依らず 1 周の回数がそろう。
public sealed class RandomMotionLoopSchedule
{
    // 何周目か（0 始まり）。まだ始まっていなければ −1。
    public int LoopIndex { get; private set; } = -1;

    // この周に動き始めてよい最初のフレーム。
    public int TriggerFrame { get; private set; } = -1;

    // この周で動いたか。
    public bool Fired { get; private set; }

    // この周の頭に来たか。loopPointReached の直後は表示のフレームがまだ前の周の終わり（最後のフレーム）に残っているので、
    // それを「時刻を過ぎた」と数えると周の頭ですぐ動く（2026-10-23 のバッチで 2・3 周目が最後のフレーム 288 で発火した）。
    // 前の周で最後に見たフレームより小さいフレーム（巻き戻った）か、くじの時刻より前のフレームを見てから数える。
    private bool armed;
    private int wrapReference = int.MaxValue;
    private int lastFrame = -1;

    // 周の頭で呼ぶ。random01 は [0, 1) の一様乱数。頭と終わりの margin 秒には置かない（その中に動ける体が居なければ、
    // 周全体の動ける体のフレームから。eligibleFrames が空なら周全体から）。eligibleFrames は昇順。
    // armedNow: 再生の最初の周（今が周の頭）なら true。loopPointReached の後は false（巻き戻りを待つ）。
    public void BeginLoop(int numFrames, float fps, float startMarginSeconds, float endMarginSeconds, float random01,
        IReadOnlyList<int> eligibleFrames = null, bool armedNow = true)
    {
        LoopIndex++;
        Fired = false;
        armed = armedNow;
        wrapReference = lastFrame >= 0 ? lastFrame : int.MaxValue;

        int frames = Mathf.Max(1, numFrames);
        float rate = fps > 0f ? fps : 30f;
        int lo = Mathf.Max(0, Mathf.RoundToInt(startMarginSeconds * rate));
        int hi = frames - Mathf.Max(0, Mathf.RoundToInt(endMarginSeconds * rate));
        if (hi <= lo)
        {
            lo = 0;
            hi = frames;
        }

        float r = Mathf.Clamp01(random01);
        if (eligibleFrames != null && eligibleFrames.Count > 0)
        {
            int inWindow = 0;
            for (int i = 0; i < eligibleFrames.Count; i++)
            {
                if (eligibleFrames[i] >= lo && eligibleFrames[i] < hi)
                {
                    inWindow++;
                }
            }

            if (inWindow > 0)
            {
                int pick = Mathf.Min(inWindow - 1, Mathf.FloorToInt(r * inWindow));
                for (int i = 0; i < eligibleFrames.Count; i++)
                {
                    if (eligibleFrames[i] < lo || eligibleFrames[i] >= hi)
                    {
                        continue;
                    }

                    if (pick-- == 0)
                    {
                        TriggerFrame = eligibleFrames[i];
                        return;
                    }
                }
            }

            TriggerFrame = eligibleFrames[Mathf.Min(eligibleFrames.Count - 1, Mathf.FloorToInt(r * eligibleFrames.Count))];
            return;
        }

        int offset = Mathf.FloorToInt(r * (hi - lo));
        TriggerFrame = Mathf.Min(hi - 1, lo + offset);
    }

    // 毎 tick 今のフレームを渡す（動画が止まっていても）。この周で動いてよい時刻に来ていれば true。
    public bool Observe(int frame)
    {
        if (!armed && (frame < wrapReference || frame < TriggerFrame))
        {
            armed = true;
        }

        lastFrame = frame;
        return LoopIndex >= 0 && armed && !Fired && frame >= TriggerFrame;
    }

    public void MarkFired()
    {
        Fired = true;
    }
}
