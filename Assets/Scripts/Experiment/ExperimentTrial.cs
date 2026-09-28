// 1 試行の内容。ExperimentPlan が生成し、以降は読み取り専用で扱う。
public struct ExperimentTrial
{
    // セッション全体での通し番号（0..8。3 条件 × 3 動画 = 9 試行）。
    public int trialIndex;

    // 条件ブロック（0 = 単眼、1・2 = 群ごとに入れ替える 2 条件）。
    public int blockIndex;

    // ブロック内での順番（0..2）。
    public int indexInBlock;

    public ExperimentVideo video;
    public ExperimentDisplayMode mode;

    // **ログ用**の 1 行表記（例: "3/9  Animal / ModelReplaced"）。動画名と条件名が入るので、
    // 被験者が見るパネルには使わない（2026-09-25 の監査。パネルは DescribeForParticipant）。
    public string Describe(int totalTrials)
    {
        return $"{trialIndex + 1}/{totalTrials}  {video} / {mode}";
    }

    // **被験者が見るパネル用。**何本目かだけを出す。どの動画・どの条件かは伝えない
    // （内部の enum 名を見せない。条件を知らせて期待を作らない）。
    public string DescribeForParticipant(int totalTrials)
    {
        return $"{trialIndex + 1} / {totalTrials} 本目の動画";
    }
}
