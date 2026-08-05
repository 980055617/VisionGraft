using System;
using System.Collections.Generic;
using NUnit.Framework;

public class ExperimentPlanTests
{
    [Test]
    public void BuildTrials_ProducesSixTrials()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1);

        Assert.That(trials.Length, Is.EqualTo(6));
    }

    // 群 A は 前半 StereoOnly → 後半 ModelReplaced。
    [Test]
    public void BuildTrials_GroupA_StereoOnlyBlockComesFirst()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 1);

        Assert.That(trials[0].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[1].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[2].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[3].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[4].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[5].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
    }

    // 群 B は逆順。順序効果の相殺がこの 2 群で成立する。
    [Test]
    public void BuildTrials_GroupB_ModelReplacedBlockComesFirst()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.B, 1);

        Assert.That(trials[0].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[2].mode, Is.EqualTo(ExperimentDisplayMode.ModelReplaced));
        Assert.That(trials[3].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
        Assert.That(trials[5].mode, Is.EqualTo(ExperimentDisplayMode.StereoOnly));
    }

    // 前半と後半で同じ動画順を使う（同じ動画が両条件で同じ位置に来る）。
    [Test]
    public void BuildTrials_BothBlocksUseTheSameVideoOrder()
    {
        ExperimentTrial[] trials = ExperimentPlan.BuildTrials(ExperimentGroup.A, 4);

        Assert.That(trials[0].video, Is.EqualTo(trials[3].video));
        Assert.That(trials[1].video, Is.EqualTo(trials[4].video));
        Assert.That(trials[2].video, Is.EqualTo(trials[5].video));
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
    public void ResolveVideoOrder_Pattern1IsHumanAnimalTrain()
    {
        ExperimentVideo[] order = ExperimentPlan.ResolveVideoOrder(1);

        Assert.That(order[0], Is.EqualTo(ExperimentVideo.Human));
        Assert.That(order[1], Is.EqualTo(ExperimentVideo.Animal));
        Assert.That(order[2], Is.EqualTo(ExperimentVideo.Train));
    }

    [Test]
    public void ResolveVideoOrder_Pattern6IsTrainAnimalHuman()
    {
        ExperimentVideo[] order = ExperimentPlan.ResolveVideoOrder(6);

        Assert.That(order[0], Is.EqualTo(ExperimentVideo.Train));
        Assert.That(order[1], Is.EqualTo(ExperimentVideo.Animal));
        Assert.That(order[2], Is.EqualTo(ExperimentVideo.Human));
    }

    // 返した配列を書き換えても内部テーブルが壊れないこと。
    [Test]
    public void ResolveVideoOrder_ReturnsIndependentCopy()
    {
        ExperimentVideo[] first = ExperimentPlan.ResolveVideoOrder(1);
        first[0] = ExperimentVideo.Train;

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
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, 2));
    }

    // 群 A と群 B で、各ブロックの条件がちょうど入れ替わっていること。
    [Test]
    public void ResolveBlockMode_GroupsAreMirrored()
    {
        for (int block = 0; block < ExperimentPlan.BlockCount; block++)
        {
            ExperimentDisplayMode a = ExperimentPlan.ResolveBlockMode(ExperimentGroup.A, block);
            ExperimentDisplayMode b = ExperimentPlan.ResolveBlockMode(ExperimentGroup.B, block);

            Assert.That(a, Is.Not.EqualTo(b), $"block {block}");
        }
    }
}
