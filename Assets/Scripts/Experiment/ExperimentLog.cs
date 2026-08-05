// 再生側（StreamingStereoVideoPlayer）から実験ログへ書き出すための受け口。
//
// プレイヤーが ExperimentController を直接参照すると、実験を行わない通常シーンでも
// 実験コードに依存してしまう。static な sink を挟み、セッションが動いていないときは
// 何もしない no-op にすることで、プレイヤー側の変更を数行のフック追加だけに留める。
public static class ExperimentLog
{
    public static IExperimentLogSink Sink { get; set; }

    public static bool IsActive
    {
        get { return Sink != null; }
    }

    // 被験者の操作（一時停止・シーク・モデル変更・設定変更など）。
    public static void Operation(string action, string detail = null)
    {
        Sink?.RecordOperation(action, detail);
    }

    // インタラクティブモーションの発火。
    public static void Interaction(uint trackId, string kind, string detail = null)
    {
        Sink?.RecordInteraction(trackId, kind, detail);
    }

    // 動画が 1 周してループした。試行ログの視聴周回数に積む。
    public static void VideoLooped()
    {
        Sink?.RecordVideoLoop();
    }
}

public interface IExperimentLogSink
{
    void RecordOperation(string action, string detail);
    void RecordInteraction(uint trackId, string kind, string detail);
    void RecordVideoLoop();
}
