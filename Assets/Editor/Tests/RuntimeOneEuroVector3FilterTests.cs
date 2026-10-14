using NUnit.Framework;
using UnityEngine;

public class RuntimeOneEuroVector3FilterTests
{
    [Test]
    public void FirstFilterCallReturnsInput()
    {
        var filter = new RuntimeOneEuroVector3Filter(1f, 0.15f, 1f);
        Vector3 value = new Vector3(1f, 2f, 3f);

        Vector3 filtered = filter.Filter(value, 1f / 60f);

        Assert.That(filtered.x, Is.EqualTo(value.x).Within(0.0001f));
        Assert.That(filtered.y, Is.EqualTo(value.y).Within(0.0001f));
        Assert.That(filtered.z, Is.EqualTo(value.z).Within(0.0001f));
    }


    [Test]
    public void ResetMakesNextFilterCallStartAtResetValue()
    {
        var filter = new RuntimeOneEuroVector3Filter(1f, 0.15f, 1f);
        filter.Filter(Vector3.zero, 1f / 60f);
        Vector3 resetValue = new Vector3(5f, -2f, 1f);

        filter.Reset(resetValue);
        Vector3 filtered = filter.Filter(resetValue, 1f / 60f);

        Assert.That(filtered.x, Is.EqualTo(resetValue.x).Within(0.0001f));
        Assert.That(filtered.y, Is.EqualTo(resetValue.y).Within(0.0001f));
        Assert.That(filtered.z, Is.EqualTo(resetValue.z).Within(0.0001f));
    }


    [Test]
    public void RepeatedFilterCallsMoveTowardTargetWithoutOvershoot()
    {
        var filter = new RuntimeOneEuroVector3Filter(1f, 0f, 1f);
        filter.Filter(Vector3.zero, 1f / 60f);

        Vector3 first = filter.Filter(Vector3.right * 10f, 1f / 60f);
        Vector3 second = filter.Filter(Vector3.right * 10f, 1f / 60f);

        Assert.That(first.x, Is.GreaterThan(0f));
        Assert.That(second.x, Is.GreaterThan(first.x));
        Assert.That(second.x, Is.LessThan(10f));
        Assert.That(second.y, Is.EqualTo(0f).Within(0.0001f));
        Assert.That(second.z, Is.EqualTo(0f).Within(0.0001f));
    }


    // Shift（2026-10-13、exactScaleRefine）: ずらした後は「入力の系列がはじめから delta ずれていた」フィルタと同じ出力になる。
    [Test]
    public void ShiftMakesNextOutputMatchAShiftedHistory()
    {
        var plain = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        var shifted = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1f);
        Vector3 delta = new Vector3(0.12f, -0.05f, 0.3f);
        Vector3[] inputs = { Vector3.zero, new Vector3(0.01f, 0f, 0.02f), new Vector3(0.03f, 0.01f, 0.05f) };
        foreach (Vector3 input in inputs)
        {
            plain.Filter(input, 1f / 72f);
            shifted.Filter(input, 1f / 72f);
        }

        shifted.Shift(delta);
        Vector3 next = new Vector3(0.05f, 0.01f, 0.09f);
        for (int i = 0; i < 3; i++)
        {
            Vector3 a = plain.Filter(next, 1f / 72f);
            Vector3 b = shifted.Filter(next + delta, 1f / 72f);
            Assert.That(b.x - a.x, Is.EqualTo(delta.x).Within(0.0001f));
            Assert.That(b.y - a.y, Is.EqualTo(delta.y).Within(0.0001f));
            Assert.That(b.z - a.z, Is.EqualTo(delta.z).Within(0.0001f));
        }
    }


    [Test]
    public void ShiftBeforeFirstFilterCallIsIgnored()
    {
        var filter = new RuntimeOneEuroVector3Filter(1f, 0.15f, 1f);
        filter.Shift(new Vector3(5f, 5f, 5f));
        Vector3 value = new Vector3(1f, 2f, 3f);

        Vector3 filtered = filter.Filter(value, 1f / 60f);

        Assert.That(filtered.x, Is.EqualTo(value.x).Within(0.0001f));
        Assert.That(filtered.y, Is.EqualTo(value.y).Within(0.0001f));
        Assert.That(filtered.z, Is.EqualTo(value.z).Within(0.0001f));
    }
}
