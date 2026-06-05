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
}
