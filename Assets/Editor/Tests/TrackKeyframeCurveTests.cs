using System.Collections.Generic;
using NUnit.Framework;

// 手動 yaw / 手動スケールの共通補間。
// 「二つの frame でそれぞれ調整したら間が遷移する」がユーザー要件なので、
// 補間そのものと端点の扱いを固定する。
public class TrackKeyframeCurveTests
{
    private static SortedDictionary<int, float> Keys(params (int frame, float value)[] items)
    {
        var keys = new SortedDictionary<int, float>();
        foreach ((int frame, float value) in items)
        {
            keys[frame] = value;
        }

        return keys;
    }


    [Test]
    public void EmptyOrNullReturnsFallback()
    {
        Assert.That(TrackKeyframeCurve.Evaluate(null, 10, 1f), Is.EqualTo(1f));
        Assert.That(TrackKeyframeCurve.Evaluate(new SortedDictionary<int, float>(), 10, 1f), Is.EqualTo(1f));
        // スケールの既定は 1、yaw の既定は 0。fallback がそのまま返ること。
        Assert.That(TrackKeyframeCurve.Evaluate(null, 10, 0f), Is.EqualTo(0f));
    }


    [Test]
    public void SingleKeyAppliesToEveryFrame()
    {
        SortedDictionary<int, float> keys = Keys((100, 2.5f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 0, 1f), Is.EqualTo(2.5f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 100, 1f), Is.EqualTo(2.5f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 9999, 1f), Is.EqualTo(2.5f));
    }


    [Test]
    public void InterpolatesLinearlyBetweenTwoKeys()
    {
        SortedDictionary<int, float> keys = Keys((100, 1f), (200, 3f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 100, 1f), Is.EqualTo(1f).Within(1e-4f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 150, 1f), Is.EqualTo(2f).Within(1e-4f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 175, 1f), Is.EqualTo(2.5f).Within(1e-4f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 200, 1f), Is.EqualTo(3f).Within(1e-4f));
    }


    // 端点の外は最初／最後のキーで固定する。外挿すると区間外で意図しない値になる。
    [Test]
    public void HoldsFirstAndLastOutsideTheKeyedRange()
    {
        SortedDictionary<int, float> keys = Keys((100, 1f), (200, 3f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 0, 99f), Is.EqualTo(1f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 99, 99f), Is.EqualTo(1f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 201, 99f), Is.EqualTo(3f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 100000, 99f), Is.EqualTo(3f));
    }


    [Test]
    public void UsesTheEnclosingPairWhenThreeOrMoreKeysExist()
    {
        SortedDictionary<int, float> keys = Keys((0, 0f), (100, 10f), (300, 30f));
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 50, 0f), Is.EqualTo(5f).Within(1e-4f));
        // 2 区間目は傾きが違う。手前の区間の傾きを引きずらないこと。
        Assert.That(TrackKeyframeCurve.Evaluate(keys, 200, 0f), Is.EqualTo(20f).Within(1e-4f));
    }
}
