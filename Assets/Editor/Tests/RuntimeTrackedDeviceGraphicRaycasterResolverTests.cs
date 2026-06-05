using NUnit.Framework;
using UnityEngine;

public class RuntimeTrackedDeviceGraphicRaycasterResolverTests
{
    [Test]
    public void ResolveFirstAvailableReturnsFirstLoadableType()
    {
        System.Type type = RuntimeTrackedDeviceGraphicRaycasterResolver.ResolveFirstAvailable(
            "Missing.Type, Missing.Assembly",
            typeof(Transform).AssemblyQualifiedName);

        Assert.That(type, Is.EqualTo(typeof(Transform)));
    }

    [Test]
    public void ResolveFirstAvailableIgnoresEmptyNames()
    {
        System.Type type = RuntimeTrackedDeviceGraphicRaycasterResolver.ResolveFirstAvailable(
            null,
            string.Empty,
            typeof(GameObject).AssemblyQualifiedName);

        Assert.That(type, Is.EqualTo(typeof(GameObject)));
    }
}
