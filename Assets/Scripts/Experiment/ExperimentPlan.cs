using System;

// 参加者ごとの割り付け（群 + 動画順パターン）から 6 試行の提示順を組み立てる。
//
// 実験デザイン:
//   - 動画 3 本（Human / Animal / Train）× 表示条件 2 種 = 6 試行
//   - 表示条件はブロック化する。群 A は StereoOnly ブロック → ModelReplaced ブロック、
//     群 B はその逆。ブロック順で条件の順序効果を相殺する。
//   - 動画順は 3! = 6 パターンを番号で指定する。前半ブロックと後半ブロックは同じ順序を
//     使う（同じ動画が両条件で同じ位置に来るので、条件間の比較が直接的になる）。
//
// 群 2 通り × 動画順 6 パターン = 12 通りの割り付けを実験者が参加者に手動で振る。
public static class ExperimentPlan
{
    public const int TrialCount = 6;
    public const int TrialsPerBlock = 3;
    public const int BlockCount = 2;

    // 動画順パターンは 1 始まり（実験者が紙の割り付け表から入力するため 0 始まりは使わない）。
    public const int MinVideoOrderPattern = 1;
    public const int MaxVideoOrderPattern = 6;

    // パターン番号 → 動画順。Human < Animal < Train を基準にした 3 要素の全順列を辞書順に並べたもの。
    private static readonly ExperimentVideo[][] VideoOrderPatterns =
    {
        new[] { ExperimentVideo.Human,  ExperimentVideo.Animal, ExperimentVideo.Train  }, // 1
        new[] { ExperimentVideo.Human,  ExperimentVideo.Train,  ExperimentVideo.Animal }, // 2
        new[] { ExperimentVideo.Animal, ExperimentVideo.Human,  ExperimentVideo.Train  }, // 3
        new[] { ExperimentVideo.Animal, ExperimentVideo.Train,  ExperimentVideo.Human  }, // 4
        new[] { ExperimentVideo.Train,  ExperimentVideo.Human,  ExperimentVideo.Animal }, // 5
        new[] { ExperimentVideo.Train,  ExperimentVideo.Animal, ExperimentVideo.Human  }, // 6
    };

    public static bool IsValidVideoOrderPattern(int pattern)
    {
        return pattern >= MinVideoOrderPattern && pattern <= MaxVideoOrderPattern;
    }

    // pattern は 1..6。範囲外は ArgumentOutOfRangeException にする: 割り付けミスを
    // 既定値で握りつぶすと、間違った順序のまま実験が進んでデータが無駄になるため。
    public static ExperimentVideo[] ResolveVideoOrder(int pattern)
    {
        if (!IsValidVideoOrderPattern(pattern))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pattern),
                pattern,
                $"動画順パターンは {MinVideoOrderPattern}..{MaxVideoOrderPattern} で指定してください。");
        }

        ExperimentVideo[] source = VideoOrderPatterns[pattern - MinVideoOrderPattern];
        return (ExperimentVideo[])source.Clone();
    }

    // ブロック番号（0 = 前半, 1 = 後半）に対応する表示条件。
    public static ExperimentDisplayMode ResolveBlockMode(ExperimentGroup group, int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlockCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockIndex),
                blockIndex,
                $"ブロック番号は 0..{BlockCount - 1} です。");
        }

        bool isFirstBlock = blockIndex == 0;
        if (group == ExperimentGroup.A)
        {
            return isFirstBlock ? ExperimentDisplayMode.StereoOnly : ExperimentDisplayMode.ModelReplaced;
        }

        return isFirstBlock ? ExperimentDisplayMode.ModelReplaced : ExperimentDisplayMode.StereoOnly;
    }

    public static ExperimentTrial[] BuildTrials(ExperimentGroup group, int videoOrderPattern)
    {
        ExperimentVideo[] videoOrder = ResolveVideoOrder(videoOrderPattern);
        ExperimentTrial[] trials = new ExperimentTrial[TrialCount];

        for (int i = 0; i < TrialCount; i++)
        {
            int blockIndex = i / TrialsPerBlock;
            int indexInBlock = i % TrialsPerBlock;

            trials[i] = new ExperimentTrial
            {
                trialIndex = i,
                blockIndex = blockIndex,
                indexInBlock = indexInBlock,
                video = videoOrder[indexInBlock],
                mode = ResolveBlockMode(group, blockIndex),
            };
        }

        return trials;
    }

    // 割り付け表の確認用。実験者UIにそのまま出す。
    public static string DescribeAssignment(ExperimentGroup group, int videoOrderPattern)
    {
        if (!IsValidVideoOrderPattern(videoOrderPattern))
        {
            return $"群 {group} / 動画順 {videoOrderPattern}（範囲外）";
        }

        ExperimentVideo[] order = ResolveVideoOrder(videoOrderPattern);
        string orderText = $"{order[0]} → {order[1]} → {order[2]}";
        string firstBlock = ResolveBlockMode(group, 0).ToString();
        string secondBlock = ResolveBlockMode(group, 1).ToString();
        return $"群 {group} / 動画順 {videoOrderPattern}: {orderText}\n前半 [{firstBlock}] → 後半 [{secondBlock}]";
    }
}
