using UnityEngine;

// 試行中の描画レート・動画の進み・コントローラの動きを 1 秒窓で集計する（perf.csv の 1 行分）。
//
// MonoBehaviour に依存しない純粋なクラスにしてある（UnityEngine.Vector3 は使う。EditMode テストで検算できるように）。
// 呼び出し側（ExperimentController.Update）が毎フレーム Push し、窓が閉じたら Sample を受け取って書く。
//
// 何を測るか（2026-09-25、論文側の依頼 §3.2 / §3.3）:
//   - fps / frame_time_max_ms / long_frames … 描画レートとコマ落ち（72 Hz なら 1 フレーム 13.9 ms。20 ms 超を「落ちた」と数える）
//   - video_frames_advanced / video_frame_skips … 動画のフレームがいくつ進んだか、表示されずに飛んだフレームがいくつあったか
//     （72 Hz 描画 × 30 fps 動画なら 1 描画フレームで進む動画フレームは 0 か 1。2 以上進んだら間のフレームは誰にも見られていない）
//   - video_time_advanced_sec … 窓の中で動画が実際に進んだ秒数（一時停止中は進まない）。試行を通して足すと「動画の再生時間」になり、
//     試行の経過時間（Motion で止まる時間を含む）と分けて記録できる
//   - ctrl_moved_m / ctrl_trigger_frames / ctrl_button_frames … コントローラの移動量とボタンを押していたフレーム数（能動的に関わっていたか）
public sealed class ExperimentPerfAccumulator
{
    public struct Sample
    {
        public float windowSeconds;
        public int frames;
        public float fps;
        public float maxFrameMs;
        public int longFrames;
        public long videoFramesAdvanced;
        public int videoFrameSkips;
        // 前向きのシークで飛んだ回数（進み・コマ落ちからは除外してある）。
        public int videoSeekJumps;
        public double videoTimeAdvancedSec;
        public bool videoPlayingAtEnd;
        public float controllerMovedMeters;
        public int triggerFrames;
        public int buttonFrames;
    }

    // 20 ms = 72 Hz の 1.44 フレーム。1 フレーム落ちると 27.8 ms になるので、20 ms 超を「落ちた」とみなす。
    public float LongFrameThresholdSeconds = 0.020f;
    public float WindowLengthSeconds = 1f;

    // 前向きの飛びをシークとみなす閾値: 動画の進み > 描画の刻み × 1.5 + 0.1 秒。
    // 1 描画フレームで動画が進める量は刻み × 再生速度（1 倍）なので、それを 1.5 倍 + 0.1 秒超えたら再生ではない。
    //   72 Hz 通常再生: 進み 0.0139 vs 閾値 0.121 → 再生 / 2 フレーム進み 0.0667 → 再生 / GC で 0.4 秒伸びた
    //   フレームの進み 0.4 vs 0.7 → 再生 / 着地フレームが 0.3 秒詰まった 1.0 秒のシーク vs 0.55 → シーク。
    // 以前の max(0.25, 刻み × 4) は、car 動画の scrub 最小刻み（0.240 秒）が再生扱いになり、
    // 着地フレームが長いとシークまで再生扱いになった（2026-09-29 の監査）。
    public const double SeekJumpDeltaFactor = 1.5d;
    public const double SeekJumpSlackSeconds = 0.1d;

    private float windowSeconds;
    private int frames;
    private float maxDt;
    private int longFrames;
    private long lastVideoFrame = -1;
    private long videoFramesAdvanced;
    private int videoFrameSkips;
    private int videoSeekJumps;
    private double lastVideoTime = double.NaN;
    private double videoTimeAdvanced;
    private bool hasLastPointer;
    private Vector3 lastPointer;
    private float controllerMoved;
    private int triggerFrames;
    private int buttonFrames;

    // 試行の切れ目で呼ぶ。動画のフレーム・時刻の「前回値」も捨てる。
    public void Reset()
    {
        ResetWindow();
        lastVideoFrame = -1;
        lastVideoTime = double.NaN;
        hasLastPointer = false;
    }

    // 毎フレーム呼ぶ。窓が閉じたら true を返し、sample にその窓の集計を入れる。
    public bool Push(
        float deltaSeconds,
        long videoFrame,
        double videoTimeSeconds,
        bool videoPlaying,
        bool hasPointerPose,
        Vector3 pointerPosition,
        bool triggerPressed,
        bool buttonPressed,
        out Sample sample)
    {
        sample = default;
        if (deltaSeconds < 0f)
        {
            deltaSeconds = 0f;
        }

        // TryFlush（試行の終わり・休止）が使う「最後に見た再生状態」。窓が閉じたときだけ覚えると、
        // 1 秒未満の端の窓では前の窓の値が出る（2026-09-29 の 3 回目の監査）。
        lastVideoPlaying = videoPlaying;

        windowSeconds += deltaSeconds;
        frames++;
        if (deltaSeconds > maxDt)
        {
            maxDt = deltaSeconds;
        }
        if (deltaSeconds > LongFrameThresholdSeconds)
        {
            longFrames++;
        }

        // 前向きの飛び（シークバーを前へ動かした）を通常の再生と区別する。
        // 1 描画フレームで動画が進める上限は描画の刻み × 再生速度なので、それを大きく超えた進みは
        // 再生ではなくシーク。除外しないと「見た秒数」に飛ばした区間が丸ごと入り、コマ落ちには
        // その区間のフレーム数がそのまま乗る（2026-09-25 の監査。実機ログに前方シークの連続あり）。
        // 刻みに比例させるので、GC や読み込みで 1 フレームが伸びた場合を誤ってシーク扱いしない。
        bool seekJump =
            !double.IsNaN(lastVideoTime) &&
            videoTimeSeconds - lastVideoTime > deltaSeconds * SeekJumpDeltaFactor + SeekJumpSlackSeconds;
        if (seekJump)
        {
            videoSeekJumps++;
        }

        // 動画のフレーム。ループ（2166 → 0）やシークで戻ったら差は数えない。前向きの飛びも数えない。
        if (videoFrame >= 0)
        {
            if (lastVideoFrame >= 0 && videoFrame > lastVideoFrame && !seekJump)
            {
                long diff = videoFrame - lastVideoFrame;
                videoFramesAdvanced += diff;
                if (diff > 1)
                {
                    videoFrameSkips += (int)(diff - 1);
                }
            }
            lastVideoFrame = videoFrame;
        }

        // 動画の時刻。戻ったとき（ループ・シーク）と前へ飛んだときは進みに数えない。
        if (!double.IsNaN(lastVideoTime) && videoTimeSeconds > lastVideoTime && !seekJump)
        {
            videoTimeAdvanced += videoTimeSeconds - lastVideoTime;
        }
        lastVideoTime = videoTimeSeconds;

        if (hasPointerPose)
        {
            if (hasLastPointer)
            {
                controllerMoved += Vector3.Distance(lastPointer, pointerPosition);
            }
            lastPointer = pointerPosition;
            hasLastPointer = true;
        }
        else
        {
            hasLastPointer = false;
        }

        if (triggerPressed)
        {
            triggerFrames++;
        }
        if (buttonPressed)
        {
            buttonFrames++;
        }

        if (windowSeconds < WindowLengthSeconds)
        {
            return false;
        }

        sample = BuildSample(videoPlaying);
        ResetWindow();
        return true;
    }

    // 閉じていない窓（1 秒未満）を書き切る。試行の終わりに呼ぶ。捨てると video_played_sec が
    // 最大 1 秒少なくなる。フレームが 1 つも無ければ何も返さない。
    public bool TryFlush(out Sample sample)
    {
        sample = default;
        if (frames <= 0)
        {
            return false;
        }

        sample = BuildSample(lastVideoPlaying);
        ResetWindow();
        return true;
    }

    private bool lastVideoPlaying;

    private Sample BuildSample(bool videoPlaying)
    {
        return new Sample
        {
            windowSeconds = windowSeconds,
            frames = frames,
            fps = windowSeconds > 0f ? frames / windowSeconds : 0f,
            maxFrameMs = maxDt * 1000f,
            longFrames = longFrames,
            videoFramesAdvanced = videoFramesAdvanced,
            videoFrameSkips = videoFrameSkips,
            videoSeekJumps = videoSeekJumps,
            videoTimeAdvancedSec = videoTimeAdvanced,
            videoPlayingAtEnd = videoPlaying,
            controllerMovedMeters = controllerMoved,
            triggerFrames = triggerFrames,
            buttonFrames = buttonFrames,
        };
    }

    private void ResetWindow()
    {
        windowSeconds = 0f;
        frames = 0;
        maxDt = 0f;
        longFrames = 0;
        videoFramesAdvanced = 0;
        videoFrameSkips = 0;
        videoSeekJumps = 0;
        videoTimeAdvanced = 0d;
        controllerMoved = 0f;
        triggerFrames = 0;
        buttonFrames = 0;
    }
}
