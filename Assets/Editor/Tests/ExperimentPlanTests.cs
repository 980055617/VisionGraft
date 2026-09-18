using System;
using System.Collections.Generic;
using NUnit.Framework;

public class ExperimentPlanTests
{
    [Test]
    public void BuildTrials_ProducesNineTrials()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1);

        Assert.That(trials.Length, Is.EqualTo(9));
        Assert.That(ExperimentPlan.TrialCount, Is.EqualTo(9));
        Assert.That(ExperimentPlan.BlockCount, Is.EqualTo(3));
    }

    // 最初のブロックは両群とも Monocular（単眼）。2026-09-11 の設計変更。
    [Test]
    public void BuildTrials_FirstBlockIsMonocularForBothGroups()
    {
        foreach (ExperimentGroup group in new[] { ExperimentGroup.A, ExperimentGroup.B })
        {
            ExperimentTrial[] trials = ExperimentPlan.BuildTrials(group, 1);

            Assert.That(trials[0].mode, Is.EqualTo(ExperimentDisplayMode.Monocular), $"group {group}");
            Assert.That(trials[1].mode, Is.EqualTo(ExperimentDisplayMode.Monocular), $"group {group}");
            Assert.That(trials[2].mode, Is.EqualTo(ExperimentDisplayMode.Monocular), $"group {group}");
        }
    }

    // 群 A は 単眼 → StereoOnly → ModelReplaced。
    [Test]
    public void BuildTrials_GroupA_StereoOnlyThenModelReplaced()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1);

        Assert.That(trials[3].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[4].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[5].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[6].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[7].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[8].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
    }

    // 群 B は 単眼 → ModelReplaced → StereoOnly。後半 2 ブロックの入れ替えで順序効果を相殺する。
    [Test]
    public void BuildTrials_GroupB_ModelReplacedThenStereoOnly()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.B, 1);

        Assert.That(trials[3].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[5].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[6].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[8].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
    }

    // 3 ブロックとも同じ動画順を使う（同じ動画が全条件で同じ位置に来る）。
    [Test]
    public void BuildTrials_AllBlocksUseTheSameVideoOrder()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 4);

        for (int i = 0; i < ExperimentPlan.TrialsPerBlock; i++)
        {
            Assert.That(trials[i + 3].video, Is.EqualTo(trials[i].video), $"block 1 index {i}");
            Assert.That(trials[i + 6].video, Is.EqualTo(trials[i].video), $"block 2 index {i}");
        }
    }

    [Test]
    public void BuildTrials_AssignsBlockAndIndexInBlock()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1);

        Assert.That(trials[0].blockIndex, Is.EqualTo(0));
        Assert.That(trials[0].indexInBlock, Is.EqualTo(0));
        Assert.That(trials[2].blockIndex, Is.EqualTo(0));
        Assert.That(trials[2].indexInBlock, Is.EqualTo(2));
        Assert.That(trials[3].blockIndex, Is.EqualTo(1));
        Assert.That(trials[3].indexInBlock, Is.EqualTo(0));
        Assert.That(trials[5].blockIndex, Is.EqualTo(1));
        Assert.That(trials[5].indexInBlock, Is.EqualTo(2));
        Assert.That(trials[6].blockIndex, Is.EqualTo(2));
        Assert.That(trials[6].indexInBlock, Is.EqualTo(0));
        Assert.That(trials[8].blockIndex, Is.EqualTo(2));
        Assert.That(trials[8].indexInBlock, Is.EqualTo(2));
    }

    [Test]
    public void BuildTrials_TrialIndexIsSequential()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.B, 6);

        for (int i = 0; i < trials.Length; i++)
        {
            Assert.That(trials[i].trialIndex, Is.EqualTo(i));
        }
    }

    // 各ブロックは 3 動画をちょうど 1 回ずつ含む。
    [Test]
    public void BuildTrials_EachBlockContainsEachVideoExactlyOnce()
    {
        for (int pattern = ExperimentPlan.MinVideoOrderPattern; pattern <= ExperimentPlan.MaxVideoOrderPattern; pattern++)
        {
            ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, pattern);

            for (int block = 0; block < ExperimentPlan.BlockCount; block++)
            {
                HashSet<ExperimentVideo> seen = new HashSet<ExperimentVideo>();
                for (int i = 0; i < trials.Length; i++)
                {
                    if (trials[i].blockIndex == block)
                    {
                        Assert.That(seen.Add(trials[i].video), Is.True,
                            $"pattern {pattern} block {block} に {trials[i].video} が重複しています。");
                    }
                }

                Assert.That(seen.Count, Is.EqualTo(3), $"pattern {pattern} block {block}");
            }
        }
    }

    // 6 パターンが互いに異なる順列であること。1 つでも重複していると
    // 割り付け表とプログラムの対応が崩れる。
    [Test]
    public void ResolveVideoOrder_SixPatternsAreDistinctPermutations()
    {
        HashSet<string> seen = new HashSet<string>();

        for (int pattern = ExperimentPlan.MinVideoOrderPattern; pattern <= ExperimentPlan.MaxVideoOrderPattern; pattern++)
        {
            ExperimentVideo[] order = ExperimentPlan.ResolveVideoOrder(pattern);

            Assert.That(order.Length, Is.EqualTo(3));
            Assert.That(new HashSet<ExperimentVideo>(order).Count, Is.EqualTo(3),
                $"pattern {pattern} に同じ動画が複数あります。");
            Assert.That(seen.Add(string.Join(",", order)), Is.True,
                $"pattern {pattern} が他のパターンと重複しています。");
        }

        Assert.That(seen.Count, Is.EqualTo(6));
    }

    [Test]
    public void ResolveVideoOrder_Pattern1IsHumanAnimalCar()
    {
        ExperimentVideo[] order = ExperimentPlan.ResolveVideoOrder(1);

        Assert.That(order[0], Is.EqualTo(ExperimentVideo.Human));
        Assert.That(order[1], Is.EqualTo(ExperimentVideo.Animal));
        Assert.That(order[2], Is.EqualTo(ExperimentVideo.Car));
    }

    [Test]
    public void ResolveVideoOrder_Pattern6IsCarAnimalHuman()
    {
        ExperimentVideo[] order = ExperimentPlan.ResolveVideoOrder(6);

        Assert.That(order[0], Is.EqualTo(ExperimentVideo.Car));
        Assert.That(order[1], Is.EqualTo(ExperimentVideo.Animal));
        Assert.That(order[2], Is.EqualTo(ExperimentVideo.Human));
    }

    // 返した配列を書き換えても内部テーブルが壊れないこと。
    [Test]
    public void ResolveVideoOrder_ReturnsIndependentCopy()
    {
        ExperimentVideo[] first = ExperimentPlan.ResolveVideoOrder(1);
        first[0] = ExperimentVideo.Car;

        ExperimentVideo[] second = ExperimentPlan.ResolveVideoOrder(1);
        Assert.That(second[0], Is.EqualTo(ExperimentVideo.Human));
    }

    // 割り付けミスを既定値で握りつぶさない。
    [Test]
    public void ResolveVideoOrder_OutOfRangePattern_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveVideoOrder(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveVideoOrder(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveVideoOrder(-1));
    }

    [Test]
    public void IsValidVideoOrderPattern_AcceptsOneToSixOnly()
    {
        Assert.That(ExperimentPlan.IsValidVideoOrderPattern(0), Is.False);
        Assert.That(ExperimentPlan.IsValidVideoOrderPattern(1), Is.True);
        Assert.That(ExperimentPlan.IsValidVideoOrderPattern(6), Is.True);
        Assert.That(ExperimentPlan.IsValidVideoOrderPattern(7), Is.False);
    }

    [Test]
    public void ResolveBlockMode_OutOfRangeBlock_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, ExperimentPlan.BlockCount));
    }

    // 群 A と群 B で、単眼ブロックは同じ、残り 2 ブロックの条件はちょうど入れ替わっていること。
    [Test]
    public void ResolveBlockMode_GroupsAreMirroredAfterTheMonocularBlock()
    {
        Assert.That(ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, 0), Is.EqualTo(ExperimentDisplayMode.Monocular));
        Assert.That(ExperimentPlan.ResolveBlockMode(ExperimentGroup.B, 0), Is.EqualTo(ExperimentDisplayMode.Monocular));

        for (int block = 1; block < ExperimentPlan.BlockCount; block++)
        {
            ExperimentDisplayMode a = ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, block);
            ExperimentDisplayMode b = ExperimentPlan.ResolveBlockMode(ExperimentGroup.B, block);

            Assert.That(a, Is.Not.EqualTo(b), $"block {block}");
            Assert.That(a, Is.Not.EqualTo(ExperimentDisplayMode.Monocular), $"block {block}");
            Assert.That(b, Is.Not.EqualTo(ExperimentDisplayMode.Monocular), $"block {block}");
        }

        // 各群の中で StereoOnly と ModelReplaced が 1 回ずつ出る。
        Assert.That(ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, 1), Is.Not.EqualTo(ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, 2)));
        Assert.That(ExperimentPlan.ResolveBlockMode(ExperimentGroup.B, 1), Is.Not.EqualTo(ExperimentPlan.ResolveBlockMode(ExperimentGroup.B, 2)));
    }

    [Test]
    public void DescribeAssignment_ListsAllThreeBlocks()
    {
        string text = ExperimentPlan.DescribeAssignment(ExperimentGroup.B, 2);

        Assert.That(text, Does.Contain("Human → Car → Animal"));
        Assert.That(text, Does.Contain("[Monocular] → [ModelReplaced] → [StereoOnly]"));
    }

    // 参加者番号 → 割り付け。奇数 = A、偶数 = B、動画順は 2 人ごとに 1 → 6。
    [Test]
    public void ResolveAssignment_AlternatesGroupAndCyclesPattern()
    {
        ExperimentPlan.ResolveAssignment(1, out ExperimentGroup g1, out int p1);
        ExperimentPlan.ResolveAssignment(2, out ExperimentGroup g2, out int p2);
        ExperimentPlan.ResolveAssignment(3, out ExperimentGroup g3, out int p3);
        ExperimentPlan.ResolveAssignment(12, out ExperimentGroup g12, out int p12);
        ExperimentPlan.ResolveAssignment(13, out ExperimentGroup g13, out int p13);

        Assert.That((g1, p1), Is.EqualTo((ExperimentGroup.A, 1)));
        Assert.That((g2, p2), Is.EqualTo((ExperimentGroup.B, 1)));
        Assert.That((g3, p3), Is.EqualTo((ExperimentGroup.A, 2)));
        Assert.That((g12, p12), Is.EqualTo((ExperimentGroup.B, 6)));
        Assert.That((g13, p13), Is.EqualTo((ExperimentGroup.A, 1)));
    }

    // 連続する 12 人で 12 通りが 1 回ずつ揃う。
    [Test]
    public void ResolveAssignment_TwelveConsecutiveParticipantsCoverAllCombinations()
    {
        HashSet<string> seen = new HashSet<string>();
        for (int n = 1; n <= 12; n++)
        {
            ExperimentPlan.ResolveAssignment(n, out ExperimentGroup group, out int pattern);
            Assert.That(ExperimentPlan.IsValidVideoOrderPattern(pattern), Is.True, $"P{n:00}");
            Assert.That(seen.Add($"{group}/{pattern}"), Is.True, $"P{n:00} の割り付けが重複");
        }

        Assert.That(seen.Count, Is.EqualTo(12));
    }

    // P00（テスト用）は P01 と同じ。
    [Test]
    public void ResolveAssignment_TestParticipantMatchesFirst()
    {
        ExperimentPlan.ResolveAssignment(0, out ExperimentGroup g0, out int p0);
        ExperimentPlan.ResolveAssignment(1, out ExperimentGroup g1, out int p1);

        Assert.That(g0, Is.EqualTo(g1));
        Assert.That(p0, Is.EqualTo(p1));
    }
}
