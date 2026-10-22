using UnityEngine;

// 動画 1 周（頭から終わりまで）に 1 回だけ Random モーションを起こす時刻の決め方（2026-10-23）。
// ユーザー「一周に一回でいいかな」「（前に言った）確率どんどん増えるのは却下？」への形:
//   「まだ動いていなければ時間とともに確率が上がり、周の終わりまでに必ず 1 回動く」は、周の中の時刻を一様に 1 つ
//   くじで選ぶのと同じ分布（残り時間 r に対して 1 / r で上がっていく確率）。なので周の頭でくじを 1 回引き、その時刻を待つ。
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

    // くじの時刻より前のフレームをこの周で一度通ったか。loopPointReached の直後は表示のフレームがまだ前の周の
    // 終わり（最後のフレーム）に残っているので、それを「時刻を過ぎた」と数えると周の頭ですぐ動く（2026-10-23 のバッチで
    // 2・3 周目が frame=288 = 最後のフレームで発火した）。巻き戻って時刻より前を通ってから数える。
    private bool armed;

    // 周の頭で呼ぶ。random01 は [0, 1) の一様乱数。頭と終わりの margin 秒には置かない（短すぎる動画では全体から選ぶ）。
    public void BeginLoop(int numFrames, float fps, float startMarginSeconds, float endMarginSeconds, float random01)
    {
        LoopIndex++;
        Fired = false;
        armed = false;
        int frames = Mathf.Max(1, numFrames);
        float rate = fps > 0f ? fps : 30f;
        int lo = Mathf.Max(0, Mathf.RoundToInt(startMarginSeconds * rate));
        int hi = frames - Mathf.Max(0, Mathf.RoundToInt(endMarginSeconds * rate));
        if (hi <= lo)
        {
            lo = 0;
            hi = frames;
        }

        int offset = Mathf.FloorToInt(Mathf.Clamp01(random01) * (hi - lo));
        TriggerFrame = Mathf.Min(hi - 1, lo + offset);
    }

    // 毎 tick 今のフレームを渡す。この周で動いてよい時刻に来ていれば true。
    public bool Observe(int frame)
    {
        if (LoopIndex < 0 || Fired)
        {
            return false;
        }

        if (frame < TriggerFrame)
        {
            armed = true;
            return false;
        }

        return armed;
    }

    public void MarkFired()
    {
        Fired = true;
    }
}
