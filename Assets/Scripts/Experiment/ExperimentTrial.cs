// 1 試行の内容。ExperimentPlan が生成し、以降は読み取り専用で扱う。
public struct ExperimentTrial
{
    // セッション全体での通し番号（0..5）。
    public int trialIndex;

    // 条件ブロック（0 = 前半, 1 = 後半）。
    public int blockIndex;

    // ブロック内での順番（0..2）。
    public int indexInBlock;

    public ExperimentVideo video;
    public ExperimentDisplayMode mode;

    // ログ・待機画面に出す 1 行表記（例: "3/6  Animal / ModelReplaced"）。
    public string Describe(int totalTrials)
    {
        return $"{trialIndex + 1}/{totalTrials}  {video} / {mode}";
    }
}
