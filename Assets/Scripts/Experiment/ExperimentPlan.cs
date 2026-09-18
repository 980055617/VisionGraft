using System;

// 参加者ごとの割り付け（群 + 動画順パターン）から 9 試行の提示順を組み立てる。
//
// 実験デザイン（2026-09-11 に 2 条件 → 3 条件へ変更）:
//   - 動画 3 本（Human / Animal / Car）× 表示条件 3 種 = 9 試行（2026-09-17 に Train → Car）
//   - 表示条件はブロック化する。**最初のブロックは両群とも Monocular（単眼）**。
//     残り 2 ブロックは 群 A が StereoOnly → ModelReplaced、群 B が ModelReplaced → StereoOnly。
//     単眼を先に置くのは「平面の動画 → 立体 → 置換」の順で体験させるため。順序効果の相殺は
//     後半 2 ブロックの入れ替えで行う。
//   - 動画順は 3! = 6 パターンを番号で指定する。3 ブロックとも同じ順序を使う
//     （同じ動画が全条件で同じ位置に来るので、条件間の比較が直接的になる）。
//
// 群 2 通り × 動画順 6 パターン = 12 通りの割り付けを実験者が参加者に手動で振る。
public static class ExperimentPlan
{
    public const int TrialsPerBlock = 3;
    public const int BlockCount = 3;
    public const int TrialCount = TrialsPerBlock * BlockCount;

    // 動画順パターンは 1 始まり（実験者が紙の割り付け表から入力するため 0 始まりは使わない）。
    public const int MinVideoOrderPattern = 1;
    public const int MaxVideoOrderPattern = 6;
    public const int VideoOrderPatternCount = MaxVideoOrderPattern - MinVideoOrderPattern + 1;

    // 参加者番号 → 割り付け（群 + 動画順パターン）。2026-09-11、実験者が手で振るのをやめて番号から決める。
    // 奇数番号 = 群 A、偶数番号 = 群 B。動画順パターンは 2 人ごとに 1 → 6 と回す。
    // 12 人で 群 2 × 動画順 6 = 12 通りが 1 回ずつ揃い、13 人目から繰り返す。
    //   P01 A/1, P02 B/1, P03 A/2, P04 B/2, ..., P11 A/6, P12 B/6, P13 A/1, ...
    // P00（テスト用）は P01 と同じ A/1。
    public static void ResolveAssignment(int participantNumber, out ExperimentGroup group, out int videoOrderPattern)
    {
        int index = Math.Max(0, participantNumber - 1) % (2 * VideoOrderPatternCount);
        group = index % 2 == 0 ? ExperimentGroup.A : ExperimentGroup.B;
        videoOrderPattern = MinVideoOrderPattern + index / 2;
    }

    // パターン番号 → 動画順。Human < Animal < Car を基準にした 3 要素の全順列を辞書順に並べたもの。
    // 2026-09-17 に Train の席へ Car を入れた。表の並びは変えていない（参加者番号 → パターンの
    // 対応が ResolveAssignment で決まるので、並べ替えると P01〜P12 が見た順序の記録が変わる）。
    private static readonly ExperimentVideo[][] VideoOrderPatterns =
    {
        new[] { ExperimentVideo.Human,  ExperimentVideo.Animal, ExperimentVideo.Car    }, // 1
        new[] { ExperimentVideo.Human,  ExperimentVideo.Car,    ExperimentVideo.Animal }, // 2
        new[] { ExperimentVideo.Animal, ExperimentVideo.Human,  ExperimentVideo.Car    }, // 3
        new[] { ExperimentVideo.Animal, ExperimentVideo.Car,    ExperimentVideo.Human  }, // 4
        new[] { ExperimentVideo.Car,    ExperimentVideo.Human,  ExperimentVideo.Animal }, // 5
        new[] { ExperimentVideo.Car,    ExperimentVideo.Animal, ExperimentVideo.Human  }, // 6
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

    // ブロック番号（0 = 単眼, 1 = 2 番目, 2 = 3 番目）に対応する表示条件。
    public static ExperimentDisplayMode ResolveBlockMode(ExperimentGroup group, int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlockCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockIndex),
                blockIndex,
                $"ブロック番号は 0..{BlockCount - 1} です。");
        }

        if (blockIndex == 0)
        {
            return ExperimentDisplayMode.Monocular;
        }

        bool isSecondBlock = blockIndex == 1;
        if (group == ExperimentGroup.A)
        {
            return isSecondBlock ? ExperimentDisplayMode.StereoOnly : ExperimentDisplayMode.ModelReplaced;
        }

        return isSecondBlock ? ExperimentDisplayMode.ModelReplaced : ExperimentDisplayMode.StereoOnly;
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
        string[] blocks = new string[BlockCount];
        for (int block = 0; block < BlockCount; block++)
        {
            blocks[block] = $"[{ResolveBlockMode(group, block)}]";
        }

        return $"群 {group} / 動画順 {videoOrderPattern}: {orderText}\n{string.Join(" → ", blocks)}";
    }
}
