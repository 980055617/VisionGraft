using NUnit.Framework;
using UnityEngine;

public class ManualYawGuideFactoryTests
{
    [Test]
    public void CreateBuildsRootShaftAndTipWithoutColliders()
    {
        ManualYawGuideFactory.Guide guide = ManualYawGuideFactory.Create();
        try
        {
            Assert.That(guide.root, Is.Not.Null);
            Assert.That(guide.shaft, Is.Not.Null);
            Assert.That(guide.tip, Is.Not.Null);
            Assert.That(guide.root.name, Is.EqualTo("ManualYawGuide"));
            Assert.That(guide.shaft.name, Is.EqualTo("Shaft"));
            Assert.That(guide.tip.name, Is.EqualTo("Tip"));
            Assert.That(guide.shaft.parent, Is.EqualTo(guide.root.transform));
            Assert.That(guide.tip.parent, Is.EqualTo(guide.root.transform));
            Assert.That(guide.shaft.GetComponent<Collider>(), Is.Null);
            Assert.That(guide.tip.GetComponent<Collider>(), Is.Null);
        }
        finally
        {
            Object.DestroyImmediate(guide.root);
        }
    }
}
